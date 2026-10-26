using System.Text.Json;

namespace MissionControl.Tests;

public sealed class ConversationQueryTests
{
    [Fact]
    public void Timeline_uses_original_message_time_when_old_history_was_recently_reindexed()
    {
        using var fixture = new Fixture(); using var store = fixture.Open();
        store.CommitBatch([fixture.Record("old", "one", "user", "Older message", 1) with { ChangedAt = "2026-10-21T12:00:00Z" },
            fixture.Record("new", "two", "user", "Newer message", 5) with { ChangedAt = "2026-10-21T06:00:00Z" }]);
        Assert.Equal(new[] { "old", "new" }, store.Query(new()).Records.Select(r => r.Id));
        Assert.Equal(new[] { "new", "old" }, store.Query(new(ByTimestamp: true)).Records.Select(r => r.Id));
    }
    [Fact]
    public void Search_selects_conversations_and_orders_by_latest_approved_activity_before_paging()
    {
        using var fixture = new Fixture(); using var store = fixture.Open();
        store.CommitBatch([fixture.Record("a-user", "a", "user", "Fix watermelon pagination", 1), fixture.Record("a-final", "a", "assistant", "Final explanation", 5), fixture.Record("b-user", "b", "user", "Another watermelon investigation", 3)]);
        var result = store.QuerySessions(new(Search: "watermelon", Limit: 1));
        Assert.Equal(2, result.Total); var session = Assert.Single(result.Records);
        Assert.Equal("a", session.SessionId); Assert.Equal("Fix watermelon pagination", session.Title);
        Assert.Equal("a-final", session.Id); Assert.Equal("b", Assert.Single(store.QuerySessions(new(Search: "watermelon", Limit: 1, Offset: 1)).Records).SessionId);
    }
    [Fact]
    public void Same_session_identity_in_different_sources_is_not_combined_and_unapproved_content_is_absent()
    {
        using var fixture = new Fixture(); using var store = fixture.Open();
        store.SaveSource(new("second", "codex", "Second", fixture.Root, true));
        store.CommitBatch([fixture.Record("first", "same", "user", "First source request", 1), fixture.Record("second", "same", "user", "Second source request", 2) with { SourceId = "second", SourceKind = "codex" }, fixture.Record("excluded", "secret", "user", "Personal excluded request", 3) with { WorkingDirectory = fixture.Root + "-personal" }]);
        Assert.Equal(2, store.QuerySessions(new()).Total);
        Assert.Equal(0, store.QuerySessions(new(Search: "Personal")).Total);
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MissionControl-conversations-" + Guid.NewGuid().ToString("N"));
        public WorkspaceStore Open()
        {
            Directory.CreateDirectory(Root); var store = new WorkspaceStore(Path.Combine(Root,"workspace.db"));
            store.SaveProject(new("project","Project","blue","",Root)); store.SetProjectApproval("project",true);
            store.SaveSource(new("source","claude","Claude",Root,true)); return store;
        }
        public EvidenceRecord Record(string id,string session,string author,string body,int hour) => new(id,"source","session.jsonl",session,"project","claude","message",body,body,author,$"2026-10-21T{hour:00}:00:00Z",$"2026-10-21T{hour:00}:00:00Z",WorkingDirectory:Root,DataJson:JsonSerializer.Serialize(new {schema="memory-v1",phase=author=="user"?"user":"final",final=author=="assistant",exchangeId=session}));
        public void Dispose() => Directory.Delete(Root,true);
    }
}
