using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

/// <summary>Facts from the commands a turn ran (PRs, commits, pushes, test results) and interrupted turns' last updates.</summary>
public sealed class ToolFactsTests
{
    private const string DotNetRun = "Build succeeded.\nPassed!  - Failed:     0, Passed:   148, Skipped:     2, Total:   150, Duration: 7 s - MissionControl.Tests.dll (net10.0)";
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-10-26T18:17:00Z");

    [Fact]
    public void RecognisesCommonResultsOfTheOperationsACallPerformed()
    {
        var facts = ToolFacts.From(string.Join('\n',
            "[feat/eval 1a2b3c4d] Add the agent eval", " 3 files changed",
            "To https://github.com/Acme/HelpDesk.git", " * [new branch]      feat/eval -> feat/eval",
            DotNetRun, "test result: ok. 6 passed; 0 failed; 1 ignored",
            "============ 30 passed, 1 skipped in 1.39s ============",
            "Tests:       5 passed, 5 total", "OK (12 tests, 40 assertions)",
            "https://github.com/Acme/HelpDesk/pull/36"), "pr commit push test", Observed);
        Assert.Contains("PR Acme/HelpDesk#36 https://github.com/Acme/HelpDesk/pull/36 (gh pr create, 2026-10-26 18:17 UTC)", facts);
        Assert.Contains("commit 1a2b3c4d on feat/eval: Add the agent eval (git commit, 2026-10-26 18:17 UTC)", facts);
        Assert.Contains("pushed feat/eval to Acme/HelpDesk (git push, 2026-10-26 18:17 UTC)", facts);
        Assert.Contains("tests MissionControl.Tests.dll: 148 passed, 0 failed, 2 skipped (test run, 2026-10-26 18:17 UTC)", facts);
        Assert.Contains("tests (cargo): 6 passed, 0 failed (test run, 2026-10-26 18:17 UTC)", facts);
        Assert.Contains("tests (pytest): 30 passed, 1 skipped (test run, 2026-10-26 18:17 UTC)", facts);
        Assert.Contains("tests: 5 passed, 5 total (test run, 2026-10-26 18:17 UTC)", facts);
        Assert.Contains("tests (PHPUnit): 12 passed, 0 failed (test run, 2026-10-26 18:17 UTC)", facts);
    }

    [Fact]
    public void OnlyTheOperationsTheCommandPerformedYieldFacts()
    {
        var log = "[topic 1a2b3c4] A change made last week\n" + DotNetRun + "\nhttps://github.com/synthetic/repository/pull/123";
        Assert.Empty(ToolFacts.From(log, ToolFacts.Operations("Get-Content old-build.log"), Observed));
        Assert.Empty(ToolFacts.From(log, ToolFacts.Operations("cat pytest.log; git log --oneline"), Observed));
        Assert.Equal(["tests MissionControl.Tests.dll: 148 passed, 0 failed, 2 skipped (test run, 2026-10-26 18:17 UTC)"], ToolFacts.From(log, "test", Observed));
        Assert.Empty(ToolFacts.From("#36 rating https://github.com/Acme/HelpDesk/pull/36\n#35 switch https://github.com/Acme/HelpDesk/pull/35\nShowing 2 of 2", "pr", Observed));
    }

