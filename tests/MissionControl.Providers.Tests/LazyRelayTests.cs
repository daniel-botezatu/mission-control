using System.IO.Pipelines;
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
    // From the 2026-07-28 revision on, hosts probe with server/discover and send per-request metadata instead of initialize.
    private const string Meta = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{"roots":{}},"io.modelcontextprotocol/clientInfo":{"name":"host","version":"1"}}""";
    private const string Discover = """{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":""" + Meta + "}}";
    private const string ModernToolsList = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"_meta":""" + Meta + "}}";
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
        Assert.Contains("tools/list", cache.Get("tools/list 2025-06-18"));
        Assert.DoesNotContain("next", cache.Get("tools/list 2025-06-18"));
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

    [Fact]
    public async Task DiscoverProbeIsAnsweredFromTheCacheOnceASessionHasSeenIt()
    {
        var (_, coldStarts) = await RunAsync(new StartupCache(CachePath, "key"), [Discover, ModernToolsList], new FakeServer().Start);
        Assert.Equal(1, coldStarts);
        var (output, starts) = await RunAsync(new StartupCache(CachePath, "key"), [Discover, ModernToolsList], new FakeServer().Start);
        Assert.Equal(0, starts);
        Assert.Equal(["server/discover", "tools/list"], output.Select(line => (string)line["result"]!["method"]!).ToArray());
        // Different declared capabilities can change what the server advertises, so they are not answered from the cache.
        var other = Discover.Replace("""{"roots":{}}""", """{"sampling":{}}""", StringComparison.Ordinal);
        var (_, otherStarts) = await RunAsync(new StartupCache(CachePath, "key"), [other], new FakeServer().Start);
        Assert.Equal(1, otherStarts);
    }

    [Fact]
    public async Task ModernSessionStartsTheServerForARealCallWithoutAHandshake()
    {
        await RunAsync(new StartupCache(CachePath, "key"), [Discover, ModernToolsList], new FakeServer().Start);
        var server = new FakeServer();
        var call = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_me","_meta":""" + Meta + "}}";
        var (output, starts) = await RunAsync(new StartupCache(CachePath, "key"), [Discover, ModernToolsList, call], server.Start);
        Assert.Equal(1, starts);
        Assert.Equal([call], server.Received.ToArray());
        Assert.Equal("tools/call", (string)output[2]["result"]!["method"]!);
    }

    [Fact]
    public async Task IdleServerStopsAndTheNextCallRestartsItWithTheHandshake()
    {
        var server = new FakeServer { Unanswered = "hang" };
        var starts = 0;
        var pipe = new Pipe();
        using var output = new MemoryStream();
        var relay = new LazyRelay(pipe.Reader.AsStream(), Stream.Synchronized(output), Filled(), TimeSpan.FromMilliseconds(100))
            .RunAsync(token => { starts++; return server.Start(token); }, CancellationToken.None);
        async Task Send(string line) => await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await Send(Initialize); await Send(Initialized);
        await Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_me"}}""");
        await Until(() => server.Disposed == 1);
        Assert.Equal(1, starts);
        // A request still waiting for its answer keeps the server running until the host cancels it.
        await Send("""{"jsonrpc":"2.0","id":4,"method":"hang","params":{}}""");
        await Until(() => starts == 2);
        await Task.Delay(400);
        Assert.Equal(1, server.Disposed);
        await Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":4}}""");
        await Until(() => server.Disposed == 2);
        await pipe.Writer.CompleteAsync();
        Assert.Equal(0, await relay.WaitAsync(TimeSpan.FromSeconds(10)));
        // Each start replays the handshake answered from the cache: initialize, initialized, then the call.
        var restarted = server.Received.Skip(3).Take(3).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(LazyRelay.HandshakeId, (string)restarted[0]["id"]!);
        Assert.Equal("notifications/initialized", (string)restarted[1]["method"]!);
        Assert.Equal("hang", (string)restarted[2]["method"]!);
    }

    [Fact]
    public void ConcurrentSessionsKeepEachOthersAnswers()
    {
        var path = CachePath;
        var first = new StartupCache(path, "key");
        new StartupCache(path, "key").Store("initialize 2026-07-28", """{"from":"second"}""");
        first.Store("tools/list 2025-06-18", """{"from":"first"}""");
        var merged = new StartupCache(path, "key");
        Assert.Equal("""{"from":"second"}""", merged.Get("initialize 2026-07-28"));
        Assert.Equal("""{"from":"first"}""", merged.Get("tools/list 2025-06-18"));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    private StartupCache Filled()
    {
        var cache = new StartupCache(CachePath, "key");
        cache.Store("initialize 2025-06-18", """{"method":"initialize"}""");
        cache.Store("tools/list 2025-06-18", """{"method":"tools/list"}""");
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
        private int disposed;
        public List<string> Received { get; } = [];
        public string? Unanswered { get; init; }
        public int Disposed => Volatile.Read(ref disposed);

        public Task<IUpstream> Start(CancellationToken token) => Task.FromResult<IUpstream>(new Connection(this));

        private sealed class Connection(FakeServer server) : IUpstream
        {
            private readonly Channel<string> replies = Channel.CreateUnbounded<string>();
            private readonly TaskCompletionSource<int> exit = new();

            public Task<int> Exit => exit.Task;

            public Task WriteLineAsync(string line, CancellationToken token)
            {
                lock (server.Received) server.Received.Add(line);
                var request = JsonNode.Parse(line)!;
                if (request["id"] is { } id && (string?)request["method"] != server.Unanswered)
                    replies.Writer.TryWrite(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JsonObject { ["method"] = (string)request["method"]!, ["page"] = request["params"]?["cursor"]?.DeepClone() } }.ToJsonString());
                return Task.CompletedTask;
            }

            public async Task<string?> ReadLineAsync(CancellationToken token) =>
                await replies.Reader.WaitToReadAsync(token) && replies.Reader.TryRead(out var line) ? line : null;

            public void CloseInput() { replies.Writer.TryComplete(); exit.TrySetResult(0); }

            public ValueTask DisposeAsync() { CloseInput(); Interlocked.Increment(ref server.disposed); return ValueTask.CompletedTask; }
        }
    }
}
