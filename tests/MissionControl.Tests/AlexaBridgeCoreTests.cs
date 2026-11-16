using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

public sealed class AlexaBridgeCoreTests
{
    [Fact]
    public void Project_filter_precedes_title_selection_and_journal_survives_reopen_without_duplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-alexa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "workspace.db");
        string journal;
        using (var store = new WorkspaceStore(database))
        {
            foreach (var project in new[] { "private", "allowed" })
            {
                store.SaveProject(new(project, project, "#000000", "", Path.Combine(root, project)));
                store.SetProjectApproval(project, true);
            }
            store.SaveSource(new("source", "codex", "Synthetic", Path.Combine(root, "source"), true));
            var stamp = DateTimeOffset.UtcNow;
            EvidenceRecord Record(string id, string project, string title, int second) => new(id, "source", "rollout.jsonl", "same-session",
                project, "codex", "message", title, title, "user", stamp.AddSeconds(second).ToString("O"),
                stamp.ToString("O"), WorkingDirectory: Path.Combine(root, project), Branch: "same-branch");
            var first = Record("one", "private", "PRIVATE_TITLE", 0);
            var second = Record("two", "allowed", "Allowed title", 1);
            store.CommitBatch([first]);
            store.EnablePushJournal();
            journal = store.PushJournalId;
            store.CommitBatch([second]);
            store.CommitBatch([second]);
            var found = Assert.Single(store.QuerySessions(new(ProjectId: "allowed", Limit: 1)).Records);
            Assert.DoesNotContain("PRIVATE_TITLE", found.Title);
            Assert.Equal("allowed", found.ProjectId);
            Assert.Equal(2, store.ReadPushObservations(0).Count);
            store.SetProjectApproval("private", false);
            Assert.DoesNotContain(store.ReadPushObservations(0), item => item.Record?.ProjectId == "private");
            Assert.Equal("two", store.ReadPushObservations(1)[0].Record!.Id);
        }
        using (var store = new WorkspaceStore(database))
        {
            store.EnablePushJournal();
            Assert.Equal(journal, store.PushJournalId);
            Assert.Single(store.ReadPushObservations(0)); // Revocation deletes evidence and its journal pointers.
            Assert.Empty(store.ReadPushObservations(2));
        }
    }

    [Fact]
    public void Branch_filter_runs_before_session_pagination_and_notifications_follow_commits_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-alexa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
        store.SaveProject(new("project", "Synthetic", "#000000", "", Path.Combine(root, "project")));
        store.SetProjectApproval("project", true);
        store.SaveSource(new("source", "codex", "Synthetic", Path.Combine(root, "source"), true));
        var notifications = new List<string>();
        store.RecordsCommitted += ids => notifications.AddRange(ids);
        store.RecordsCommitted += _ => throw new InvalidOperationException("Subscriber failure cannot undo collection");
        var stamp = DateTimeOffset.UtcNow;
        EvidenceRecord Record(int number, string branch) => new("record-" + number, "source", "rollout.jsonl", "session-" + number,
            "project", "codex", "message", "Review PR #142", "Synthetic PR #142", "user", stamp.AddSeconds(number).ToString("O"),
            stamp.ToString("O"), WorkingDirectory: Path.Combine(root, "project"), Branch: branch);
        var target = Record(0, "wanted");
        store.CommitBatch([target, .. Enumerable.Range(1, 150).Select(index => Record(index, "other"))]);
        Assert.Equal(151, notifications.Count);
        store.CommitBatch([target]);
        Assert.Equal(151, notifications.Count);
        store.CommitBatch([Record(999, "wanted") with { WorkingDirectory = Path.Combine(root, "unapproved") }]);
        Assert.Equal(151, notifications.Count);
        var result = store.QuerySessions(new(Branch: "wanted", Search: "142", Limit: 1), fuzzy: false);
        Assert.Equal("session-0", Assert.Single(result.Records).SessionId);
        store.SetProjectApproval("project", false);
        Assert.Null(store.GetRecord(target.Id));
        Assert.Empty(store.QuerySessions(new(Branch: "wanted")).Records);
    }

    [Fact]
    public async Task Queued_ingest_is_cancelled_by_pause_before_reading_the_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-alexa-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(project);
        using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
        store.SaveProject(new("project", "Synthetic", "#000000", "", project));
        store.SetProjectApproval("project", true);
        store.SaveSource(new("source", "claude", "Synthetic", source, true));
        await using var observer = new LocalObserver(store);
        await observer.StartAsync();
        var serial = (SemaphoreSlim)typeof(LocalObserver).GetField("_serial", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(observer)!;
        await serial.WaitAsync();
        Task paused;
        try
        {
            var path = Path.Combine(source, "synthetic.jsonl");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { type = "user", uuid = Guid.NewGuid().ToString(),
                sessionId = "synthetic", cwd = project, timestamp = DateTimeOffset.UtcNow.ToString("O"),
                message = new { role = "user", content = "Must not ingest while paused" } }) + "\n");
            var pending = observer.IngestNowAsync("source", path);
            Assert.False(pending.IsCompleted);
            paused = observer.PauseAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { serial.Release(); }
        await paused;
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public async Task Ingest_now_rejects_paths_outside_configured_source_and_keeps_original_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-alexa-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(project);
        using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
        store.SaveProject(new("project", "Synthetic", "#000000", "", project));
        store.SetProjectApproval("project", true);
        store.SaveSource(new("source", "claude", "Synthetic", source, true));
        await using var observer = new LocalObserver(store);
        await observer.StartAsync();
        var text = JsonSerializer.Serialize(new { type = "user", uuid = Guid.NewGuid().ToString(), sessionId = "synthetic",
            cwd = project, gitBranch = "test", timestamp = DateTimeOffset.UtcNow.ToString("O"),
            message = new { role = "user", content = "Synthetic ingest test" } }) + "\n";
        var path = Path.Combine(source, "synthetic.jsonl");
        await File.WriteAllTextAsync(path, text);
        await observer.IngestNowAsync("source", path);
        Assert.Equal(text, await File.ReadAllTextAsync(path));
        Assert.NotEmpty(store.QuerySessions(new(SourceId: "source")).Records);
        var outside = Path.Combine(root, "outside.jsonl");
        await File.WriteAllTextAsync(outside, text);
        await Assert.ThrowsAsync<ArgumentException>(() => observer.IngestNowAsync("source", outside));
        await observer.PauseAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.IngestNowAsync("source", path));
    }
}