    [Theory]
    [InlineData("dotnet test MissionControl.slnx --no-restore", "test")]
    [InlineData("git -C \"C:/My Repo\" add -A && git -C \"C:/My Repo\" commit -m \"Fix\" && git push -u origin HEAD", "commit push")]
    [InlineData("git add -A && git -c user.name=\"Robin B\" -c user.email=\"d@example.invalid\" commit -q -m \"Fix\"", "commit")]
    [InlineData("git --no-pager -C 'C:/My Repo' push origin HEAD", "push")]
    [InlineData("git --no-pager log --oneline -5", "")]
    [InlineData("gh pr create --fill --base main", "pr")]
    [InlineData("python -m pytest -q", "test")]
    [InlineData("./vendor/bin/pest --parallel", "test")]
    [InlineData("npm run test:unit -- --run", "test")]
    [InlineData("cd /c/work/app && npm test 2>&1 | sed 's/x//g' | grep -v '^at ' | tail -25", "test")]
    [InlineData("dotnet test 2>&1 | Select-String Passed", "test")]
    [InlineData("$out = dotnet test 2>&1; $out | Select-Object -Last 5", "test")]
    [InlineData("sed -i 's/a/b/' tests/app.test.js && npm test", "test")]
    [InlineData("cat > package.json <<'EOF'\n{ \"scripts\": { \"test\": \"git commit -m x\" } }\nEOF\nnpm test 2>&1 | tail -5", "test")]
    [InlineData("wsl.exe -e bash -lc \"cd ~/app && npm test\"", "test")]
    [InlineData("powershell -Command \"cd C:/app; dotnet test\"", "test")]
    [InlineData("bash -lc 'git commit -m x && git push'", "commit push")]
    [InlineData("npm run -s test:coverage > \"$TEMP/cov.log\" 2>&1; echo rc=$?; grep -E 'Tests |FAIL' \"$TEMP/cov.log\"", "test")]
    [InlineData("dotnet test 2>&1 | tee run.log; tail -5 run.log", "test")]
    [InlineData("PHP=/c/tools/php85/php.exe; \"$PHP\" vendor/bin/pest --parallel", "test")]
    [InlineData("$php = \"C:\\tools\\php.exe\"; & $php vendor/bin/phpunit", "test")]
    [InlineData("/c/tools/php85/php.exe -d memory_limit=2G vendor/bin/pest tests/Feature", "test")]
    [InlineData("grep -E 'Tests ' cov.log; npm test > cov.log", "")]
    [InlineData("php vendor/bin/pest --parallel 2>&1 | grep -E 'FAIL |Tests:' | head -20; php vendor/bin/pint --test 2>&1 | tail -1", "test")]
    [InlineData("cd /app && APP_KEY=base64:$(head -c 32 /dev/urandom | base64) DB_CONNECTION=pgsql php artisan test --parallel", "test")]
    [InlineData("for f in $(git status --porcelain | grep '^??' | cut -c4-); do npx vitest run $f; done", "test")]
    [InlineData("cat old-run.log | grep Tests; npm test", "")]
    [InlineData("wsl.exe -e bash -lc 'php vendor/bin/pest 2>&1 | sed \"s/x//\" > ~/runs/a.txt; grep -E Tests: ~/runs/a.txt'", "test")]
    [InlineData("timeout 60 wsl.exe -u root -e su postgres -c \"cd /app && vendor/bin/phpunit --testdox\"", "test")]
    // Review findings: searching a log for a command, and dry runs, perform nothing.
    [InlineData("rg 'dotnet test|Passed!' old-build.log", "")]
    [InlineData("git push --dry-run origin topic", "")]
    [InlineData("git push -n origin topic", "")]
    [InlineData("git push -nu origin topic", "")]
    [InlineData("git commit --dry-run", "")]
    [InlineData("gh pr create --dry-run --fill", "")]
    [InlineData("echo 'npm test' && grep -c Passed run.log", "")]
    [InlineData("Get-Content old.log; dotnet test", "")]
    [InlineData("python - <<'PYEOF'\nimport subprocess\nsubprocess.run(['pytest'])\nPYEOF", "")]
    [InlineData("Get-Content build.log", "")]
    [InlineData("cat pytest.log", "")]
    [InlineData("git commit-tree HEAD^{tree} -m x", "")]
    [InlineData("git log --grep commit", "")]
    [InlineData("gh pr view 36", "")]
    public void CommandsAreClassifiedByTheOperationsTheyPerform(string command, string operations) =>
        Assert.Equal(operations, ToolFacts.Operations(command));

    [Fact]
    public void CodeModeScriptsAreReadThroughTheCommandsTheyRun()
    {
        Assert.Equal("cargo test --locked", ToolFacts.CodeModeCommands("text(await tools.exec_command({cmd:\"cargo test --locked\"}))"));
        Assert.Equal("Get-Content -LiteralPath 'C:\\notes\\a.md'", ToolFacts.CodeModeCommands("tools.exec_command({cmd:\"Get-Content -LiteralPath 'C:\\\\notes\\\\a.md'\",max_output_tokens:1000})"));
        Assert.Equal("test", ToolFacts.Operations(ToolFacts.CodeModeCommands("text(await tools.exec_command({cmd:\"dotnet test\"}))")));
        // Reading a file alongside the test run makes the combined output unattributable.
        Assert.Equal("", ToolFacts.Operations(ToolFacts.CodeModeCommands(
            "await Promise.allSettled([tools.exec_command({cmd:\"Get-Content README.md\"}), tools.exec_command({cmd:\"dotnet test\"})])")));
        Assert.Equal("", ToolFacts.Operations(ToolFacts.CodeModeCommands("console.log('npm test')")));
    }

