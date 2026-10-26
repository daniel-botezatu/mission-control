using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MissionControl.Providers;

/// <summary>One running upstream MCP server, spoken to in newline-delimited JSON-RPC.</summary>
internal interface IUpstream : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken token);
    Task<string?> ReadLineAsync(CancellationToken token);
    void CloseInput();
    Task<int> Exit { get; }
}

/// <summary>
/// Answers the host's startup handshake and list requests from <see cref="StartupCache"/>, so the upstream server
/// (and its credential lookup) starts only when the host first needs a real call. Most sessions never call GitHub,
/// and each idle upstream server would otherwise hold about 65 MB. When the cache cannot answer, the server starts
/// at once and the relay is transparent; answers it gives to cacheable requests refill the cache.
/// </summary>
internal sealed class LazyRelay(Stream hostInput, Stream hostOutput, StartupCache cache)
{
    internal const string HandshakeId = "mission-control-deferred-start";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] ListMethods = ["tools/list", "prompts/list", "resources/list", "resources/templates/list"];
    private readonly SemaphoreSlim hostWrite = new(1, 1);
    private readonly ConcurrentDictionary<string, string> captures = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<int> upstreamExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IUpstream? upstream;
    private string? initializeParams;
    private bool initialized;

    public async Task<int> RunAsync(Func<CancellationToken, Task<IUpstream>> start, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var host = ReadHostAsync(start, lifetime.Token);
        try
        {
            var first = await Task.WhenAny(host, upstreamExited.Task).ConfigureAwait(false);
            return await first.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            if (upstream is not null) await upstream.DisposeAsync().ConfigureAwait(false);
            // Console stdin may not observe cancellation; do not hold the launcher alive waiting on that read.
            _ = host.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task<int> ReadHostAsync(Func<CancellationToken, Task<IUpstream>> start, CancellationToken token)
    {
        using var reader = new StreamReader(hostInput, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
        Task? pump = null;
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var message = Message.Parse(line);
            if (upstream is null)
            {
                if (Answer(message) is { } answer) { await WriteHostAsync(answer, token).ConfigureAwait(false); continue; }
                // Notifications before the server starts concern nothing it has seen.
                if (message is { IsObject: true, Id: null }) continue;
                try { upstream = await StartAsync(start, token).ConfigureAwait(false); }
                catch (Exception) when (!token.IsCancellationRequested && message?.Id is { } id)
                {
                    await WriteHostAsync(Error(id, Program.Failure), token).ConfigureAwait(false);
                    continue;
                }
                pump = PumpAsync(upstream, token);
            }
            if (message is { Id: { } requestId, Entry: { } entry }) captures[requestId] = entry;
            try { await upstream.WriteLineAsync(line, token).ConfigureAwait(false); }
            // The server has left; report how it ended rather than the broken pipe.
            catch (IOException) { return await upstreamExited.Task.ConfigureAwait(false); }
        }
        if (upstream is null) return 0;
        // A host that closes stdin expects the whole local server tree to leave promptly.
        upstream.CloseInput();
        try { await upstream.Exit.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch (TimeoutException) { await upstream.DisposeAsync().ConfigureAwait(false); }
        await pump!.ConfigureAwait(false);
        return await upstream.Exit.ConfigureAwait(false);
    }

    private string? Answer(Message? message)
    {
        if (message is not { Method: { } method }) return null;
        if (method == "notifications/initialized") { initialized = true; return null; }
        if (message.Id is not { } id) return null;
        if (method == "ping") return Response(id, "{}");
        if (message.Entry is not { } entry || cache.Get(entry) is not { } result) return null;
        if (method == "initialize") initializeParams = message.Params;
        return Response(id, result);
    }

    /// <summary>Starts the server and, when the host's handshake was answered from the cache, replays it first.</summary>
    private async Task<IUpstream> StartAsync(Func<CancellationToken, Task<IUpstream>> start, CancellationToken token)
    {
        var started = await start(token).ConfigureAwait(false);
        try
        {
            if (initializeParams is null) return started;
            await started.WriteLineAsync($$"""{"jsonrpc":"2.0","id":"{{HandshakeId}}","method":"initialize","params":{{initializeParams}}}""", token).ConfigureAwait(false);
            while (true)
            {
                var line = await started.ReadLineAsync(token).WaitAsync(HandshakeTimeout, token).ConfigureAwait(false) ?? throw new InvalidOperationException();
                if (Message.Parse(line) is not { Id: "\"" + HandshakeId + "\"", Method: null } reply) continue;
                if (reply.Result is null) throw new InvalidOperationException();
                break;
            }
            if (initialized) await started.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", token).ConfigureAwait(false);
            return started;
        }
        catch
        {
            await started.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PumpAsync(IUpstream server, CancellationToken token)
    {
        try
        {
            while (await server.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                // Only parse upstream output while a cacheable request is outstanding; tool results can be large.
                if (!captures.IsEmpty && Message.Parse(line) is { Id: { } id, Method: null } reply && captures.TryRemove(id, out var entry) && reply.Result is { } result)
                    cache.Store(entry, result);
                await WriteHostAsync(line, token).ConfigureAwait(false);
            }
            upstreamExited.TrySetResult(await server.Exit.ConfigureAwait(false));
        }
        catch (Exception exception) { upstreamExited.TrySetException(exception); }
    }

    private async Task WriteHostAsync(string line, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await hostWrite.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await hostOutput.WriteAsync(bytes, token).ConfigureAwait(false);
            await hostOutput.FlushAsync(token).ConfigureAwait(false);
        }
        finally { hostWrite.Release(); }
    }

    private static string Response(string id, string result) => $$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""";

    private static string Error(string id, string text) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id), ["error"] = new JsonObject { ["code"] = -32603, ["message"] = text } }.ToJsonString();

    /// <summary>The parts of one JSON-RPC line the relay needs; Id and Params keep their raw JSON text.</summary>
    private sealed record Message(bool IsObject, string? Id, string? Method, string? Params, string? Entry, string? Result)
    {
        internal static Message? Parse(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new(false, null, null, null, null, null);
                string? id = root.TryGetProperty("id", out var idValue) && idValue.ValueKind is JsonValueKind.String or JsonValueKind.Number ? idValue.GetRawText() : null;
                string? method = root.TryGetProperty("method", out var methodValue) && methodValue.ValueKind == JsonValueKind.String ? methodValue.GetString() : null;
                JsonElement? parameters = root.TryGetProperty("params", out var paramsValue) && paramsValue.ValueKind == JsonValueKind.Object ? paramsValue : null;
                string? result = root.TryGetProperty("result", out var resultValue) && resultValue.ValueKind == JsonValueKind.Object ? resultValue.GetRawText() : null;
                return new(true, id, method, parameters?.GetRawText(), CacheEntry(method, parameters), result);
            }
            catch (JsonException) { return null; }
        }

        private static string? CacheEntry(string? method, JsonElement? parameters)
        {
            if (method == "initialize")
                return parameters?.TryGetProperty("protocolVersion", out var version) == true && version.ValueKind == JsonValueKind.String ? "initialize " + version.GetString() : null;
            // Later pages depend on a cursor and are always fetched from the server.
            return ListMethods.Contains(method) && parameters?.TryGetProperty("cursor", out _) != true ? method : null;
        }
    }
}
