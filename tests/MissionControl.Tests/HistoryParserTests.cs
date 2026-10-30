using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using MissionControl.Core;

namespace MissionControl.Tests;

public sealed class HistoryParserTests
{
    [Fact]
    public async Task CodexIndexesOnlyHumanAndFinalPhaseAndDeduplicatesCompletionAcrossRestart()
    {
        using var f = new Fixture("codex");
        await f.Append(Header(f.Cwd), Event("task_started", "turn-1"), User("Please add <button> markup and # examples."),
            Response("assistant", "I will inspect it.", "commentary"),
            Raw("response_item", new { type = "reasoning", summary = new[] { new { text = "PRIVATE_REASONING" } } }),
            Raw("response_item", new { type = "function_call_output", output = "PRIVATE_TERMINAL" }),
            Response("assistant", "The button is ready. Tests were not run.", "final_answer"));
        await f.Run();
        Assert.Equal(3, f.Store.Count());
        var messages = f.Messages();
        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain(f.AllText(), "PRIVATE_");
        Assert.DoesNotContain(f.AllText(), "I will inspect");
        var answer = Assert.Single(messages, r => r.Author == "assistant");
        Assert.Equal("unknown", answer.State);
        var id = answer.Id;
        await f.Append(Event("task_complete", "turn-1", "The button is ready. Tests were not run."));
        await f.Run();
        Assert.Equal(id, Assert.Single(f.Messages(), r => r.Author == "assistant").Id);
        var json = JsonDocument.Parse(answer.DataJson!).RootElement;
        Assert.Equal("final", json.GetProperty("phase").GetString());
        Assert.Equal("turn-1", json.GetProperty("turnId").GetString());
        Assert.Equal(JsonDocument.Parse(messages.Single(r => r.Author == "user").DataJson!).RootElement.GetProperty("exchangeId").GetString(), json.GetProperty("exchangeId").GetString());
    }

    [Fact]
    public async Task CodexOlderFinalFallbackRequiresExplicitCompletionAndDoesNotPromoteIntermediate()
    {
        using var f = new Fixture("codex");
        await f.Append(Header(f.Cwd), Event("task_started", "old-turn"), User("Continue migration"), Response("assistant", "LEGACY_INTERMEDIATE"));
        await f.Run();
        Assert.DoesNotContain(f.Messages(), r => r.Author == "assistant");
        await f.Append(Event("task_complete", "old-turn", "Final migration summary; this is a claim, not a test result."));
        await f.Run();
        var final = Assert.Single(f.Messages(), r => r.Author == "assistant");
        Assert.Contains("Final migration summary", final.Body);
        Assert.Equal("unknown", final.State);
        Assert.DoesNotContain(f.AllText(), "LEGACY_INTERMEDIATE");
        Assert.Contains(f.Store.Query(new(Limit: 100)).Records, r => r.State == "last-observed: task_complete");
    }

    [Fact]
    public async Task ClaudeBuffersLastTextThroughRestartAndRequiresTurnBoundary()
    {
        using var f = new Fixture("claude");
        await f.Append(Claude("user", "u-1", f.Cwd, "Fix this"), Claude("assistant", "a-1", f.Cwd, "I will inspect first"),
            ClaudeTool(f.Cwd), Claude("assistant", "a-2", f.Cwd, "Final answer kept through a restart"));
        await f.Run();
        Assert.Single(f.Messages());
        Assert.Contains("Final answer kept through a restart", f.CheckpointText());
        await f.Append(new { type = "system", subtype = "turn_duration", sessionId = "s", cwd = f.Cwd, timestamp = Now, uuid = "duration" });
        await f.Run();
        var final = Assert.Single(f.Messages(), r => r.Author == "assistant");
        Assert.Equal("Final answer kept through a restart", final.Body);
        Assert.Equal("u-1", JsonDocument.Parse(final.DataJson!).RootElement.GetProperty("turnId").GetString());
        Assert.DoesNotContain("Final answer kept through a restart", f.CheckpointText());
        // An update superseded by a tool call is held only while the turn could still be interrupted.
        Assert.DoesNotContain("I will inspect first", f.CheckpointText());
        await f.Run();
        Assert.Equal(2, f.Messages().Count);
    }

