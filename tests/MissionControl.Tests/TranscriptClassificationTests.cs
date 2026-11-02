using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

public sealed class TranscriptClassificationTests
{
    [Fact]
    public async Task HarnessNoticesAreExcludedAndTypedRequestsRemainSearchable()
    {
        using var fixture = new Fixture();
        var notices = new[] { "[Request interrupted by user]", "[Request interrupted by user for tool use]",
            "Stop hook feedback:\nContinue the generated checklist.", "A session-scoped Stop hook is now active with the goal: finish.",
            "Goal check-in: still open.", "[Image: source: C:\\synthetic.png]", "<environment_context>injected</environment_context>", "# AGENTS.md instructions\nGenerated instructions",
            "<ide_opened_file>context only</ide_opened_file>", "<send_user_message_question_reply>unreadable</send_user_message_question_reply>" };
        var prompts = new[] { "Why did the stop hook fire?", "[Request interrupted by user] happened twice; can you resume?",
            "[Image: source: C:\\synthetic.png]\nWhy is the button misaligned?", "# Design\nSupport <button> and image URLs in examples.",
            "Explain this generated tag:\n<environment_context>example</environment_context>", new string('x', 5000) };
        await fixture.Write(notices.Concat(prompts).ToArray());
        await using var observer = new LocalObserver(fixture.Store); await observer.StartAsync();
        var records = fixture.Store.Query(new(Kind: "message", Limit: 100)).Records;
        Assert.Equal(prompts.Length, records.Count);
        Assert.All(records, record => Assert.Equal("user", record.Author));
        Assert.All(notices, notice => Assert.DoesNotContain(records, record => record.Body == notice));
        Assert.All(prompts, prompt => Assert.Contains(records, record => record.Body == prompt));
    }

