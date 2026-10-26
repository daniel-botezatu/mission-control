using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

public sealed class MemoryWindowTests
{
    private const string From = "2026-10-20T22:00:00Z";
    private const string Before = "2026-10-21T22:00:00Z";

    [Fact]
    public async Task Whole_day_stream_covers_seven_sessions_middle_topics_and_complete_multiday_chat_messages()
    {
        using var f = new Fixture();
        var sdk = Path.Combine(f.Root, "sdk"); var sp = Path.Combine(f.Root, "sp");
        Directory.CreateDirectory(sdk); Directory.CreateDirectory(sp);
        f.Store.SaveProject(new("sdk", "SDK", "blue", "", sdk)); f.Store.SetProjectApproval("sdk", true);
        f.Store.SaveProject(new("sp", "SP", "blue", "", sp)); f.Store.SetProjectApproval("sp", true);
        var expected = new Dictionary<string, string>();
        for (var i = 0; i < 7; i++)
        {
            var body = i == 4 ? "SDK H11 pause. " + new string('x', 750) + " SECOND_SUBJECT_IN_MIDDLE: auth repair. " + new string('y', 750) + " Branch unpushed; inspect mutation ledger."
                : "Topic for session " + i;
            var record = f.Record("today-" + i, "session-" + i, body, $"2026-10-21T{8 + i:00}:00:00Z", i == 4 ? "assistant" : "user")
                with { ProjectId = i < 3 ? "work" : i < 5 ? "sdk" : "sp", WorkingDirectory = i < 3 ? f.Root : i < 5 ? sdk : sp };
            f.Store.CommitBatch([record]); expected.Add(record.Id, body);
        }
        f.Store.CommitBatch([
            f.Record("yesterday", "session-0", "YESTERDAY_ONLY first message of the whole chat", "2026-10-20T10:00:00Z"),
            f.Record("tomorrow", "session-0", "TOMORROW_ONLY", "2026-10-22T10:00:00Z"),
            f.Record("middle", "session-0", "MIDDLE_TOPIC: investigate a separate security subject", "2026-10-21T08:30:00Z"),
            f.Record("later-sp", "session-6", "Later update: Corrections PR 26 is now open; PR preparation is resolved.", "2026-10-21T17:00:00Z", "assistant") with { ProjectId = "sp", WorkingDirectory = sp },
            f.Record("midnight", "session-0", "Amsterdam midnight is included", "2026-10-21T00:00:00+02:00"),
            f.Record("next-midnight", "session-0", "NEXT_MIDNIGHT_EXCLUDED", "2026-10-22T00:00:00+02:00")]);
        expected["middle"] = "MIDDLE_TOPIC: investigate a separate security subject";
        expected["later-sp"] = "Later update: Corrections PR 26 is now open; PR preparation is resolved.";
        expected["midnight"] = "Amsterdam midnight is included";
        f.Store.SetSourceStatus("source", "gap", "Referenced Codex history: older source references.");
        var tools = new HistoryTools(f.Store);
        var manifest = await Read(tools, new { from = From, before = Before, limit = 20, maxOutputCharacters = 48000 });
        Assert.Equal(7, manifest.GetProperty("total").GetInt32());
        Assert.Equal(expected.Count, manifest.GetProperty("sessions").EnumerateArray().Sum(s => s.GetProperty("messages").GetInt32()));
        var bodies = new Dictionary<string, string>(); var sessions = new HashSet<string>();
        string? cursor = null; JsonElement page = default;
        for (var i = 0; i < 100; i++)
        {
            page = await Read(tools, new { view = "messages", from = From, before = Before, cursor, limit = 50, bodyCharacters = 200, maxOutputCharacters = 6000 });
            foreach (var record in page.GetProperty("records").EnumerateArray())
            {
                var id = record.GetProperty("id").GetString()!;
                var existing = bodies.GetValueOrDefault(id, "");
                Assert.Equal(existing.Length, record.GetProperty("bodyOffset").GetInt32());
                bodies[id] = existing + record.GetProperty("body").GetString();
                sessions.Add(record.GetProperty("sessionId").GetString()!);
            }
            cursor = page.GetProperty("nextCursor").GetString();
            if (cursor is null) break;
            Assert.False(page.GetProperty("deliveryComplete").GetBoolean());
        }
        Assert.Null(cursor); Assert.True(page.GetProperty("deliveryComplete").GetBoolean());
        Assert.False(manifest.GetProperty("coverage").GetProperty("complete").GetBoolean());
        Assert.Equal("unknown", manifest.GetProperty("coverage").GetProperty("sources")[0].GetProperty("requestedRangeImpact").GetString());
        Assert.Equal(7, sessions.Count); Assert.Equal(expected.Count, bodies.Count);
        foreach (var pair in expected) Assert.Equal(pair.Value, bodies[pair.Key]);
        Assert.DoesNotContain("yesterday", bodies.Keys); Assert.DoesNotContain("tomorrow", bodies.Keys);
        Assert.Equal(expected.Count, page.GetProperty("deliveredMessages").GetInt32());
    }

