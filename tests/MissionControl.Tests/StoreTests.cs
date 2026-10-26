using System.Reflection;
using Microsoft.Data.Sqlite;
using MissionControl.Core;

namespace MissionControl.Tests;

public sealed class StoreTests
{
    [Fact]
    public void Live_statistics_count_all_approved_sessions_and_storage_and_follow_revocation()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        store.SaveSource(new("source","codex","Local",fixture.Root,true));
        store.SetSourceStatus("source", "catching-up");
        var rows = Enumerable.Range(0, 300).Select(i => fixture.Record("request-" + i) with { SessionId = "session-" + i });
        store.CommitBatch(rows);
        store.CommitBatch([
            fixture.Record("final") with { Author = "assistant", DataJson = "{\"schema\":\"memory-v1\",\"phase\":\"final\"}" },
            fixture.Record("event") with { Author = "system", Kind = "lifecycle", DataJson = "{\"schema\":\"memory-v1\",\"phase\":\"lifecycle\"}" },
            fixture.Record("other-source") with { SourceId = "second", SessionId = "session" },
            fixture.Record("excluded") with { WorkingDirectory = fixture.Root + "-private" }
        ], Checkpoint(12));
        var statistics = store.Statistics("live", "running");
        Assert.Equal(303, statistics.SavedEntries); Assert.Equal(302, statistics.Sessions);
        Assert.Equal(301, statistics.UserMessages); Assert.Equal(1, statistics.FinalResponses); Assert.Equal(1, statistics.StatusEvents);
        Assert.True(statistics.CatchingUp); Assert.NotNull(statistics.LastCheckpoint);
        Assert.True(statistics.DatabaseBytes > 0); Assert.True(statistics.ActiveDatabaseBytes > 0);
        Assert.True(statistics.ActiveDatabaseBytes <= statistics.DatabaseBytes); Assert.Equal(store.StorageLimitBytes, statistics.StorageLimitBytes);
        Assert.Equal(250, store.Snapshot("live", "running").Records.Count);
        store.SetProjectApproval("work", false);
        var revoked = store.Statistics("live", "running");
        Assert.Equal(0, revoked.SavedEntries); Assert.Equal(0, revoked.Sessions); Assert.Equal(0, revoked.UserMessages);
        Assert.Equal(0, revoked.FinalResponses); Assert.Equal(0, revoked.StatusEvents);
    }
    [Fact]
    public void Unknown_and_unapproved_projects_never_persist_payload_or_search_tokens()
    {
        using var fixture = new Workspace(); using var store = fixture.Open();
        store.SaveProject(new("work", "Work", "#abcdef", "", fixture.Root));
        store.CommitBatch([fixture.Record("unknown") with { WorkingDirectory = Path.Combine(fixture.Directory, "personal"), ProjectId = "work" }, fixture.Record("unapproved")]);
        Assert.Equal(0, store.Count()); Assert.Empty(store.Query(new(Search: "walrus")).Records);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM evidence"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM evidence_fts WHERE evidence_fts MATCH 'walrus'"));
    }

    [Fact]
    public void Approval_is_explicit_and_metadata_save_cannot_grant_it()
    {
        using var fixture = new Workspace(); using var store = fixture.Open();
        store.SaveProject(new("work", "Work", "#abcdef", "", fixture.Root, IsApproved: true));
        Assert.False(Assert.Single(store.GetProjects()).IsApproved);
        Assert.False(store.CanIndexWorkingDirectory(fixture.Root));
        store.SetProjectApproval("work", true);
        Assert.True(Assert.Single(store.GetProjects()).IsApproved);
        Assert.True(store.CanIndexWorkingDirectory(Path.Combine(fixture.Root, "repo")));
        store.CommitBatch([fixture.Record("one")]);
        Assert.Equal("work", Assert.Single(store.Query(new()).Records).ProjectId);
        Assert.Equal("one", Assert.Single(store.Query(new(Search: "walrus")).Records).Id);
    }

    [Theory]
    [InlineData("../personal")]
    [InlineData("../../personal")]
    public void Normalized_path_traversal_and_spoofed_project_labels_are_denied(string suffix)
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        store.CommitBatch([fixture.Record("spoof") with { WorkingDirectory = Path.Combine(fixture.Root, suffix), ProjectId = "work" }]);
        Assert.Equal(0, store.Count()); Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM evidence"));
    }

    [Fact]
    public void Path_prefix_sibling_and_missing_cwd_are_denied()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        store.CommitBatch([fixture.Record("prefix") with { WorkingDirectory = fixture.Root + "-personal" }, fixture.Record("missing") with { WorkingDirectory = null }]);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM evidence"));
    }

    [Fact]
    public void Intermediate_reasoning_tool_and_external_records_are_rejected()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        store.CommitBatch([
            fixture.Record("user"), fixture.Record("final") with { Author = "assistant", DataJson = "{\"schema\":\"memory-v1\",\"phase\":\"final\",\"final\":true}" },
            fixture.Record("thinking") with { Author = "assistant", DataJson = "{\"phase\":\"analysis\"}" },
            fixture.Record("tool") with { Kind = "tool" }, fixture.Record("context") with { Author = "context" },
            fixture.Record("external") with { SourceKind = "slack" }
        ]);
        Assert.Equal(2, store.Count());
        foreach (var id in new[] { "thinking", "tool", "context", "external" }) Assert.Null(store.GetRecord(id));
    }

    [Fact]
    public void Checkpoint_and_records_are_atomic_and_survive_reopen_without_duplicates()
    {
        using var fixture = new Workspace();
        using (var store = fixture.Open())
        {
            fixture.Approve(store); store.CommitBatch([fixture.Record("one")], Checkpoint(10));
            var version = store.EvidenceVersion(); store.CommitBatch([fixture.Record("one")], Checkpoint(20));
            Assert.Equal(version, store.EvidenceVersion());
            Assert.Throws<ArgumentException>(() => store.CommitBatch([fixture.Record("rolledback")], Checkpoint(999) with { Length = 20 }));
            Assert.Null(store.GetRecord("rolledback")); Assert.Equal(20, store.GetCheckpoint("source", "stream")?.Offset);
        }
        using var reopened = fixture.Open(); Assert.Equal(1, reopened.Count()); Assert.Equal(20, reopened.GetCheckpoint("source", "stream")?.Offset);
    }

    [Fact]
    public void Manual_notes_survive_replay_but_never_change_privacy_ownership()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        store.CommitBatch([fixture.Record("one")]); store.SaveCorrection(new("one", "personal", "Follow up", Note: "review decision"));
        store.CommitBatch([fixture.Record("one") with { Timestamp = "2026-10-22T10:00:00Z", Body = "Updated walrus" }]);
        Assert.Equal("review decision", Assert.Single(store.GetCorrections()).Note);
        Assert.Equal("work", store.GetRecord("one")?.ProjectId);
        Assert.Empty(store.Query(new(ProjectId: "personal")).Records);
        Assert.Single(store.Query(new(Search: "review decision")).Records);
    }

    [Fact]
    public void Revocation_purges_payload_notes_search_and_pending_checkpoints_for_existing_readonly_client()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        store.CommitBatch([fixture.Record("one")], Checkpoint(10) with { MetadataJson = "{\"pending\":\"walrus\"}" });
        store.SaveCorrection(new("one", Note: "secretnote"));
        using var reader = WorkspaceStore.OpenReadOnly(fixture.Path);
        Assert.Equal(1, reader.Count()); Assert.Single(reader.Query(new(Search: "walrus")).Records);
        Assert.Equal(1, store.SetProjectApproval("work", false));
        Assert.Equal(0, reader.Count()); Assert.Null(reader.GetRecord("one")); Assert.Empty(reader.Query(new(Search: "secretnote")).Records);
        Assert.Empty(reader.GetCorrections()); Assert.Empty(store.GetCheckpoints("source"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM evidence"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM evidence_fts WHERE evidence_fts MATCH 'walrus'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM corrections_fts WHERE corrections_fts MATCH 'secretnote'"));
        Assert.Single(store.GetProjects());
    }

    [Fact]
    public void A_late_observer_checkpoint_cannot_reintroduce_revoked_pending_text()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        store.SetProjectApproval("work",false);
        var metadata = System.Text.Json.JsonSerializer.Serialize(new { cwd=fixture.Root, parserVersion=8, pendingAssistant=new { text="privatepending", cwd=fixture.Root } });
        store.CommitBatch([fixture.Record("late")],Checkpoint(10) with { MetadataJson=metadata });
        Assert.Equal(0,store.Count());
        Assert.DoesNotContain("privatepending",store.GetCheckpoint("source","stream")!.MetadataJson!);
        store.SetProjectApproval("work",true); Assert.Null(store.GetCheckpoint("source","stream"));
    }

    [Fact]
    public async Task All_five_memory_tools_and_an_existing_handle_obey_revocation()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        var metadata="{\"schema\":\"memory-v1\",\"phase\":\"user\",\"exchangeId\":\"exchange\",\"turnId\":\"turn\"}";
        store.CommitBatch([fixture.Record("one") with { DataJson=metadata }]);
        using var reader=WorkspaceStore.OpenReadOnly(fixture.Path);
        var tools=new HistoryTools(reader);
        var calls=new Dictionary<string,string> {
            ["search_history"]="{\"query\":\"walrus\"}",
            ["get_session_digest"]="{\"sourceId\":\"source\",\"sessionId\":\"session\"}",
            ["get_exchange"]="{\"exchangeId\":\"exchange\"}",
            ["get_project_activity"]="{}",
            ["get_session_details"]="{\"sourceId\":\"source\",\"sessionId\":\"session\"}"
        };
        foreach(var call in calls)
        {
            var result=await tools.CallAsync(call.Key,System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(call.Value));
            Assert.False(result.IsError); Assert.Contains("work",result.Json);
        }
        store.SetProjectApproval("work",false);
        foreach(var call in calls)
        {
            var result=await tools.CallAsync(call.Key,System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(call.Value));
            using var data=System.Text.Json.JsonDocument.Parse(result.Json);
            if(call.Key=="get_session_details") Assert.True(result.IsError);
            else Assert.Equal(0,data.RootElement.GetProperty("total").GetInt32());
            Assert.DoesNotContain("walrus",result.Json);
        }
    }

    [Fact]
    public void Moving_an_approved_root_requires_new_consent_and_clears_old_payload()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        store.CommitBatch([fixture.Record("one")]);
        store.SaveProject(Assert.Single(store.GetProjects()) with { RootPath = Path.Combine(fixture.Directory, "other") });
        Assert.False(Assert.Single(store.GetProjects()).IsApproved); Assert.Equal(0, store.Count());
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM evidence"));
    }

    [Fact]
    public void Retrieval_rechecks_root_membership_even_for_tampered_legacy_project_labels()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        store.CommitBatch([fixture.Record("one")]);
        using (var db = fixture.Sqlite())
        {
            using var command = db.CreateCommand(); command.CommandText = "UPDATE evidence SET working_directory=$cwd WHERE id='one'"; command.Parameters.AddWithValue("$cwd", Path.Combine(fixture.Directory,"personal")); command.ExecuteNonQuery();
        }
        Assert.Empty(store.Query(new()).Records); Assert.Empty(store.QuerySessions(new()).Records); Assert.Null(store.GetRecord("one")); Assert.Equal(0, store.Count());
        Assert.Empty(store.Snapshot("live", "running").Records);
        Assert.Equal(1, store.PurgeExcludedHistory());
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM evidence_fts WHERE evidence_fts MATCH 'walrus'"));
    }

    [Fact]
    public void Version_six_migration_preserves_manual_settings_and_quarantines_local_users_with_verified_backup()
    {
        using var fixture = new Workspace(); fixture.CreateLegacy();
        using var store = fixture.Open();
        Assert.Equal("dark", store.GetSetting("appearance.theme")); Assert.Null(store.GetSetting("ai.config"));
        Assert.False(Assert.Single(store.GetProjects()).IsApproved); Assert.Equal(0, store.Count());
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM evidence")); Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM evidence_fts"));
        foreach (var name in new[] { "remote_connections", "remote_selections", "remote_checkpoints", "cache", "derived_commitments", "commitment_corrections" })
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='" + name + "'"));
        Assert.Contains("live-integrations\nremote:github-000000000000000000000000:token", store.GetRetiredCredentialKeys());
        Assert.Contains(store.GetRetiredCredentialKeys(), key => key.StartsWith("live\nremote-api-key:", StringComparison.Ordinal));
        Assert.Contains("live\n*",store.GetRetiredCredentialKeys());
        Assert.Contains("live-integrations\n*",store.GetRetiredCredentialKeys());
        store.SetProjectApproval("work", true);
        Assert.Equal("legacy-user", Assert.Single(store.Query(new(Search: "walrus")).Records).Id);
        Assert.Equal("Keep decision", Assert.Single(store.GetCorrections()).Note);
        var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Directory,"migration-backups"), "*.sqlite"));
        using var backup = new SqliteConnection("Data Source=" + backupPath + ";Mode=ReadOnly;Pooling=False"); backup.Open();
        using var check = backup.CreateCommand(); check.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", check.ExecuteScalar());
        check.CommandText = "PRAGMA user_version"; Assert.Equal(6L, check.ExecuteScalar()); check.CommandText = "SELECT COUNT(*) FROM evidence"; Assert.Equal(3L, check.ExecuteScalar());
    }

    [Fact]
    public void Foreign_schema_is_refused_before_any_owned_ddl_or_backup()
    {
        using var fixture = new Workspace();
        using (var db = fixture.Sqlite()) { using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TABLE unrelated(id INTEGER); PRAGMA user_version=6"; cmd.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(() => fixture.Open());
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='unrelated'"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Directory, "migration-backups")));
    }

    [Fact]
    public void Batch_and_query_bounds_are_enforced()
    {
        using var fixture = new Workspace(); using var store = fixture.Open(); fixture.Approve(store);
        Assert.Throws<ArgumentException>(() => store.CommitBatch(Enumerable.Range(0,513).Select(i => fixture.Record(i.ToString()))));
        store.CommitBatch(Enumerable.Range(0,512).Select(i => fixture.Record(i.ToString())));
        Assert.Equal(500, store.Query(new(Limit: int.MaxValue)).Records.Count);
        Assert.Empty(store.Query(new(Search: "\"*()^{}:")).Records);
    }

    private static StreamCheckpoint Checkpoint(long offset) => new("source", "stream", offset, "fingerprint", "anchor", Math.Max(offset,100), 1234);
    private sealed class Workspace : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MissionControl-privacy-tests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory,"workspace.sqlite");
        public string Root => System.IO.Path.Combine(Directory,"work");
        public Workspace() => System.IO.Directory.CreateDirectory(Root);
        public WorkspaceStore Open() => new(Path);
        public void Approve(WorkspaceStore store) { store.SaveProject(new("work","Work","#abcdef","",Root)); store.SetProjectApproval("work",true); }
        public EvidenceRecord Record(string id) => new(id,"source","stream","session","spoofed", "codex","message","Observed request","A walrus request","user","2026-10-21T10:00:00Z","2026-10-21T10:00:00Z",Root, DataJson: "{\"schema\":\"memory-v1\",\"phase\":\"user\"}");
        public SqliteConnection Sqlite() { var db = new SqliteConnection("Data Source=" + Path + ";Pooling=False"); db.Open(); return db; }
        public object? Scalar(string sql) { using var db = Sqlite(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
        public void CreateLegacy()
        {
            using var db = Sqlite();
            for (var version=1;version<=6;version++)
            {
                var resource = typeof(WorkspaceStore).Assembly.GetManifestResourceNames().Single(name=>name.Contains(".Migrations.00"+version+"_",StringComparison.Ordinal));
                using var reader = new StreamReader(typeof(WorkspaceStore).Assembly.GetManifestResourceStream(resource)!);
                using var command = db.CreateCommand(); command.CommandText=reader.ReadToEnd(); command.ExecuteNonQuery();
            }
            using var seed = db.CreateCommand(); seed.CommandText = """
                INSERT INTO projects(id,name,color,description,root_path) VALUES('work','Work','#abcdef','',$root);
                INSERT INTO settings(key,value) VALUES('appearance.theme','dark'),('ai.config','{"provider":"remote","endpoint":"https://example.com/v1/chat/completions"}');
                INSERT INTO remote_connections(id,kind,name,endpoint,poll_minutes,lookback_days,enabled) VALUES('github-000000000000000000000000','github','GitHub','https://api.github.com',15,30,1);
                INSERT INTO evidence(id,source_id,stream_id,session_id,project_id,source_kind,kind,title,body,author,timestamp,ingested_at,working_directory,changed_at)
                VALUES('legacy-user','source','stream','session','work','codex','message','Request','legacy walrus','user','2026-10-21T10:00:00Z','2026-10-21T10:00:00Z',$root,'2026-10-21T10:00:00Z'),
                ('legacy-assistant','source','stream','session','work','codex','message','Intermediate','private reasoning','assistant','2026-10-21T10:01:00Z','2026-10-21T10:01:00Z',$root,'2026-10-21T10:01:00Z'),
                ('remote','remote','stream','session','work','slack','message','External','private Slack','alex','2026-10-21T10:01:00Z','2026-10-21T10:01:00Z',$root,'2026-10-21T10:01:00Z');
                INSERT INTO corrections(record_id,note) VALUES('legacy-user','Keep decision');
                PRAGMA user_version=6;
                """; seed.Parameters.AddWithValue("$root",Root); seed.ExecuteNonQuery();
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); System.IO.Directory.Delete(Directory,true); }
    }
}