    [Fact]
    public async Task ClaudeFinalReplyCarriesTheResultsOfCommandsItRan()
    {
        using var f = new Fixture("claude");
        await f.Write(
            f.Claude("user", "u1", "Run the tests and open the PR"),
            f.ClaudeTool("a1", "Running the suite first.", "Bash", "dotnet test MissionControl.slnx"),
            f.ClaudeResult("r1", "tool-a1", DotNetRun),
            f.ClaudeTool("a2", "", "Bash", "gh pr create --fill"),
            f.ClaudeResult("r2", "tool-a2", "Creating pull request\nhttps://github.com/Acme/HelpDesk/pull/40"),
            f.Claude("assistant", "a3", "Done: tests pass and the PR is open.", stopReason: "end_turn"));
        var final = Assert.Single(await f.Messages(), r => r.Author == "assistant");
        Assert.StartsWith("Done: tests pass and the PR is open.", final.Body);
        Assert.Contains("[Results of commands run in this turn: ", final.Body);
        Assert.Contains("tests MissionControl.Tests.dll: 148 passed, 0 failed, 2 skipped (test run, ", final.Body);
        Assert.Contains("PR Acme/HelpDesk#40 https://github.com/Acme/HelpDesk/pull/40 (gh pr create, ", final.Body);
    }

    // Review finding: reading last week's build log attached its PR, commit and test results to today's reply.
    [Fact]
    public async Task ReadingAnOldLogAttachesNothingToTheReply()
    {
        using var f = new Fixture("claude");
        var log = "[topic 1a2b3c4] A change made last week\n" + DotNetRun + "\nhttps://github.com/synthetic/repository/pull/123";
        await f.Write(
            f.Claude("user", "u1", "Read last week's build log; do not run anything."),
            f.ClaudeTool("a1", "", "Read", "old-build.log"),
            f.ClaudeResult("r1", "tool-a1", log),
            f.ClaudeTool("a2", "", "Bash", "cat old-build.log"),
            f.ClaudeResult("r2", "tool-a2", log),
            f.Claude("assistant", "a3", "I read the old log. Nothing was run or created today.", stopReason: "end_turn"));
        var final = Assert.Single(await f.Messages(), r => r.Author == "assistant");
        Assert.Equal("I read the old log. Nothing was run or created today.", final.Body);
    }

    // Review findings: searching an old log for a test command, and a dry-run push, recorded work that never happened.
    [Theory]
    [InlineData("rg 'dotnet test|Passed!' old-build.log", "Passed!  - Failed: 0, Passed: 99, Skipped: 0, Total: 99, Duration: 1 s - OldSuite.dll (net10.0)")]
    [InlineData("git push --dry-run origin topic", "To https://github.com/synthetic/repository.git\n * [new branch]      topic -> topic")]
    public async Task SearchesAndDryRunsAttachNothingToTheReply(string command, string output)
    {
        using var f = new Fixture("claude");
        await f.Write(
            f.Claude("user", "u1", "Check without changing anything."),
            f.ClaudeTool("a1", "", "Bash", command),
            f.ClaudeResult("r1", "tool-a1", output),
            f.Claude("assistant", "a2", "Nothing was run or pushed.", stopReason: "end_turn"));
        var final = Assert.Single(await f.Messages(), r => r.Author == "assistant");
        Assert.Equal("Nothing was run or pushed.", final.Body);
    }

    [Fact]
    public async Task InterruptedClaudeTurnKeepsTheAssistantsLastUpdateAtItsOwnTime()
    {
        using var f = new Fixture("claude");
        var written = DateTimeOffset.UtcNow.AddHours(-20);
        await f.Write(
            f.Claude("user", "u1", "Migrate the database", at: written.AddMinutes(-1)),
            f.ClaudeTool("a1", "Checking which migrations are pending before running them.", "Bash", "php artisan migrate:status", at: written),
            f.Claude("user", "u2", "[Request interrupted by user for tool use]"));
        var last = Assert.Single(await f.Messages(), r => r.Author == "assistant");
        Assert.StartsWith("[Interrupted before the turn finished", last.Body);
        Assert.Contains("Checking which migrations are pending", last.Body);
        Assert.Equal(written, DateTimeOffset.Parse(last.Timestamp));
    }