    [Fact]
    public async Task IdeEnvelopeIndexesOnlyExplicitRequestAndNotEditorOrPastedContext()
    {
        using var fixture = new Fixture();
        await fixture.Write("# Context from my IDE setup:\n## Active file: secret-context.txt\n## My request:\nPlease update the summary.",
            "<ide_opened_file>private context</ide_opened_file>\n<ide_selection>secret selected code</ide_selection>\nTidy the table");
        await using var observer = new LocalObserver(fixture.Store); await observer.StartAsync();
        var records = fixture.Store.Query(new(Limit: 100)).Records;
        Assert.Equal(2, records.Count);
        Assert.Contains(records, record => record.Body == "Please update the summary.");
        Assert.Contains(records, record => record.Body == "Tidy the table");
        Assert.DoesNotContain(records, record => record.Body.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostWrittenWrappersAreNotStoredAsTheUsersWords()
    {
        using var fixture = new Fixture();
        var wrappers = new[] {
            "<command-name>/compact</command-name>\n<command-message>compact</command-message>\n<command-args></command-args>",
            "<command-message>acme:transfer</command-message>\n<command-name>/acme:transfer</command-name>",
            "<local-command-stdout>Set model to `claude-opus-5-5`</local-command-stdout>",
            "<local-command-caveat>Caveat: the messages below were generated locally.</local-command-caveat>",
            "<bash-input>git status</bash-input>",
            "<external_codex_apps_open_page>{\"page_id\":null}</external_codex_apps_open_page>",
            "<user_action>\n  <context>User initiated a review task.</context>\n  <action>review</action>\n</user_action>",
            "<codex_internal_context source=\"goal\">Continue working toward the active thread goal.</codex_internal_context>",
            "This session is being continued from a previous conversation that ran out of context. The summary below covers the earlier portion." };
        var prompts = new[] { "Why did /compact lose my context?", "What does <command-name> mean in a transcript?",
            "Summarise this for me: This session is being continued from a previous conversation" };
        await fixture.WriteLines(wrappers.Concat(prompts).Select((body, index) => fixture.Line(index, body))
            .Append(fixture.Line(99, "Summary of the earlier work: the parser was fixed.", compactSummary: true)));
        await using var observer = new LocalObserver(fixture.Store); await observer.StartAsync();
        var records = fixture.Store.Query(new(Kind: "message", Limit: 100)).Records;
        Assert.Equal(prompts.OrderBy(p => p), records.Select(record => record.Body).OrderBy(b => b));
    }

    [Fact]
    public void HostWrittenMessagesStoredByAnOlderReleaseAreRemovedOnceAtStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "MissionControl-host-wrappers-" + Guid.NewGuid().ToString("N"));
        var database = Path.Combine(root, "workspace.db");
        Directory.CreateDirectory(root);
        try
        {
            EvidenceRecord Record(string id, string author, string body, int minute) => new(id, "source", "session.jsonl", "s", "approved", "claude", "message", body, body, author,
                $"2026-10-21T10:{minute:00}:00Z", $"2026-10-21T10:{minute:00}:00Z", WorkingDirectory: root,
                DataJson: JsonSerializer.Serialize(new { schema = "memory-v1", phase = author == "user" ? "user" : "final", final = author == "assistant", exchangeId = "s" }));
            using (var store = new WorkspaceStore(database))
            {
                store.SaveProject(new("approved", "Approved", "blue", "Test-only approval", root));
                store.SetProjectApproval("approved", true);
                store.SaveSource(new("source", "claude", "Claude", root, true, TestPlatform.Environment));
                store.CommitBatch([
                    Record("typed", "user", "Fix the parser please", 1),
                    Record("command", "user", "<command-name>/model</command-name>\n<command-message>model</command-message>", 2),
                    Record("output", "user", "<local-command-stdout>Set model to `claude-opus-5-5`</local-command-stdout>", 3),
                    Record("summary", "user", "This session is being continued from a previous conversation that ran out of context.", 4),
                    Record("reply", "assistant", "<command-name> is how Claude Code records a slash command.", 5)]);
                // Simulate a workspace written before this repair existed.
                store.SetSetting("repair.hostWrittenMessages", "0");
                Assert.Equal(5, store.Query(new(Kind: "message", Limit: 100)).Records.Count);
            }
            using (var reopened = new WorkspaceStore(database))
            {
                var ids = reopened.Query(new(Kind: "message", Limit: 100)).Records.Select(record => record.Id).OrderBy(id => id);
                Assert.Equal(["reply", "typed"], ids);
                Assert.Equal("1", reopened.GetSetting("repair.hostWrittenMessages"));
                Assert.Empty(reopened.Query(new(Search: "compact", Limit: 100)).Records);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReviewResultStoredAsUserByAnOlderReleaseIsRelabelledUnderTheIdentityAReplayGives()
    {
        var root = Path.Combine(Path.GetTempPath(), "MissionControl-review-relabel-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(root, "approved"); Directory.CreateDirectory(cwd);
        var database = Path.Combine(root, "workspace.db");
        // Recent, so the observer's default history lookback replays the line whatever day the test runs.
        var at = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ");
        const string review = "<user_action>\n  <context>User initiated a review task.</context>\n  <action>review</action>\n  <results>No actionable regressions found.</results>\n</user_action>";
        try
        {
            using (var store = new WorkspaceStore(database))
            {
                store.SaveProject(new("approved", "Approved", "blue", "Test-only approval", cwd));
                store.SetProjectApproval("approved", true);
                store.SaveSource(new("source", "codex", "Codex", root, true, TestPlatform.Environment));
                // The row an older parser stored for this line: the review text as the user's message.
                store.CommitBatch([new EvidenceRecord("legacy-review", "source", "rollout-session.jsonl", "s", "approved", "codex", "message", "<user_action>", review, "user",
                    at, at, WorkingDirectory: cwd,
                    DataJson: JsonSerializer.Serialize(new { schema = "memory-v1", phase = "user", turnId = "review-1", exchangeId = "legacy", sourceRecordId = "review-1" }))]);
                store.SetSetting("repair.hostWrittenMessages", "0");
            }
            using (var reopened = new WorkspaceStore(database))
            {
                var relabelled = Assert.Single(reopened.Query(new(Kind: "message", Limit: 100)).Records);
                Assert.Equal("assistant", relabelled.Author);
                Assert.Equal("Code review result:\nNo actionable regressions found.", relabelled.Body);
                Assert.NotEqual("legacy-review", relabelled.Id);
                // Replaying the original line with the current parser must land on the same row.
                static string Line(object value) => JsonSerializer.Serialize(value);
                await File.WriteAllLinesAsync(Path.Combine(root, "rollout-session.jsonl"), [
                    Line(new { type = "session_meta", timestamp = at, payload = new { id = "s", cwd, cli_version = "0.162.0" } }),
                    Line(new { type = "response_item", timestamp = at, payload = new { type = "message", role = "user", id = "review-1", content = new[] { new { type = "input_text", text = review } } } })]);
                await using (var observer = new LocalObserver(reopened)) { await observer.StartAsync(); await observer.PauseAsync(); }
                var after = Assert.Single(reopened.Query(new(Kind: "message", Limit: 100)).Records);
                Assert.Equal(relabelled.Id, after.Id);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "MissionControl-classification-" + Guid.NewGuid().ToString("N"));
        public WorkspaceStore Store { get; }
        public object Line(int index, string body, bool compactSummary = false) => compactSummary
            ? new { type = "user", uuid = "u-" + index, sessionId = "s", cwd = root, timestamp = DateTimeOffset.UtcNow.ToString("O"), isCompactSummary = true, message = new { role = "user", content = body } }
            : new { type = "user", uuid = "u-" + index, sessionId = "s", cwd = root, timestamp = DateTimeOffset.UtcNow.ToString("O"), message = new { role = "user", content = body } };
        public Task WriteLines(IEnumerable<object> lines) => File.WriteAllLinesAsync(Path.Combine(root, "session.jsonl"), lines.Select(line => JsonSerializer.Serialize(line)));
        public Fixture()
        {
            Directory.CreateDirectory(root); Store = new(Path.Combine(root, "workspace.db"));
            Store.SaveProject(new("approved", "Approved", "blue", "Test-only approval", root));
            Store.SetProjectApproval("approved", true);
            Store.SaveSource(new("source", "claude", "Claude", root, true, TestPlatform.Environment));
        }
        public Task Write(params string[] bodies) => File.WriteAllLinesAsync(Path.Combine(root, "session.jsonl"), bodies.Select((body, index) => JsonSerializer.Serialize(new
        { type = "user", uuid = "u-" + index, sessionId = "s", cwd = root, timestamp = DateTimeOffset.UtcNow.ToString("O"), message = new { role = "user", content = body } })));
        public void Dispose() { Store.Dispose(); Directory.Delete(root, true); }
    }
}