    [Fact]
    public async Task Cursor_is_signed_scope_bound_and_respects_revocation()
    {
        using var f = new Fixture(); f.Store.CommitBatch([f.Record("long", "session", new string('a', 1000), "2026-10-21T08:00:00Z")]);
        var tools = new HistoryTools(f.Store);
        var first = await Read(tools, new { view = "messages", from = From, before = Before, bodyCharacters = 100 });
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var modified = Convert.FromBase64String(cursor); modified[20] ^= 1;
        foreach (var args in new object[] {
            new { view = "messages", from = From, before = Before, cursor = Convert.ToBase64String(modified) },
            new { view = "messages", from = "2026-10-21T07:00:00Z", before = Before, cursor },
            new { view = "messages", from = From, before = Before, projectId = "other", cursor } })
            Assert.True((await tools.CallAsync("get_project_activity", JsonSerializer.SerializeToElement(args))).IsError);
        f.Store.SetProjectApproval("work", false);
        var denied = await tools.CallAsync("get_project_activity", JsonSerializer.SerializeToElement(new { view = "messages", from = From, before = Before, cursor }));
        Assert.DoesNotContain(new string('a', 100), denied.Json);
        Assert.True(denied.IsError);
    }

    [Fact]
    public async Task Changed_continuation_body_requires_restart_instead_of_splicing_different_versions()
    {
        using var f = new Fixture(); var record = f.Record("long", "session", new string('a', 1000), "2026-10-21T08:00:00Z");
        f.Store.CommitBatch([record]); var tools = new HistoryTools(f.Store);
        var page = await Read(tools, new { view = "messages", from = From, before = Before, bodyCharacters = 100 });
        f.Store.CommitBatch([record with { Body = new string('b', 1000) }]);
        var next = await tools.CallAsync("get_project_activity", JsonSerializer.SerializeToElement(new { view = "messages", from = From, before = Before, cursor = page.GetProperty("nextCursor").GetString() }));
        Assert.True(next.IsError); Assert.Contains("Restart", next.Json);
    }