    [Fact]
    public async Task ClaudeEndTurnIsFinalButToolUseAndThinkingAreOmitted()
    {
        using var f = new Fixture("claude");
        await f.Append(Claude("user", "u-1", f.Cwd, "Investigate"),
            new { type = "assistant", uuid = "a-1", sessionId = "s", cwd = f.Cwd, timestamp = Now,
                message = new { role = "assistant", id = "m", stop_reason = "end_turn", content = new[] { new { type = "thinking", text = "HIDDEN_THINKING" }, new { type = "text", text = "Final result" } } } },
            new { type = "user", uuid = "tool-result", sessionId = "s", cwd = f.Cwd, timestamp = Now,
                message = new { role = "user", content = new[] { new { type = "tool_result", content = "HIDDEN_TOOL_OUTPUT" } } } });
        await f.Run();
        Assert.Equal(2, f.Messages().Count);
        Assert.Equal("Final result", Assert.Single(f.Messages(), r => r.Author == "assistant").Body);
        Assert.DoesNotContain(f.AllText(), "HIDDEN_");
    }

    [Fact]
    public async Task ClaudeNewHumanRequestFinalizesPriorTextButInjectedEnvelopeDoesNot()
    {
        using var f = new Fixture("claude");
        await f.Append(Claude("user", "u-1", f.Cwd, "First request"), Claude("assistant", "a-1", f.Cwd, "Prior final"),
            Claude("user", "context", f.Cwd, "<environment_context>generated</environment_context>"));
        await f.Run();
        Assert.Single(f.Messages());
        await f.Append(Claude("user", "u-2", f.Cwd, "Second request"));
        await f.Run();
        Assert.Equal(3, f.Messages().Count);
        Assert.DoesNotContain(f.AllText(), "generated");
        Assert.Equal("u-1", JsonDocument.Parse(f.Messages().Single(r => r.Author == "assistant").DataJson!).RootElement.GetProperty("turnId").GetString());
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task ExcludedWorkspaceNeverPersistsPayloadIncludingPendingCheckpoint(string kind)
    {
        using var f = new Fixture(kind);
        var excluded = Path.Combine(f.Root, "personal"); Directory.CreateDirectory(excluded);
        if (kind == "codex") await f.Append(Header(excluded), Event("task_started", "secret-turn"), User("PERSONAL_PRIVATE_PROMPT"), Response("assistant", "PERSONAL_PRIVATE_FINAL", "final_answer"));
        else await f.Append(Claude("user", "private-user", excluded, "PERSONAL_PRIVATE_PROMPT"), Claude("assistant", "private-answer", excluded, "PERSONAL_PRIVATE_FINAL"));
        await f.Run();
        Assert.Equal(0, f.Store.Count());
        Assert.DoesNotContain("PERSONAL_PRIVATE", f.CheckpointText());
        Assert.Empty(f.Store.Query(new(Search: "PERSONAL_PRIVATE")).Records);
        f.Store.SaveProject(new("personal", "Personal", "blue", "Explicit test approval", excluded, IsApproved: true));
        // Approval APIs deliberately reset checkpoints so previously excluded data is recovered.
        f.Store.SetProjectApproval("personal", true);
        await f.Run();
        Assert.NotEmpty(f.Messages());
    }

    [Fact]
    public async Task MissingOrUnapprovedCwdCannotBeBypassedWithMessageProjectIdentity()
    {
        using var f = new Fixture("codex");
        await f.Append(User("NO_CWD_PAYLOAD"), Response("assistant", "NO_CWD_FINAL", "final_answer"));
        await f.Run();
        Assert.Equal(0, f.Store.Count());
        Assert.DoesNotContain("NO_CWD", f.CheckpointText());
    }

    [Fact]
    public async Task ClaudePendingTextIsBoundedAndNoRawToolInputIsPersisted()
    {
        using var f = new Fixture("claude");
        await f.Append(Claude("user", "u", f.Cwd, "Long answer"), Claude("assistant", "a", f.Cwd, new string('a', 60000)), ClaudeTool(f.Cwd));
        await f.Run();
        Assert.DoesNotContain("TOOL_SECRET", f.CheckpointText());
        Assert.True(f.CheckpointText().Length < 18000);
        Assert.DoesNotContain(new string('a', 20000), f.CheckpointText());
    }

    [Fact]
    public async Task LongPendingFinalKeepsParserStateAndTailThroughRestartWithoutRewinding()
    {
        using var f = new Fixture("claude");
        var text = new string('a', 60000) + " TAIL_MARKER";
        await f.Append(Claude("user", "u", f.Cwd, "Long answer"), Claude("assistant", "a", f.Cwd, text));
        await f.Run();
        Assert.Contains("ParserVersion", f.CheckpointText());
        Assert.DoesNotContain("Content withheld", f.CheckpointText());
        Assert.Contains("TAIL_MARKER", f.CheckpointText());
        var offset = f.Store.GetCheckpoints("source").Single().Offset;
        await f.Run();
        Assert.Equal(offset, f.Store.GetCheckpoints("source").Single().Offset);
        await f.Append(new { type = "system", subtype = "turn_duration", sessionId = "s", cwd = f.Cwd, timestamp = Now, uuid = "duration" });
        await f.Run();
        Assert.Equal(text, Assert.Single(f.Messages(), r => r.Author == "assistant").Body);
        Assert.Equal(new FileInfo(f.SourceFile).Length, f.Store.GetCheckpoints("source").Single().Offset);
    }

    [Fact]
    public async Task CodexInterruptionKeepsTheLastUpdateMarkedAsInterrupted()
    {
        using var f = new Fixture("codex");
        await f.Append(Header(f.Cwd), Event("task_started", "turn-abort"), User("Try migration"), Response("assistant", "Working", "commentary"), Event("turn_aborted", "turn-abort"));
        await f.Run();
        var last = Assert.Single(f.Messages(), r => r.Author == "assistant");
        Assert.Equal("[Interrupted before the turn finished; the assistant's last update]\nWorking", last.Body);
        Assert.Equal("interrupted", JsonDocument.Parse(last.DataJson!).RootElement.GetProperty("finalSource").GetString());
        Assert.Contains(f.Store.Query(new(Limit: 100)).Records, r => r.State == "last-observed: turn_aborted");
    }

    [Fact]
    public async Task ClaudeInterruptionKeepsTheLastUpdateMarkedAsInterrupted()
    {
        using var f = new Fixture("claude");
        await f.Append(Claude("user", "u1", f.Cwd, "Start"), Claude("assistant", "a1", f.Cwd, "UNFINISHED_COMMENTARY"),
            Claude("user", "interrupt", f.Cwd, "[Request interrupted by user]"), Claude("user", "u2", f.Cwd, "Different task"));
        await f.Run();
        Assert.Equal(3, f.Messages().Count);
        var last = Assert.Single(f.Messages(), r => r.Author == "assistant");
        Assert.StartsWith("[Interrupted before the turn finished", last.Body);
        Assert.Contains("UNFINISHED_COMMENTARY", last.Body);
        Assert.DoesNotContain("UNFINISHED_COMMENTARY", f.CheckpointText());
        Assert.Contains(f.Store.Query(new(Limit: 100)).Records, r => r.State == "last-observed: turn_aborted");
    }

    [Fact]
    public async Task ClaudeCandidateSurvivesBoundedPassAndHasTheOriginalSourceOffset()
    {
        using var f = new Fixture("claude");
        await f.Append(Claude("user", "u1", f.Cwd, "Explain café sorting"));
        var answerOffset = new FileInfo(f.SourceFile).Length;
        await f.Append(Claude("assistant", "a1", f.Cwd, "Use a stable comparer."));
        await f.Append(Enumerable.Range(0, 8300).Select(i => (object)new { type = "progress", sessionId = "s", cwd = f.Cwd, timestamp = Now, uuid = "p" + i }).ToArray());
        await f.Run();
        Assert.Single(f.Messages());
        Assert.True(f.Store.GetCheckpoints("source").Single().Offset < new FileInfo(f.SourceFile).Length);
        await f.Append(Claude("user", "u2", f.Cwd, "Continue"));
        await f.Run();
        var answer = Assert.Single(f.Messages(), r => r.Author == "assistant");
        var data = JsonDocument.Parse(answer.DataJson!).RootElement;
        Assert.Equal(answerOffset, data.GetProperty("sourceByteOffset").GetInt64());
        Assert.Equal("a1", data.GetProperty("sourceRecordId").GetString());
        Assert.Equal("assistant", data.GetProperty("sourceType").GetString());
    }

    [Fact]
    public async Task ReplayUpgradesLegacyHumanIdentityWithoutDuplicatingOrLosingAnnotation()
    {
        using var f = new Fixture("claude");
        var environment = TestPlatform.Environment;
        var legacyId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"claude\n{environment}\ns\nu1\n")));
        f.Store.CommitBatch([new(legacyId, "source", "session.jsonl", "s", "approved", "claude", "message", "Prior prompt", "Prior prompt", "user", Now, Now, f.Cwd)]);
        f.Store.SaveCorrection(new(legacyId, Note: "Retained annotation"));
        await f.Append(Claude("user", "u1", f.Cwd, "Recovered prompt"));
        await f.Run();
        var message = Assert.Single(f.Messages());
        Assert.Equal(legacyId, message.Id);
        Assert.Equal("Recovered prompt", message.Body);
        Assert.Equal("Retained annotation", f.Store.GetCorrection(legacyId)!.Note);
        Assert.Equal("memory-v1", JsonDocument.Parse(message.DataJson!).RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public async Task Revocation_during_a_cached_batch_still_prevents_all_payload_persistence()
    {
        using var f = new Fixture("codex");
        await f.Append(Header(f.Cwd), User("PRIVATE_FIRST"), User("PRIVATE_SECOND"), Response("assistant", "PRIVATE_FINAL", "final_answer"));
        await using var observer = new LocalObserver(f.Store);
        var seen = 0;
        observer.WorkspaceObserved += _ => { if (++seen == 2) f.Store.SetProjectApproval("approved", false); };
        await observer.StartAsync(); await observer.PauseAsync();
        Assert.Equal(0, f.Store.Count()); Assert.DoesNotContain("PRIVATE_", f.CheckpointText());
    }

    [Fact]
    public async Task Repeated_streaming_snapshots_extend_text_without_duplicating_it()
    {
        using var f = new Fixture("claude");
        object Snapshot(string id, string text) => new { type = "assistant", uuid = id, sessionId = "s", cwd = f.Cwd, timestamp = Now,
            message = new { id = "streamed", role = "assistant", content = text } };
        await f.Append(Claude("user", "u", f.Cwd, "Explain"), Snapshot("a1", "A short"), Snapshot("a2", "A short answer"), Snapshot("a3", "A short answer"), Snapshot("a4", "and tail"),
            new { type = "system", subtype = "turn_duration", sessionId = "s", cwd = f.Cwd, timestamp = Now, uuid = "end" });
        await f.Run();
        Assert.Equal("A short answer\nand tail", Assert.Single(f.Messages(), r => r.Author == "assistant").Body);
    }

    [Fact]
    public async Task Paginated_ordinal_envelopes_are_supported_without_a_blanket_gap()
    {
        using var f = new Fixture("codex");
        await f.Append(Raw("session_meta", new { id = "s", cwd = f.Cwd, history_mode = "paginated" }),
            new { type = "response_item", ordinal = 3, timestamp = Now, payload = new { type = "message", role = "user", content = "Current request" } },
            new { type = "compacted", ordinal = 4, timestamp = Now, payload = new { message = "PRIVATE_COMPACTION" } },
            Response("assistant", "Final response", "final_answer"));
        await f.Run();
        Assert.Equal(2, f.Messages().Count);
        Assert.Null(Assert.Single(f.Store.GetStatuses()).Gap);
        Assert.DoesNotContain("PRIVATE_COMPACTION", f.AllText());
    }

    [Fact]
    public async Task Oversized_omitted_tools_do_not_make_human_history_partial()
    {
        using var f = new Fixture("codex");
        await f.Append(Header(f.Cwd), User("Keep this request"),
            Raw("response_item", new { type = "function_call_output", output = "PRIVATE_TOOL_BLOB" + new string('x', 5 * 1024 * 1024) }),
            Response("assistant", "Keep this final", "final_answer"));
        await f.Run();
        Assert.Equal(2, f.Messages().Count); Assert.Null(Assert.Single(f.Store.GetStatuses()).Gap);
        Assert.DoesNotContain("PRIVATE_TOOL_BLOB", f.AllText());
    }

    [Fact]
    public async Task Codex_review_results_are_kept_as_ai_written_results_not_user_words()
    {
        using var f = new Fixture("codex");
        const string review = "<user_action>\n  <context>User initiated a review task.</context>\n  <action>review</action>\n  <results>\n  No actionable regressions found.\n  </results>\n</user_action>";
        await f.Append(Header(f.Cwd), User("Please fix the parser"), Response("assistant", "Fixed it", "final_answer"),
            Raw("response_item", new { type = "message", role = "user", id = "review-1", content = new[] { new { type = "input_text", text = review } } }));
        await f.Run();
        var messages = f.Messages();
        Assert.DoesNotContain(messages, r => r.Body.Contains("user_action", StringComparison.Ordinal));
        var result = Assert.Single(messages, r => r.Body.StartsWith("Code review result:", StringComparison.Ordinal));
        Assert.Equal("assistant", result.Author);
        Assert.Equal("Code review result:\nNo actionable regressions found.", result.Body);
        Assert.Contains(messages, r => r.Author == "assistant" && r.Body == "Fixed it");
        Assert.Contains(messages, r => r.Author == "user" && r.Body == "Please fix the parser");
    }

    private static string Now => DateTimeOffset.UtcNow.ToString("O");
    private static object Raw(string type, object payload) => new { type, timestamp = Now, payload };
    private static object Header(string cwd) => Raw("session_meta", new { id = "s", cwd, cli_version = "0.162.0", git = new { branch = "feature/test" } });
    private static object User(string text) => Response("user", text);
    private static object Response(string role, string text, string? phase = null) => Raw("response_item", new { type = "message", role, phase, content = new[] { new { type = role == "user" ? "input_text" : "output_text", text } } });
    private static object Event(string type, string turn, string? last = null) => Raw("event_msg", new { type, turn_id = turn, last_agent_message = last });
    private static object Claude(string type, string id, string cwd, string text) => new { type, uuid = id, sessionId = "s", cwd, timestamp = Now, message = new { role = type, content = text } };
    private static object ClaudeTool(string cwd) => new { type = "assistant", uuid = "tool", sessionId = "s", cwd, timestamp = Now, message = new { role = "assistant", content = new[] { new { type = "tool_use", id = "call", name = "exec", input = "TOOL_SECRET" } } } };
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MissionControl-history-test-" + Guid.NewGuid().ToString("N"));
        public string Cwd { get; }
        public WorkspaceStore Store { get; }
        private readonly string file;
        public string SourceFile => file;
        public Fixture(string kind)
        {
            Directory.CreateDirectory(Root); Cwd = Path.Combine(Root, "approved"); Directory.CreateDirectory(Cwd);
            Store = new(Path.Combine(Root, "memory.db"));
            Store.SaveProject(new("approved", "Approved", "blue", "Explicit test approval", Cwd));
            Store.SetProjectApproval("approved", true);
            Store.SaveSource(new("source", kind, kind, Root, true, TestPlatform.Environment));
            file = Path.Combine(Root, kind == "codex" ? "rollout-session.jsonl" : "session.jsonl");
        }
        public Task Append(params object[] lines) => File.AppendAllTextAsync(file, string.Join('\n', lines.Select(line => JsonSerializer.Serialize(line))) + "\n");
        public async Task Run() { await using var observer = new LocalObserver(Store); await observer.StartAsync(); await observer.PauseAsync(); }
        public IReadOnlyList<EvidenceRecord> Messages() => Store.Query(new(Kind: "message", Limit: 100)).Records;
        public string AllText() => string.Join('\n', Store.Query(new(Limit: 100)).Records.Select(r => r.Body));
        public string CheckpointText() => Store.GetCheckpoints("source").Single().MetadataJson ?? "";
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }
}
