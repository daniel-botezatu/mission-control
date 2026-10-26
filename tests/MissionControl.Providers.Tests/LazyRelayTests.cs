using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MissionControl.Providers;

namespace MissionControl.Providers.Tests;

/// <summary>The upstream GitHub server starts only when the host needs more than the startup answers.</summary>
public sealed class LazyRelayTests : IDisposable
{
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"host","version":"1"}}}""";
    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string ToolsList = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mission-control-provider-tests-" + Guid.NewGuid().ToString("N"));
    private string CachePath { get { Directory.CreateDirectory(directory); return Path.Combine(directory, "startup-cache.json"); } }

    [Fact]
    public async Task FirstSessionStartsTheServerAtOnceAndKeepsItsStartupAnswers()
    {
        var server = new FakeServer();
        var (output, starts) = await RunAsync(new StartupCache(CachePath, "key"), [Initialize, Initialized, ToolsList, """{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{"cursor":"next"}}"""], server.Start);
        Assert.Equal(1, starts);
        Assert.Equal([1, 2, 3], output.Select(line => (int)line["id"]!).ToArray());
        Assert.Equal([Initialize, Initialized, ToolsList], server.Received.Take(3).ToArray());
        var cache = new StartupCache(CachePath, "key");
        Assert.Contains("initialize", cache.Get("initialize 2025-06-18"));
        Assert.Contains("tools/list", cache.Get("tools/list"));
        Assert.DoesNotContain("next", cache.Get("tools/list"));
    }

    [Fact]
    public async Task LaterSessionAnswersTheStartupWithoutStartingTheServer()
    {
        var cache = Filled();
        var (output, starts) = await RunAsync(cache, [Initialize, Initialized, ToolsList, """{"jsonrpc":"2.0","id":"p","method":"ping"}""", """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}"""], new FakeServer().Start);
        Assert.Equal(0, starts);
        Assert.Equal(["1", "2", "\"p\""], output.Select(line => line["id"]!.ToJsonString()).ToArray());
        Assert.Equal("initialize", (string)output[0]["result"]!["method"]!);
        Assert.Equal("tools/list", (string)output[1]["result"]!["method"]!);
    }

    [Fact]
    public async Task FirstRealCallStartsTheServerAndReplaysTheHandshakeOutOfSight()
    {
        var server = new FakeServer();
        var (output, starts) = await RunAsync(Filled(), [Initialize, Initialized, ToolsList, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_me"}}"""], server.Start);
        Assert.Equal(1, starts);
        Assert.Equal([1, 2, 3], output.Select(line => (int)line["id"]!).ToArray());
        Assert.Equal("tools/call", (string)output[2]["result"]!["method"]!);
        var replayed = JsonNode.Parse(server.Received[0])!;
        Assert.Equal(LazyRelay.HandshakeId, (string)replayed["id"]!);
        Assert.Equal("initialize", (string)replayed["method"]!);
        Assert.Equal("host", (string)replayed["params"]!["clientInfo"]!["name"]!);
        Assert.Equal([Initialized, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_me"}}"""], server.Received.Skip(1).ToArray());
        Assert.DoesNotContain(output, line => line.ToJsonString().Contains(LazyRelay.HandshakeId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedStartAnswersWithTheFixedDiagnosticAndTheNextCallRetries()
    {
        var server = new FakeServer();
        var attempts = 0;
        Task<IUpstream> Start(CancellationToken token) => ++attempts == 1 ? throw new InvalidOperationException("synthetic-secret-diagnostic") : server.Start(token);
        var (output, _) = await RunAsync(Filled(), [Initialize, Initialized,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{}}""", """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{}}"""], Start);
        Assert.Equal(2, attempts);
        Assert.Equal(Program.Failure, (string)output[1]["error"]!["message"]!);
        Assert.DoesNotContain("synthetic-secret", output[1].ToJsonString());
        Assert.Equal("tools/call", (string)output[2]["result"]!["method"]!);
    }

    [Fact]
    public void AnswersAreDiscardedWhenTheServerOrToolSetChanges()
    {
        var path = CachePath;
        new StartupCache(path, "server-a|tools").Store("tools/list", """{"tools":[]}""");
        Assert.Null(new StartupCache(path, "server-b|tools").Get("tools/list"));
        Assert.Equal("""{"tools":[]}""", new StartupCache(path, "server-a|tools").Get("tools/list"));
        File.WriteAllText(path, "not json");
        Assert.Null(new StartupCache(path, "server-a|tools").Get("tools/list"));
    }

    private StartupCache Filled()
    {
        var cache = new StartupCache(CachePath, "key");
        cache.Store("initialize 2025-06-18", """{"method":"initialize"}""");
        cache.Store("tools/list", """{"method":"tools/list"}""");
        return cache;
    }

    private static async Task<(JsonNode[] Output, int Starts)> RunAsync(StartupCache cache, string[] lines, Func<CancellationToken, Task<IUpstream>> start)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        using var output = new MemoryStream();
        var starts = 0;
        var status = await new LazyRelay(input, output, cache).RunAsync(token => { starts++; return start(token); }, CancellationToken.None);
        Assert.Equal(0, status);
        var replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        return (replies, starts);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    /// <summary>Answers every request with its method name, as a server would answer with its result.</summary>
    private sealed class FakeServer
    {
        public List<string> Received { get; } = [];

        public Task<IUpstream> Start(CancellationToken token) => Task.FromResult<IUpstream>(new Connection(this));

        private sealed class Connection(FakeServer server) : IUpstream
        {
            private readonly Channel<string> replies = Channel.CreateUnbounded<string>();
            private readonly TaskCompletionSource<int> exit = new();

            public Task<int> Exit => exit.Task;

            public Task WriteLineAsync(string line, CancellationToken token)
            {
                server.Received.Add(line);
                var request = JsonNode.Parse(line)!;
                if (request["id"] is { } id)
                    replies.Writer.TryWrite(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JsonObject { ["method"] = (string)request["method"]!, ["page"] = request["params"]?["cursor"]?.DeepClone() } }.ToJsonString());
                return Task.CompletedTask;
            }

            public async Task<string?> ReadLineAsync(CancellationToken token) =>
                await replies.Reader.WaitToReadAsync(token) && replies.Reader.TryRead(out var line) ? line : null;

            public void CloseInput() { replies.Writer.TryComplete(); exit.TrySetResult(0); }

            public ValueTask DisposeAsync() { CloseInput(); return ValueTask.CompletedTask; }
        }
    }
}
