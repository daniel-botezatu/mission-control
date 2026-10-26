using System.Text;
using System.Text.Json;
using MissionControl.Core;
using ZstdSharp;

namespace MissionControl.Tests;

/// <summary>Codex compresses idle rollouts to name.jsonl.zst; both spellings are one conversation.</summary>
public sealed class CompressedRolloutTests
{
    [Fact]
    public async Task RolloutCompressedBeforeItWasEverReadIsIndexed()
    {
        using var f = new Fixture();
        f.WriteCompressed(f.Lines("Request from a compressed rollout", "Answer from a compressed rollout"));
        await f.Run();
        Assert.Equal(["Answer from a compressed rollout", "Request from a compressed rollout"], f.Bodies());
        Assert.Null(f.Gap());
    }

    [Fact]
    public async Task CompressingAnIndexedRolloutKeepsTheSameConversationWithoutDuplicatesOrAMissingWarning()
    {
        using var f = new Fixture();
        f.WritePlain(f.Lines("First request", "First answer"));
        await f.Run();
        var before = f.Records();
        var checkpoint = Assert.Single(f.Store.GetCheckpoints("source"));

        // What Codex does: write name.jsonl.zst, give it a new time, delete name.jsonl.
        f.WriteCompressed(File.ReadAllBytes(f.Plain));
        File.SetLastWriteTimeUtc(f.Compressed, DateTime.UtcNow.AddMinutes(5));
        File.Delete(f.Plain);
        await f.Run();

        var after = f.Records();
        Assert.Equal(before.Select(r => (r.Id, r.Body, r.ChangedAt)), after.Select(r => (r.Id, r.Body, r.ChangedAt)));
        var stream = Assert.Single(f.Store.GetCheckpoints("source"));
        Assert.Equal(checkpoint.StreamId, stream.StreamId);
        Assert.Equal(checkpoint.Offset, stream.Offset);
        Assert.Null(f.Gap());
        // The compressed file's own size and time are kept, so later scans skip it without decompressing.
        using var state = JsonDocument.Parse(stream.MetadataJson!);
        long Value(string name) => state.RootElement.EnumerateObject().Single(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value.GetInt64();
        Assert.Equal(new FileInfo(f.Compressed).Length, Value("compressedLength"));
        Assert.Equal(File.GetLastWriteTimeUtc(f.Compressed).Ticks, Value("compressedTicks"));
    }

    [Fact]
    public async Task RestoredRolloutContinuesFromWhereTheCompressedCopyEnded()
    {
        using var f = new Fixture();
        f.WriteCompressed(f.Lines("Older request", "Older answer"));
        await f.Run();
        var ids = f.Records().Select(r => r.Id).ToHashSet();

        // Resuming a compressed thread brings back name.jsonl with new messages appended.
        File.WriteAllBytes(f.Plain, Decompress(File.ReadAllBytes(f.Compressed)).Concat(f.Lines("Newer request", "Newer answer", turn: "t2")).ToArray());
        File.Delete(f.Compressed);
        await f.Run();

        var records = f.Records();
        Assert.Equal(4, records.Count);
        Assert.True(ids.IsSubsetOf(records.Select(r => r.Id)));
        Assert.Single(f.Store.GetCheckpoints("source"));
        Assert.Null(f.Gap());
    }

    [Fact]
    public async Task WhileBothSpellingsExistThePlainFileIsReadOnce()
    {
        using var f = new Fixture();
        var lines = f.Lines("Request in both copies", "Answer in both copies");
        f.WritePlain(lines);
        f.WriteCompressed(lines);
        await f.Run();
        Assert.Equal(2, f.Records().Count);
        Assert.Single(f.Store.GetCheckpoints("source"));
    }

    [Fact]
    public async Task UnreadableCompressedRolloutIsReportedWithoutStoppingCollection()
    {
        using var f = new Fixture();
        File.WriteAllBytes(f.Compressed, [0x28, 0xb5, 0x2f, 0xfd, 1, 2, 3, 4, 5]);
        await f.Run();
        Assert.Empty(f.Records());
        Assert.Contains("compressed Codex rollout could not be decompressed", f.Gap() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileDeletedAfterItWasCompletelyReadIsNotReportedAsMissingHistory()
    {
        using var f = new Fixture();
        f.WritePlain(f.Lines("Request before the host's clean-up", "Answer before the host's clean-up"));
        await f.Run();
        File.Delete(f.Plain); // what Claude Code's 30-day clean-up does
        await f.Run();
        Assert.Equal(2, f.Records().Count);
        Assert.Null(f.Gap());
    }

    [Fact]
    public async Task FileDeletedBeforeItsLastRecordWasCompleteIsReported()
    {
        using var f = new Fixture();
        f.WritePlain(f.Lines("Request", "Answer").Concat(Encoding.UTF8.GetBytes("{\"type\":\"response_item\",\"payload\":{\"type\":\"mess")).ToArray());
        await f.Run();
        File.Delete(f.Plain);
        await f.Run();
        Assert.Equal(2, f.Records().Count);
        Assert.Contains("deleted before Mission Control finished reading", f.Gap() ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("complete", false)]
    [InlineData("missing", true)]
    [InlineData("shorter", true)]
    [InlineData("outside-lookback", false)]
    public async Task ContinuedCodexConversationWarnsOnlyWhileItsEarlierThreadIsUnread(string earlier, bool warned)
    {
        using var f = new Fixture();
        const string baseThread = "01a0c2d3-ff21-77b1-ab3c-8f6d53f52cf3";
        var baseContent = f.Lines("Earlier request", "Earlier answer");
        var basePath = f.PathFor($"rollout-2026-10-21T09-00-00-{baseThread}.jsonl");
        if (earlier != "missing") File.WriteAllBytes(basePath, baseContent);
        // Older than the 30-day history window: excluded by configuration, not missing.
        if (earlier == "outside-lookback") File.SetLastWriteTimeUtc(basePath, DateTime.UtcNow.AddDays(-60));
        var continuedFrom = earlier == "shorter" ? baseContent.Length + 5000 : baseContent.Length;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var header = JsonSerializer.Serialize(new { type = "session_meta", timestamp = now, payload = new { id = "child", cwd = f.Cwd, cli_version = "0.162.0",
            history_base = new { thread_id = baseThread, end_ordinal_exclusive = 4, end_byte_offset = continuedFrom } } }) + "\n";
        var body = Encoding.UTF8.GetString(f.Lines("Later request", "Later answer", turn: "t2"));
        File.WriteAllText(f.PathFor("rollout-2026-10-21T10-00-00-01a0cd56-2a9a-7e91-ad1c-de7e64525e10.jsonl"), header + body);
        await f.Run();
        Assert.Equal(warned, (f.Gap() ?? "").Contains("Referenced Codex history", StringComparison.Ordinal));
        // Review finding: the memory server kept listing a resolved continuation; it must agree with the dashboard.
        var activity = await new HistoryTools(f.Store).CallAsync("get_project_activity", JsonSerializer.SerializeToElement(new { view = "sessions", sourceId = "source", sessionId = "child" }));
        using var document = JsonDocument.Parse(activity.Json);
        var affected = document.RootElement.GetProperty("coverage").GetProperty("affectedSessions").EnumerateArray()
            .Count(session => session.GetProperty("sessionId").GetString() == "child" && session.GetProperty("kind").GetString() == "referenced_history");
        Assert.Equal(warned ? 1 : 0, affected);
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new DecompressionStream(new MemoryStream(data));
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "MissionControl-compressed-" + Guid.NewGuid().ToString("N"));
        private readonly string cwd;
        public WorkspaceStore Store { get; }
        public string Plain => Path.Combine(root, "2026", "10", "21", "rollout-2026-10-21T10-00-00-session.jsonl");
        public string Compressed => Plain + ".zst";
        public string Cwd => cwd;
        public string PathFor(string name) => Path.Combine(Path.GetDirectoryName(Plain)!, name);

        public Fixture()
        {
            cwd = Path.Combine(root, "approved");
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(Path.GetDirectoryName(Plain)!);
            Store = new(Path.Combine(root, "workspace.db"));
            Store.SaveProject(new("approved", "Approved", "blue", "Test-only approval", cwd));
            Store.SetProjectApproval("approved", true);
            Store.SaveSource(new("source", "codex", "Codex", root, true, OperatingSystem.IsWindows() ? "windows" : "linux"));
        }

        public byte[] Lines(string request, string answer, string turn = "t1")
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            object Item(object payload) => new { type = "response_item", timestamp = now, payload };
            var lines = new List<object>();
            if (turn == "t1") lines.Add(new { type = "session_meta", timestamp = now, payload = new { id = "session", cwd, cli_version = "0.162.0" } });
            lines.Add(new { type = "turn_context", timestamp = now, payload = new { cwd, turn_id = turn } });
            lines.Add(Item(new { type = "message", role = "user", id = turn + "-user", content = new[] { new { type = "input_text", text = request } } }));
            lines.Add(Item(new { type = "message", role = "assistant", phase = "final_answer", id = turn + "-final", content = new[] { new { type = "output_text", text = answer } } }));
            return Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => JsonSerializer.Serialize(line) + "\n")));
        }

        public void WritePlain(byte[] content) => File.WriteAllBytes(Plain, content);

        public void WriteCompressed(byte[] content)
        {
            using var compressor = new Compressor();
            File.WriteAllBytes(Compressed, compressor.Wrap(content).ToArray());
        }

        public async Task Run()
        {
            await using var observer = new LocalObserver(Store);
            await observer.StartAsync();
            await observer.PauseAsync();
        }

        public IReadOnlyList<EvidenceRecord> Records() => Store.Query(new(Kind: "message", Limit: 100)).Records.OrderBy(r => r.Id).ToArray();
        public string[] Bodies() => Records().Select(r => r.Body).OrderBy(b => b).ToArray();
        public string? Gap() => Assert.Single(Store.GetStatuses(), s => s.Id == "source").Gap;

        public void Dispose()
        {
            Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