    [Fact]
    public async Task CodexResultsOfCommandsItRanIncludingCodeModeScripts()
    {
        using var f = new Fixture("codex");
        await f.Write(
            f.Codex("session_meta", new { id = "s", cwd = f.Cwd, cli_version = "0.162.0" }),
            f.Codex("event_msg", new { type = "task_started", turn_id = "t1" }),
            f.Codex("response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Run the tests" } } }),
            f.Codex("response_item", new { type = "function_call", name = "exec_command", call_id = "c1", arguments = JsonSerializer.Serialize(new { cmd = "pytest -q" }) }),
            f.Codex("response_item", new { type = "function_call_output", call_id = "c1", output = "===== 30 passed, 1 skipped in 1.39s =====" }),
            f.Codex("response_item", new { type = "custom_tool_call", name = "exec", call_id = "c2", input = "text(await tools.exec_command({cmd:\"dotnet test MissionControl.slnx\"}))" }),
            f.Codex("response_item", new { type = "custom_tool_call_output", call_id = "c2", output = new[] { new { type = "input_text", text = "Script completed\nOutput:\n" + DotNetRun } } }),
            f.Codex("response_item", new { type = "custom_tool_call", name = "exec", call_id = "c3", input = "text(await tools.exec_command({cmd:\"Get-Content old.log\"}))" }),
            f.Codex("response_item", new { type = "custom_tool_call_output", call_id = "c3", output = new[] { new { type = "input_text", text = "[old 9f8e7d6] Last week\nhttps://github.com/synthetic/repository/pull/9" } } }),
            f.Codex("response_item", new { type = "message", role = "assistant", phase = "final_answer", content = new[] { new { type = "output_text", text = "All tests pass." } } }),
            f.Codex("event_msg", new { type = "task_complete", turn_id = "t1" }));
        var final = Assert.Single(await f.Messages(), r => r.Author == "assistant");
        Assert.Contains("tests (pytest): 30 passed, 1 skipped", final.Body);
        Assert.Contains("tests MissionControl.Tests.dll: 148 passed", final.Body);
        Assert.DoesNotContain("9f8e7d6", final.Body);
        Assert.DoesNotContain("pull/9", final.Body);
    }

    // Review finding: an update written yesterday evening was dated today after an interruption today.
    [Fact]
    public async Task CodexOvernightInterruptionKeepsTheUpdateOnTheDayItWasWritten()
    {
        using var f = new Fixture("codex");
        var now = DateTimeOffset.UtcNow;
        var yesterday = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-1).AddHours(21), TimeSpan.Zero);
        await f.Write(
            f.Codex("session_meta", new { id = "overnight", cwd = f.Cwd, cli_version = "0.162.0" }, yesterday),
            f.Codex("event_msg", new { type = "task_started", turn_id = "t1" }, yesterday),
            f.Codex("response_item", new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Begin the work" } } }, yesterday),
            f.Codex("response_item", new { type = "message", role = "assistant", phase = "commentary", id = "old-update", content = new[] { new { type = "output_text", text = "Yesterday-only update: checking the old deployment." } } }, yesterday.AddMinutes(1)),
            f.Codex("event_msg", new { type = "turn_aborted", turn_id = "t1" }, now));
        var update = Assert.Single(await f.Messages(), r => r.Author == "assistant");
        Assert.Contains("Yesterday-only update", update.Body);
        Assert.Equal(yesterday.AddMinutes(1), DateTimeOffset.Parse(update.Timestamp));
        Assert.DoesNotContain(f.Store.Query(new(Kind: "message", Limit: 100)).Records, r => r.Author == "assistant" && DateTimeOffset.Parse(r.Timestamp) >= new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero));
        var aborted = Assert.Single(f.Store.Query(new(Kind: "lifecycle", Limit: 100)).Records, r => r.State == "last-observed: turn_aborted");
        Assert.Equal(now, DateTimeOffset.Parse(aborted.Timestamp));
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

        private static string Time(DateTimeOffset? at) => (at ?? DateTimeOffset.UtcNow).ToString("O");
        public object Claude(string type, string id, string text, string? stopReason = null, DateTimeOffset? at = null) => new { type, uuid = id, sessionId = "s", cwd = Cwd, timestamp = Time(at),
            message = new { role = type, id = "m-" + id, stop_reason = stopReason, content = new[] { new { type = "text", text } } } };
        public object ClaudeTool(string id, string text, string tool, string command, DateTimeOffset? at = null)
        {
            var call = new { type = "tool_use", id = "tool-" + id, name = tool, input = tool == "Read" ? (object)new { file_path = command } : new { command } };
            object[] content = text.Length > 0 ? [new { type = "text", text }, call] : [call];
            return new { type = "assistant", uuid = id, sessionId = "s", cwd = Cwd, timestamp = Time(at), message = new { role = "assistant", id = "m-" + id, stop_reason = "tool_use", content } };
        }
        public object ClaudeResult(string id, string toolId, string output) => new { type = "user", uuid = id, sessionId = "s", cwd = Cwd, timestamp = Time(null),
            message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = toolId, content = output } } } };
        public object Codex(string type, object payload, DateTimeOffset? at = null) => new { type, timestamp = Time(at), payload };

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
