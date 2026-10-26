using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

/// <summary>Facts from tool output (PRs, commits, pushes, test results) and interrupted turns' last updates.</summary>
public sealed class ToolFactsTests
{
    private const string DotNetRun = "Build succeeded.\nPassed!  - Failed:     0, Passed:   148, Skipped:     2, Total:   150, Duration: 7 s - MissionControl.Tests.dll (net10.0)";

    [Fact]
    public void RecognisesCommonToolResults()
    {
        var facts = ToolFacts.From(string.Join('\n',
            "[feat/eval 1a2b3c4d] Add the agent eval", " 3 files changed",
            "To https://github.com/Acme/HelpDesk.git", " * [new branch]      feat/eval -> feat/eval",
            DotNetRun, "test result: ok. 6 passed; 0 failed; 1 ignored",
            "============ 30 passed, 1 skipped in 1.39s ============",
            "Tests:       5 passed, 5 total", "OK (12 tests, 40 assertions)",
            "https://github.com/Acme/HelpDesk/pull/36"));
        Assert.Contains("PR Acme/HelpDesk#36 https://github.com/Acme/HelpDesk/pull/36", facts);
        Assert.Contains("commit 1a2b3c4d on feat/eval: Add the agent eval", facts);
        Assert.Contains("pushed feat/eval to Acme/HelpDesk", facts);
        Assert.Contains("tests MissionControl.Tests.dll: 148 passed, 0 failed, 2 skipped", facts);
        Assert.Contains("tests (cargo): 6 passed, 0 failed", facts);
        Assert.Contains("tests (pytest): 30 passed, 1 skipped", facts);
        Assert.Contains("tests: 5 passed, 5 total", facts);
        Assert.Contains("tests (PHPUnit): 12 passed, 0 failed", facts);
    }

    [Fact]
    public void PullRequestLinksInsideListingsAreNotTreatedAsThisTurnsWork() =>
        Assert.Empty(ToolFacts.From("#36 rating https://github.com/Acme/HelpDesk/pull/36\n#35 switch https://github.com/Acme/HelpDesk/pull/35\nShowing 2 of 2"));

    [Fact]
    public async Task ClaudeFinalReplyCarriesTheTurnsToolResults()
    {
        using var f = new Fixture("claude");
        await f.Write(
            f.Claude("user", "u1", "Run the tests and open the PR"),
            f.ClaudeTool("a1", "Running the suite first."),
            f.ClaudeResult("r1", DotNetRun),
            f.ClaudeResult("r2", "Creating pull request\nhttps://github.com/Acme/HelpDesk/pull/40"),
            f.Claude("assistant", "a2", "Done: tests pass and the PR is open.", stopReason: "end_turn"));
        var final = Assert.Single(await f.Messages(), r => r.Author == "assistant");
        Assert.StartsWith("Done: tests pass and the PR is open.", final.Body);
        Assert.Contains("[Tool results this turn: ", final.Body);
        Assert.Contains("tests MissionControl.Tests.dll: 148 passed, 0 failed, 2 skipped", final.Body);
        Assert.Contains("PR Acme/HelpDesk#40", final.Body);
    }

    [Fact]
    public async Task InterruptedClaudeTurnKeepsTheAssistantsLastUpdate()
    {
        using var f = new Fixture("claude");
        await f.Write(
            f.Claude("user", "u1", "Migrate the database"),
            f.ClaudeTool("a1", "Checking which migrations are pending before running them."),
            f.Claude("user", "u2", "[Request interrupted by user for tool use]"));
        var last = Assert.Single(await f.Messages(), r => r.Author == "assistant");
        Assert.StartsWith("[Interrupted before the turn finished", last.Body);
        Assert.Contains("Checking which migrations are pending", last.Body);
    }

    [Fact]
    public async Task CodexFinalReplyCarriesToolResultsAndAnAbortedTurnKeepsItsLastUpdate()
    {
        using var f = new Fixture("codex");
        await f.Write(
            f.Codex("session_meta", new { id = "s", cwd = f.Cwd, cli_version = "0.162.0" }),
            f.Codex("event_msg", new { type = "task_started", turn_id = "t1" }),
            f.Codex("response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Run pytest" } } }),
            f.Codex("response_item", new { type = "function_call_output", call_id = "c1", output = "===== 30 passed, 1 skipped in 1.39s =====" }),
            f.Codex("response_item", new { type = "message", role = "assistant", phase = "final_answer", content = new[] { new { type = "output_text", text = "All tests pass." } } }),
            f.Codex("event_msg", new { type = "task_complete", turn_id = "t1" }),
            f.Codex("event_msg", new { type = "task_started", turn_id = "t2" }),
            f.Codex("response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Now deploy it" } } }),
            f.Codex("response_item", new { type = "message", role = "assistant", phase = "commentary", content = new[] { new { type = "output_text", text = "Building the release bundle first." } } }),
            f.Codex("event_msg", new { type = "turn_aborted", turn_id = "t2" }));
        var replies = (await f.Messages()).Where(r => r.Author == "assistant").Select(r => r.Body).ToArray();
        Assert.Contains(replies, body => body.StartsWith("All tests pass.") && body.Contains("tests (pytest): 30 passed, 1 skipped"));
        Assert.Contains(replies, body => body.StartsWith("[Interrupted before the turn finished") && body.Contains("Building the release bundle first."));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "MissionControl-facts-" + Guid.NewGuid().ToString("N"));
        private readonly string kind;
        public string Cwd { get; }
        public WorkspaceStore Store { get; }

        public Fixture(string kind)
        {
            this.kind = kind;
            Cwd = Path.Combine(root, "approved"); Directory.CreateDirectory(Cwd);
            Store = new(Path.Combine(root, "workspace.db"));
            Store.SaveProject(new("approved", "Approved", "blue", "Test-only approval", Cwd));
            Store.SetProjectApproval("approved", true);
            Store.SaveSource(new("source", kind, kind, root, true, OperatingSystem.IsWindows() ? "windows" : "linux"));
        }

        private static string Now => DateTimeOffset.UtcNow.ToString("O");
        public object Claude(string type, string id, string text, string? stopReason = null) => new { type, uuid = id, sessionId = "s", cwd = Cwd, timestamp = Now,
            message = new { role = type, id = "m-" + id, stop_reason = stopReason, content = new[] { new { type = "text", text } } } };
        public object ClaudeTool(string id, string text) => new { type = "assistant", uuid = id, sessionId = "s", cwd = Cwd, timestamp = Now,
            message = new { role = "assistant", id = "m-" + id, stop_reason = "tool_use", content = new object[] { new { type = "text", text }, new { type = "tool_use", id = "tool-" + id, name = "Bash", input = new { command = "run" } } } } };
        public object ClaudeResult(string id, string output) => new { type = "user", uuid = id, sessionId = "s", cwd = Cwd, timestamp = Now,
            message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "tool", content = output } } } };
        public object Codex(string type, object payload) => new { type, timestamp = Now, payload };

        public Task Write(params object[] lines) => File.WriteAllLinesAsync(Path.Combine(root, kind == "codex" ? "rollout-session.jsonl" : "session.jsonl"), lines.Select(line => JsonSerializer.Serialize(line)));

        public async Task<IReadOnlyList<EvidenceRecord>> Messages()
        {
            await using (var observer = new LocalObserver(Store)) { await observer.StartAsync(); await observer.PauseAsync(); }
            return Store.Query(new(Kind: "message", Limit: 100)).Records;
        }

        public void Dispose()
        {
            Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
