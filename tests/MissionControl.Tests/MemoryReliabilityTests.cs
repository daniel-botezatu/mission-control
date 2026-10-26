using System.Text.Json;

namespace MissionControl.Tests;

public sealed class MemoryReliabilityTests
{
    [Fact]
    public async Task Empty_results_report_real_backlog_and_only_approved_project_names()
    {
        using var fixture = new Fixture(); using var store = fixture.Open();
        store.SetSourceStatus("source", "catching-up", pending: 0);
        store.CommitBatch([], new("source", "stream.jsonl", 20, "", "", 2000, 0));
        var tools = new HistoryTools(store);
        var result = await Call(tools, "get_project_activity", new { from = "2030-01-01T00:00:00Z" });
        Assert.Equal(0, result.GetProperty("total").GetInt32());
        var coverage = result.GetProperty("coverage");
        Assert.False(coverage.GetProperty("complete").GetBoolean());
        var source = coverage.GetProperty("sources")[0];
        Assert.Equal(1, source.GetProperty("pending").GetInt32());
        Assert.Equal(1980, source.GetProperty("progress").GetProperty("pendingBytes").GetInt64());
        Assert.DoesNotContain("Excluded personal project", coverage.GetRawText());
        var denied = await tools.CallAsync("get_session_details", JsonSerializer.SerializeToElement(new { sourceId = "source", sessionId = "missing" }));
        Assert.True(denied.IsError); Assert.Contains("coverage", denied.Json);
        var stats = store.Statistics("live", "running");
        Assert.Equal(1980, stats.PendingBytes); Assert.Equal(1, stats.PendingFiles);
    }

    [Fact]
    public async Task Long_text_is_retained_redacted_searchable_and_pageable_beyond_the_old_limit()
    {
        using var fixture = new Fixture(); using var store = fixture.Open();
        var text = new string('a', 60000) + "\napi_key=PRIVATE_TEST_VALUE\nTAIL_SENTINEL";
        store.CommitBatch([fixture.Record("long", text)]);
        var saved = store.GetRecord("long")!;
        Assert.Contains("TAIL_SENTINEL", saved.Body); Assert.DoesNotContain("PRIVATE_TEST_VALUE", saved.Body);
        var preview = Assert.Single(store.Query(new(), previews: true).Records);
        Assert.True(preview.Body.Length <= 2000); Assert.Equal(0, preview.NextBodyOffset);
        var tools = new HistoryTools(store);
        var page = await Call(tools, "get_exchange", new { exchangeId = "exchange", bodyOffset = 60000, bodyCharacters = 500, limit = 1 });
        Assert.Contains("TAIL_SENTINEL", page.GetProperty("records")[0].GetProperty("body").GetString());
        var search = await Call(tools, "search_history", new { query = "TAIL_SENTINEL", mode = "all" });
        Assert.Equal(1, search.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Automatic_search_tolerates_transpositions_and_synonyms_with_strict_opt_out()
    {
        using var fixture = new Fixture(); using var store = fixture.Open();
        store.CommitBatch([fixture.Record("login", "HelpDesk authentication retries")]);
        var tools = new HistoryTools(store);
        Assert.Equal(0, (await Call(tools, "search_history", new { query = "authetnication", mode = "all" })).GetProperty("total").GetInt32());
        var corrected = await Call(tools, "search_history", new { query = "authetnication" });
        Assert.Equal(1, corrected.GetProperty("total").GetInt32()); Assert.Contains("spelling", corrected.GetProperty("searchNotice").GetString());
        Assert.Equal(1, (await Call(tools, "search_history", new { query = "login" })).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Call(tools, "search_history", new { query = "login", mode = "phrase" })).GetProperty("total").GetInt32());
        Assert.Equal(1, store.QuerySessions(new(Search: "authetnication")).Total);
        Assert.Equal(1, store.Query(new(Search: "authetnication")).Total);
        store.SetProjectApproval("work", false);
        Assert.Equal(0, (await Call(tools, "search_history", new { query = "authetnication" })).GetProperty("total").GetInt32());
    }

    [Fact]
    public void Overview_orders_source_activity_before_recently_imported_old_messages()
    {
        using var fixture = new Fixture(); using var store = fixture.Open();
        store.CommitBatch([fixture.Record("older", "Old work") with { Timestamp = "2026-10-01T12:00:00Z", ChangedAt = "2026-10-21T20:00:00Z" },
            fixture.Record("today", "Today work") with { Timestamp = "2026-10-21T12:00:00Z", ChangedAt = "2026-10-21T12:00:00Z" }]);
        Assert.Equal("today", store.Snapshot("live", "running").Records[0].Id);
    }

    private static async Task<JsonElement> Call(HistoryTools tools, string name, object args)
    {
        var result = await tools.CallAsync(name, JsonSerializer.SerializeToElement(args));
        Assert.False(result.IsError, result.Json);
        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MissionControl-reliability-" + Guid.NewGuid().ToString("N"));
        public WorkspaceStore Open()
        {
            Directory.CreateDirectory(Root); var store = new WorkspaceStore(Path.Combine(Root, "workspace.db"));
            store.SaveProject(new("work", "Work", "blue", "", Root)); store.SetProjectApproval("work", true);
            store.SaveProject(new("excluded", "Excluded personal project", "blue", "", Root + "-personal"));
            store.SaveSource(new("source", "claude", "Claude", Root, true)); return store;
        }
        public EvidenceRecord Record(string id, string body) => new(id, "source", "stream.jsonl", "session", "work", "claude", "message", body[..Math.Min(body.Length, 100)], body, "user", "2026-10-21T12:00:00Z", "2026-10-21T12:00:00Z", Root,
            DataJson: "{\"schema\":\"memory-v1\",\"phase\":\"user\",\"exchangeId\":\"exchange\"}");
        public void Dispose() => Directory.Delete(Root, true);
    }
}
