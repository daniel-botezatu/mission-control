using System.Collections.Concurrent;
using System.Security.Cryptography;
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
/// Answers the host's startup requests from <see cref="StartupCache"/>, so the upstream server (and its credential
/// lookup) starts only when the host first needs a real call, and stops it again after it has been idle. Most
/// sessions never call GitHub, and each running upstream server holds about 60 MB. Startup requests are the
/// initialize handshake and list calls of earlier protocol revisions, and server/discover plus list calls with
/// per-request metadata from 2026-07-28 on. When the cache cannot answer, the server starts at once and the relay
/// is transparent; answers it gives to cacheable requests refill the cache.
/// </summary>
internal sealed class LazyRelay(Stream hostInput, Stream hostOutput, StartupCache cache, TimeSpan? idleTimeout = null)
{
    internal const string HandshakeId = "mission-control-deferred-start";
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);
    private const string MetaPrefix = "io.modelcontextprotocol/";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] ListMethods = ["tools/list", "prompts/list", "resources/list", "resources/templates/list"];
    private readonly TimeSpan idle = idleTimeout ?? DefaultIdleTimeout;
    private readonly SemaphoreSlim hostWrite = new(1, 1), upstreamGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> captures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> outstanding = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<int> upstreamExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IUpstream? upstream, stopped;
    private Task? pump;
    private string? initializeParams, initializeId, protocolVersion;
    private bool initialized, handshakeAnswered;
    private long lastActivity = Environment.TickCount64;

    public async Task<int> RunAsync(Func<CancellationToken, Task<IUpstream>> start, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var host = ReadHostAsync(start, lifetime.Token);
        var watch = StopWhenIdleAsync(lifetime.Token);
        try
        {
            var first = await Task.WhenAny(host, upstreamExited.Task).ConfigureAwait(false);
            return await first.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            try { await watch.ConfigureAwait(false); } catch (OperationCanceledException) { }
            if (upstream is not null) await upstream.DisposeAsync().ConfigureAwait(false);
            // Console stdin may not observe cancellation; do not hold the launcher alive waiting on that read.
            _ = host.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task<int> ReadHostAsync(Func<CancellationToken, Task<IUpstream>> start, CancellationToken token)
    {
        using var reader = new StreamReader(hostInput, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var message = Message.Parse(line);
            Observe(message);
            await upstreamGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (upstream is null)
                {
                    if (Answer(message) is { } answer) { await WriteHostAsync(answer, token).ConfigureAwait(false); continue; }
                    // Notifications while no server runs concern nothing a server has seen.
                    if (message is { IsObject: true, Id: null }) continue;
                    try { upstream = await StartAsync(start, token).ConfigureAwait(false); }
                    catch (Exception) when (!token.IsCancellationRequested && message?.Id is { } id)
                    {
                        await WriteHostAsync(Error(id, Program.Failure), token).ConfigureAwait(false);
                        continue;
                    }
                    pump = PumpAsync(upstream, token);
                }
                if (message is { Id: { } requestId, Method: { } })
                {
                    outstanding[requestId] = 0;
                    if (EntryFor(message) is { } entry) captures[requestId] = entry;
                }
                Touch();
                try { await upstream.WriteLineAsync(line, token).ConfigureAwait(false); }
                // The server has left; report how it ended rather than the broken pipe.
                catch (IOException) { return await upstreamExited.Task.ConfigureAwait(false); }
            }
            finally { upstreamGate.Release(); }
        }
        await upstreamGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (upstream is null) return 0;
            // A host that closes stdin expects the whole local server tree to leave promptly.
            var current = upstream;
            current.CloseInput();
            try { await current.Exit.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
            catch (TimeoutException) { await current.DisposeAsync().ConfigureAwait(false); }
            await pump!.ConfigureAwait(false);
            return await current.Exit.ConfigureAwait(false);
        }
        finally { upstreamGate.Release(); }
    }

    /// <summary>Remembers what a restarted server must be told again, whichever way the host's message is answered.</summary>
    private void Observe(Message? message)
    {
        switch (message?.Method)
        {
            case "initialize":
                (initializeParams, initializeId, protocolVersion) = (message.Params, message.Id, message.InitializeVersion);
                break;
            case "notifications/initialized":
                initialized = true;
                break;
            case "notifications/cancelled" when message.CancelledId is { } cancelled:
                outstanding.TryRemove(cancelled, out _);
                captures.TryRemove(cancelled, out _);
                break;
        }
    }

    private string? Answer(Message? message)
    {
        if (message is not { Method: { } method, Id: { } id }) return null;
        if (method == "ping") return Response(id, "{}");
        if (EntryFor(message) is not { } entry || cache.Get(entry) is not { } result) return null;
        if (method == "initialize") handshakeAnswered = true;
        return Response(id, result);
    }

    /// <summary>
    /// Startup answers are kept per protocol revision. Requests with per-request metadata (2026-07-28 on) are also
    /// kept per declared client capabilities, which can change what the server advertises.
    /// </summary>
    private string? EntryFor(Message message) => message.Method switch
    {
        "initialize" => message.InitializeVersion is { } version ? "initialize " + version : null,
        "server/discover" => "server/discover " + Scope(message),
        // Later pages depend on a cursor and are always fetched from the server.
        { } method when ListMethods.Contains(method) && !message.HasCursor => method + " " + Scope(message),
        _ => null
    };

    private string Scope(Message message) => message.MetaVersion is { } version
        ? version + (message.ClientCapabilities is { } capabilities ? " " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capabilities)))[..12] : "")
        : protocolVersion ?? "unknown";

    /// <summary>Starts the server and, when the host's handshake was already answered, replays it first.</summary>
    private async Task<IUpstream> StartAsync(Func<CancellationToken, Task<IUpstream>> start, CancellationToken token)
    {
        var started = await start(token).ConfigureAwait(false);
        try
        {
            if (!handshakeAnswered || initializeParams is null) return started;
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
                // Only parse upstream output while a request is outstanding; notifications need no bookkeeping.
                if (!outstanding.IsEmpty && Message.Parse(line) is { Id: { } id, Method: null } reply && outstanding.TryRemove(id, out _))
                {
                    if (id == initializeId) handshakeAnswered = true;
                    if (captures.TryRemove(id, out var entry) && reply.Result is { } result) cache.Store(entry, result);
                }
                Touch();
                await WriteHostAsync(line, token).ConfigureAwait(false);
            }
            var code = await server.Exit.ConfigureAwait(false);
            if (!ReferenceEquals(server, stopped)) upstreamExited.TrySetResult(code);
        }
        catch (Exception exception) when (!ReferenceEquals(server, stopped)) { upstreamExited.TrySetException(exception); }
        catch (Exception) { }
    }

    /// <summary>Stops a server that has answered everything it was asked and then sat unused; the next call starts it again.</summary>
    private async Task StopWhenIdleAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(idle.TotalMilliseconds / 4, 10, 30_000)));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (!Idle()) continue;
            await upstreamGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!Idle()) continue;
                var current = upstream!;
                (stopped, upstream) = (current, null);
                current.CloseInput();
                try { await current.Exit.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
                catch (TimeoutException) { }
                await current.DisposeAsync().ConfigureAwait(false);
                await pump!.ConfigureAwait(false);
                (pump, stopped) = (null, null);
                captures.Clear();
            }
            finally { upstreamGate.Release(); }
        }
    }

    private bool Idle() => upstream is not null && outstanding.IsEmpty && Environment.TickCount64 - Volatile.Read(ref lastActivity) >= idle.TotalMilliseconds;

    private void Touch() => Volatile.Write(ref lastActivity, Environment.TickCount64);

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

    /// <summary>The parts of one JSON-RPC line the relay needs; ids, params and capabilities keep their raw JSON text.</summary>
    private sealed record Message(bool IsObject, string? Id, string? Method, string? Params, string? Result,
        string? InitializeVersion, string? MetaVersion, string? ClientCapabilities, bool HasCursor, string? CancelledId)
    {
        internal static Message? Parse(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new(false, null, null, null, null, null, null, null, false, null);
                string? method = root.TryGetProperty("method", out var methodValue) && methodValue.ValueKind == JsonValueKind.String ? methodValue.GetString() : null;
                JsonElement? parameters = root.TryGetProperty("params", out var paramsValue) && paramsValue.ValueKind == JsonValueKind.Object ? paramsValue : null;
                JsonElement? meta = parameters?.TryGetProperty("_meta", out var metaValue) == true && metaValue.ValueKind == JsonValueKind.Object ? metaValue : null;
                return new(true, RawId(root, "id"), method, parameters?.GetRawText(),
                    root.TryGetProperty("result", out var resultValue) && resultValue.ValueKind == JsonValueKind.Object ? resultValue.GetRawText() : null,
                    method == "initialize" ? Text(parameters, "protocolVersion") : null,
                    Text(meta, MetaPrefix + "protocolVersion"),
                    meta?.TryGetProperty(MetaPrefix + "clientCapabilities", out var capabilities) == true ? capabilities.GetRawText() : null,
                    parameters?.TryGetProperty("cursor", out _) == true,
                    method == "notifications/cancelled" && parameters is { } cancelled ? RawId(cancelled, "requestId") : null);
            }
            catch (JsonException) { return null; }
        }

        private static string? RawId(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.GetRawText() : null;

        private static string? Text(JsonElement? element, string name) =>
            element?.TryGetProperty(name, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
