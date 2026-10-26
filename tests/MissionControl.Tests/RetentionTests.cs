using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

/// <summary>Retention removes messages by when they were written, and applies itself at most once a day.</summary>
public sealed class RetentionTests
{
    [Fact]
    public void RetentionJudgesMessagesByWhenTheyWereWrittenNotWhenTheyWereSaved()
    {
        using var f = new Fixture();
        var savedNow = DateTimeOffset.UtcNow.ToString("O");
        f.Store.CommitBatch([
            f.Record("old", DateTimeOffset.UtcNow.AddDays(-45), savedNow),
            f.Record("recent", DateTimeOffset.UtcNow.AddDays(-5), savedNow),
            f.Record("old-annotated", DateTimeOffset.UtcNow.AddDays(-60), savedNow)]);
        f.Store.SaveCorrection(new("old-annotated", Note: "keep this"));

        Assert.Equal(1, f.Store.ApplyRetention(30));
        Assert.Equal(["old-annotated", "recent"], f.Ids());
    }

    [Fact]
    public void SavedPeriodAppliesOnlyWhenDue()
    {
        using var f = new Fixture();
        f.Store.SetSetting("retention.days", "30");
        f.Store.CommitBatch([f.Record("first-old", DateTimeOffset.UtcNow.AddDays(-40))]);

        Assert.Equal(1, f.Store.ApplyRetentionIfDue(TimeSpan.FromDays(1)));
        f.Store.CommitBatch([f.Record("second-old", DateTimeOffset.UtcNow.AddDays(-40))]);
        Assert.Null(f.Store.ApplyRetentionIfDue(TimeSpan.FromDays(1)));
        Assert.Equal(["second-old"], f.Ids());

        // A day later the saved period applies again.
        f.Store.SetSetting("retention.lastApplied", DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
        Assert.Equal(1, f.Store.ApplyRetentionIfDue(TimeSpan.FromDays(1)));
        Assert.Empty(f.Ids());
    }

    [Fact]
    public void WithoutASavedPeriodNinetyDaysIsUsed()
    {
        using var f = new Fixture();
        f.Store.CommitBatch([f.Record("older", DateTimeOffset.UtcNow.AddDays(-100)), f.Record("newer", DateTimeOffset.UtcNow.AddDays(-80))]);
        Assert.Equal(1, f.Store.ApplyRetentionIfDue(TimeSpan.FromDays(1)));
        Assert.Equal(["newer"], f.Ids());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "MissionControl-retention-" + Guid.NewGuid().ToString("N"));
        public WorkspaceStore Store { get; }

        public Fixture()
        {
            Directory.CreateDirectory(root);
            Store = new(Path.Combine(root, "workspace.db"));
            Store.SaveProject(new("approved", "Approved", "blue", "Test-only approval", root));
            Store.SetProjectApproval("approved", true);
            Store.SaveSource(new("source", "claude", "Claude", root, true, OperatingSystem.IsWindows() ? "windows" : "linux"));
        }

        public EvidenceRecord Record(string id, DateTimeOffset written, string? saved = null) => new(id, "source", "session.jsonl", "s", "approved", "claude", "message",
            id, "Message " + id, "user", written.ToString("O"), saved ?? DateTimeOffset.UtcNow.ToString("O"), WorkingDirectory: root,
            DataJson: JsonSerializer.Serialize(new { schema = "memory-v1", phase = "user", exchangeId = id }), ChangedAt: saved ?? DateTimeOffset.UtcNow.ToString("O"));

        public string[] Ids() => Store.Query(new(Kind: "message", Limit: 100)).Records.Select(r => r.Id).OrderBy(id => id).ToArray();

        public void Dispose()
        {
            Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