    [Fact]
    public async Task Concurrent_earlier_import_is_reported_and_never_claims_complete_delivery()
    {
        using var f = new Fixture(); f.Store.CommitBatch([
            f.Record("first", "one", "first", "2026-10-21T10:00:00Z"),
            f.Record("last", "two", "last", "2026-10-21T11:00:00Z")]);
        var tools = new HistoryTools(f.Store);
        var first = await Read(tools, new { view = "messages", from = From, before = Before, limit = 1 });
        f.Store.CommitBatch([f.Record("imported", "one", "earlier imported work", "2026-10-21T09:00:00Z")]);
        var second = await Read(tools, new { view = "messages", from = From, before = Before, cursor = first.GetProperty("nextCursor").GetString() });
        Assert.True(second.GetProperty("historyChanged").GetBoolean()); Assert.True(second.GetProperty("restartRequired").GetBoolean());
        Assert.False(second.GetProperty("deliveryComplete").GetBoolean());
        var restart = await Read(tools, new { view = "messages", from = From, before = Before });
        Assert.True(restart.GetProperty("deliveryComplete").GetBoolean());
        Assert.Equal(3, restart.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Recent_digest_reads_latest_in_range_and_keeps_default_chronology()
    {
        using var f = new Fixture(); f.Store.CommitBatch([
            f.Record("old", "chat", "old topic", "2026-10-20T08:00:00Z"),
            f.Record("early", "chat", "prepare PR", "2026-10-21T08:00:00Z"),
            f.Record("late", "chat", "PR already opened", "2026-10-21T18:00:00Z")]);
        var tools = new HistoryTools(f.Store);
        var recent = await Call(tools, "get_session_digest", new { sourceId = "source", sessionId = "chat", from = From, before = Before, order = "desc", limit = 1 });
        Assert.Equal("late", recent.GetProperty("records")[0].GetProperty("id").GetString());
        var chronological = await Call(tools, "get_session_digest", new { sourceId = "source", sessionId = "chat", from = From, before = Before, limit = 1 });
        Assert.Equal("early", chronological.GetProperty("records")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Cross_midnight_exchange_exposes_prior_context_without_counting_it_as_today()
    {
        using var f = new Fixture();
        var user = f.Record("prior", "chat", "Investigate period statistics", "2026-10-20T20:00:00Z") with { DataJson = "{\"schema\":\"memory-v1\",\"phase\":\"user\",\"exchangeId\":\"cross-midnight\"}" };
        var final = f.Record("reply", "chat", "The investigation is ready", "2026-10-21T08:00:00Z", "assistant") with { DataJson = "{\"schema\":\"memory-v1\",\"phase\":\"final\",\"final\":true,\"exchangeId\":\"cross-midnight\"}" };
        f.Store.CommitBatch([user, final]); var tools = new HistoryTools(f.Store);
        var window = await Read(tools, new { view = "messages", from = From, before = Before });
        Assert.Equal(1, window.GetProperty("total").GetInt32());
        Assert.Equal("reply", window.GetProperty("records")[0].GetProperty("id").GetString());
        var context = await Call(tools, "get_exchange", new { sourceId = "source", sessionId = "chat", exchangeId = "cross-midnight" });
        Assert.Equal(2, context.GetProperty("total").GetInt32());
        Assert.StartsWith("2026-10-20", context.GetProperty("records")[0].GetProperty("timestamp").GetString());
        var restricted = new HistoryTools(f.Store, new(Since: From, Until: Before));
        var scoped = await Call(restricted, "get_exchange", new { exchangeId = "cross-midnight" });
        Assert.Equal(1, scoped.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Equal_length_edit_to_an_already_delivered_message_invalidates_the_window()
    {
        using var f = new Fixture(); var first = f.Record("first", "chat", "prepare PR", "2026-10-21T10:00:00Z");
        f.Store.CommitBatch([first, f.Record("last", "chat", "Later topic", "2026-10-21T11:00:00Z")]);
        var tools = new HistoryTools(f.Store);
        var page = await Read(tools, new { view = "messages", from = From, before = Before, limit = 1 });
        // Preserve the original change timestamp, as a parser replay does.
        f.Store.CommitBatch([first with { Body = "PR is open" }], reinterpretation: true);
        var next = await Read(tools, new { view = "messages", from = From, before = Before, cursor = page.GetProperty("nextCursor").GetString() });
        Assert.True(next.GetProperty("historyChanged").GetBoolean());
        Assert.False(next.GetProperty("deliveryComplete").GetBoolean());
    }

    [Fact]
    public async Task Small_manifest_pages_make_progress_until_every_session_is_discovered()
    {
        using var f = new Fixture();
        for (var i = 0; i < 7; i++) f.Store.CommitBatch([f.Record("record-" + i, i + new string('s', 160), "work", "2026-10-21T10:00:00Z")]);
        var tools = new HistoryTools(f.Store); var offset = 0; var seen = new HashSet<string>();
        for (var i = 0; i < 10; i++)
        {
            var page = await Read(tools, new { from = From, before = Before, offset, limit = 50, maxOutputCharacters = 6000 });
            foreach (var session in page.GetProperty("sessions").EnumerateArray()) Assert.True(seen.Add(session.GetProperty("sessionId").GetString()!));
            if (page.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            var next = page.GetProperty("nextOffset").GetInt32(); Assert.True(next > offset); offset = next;
        }
        Assert.Equal(7, seen.Count);
    }

    [Fact]
    public async Task Whole_wire_budget_includes_metadata_escaping_and_long_message_continuations()
    {
        using var f = new Fixture(); var body = string.Concat(Enumerable.Repeat("Quoted \"text\" \\ 中文 😀\n", 1000));
        f.Store.CommitBatch(Enumerable.Range(0, 20).Select(i => f.Record("record-" + i, "chat", body, "2026-10-21T10:00:00Z")));
        var tools = new HistoryTools(f.Store);
        foreach (var (name,args) in new (string,object)[] {
            ("get_project_activity", new { view = "messages", from = From, before = Before, limit = 50, bodyCharacters = 12000, maxOutputCharacters = 6000 }),
            ("get_session_digest", new { sourceId = "source", sessionId = "chat", limit = 50, excerptCharacters = 2000, maxOutputCharacters = 6000 }),
            ("get_exchange", new { exchangeId = "exchange-record-0", bodyCharacters = 12000, maxOutputCharacters = 6000 }),
            ("search_history", new { query = "Quoted", limit = 30, excerptCharacters = 2000, maxOutputCharacters = 6000 }) })
        {
            var result = await tools.CallAsync(name, JsonSerializer.SerializeToElement(args));
            Assert.False(result.IsError, result.Json); Assert.True(HistoryResponseSize.Characters(result.Json) <= 6000, name);
            var page = JsonDocument.Parse(result.Json).RootElement;
            Assert.NotEmpty(page.GetProperty("records").EnumerateArray());
            Assert.True(page.GetProperty("records")[0].GetProperty("nextBodyOffset").GetInt32() > 0);
        }
    }

    private static Task<JsonElement> Read(HistoryTools tools, object args) => Call(tools, "get_project_activity", args);

    [Fact]
    public async Task Compact_stream_preserves_all_text_references_and_reports_coverage_only_when_changed()
    {
        using var f = new Fixture();
        var id = new string('a', 64);
        var body = "First topic. " + new string('x', 900) + " Middle topic! " + new string('y', 900) + " Last topic.";
        f.Store.CommitBatch([f.Record(id, "chat", body, "2026-10-21T10:00:00Z")]);
        var tools = new HistoryTools(f.Store);
        var page = await Read(tools, new { view = "messages", format = "compact", from = From, before = Before, bodyCharacters = 200 });
        Assert.True(page.TryGetProperty("coverage", out _));
        string reconstructed = "", reference = "";
        var pages = 0;
        while (true)
        {
            foreach (var group in page.GetProperty("conversations").EnumerateArray())
                foreach (var row in group.GetProperty("messages").EnumerateArray())
                {
                    reference = row[0].GetString()!;
                    Assert.StartsWith("h:", reference); Assert.True(reference.Length < id.Length);
                    Assert.Equal(reconstructed.Length, row[4].GetInt32());
                    reconstructed += row[3].GetString();
                }
            var cursor = page.GetProperty("nextCursor").GetString();
            if (cursor is null) break;
            Assert.True(++pages < 40);
            if (pages == 2) f.Store.SetSourceStatus("source", "gap", "New source problem.");
            page = await Read(tools, new { view = "messages", format = "compact", from = From, before = Before, cursor, bodyCharacters = 200 });
            Assert.Equal(pages == 2, page.TryGetProperty("coverage", out _));
        }
        Assert.Equal(body, reconstructed); Assert.True(page.GetProperty("deliveryComplete").GetBoolean());
        var exchange = await Call(tools, "get_exchange", new { recordId = reference, bodyCharacters = 12000 });
        Assert.Equal(id, exchange.GetProperty("records")[0].GetProperty("id").GetString());
        var scoped = new HistoryTools(f.Store, new(SessionId: "another"));
        var denied = await Call(scoped, "get_exchange", new { recordId = reference });
        Assert.Equal(0, denied.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Gap_sessions_are_attributed_without_leaking_other_projects_or_dates()
    {
        using var f = new Fixture();
        f.Store.CommitBatch([f.Record("today", "today-chat", "today", "2026-10-21T10:00:00Z"),
            f.Record("old", "old-chat", "older", "2026-10-19T10:00:00Z")]);
        foreach (var session in new[] { "today-chat", "old-chat" })
            f.Store.CommitBatch([], new("source", session + ".jsonl", 100, "", "", 100, 0,
                JsonSerializer.Serialize(new { Session = session, Cwd = f.Root, Gap = "Referenced Codex history: unavailable earlier messages." })));
        f.Store.SetSourceStatus("source", "gap", "Referenced Codex history: unavailable earlier messages.");
        var page = await Read(new(f.Store), new { from = From, before = Before });
        var gaps = page.GetProperty("coverage").GetProperty("affectedSessions");
        Assert.Single(gaps.EnumerateArray()); Assert.Equal("today-chat", gaps[0].GetProperty("sessionId").GetString());
        var denied = await Read(new(f.Store, new(SessionId: "no-such-chat")), new { from = From, before = Before });
        Assert.Empty(denied.GetProperty("coverage").GetProperty("affectedSessions").EnumerateArray());
        Assert.DoesNotContain("old-chat", denied.GetRawText());
    }

    [Fact]
    public void Compaction_preserves_settings_approvals_and_retained_evidence()
    {
        using var f = new Fixture();
        f.Store.SetSetting("synthetic-test-setting", "preserve-me");
        f.Store.CommitBatch([f.Record("keep", "chat", "retained", "2026-10-21T10:00:00Z")]);
        f.Store.CompactStorage();
        Assert.Equal("preserve-me", f.Store.GetSetting("synthetic-test-setting"));
        Assert.True(Assert.Single(f.Store.GetProjects()).IsApproved);
        Assert.Equal("retained", f.Store.GetRecord("keep")!.Body);
        Assert.Single(f.Store.Query(new(Search: "retained")).Records);
    }
    [Theory]
    [InlineData("full")]
    [InlineData("compact")]
    public async Task Fresh_sync_timestamps_do_not_repeat_coverage_but_real_gaps_do(string format)
    {
        using var f = new Fixture();
        f.Store.CommitBatch([f.Record("long", "chat", new string('a', 2000), "2026-10-21T10:00:00Z")]);
        var tools = new HistoryTools(f.Store);
        var page = await Read(tools, new { view = "messages", format, bodyCharacters = 200 });
        Assert.True(page.TryGetProperty("coverage", out _));
        f.Store.SetSourceStatus("source", "current");
        page = await Read(tools, new { view = "messages", format, bodyCharacters = 200, cursor = page.GetProperty("nextCursor").GetString() });
        Assert.False(page.TryGetProperty("coverage", out _));
        f.Store.SetSourceStatus("source", "gap", "new unavailable file");
        page = await Read(tools, new { view = "messages", format, bodyCharacters = 200, cursor = page.GetProperty("nextCursor").GetString() });
        Assert.False(page.GetProperty("coverage").GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Amsterdam_dates_and_all_returned_times_use_the_configured_zone()
    {
        using var f = new Fixture();
        f.Store.CommitBatch([f.Record("included", "chat", "pricing", "2026-10-20T22:30:00Z"),
            f.Record("excluded", "chat", "tomorrow", "2026-10-21T22:00:00Z"),
            f.Record("pricing", "chat", "Synthetic pricing point", "2026-10-21T13:39:00Z")]);
        var tools = new HistoryTools(f.Store, defaultTimeZone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"));
        var page = await Read(tools, new { view = "messages", from = "2026-10-21", before = "2026-10-22" });
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal("2026-10-20T22:00:00.0000000+00:00", page.GetProperty("requestedRange").GetProperty("from").GetString());
        Assert.Equal("2026-10-21T15:39:00.0000000+02:00", page.GetProperty("records")[1].GetProperty("timestamp").GetString());
        var search = await Call(tools, "search_history", new { query = "pricing" });
        Assert.All(search.GetProperty("records").EnumerateArray(), r => Assert.EndsWith("+02:00", r.GetProperty("timestamp").GetString()));
        var bad = await tools.CallAsync("get_project_activity", JsonSerializer.SerializeToElement(new { from = "2026-10-21T00:00:00" }));
        Assert.True(bad.IsError);
        var dst = await Read(tools, new { from = "2026-10-25", before = "2026-10-26" });
        var range = dst.GetProperty("requestedRange");
        Assert.Equal(TimeSpan.FromHours(25), DateTimeOffset.Parse(range.GetProperty("before").GetString()!) - DateTimeOffset.Parse(range.GetProperty("from").GetString()!));
    }

    [Fact]
    public async Task Large_page_uses_available_budget_without_halving_or_losing_middle_text()
    {
        using var f = new Fixture(); var text = new string('a', 40000) + " MIDDLE_DECISION " + new string('b', 40000);
        f.Store.CommitBatch([f.Record("long", "chat", text, "2026-10-21T10:00:00Z")]);
        var tools = new HistoryTools(f.Store); string? cursor = null; var reconstructed = ""; var pages = 0;
        do
        {
            var page = await Read(tools, new { view = "messages", format = "compact", bodyCharacters = 48000, maxOutputCharacters = 48000, cursor });
            var row = page.GetProperty("conversations")[0].GetProperty("messages")[0];
            Assert.Equal(reconstructed.Length, row[4].GetInt32());
            if (pages == 0) Assert.True(row[3].GetString()!.Length > 40000);
            reconstructed += row[3].GetString(); cursor = page.GetProperty("nextCursor").GetString(); pages++;
        } while (cursor is not null && pages < 4);
        Assert.Null(cursor); Assert.Equal(2, pages); Assert.Equal(text, reconstructed);
    }

    [Fact]
    public async Task Outline_indexes_middle_checklists_and_never_claims_complete_message_review()
    {
        using var f = new Fixture();
        var body = new string('a', 1200) + "\n- HelpDesk: test end-to-end drafting in production.\n"
            + new string('b', 1200) + "\n- No backfill write before prevention fixes are live.\n" + new string('c', 1200);
        f.Store.CommitBatch([f.Record("middle", "multi-day-chat", body, "2026-10-21T10:00:00Z"),
            f.Record("older", "multi-day-chat", "old request", "2026-10-19T10:00:00Z")]);
        var tools = new HistoryTools(f.Store);
        var outline = await Read(tools, new { view = "outline", from = From, before = Before, maxOutputCharacters = 48000 });
        Assert.True(outline.GetProperty("outlineOnly").GetBoolean());
        var rows = outline.GetProperty("conversations")[0].GetProperty("messages");
        Assert.Single(rows.EnumerateArray()); var index = rows[0][3].GetString()!;
        Assert.Contains("HelpDesk: test end-to-end drafting", index);
        Assert.Contains("No backfill write before prevention fixes are live.", index);
        Assert.Contains("excerpted", index);
        var original = await Call(tools, "get_exchange", new { recordId = rows[0][0].GetString(), bodyCharacters = 48000 });
        Assert.Equal(body, original.GetProperty("records")[0].GetProperty("body").GetString());
        var full = await Read(tools, new { view = "messages", from = From, before = Before, bodyCharacters = 48000, maxOutputCharacters = 48000 });
        Assert.Equal(body, full.GetProperty("records")[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Partitions_read_every_original_body_once_across_sessions_and_equal_timestamps()
    {
        using var f = new Fixture();
        var expected = new Dictionary<string, string>();
        for (var session = 0; session < 3; session++)
            for (var i = 0; i < 6; i++)
            {
                var id = $"s{session}-m{i}";
                var body = i == 2 ? new string('x', 51000) + " middle checklist / unposted draft / prerequisite " : new string('a', 6000);
                expected[id] = body;
                f.Store.CommitBatch([f.Record(id, "s" + session, body, $"2026-10-21T{8 + i / 2:00}:00:00Z")]);
            }
        f.Store.CommitBatch([f.Record("older", "s0", "excluded previous date", "2026-10-19T12:00:00Z")]);
        var tools = new HistoryTools(f.Store);
        var bodies = new Dictionary<string, string>(); var partitions = new List<JsonElement>();
        int? offset = 0; string? manifestRevision = null;
        do
        {
            var page = await Read(tools, new { view = "partitions", from = From, before = Before, partitionBytes = 10000, limit = 2, offset });
            manifestRevision ??= page.GetProperty("manifestRevision").GetString();
            Assert.Equal(manifestRevision, page.GetProperty("manifestRevision").GetString());
            Assert.Equal(18, page.GetProperty("totalMessages").GetInt32());
            partitions.AddRange(page.GetProperty("partitions").EnumerateArray().Select(p => p.Clone()));
            offset = page.GetProperty("nextOffset").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextOffset").GetInt32();
        } while (offset is not null);
        Assert.Equal(9, partitions.Count);
        Assert.All(partitions, p => Assert.True(p.GetProperty("oversized").GetBoolean()));
        foreach (var partition in partitions)
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, object>>(partition.GetProperty("readArguments").GetRawText())!;
            JsonElement page;
            do
            {
                page = await Read(new HistoryTools(f.Store), args); // fresh catalog, stable revision
                foreach (var conversation in page.GetProperty("conversations").EnumerateArray())
                    foreach (var row in conversation.GetProperty("messages").EnumerateArray())
                    {
                        var id = row[0].GetString()!; var previous = bodies.GetValueOrDefault(id, "");
                        Assert.Equal(previous.Length, row[4].GetInt32());
                        bodies[id] = previous + row[3].GetString();
                    }
                if (page.GetProperty("nextCursor").GetString() is not { } cursor) break;
                args["cursor"] = cursor;
            } while (true);
            Assert.True(page.GetProperty("deliveryComplete").GetBoolean());
            Assert.Equal(partition.GetProperty("messageCount").GetInt32(), page.GetProperty("deliveredMessages").GetInt32());
        }
        Assert.Equal(expected.Count, bodies.Count);
        foreach (var pair in expected) Assert.Equal(pair.Value, bodies[pair.Key]);
    }

    [Fact]
    public async Task Stale_partition_cannot_claim_delivery_and_unapproved_projects_never_enter_manifest()
    {
        using var f = new Fixture(); var record = f.Record("one", "chat", "original", "2026-10-21T10:00:00Z");
        f.Store.CommitBatch([record]); var tools = new HistoryTools(f.Store);
        var manifest = await Read(tools, new { view = "partitions", from = From, before = Before });
        var full = await Read(tools, manifest.GetProperty("fullReadArguments"));
        Assert.True(full.GetProperty("deliveryComplete").GetBoolean());
        Assert.Equal(manifest.GetProperty("totalMessages").GetInt32(), full.GetProperty("deliveredMessages").GetInt32());
        var args = manifest.GetProperty("partitions")[0].GetProperty("readArguments");
        f.Store.CommitBatch([record with { Body = "modified" }]);
        var stale = await tools.CallAsync("get_project_activity", args);
        Assert.True(stale.IsError); Assert.True(JsonDocument.Parse(stale.Json).RootElement.GetProperty("restartRequired").GetBoolean());
        var changed = await Read(tools, new { view = "partitions", from = From, before = Before });
        Assert.NotEqual(manifest.GetProperty("manifestRevision").GetString(), changed.GetProperty("manifestRevision").GetString());
        f.Store.SetProjectApproval("work", false);
        var denied = await Read(tools, new { view = "partitions", from = From, before = Before });
        Assert.Equal(0, denied.GetProperty("totalMessages").GetInt32());
        Assert.Empty(denied.GetProperty("partitions").EnumerateArray());
        Assert.True((await tools.CallAsync("get_project_activity", args)).IsError);
    }

    [Fact]
    public async Task Cursor_cannot_be_reused_for_another_session()
    {
        using var f = new Fixture();
        f.Store.CommitBatch([f.Record("a", "one", new string('a', 1000), "2026-10-21T08:00:00Z"),
            f.Record("b", "two", "other", "2026-10-21T08:00:00Z")]);
        var tools = new HistoryTools(f.Store);
        var page = await Read(tools, new { view = "messages", sessionId = "one", bodyCharacters = 100 });
        var invalid = await tools.CallAsync("get_project_activity", JsonSerializer.SerializeToElement(new { view = "messages", sessionId = "two", cursor = page.GetProperty("nextCursor").GetString() }));
        Assert.True(invalid.IsError);
    }

    private static async Task<JsonElement> Call(HistoryTools tools, string name, object args)
    {
        // Optional null cursor must be omitted, as documented for MCP arguments.
        var element = JsonSerializer.SerializeToElement(args, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var result = await tools.CallAsync(name, element); Assert.False(result.IsError, result.Json);
        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MissionControl-window-" + Guid.NewGuid().ToString("N"));
        public WorkspaceStore Store { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "workspace.db"));
            Store.SaveProject(new("work", "Work", "blue", "", Root)); Store.SetProjectApproval("work", true);
            Store.SaveSource(new("source", "codex", "Codex", Root, true)); Store.SetSourceStatus("source", "current");
        }
        public EvidenceRecord Record(string id, string session, string body, string timestamp, string author = "user") => new(id, "source", session + ".jsonl", session, "work", "codex", "message", "Evidence", body, author, timestamp, "2026-10-21T20:00:00Z", Root,
            DataJson: JsonSerializer.Serialize(new { schema = "memory-v1", phase = author == "user" ? "user" : "final", final = author == "assistant", exchangeId = "exchange-" + id }));
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }
}
