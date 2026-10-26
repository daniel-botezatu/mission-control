using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

/// <summary>A re-read that gives a final reply a new identity must not leave the old copy behind.</summary>
public sealed class SupersededFinalsTests
{
    [Fact]
    public void OlderCopyOfTheSameSourceMessageIsRemovedAndItsAnnotationKept()
    {
        var root = Path.Combine(Path.GetTempPath(), "MissionControl-superseded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
            store.SaveProject(new("approved", "Approved", "blue", "Test-only approval", root));
            store.SetProjectApproval("approved", true);
            store.SaveSource(new("source", "claude", "Claude", root, true, OperatingSystem.IsWindows() ? "windows" : "linux"));
            EvidenceRecord Final(string id, string sourceRecordId, string ingested, string stream = "session.jsonl") => new(id, "source", stream, "s", "approved", "claude", "message",
                "Reply", "Reply " + sourceRecordId, "assistant", "2026-10-21T10:00:00Z", ingested, WorkingDirectory: root,
                DataJson: JsonSerializer.Serialize(new { schema = "memory-v1", phase = "final", final = true, sourceRecordId, exchangeId = id }));
            store.CommitBatch([
                Final("old-turn-id", "msg-1", "2026-10-21T10:00:00Z"),
                Final("other-reply", "msg-2", "2026-10-21T10:00:00Z"),
                Final("other-stream", "msg-1", "2026-10-21T10:00:00Z", stream: "other.jsonl")]);
            store.SaveCorrection(new("old-turn-id", Note: "keep this note"));
            store.CommitBatch([Final("new-turn-id", "msg-1", "2026-10-26T16:00:00Z")]);

            Assert.Equal(1, store.RemoveSupersededFinals("source", "session.jsonl"));
            var ids = store.Query(new(Kind: "message", Limit: 100)).Records.Select(r => r.Id).OrderBy(id => id).ToArray();
            Assert.Equal(["new-turn-id", "other-reply", "other-stream"], ids);
            Assert.Equal("keep this note", store.GetCorrection("new-turn-id")?.Note);
            Assert.Equal(0, store.RemoveSupersededFinals("source", "session.jsonl"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    // Review finding: an interrupted update re-dated to its earlier original time must replace the row a previous
    // parser dated at the interruption; an ordinary older copy still never replaces a newer one.
    [Fact]
    public void ReReadingAStreamMayMoveARecordToItsEarlierOriginalTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "MissionControl-reread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
            store.SaveProject(new("approved", "Approved", "blue", "Test-only approval", root));
            store.SetProjectApproval("approved", true);
            store.SaveSource(new("source", "codex", "Codex", root, true, OperatingSystem.IsWindows() ? "windows" : "linux"));
            EvidenceRecord Update(string timestamp, string stream = "session.jsonl") => new("update", "source", stream, "s", "approved", "codex", "message",
                "Update", "Update text", "assistant", timestamp, timestamp, WorkingDirectory: root,
                DataJson: JsonSerializer.Serialize(new { schema = "memory-v1", phase = "final", final = true }));
            store.CommitBatch([Update("2026-10-26T18:00:00Z")]);
            store.CommitBatch([Update("2026-10-25T21:01:00Z")]);
            Assert.Equal("2026-10-26T18:00:00", Time());
            store.CommitBatch([Update("2026-10-25T21:01:00Z", "other.jsonl")], reinterpretation: true);
            Assert.Equal("2026-10-26T18:00:00", Time());
            store.CommitBatch([Update("2026-10-25T21:01:00Z")], reinterpretation: true);
            Assert.Equal("2026-10-25T21:01:00", Time());
            string Time() => DateTimeOffset.Parse(Assert.Single(store.Query(new(Kind: "message", Limit: 10)).Records).Timestamp).UtcDateTime.ToString("s");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
