using System.Text.Json.Nodes;

namespace MissionControl.Core;

public sealed partial class WorkspaceStore
{
    // Bump the version when the transcript filter learns another host-written wrapper,
    // so rows stored by an older release are checked once more.
    private const string HostWrapperRepairKey = "repair.hostWrittenMessages";
    private const string HostWrapperRepairVersion = "1";

    /// <summary>
    /// Corrects stored user messages that the current transcript filter recognises as written by the
    /// host rather than the person. Codex review results are re-labelled as AI-written results under
    /// the identity the parser now gives them; slash commands and their output, compaction summaries,
    /// app events and goal continuations are removed. Replays only add or update rows, so they cannot
    /// do this. Runs once per filter version; original transcripts are never touched.
    /// </summary>
    private (int Removed, int Relabelled) RemoveHostWrittenMessages()
    {
        if (Scalar("SELECT value FROM settings WHERE key=$key", ("$key", HostWrapperRepairKey)) as string == HostWrapperRepairVersion) return (0, 0);
        var stale = new List<string>();
        var reviews = new List<(string Id, string NewId, string Title, string Body, string? Data)>();
        using (var command = Command("SELECT r.id,r.body,r.source_kind,s.environment,r.session_id,r.data_json FROM evidence r LEFT JOIN sources s ON s.id=r.source_id WHERE r.kind='message' AND r.author='user' AND r.source_kind IN ('claude','codex')"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var (id, body) = (reader.GetString(0), reader.GetString(1));
                if (TranscriptContent.ReviewResult(body) is { } review)
                    reviews.Add(Relabel(id, review, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
                else if (!TranscriptContent.IsHumanText(body)) stale.Add(id);
            }
        using var transaction = connection.BeginTransaction();
        foreach (var id in stale) ProjectExecute(transaction, "DELETE FROM evidence WHERE id=$id", ("$id", id));
        foreach (var review in reviews)
        {
            // A replay may already hold the result under its new identity; keep that row only.
            if (review.NewId != review.Id && ProjectScalar(transaction, "SELECT 1 FROM evidence WHERE id=$id", ("$id", review.NewId)) is not null)
            { ProjectExecute(transaction, "DELETE FROM evidence WHERE id=$id", ("$id", review.Id)); continue; }
            ProjectExecute(transaction, "UPDATE evidence SET id=$new,author='assistant',title=$title,body=$body,data_json=$data WHERE id=$old",
                ("$new", review.NewId), ("$old", review.Id), ("$title", review.Title), ("$body", review.Body), ("$data", review.Data));
            ProjectExecute(transaction, "UPDATE corrections SET record_id=$new WHERE record_id=$old", ("$new", review.NewId), ("$old", review.Id));
        }
        if (stale.Count + reviews.Count > 0)
        {
            ProjectExecute(transaction, "DELETE FROM session_corrections WHERE NOT EXISTS(SELECT 1 FROM evidence r WHERE r.source_id=session_corrections.source_id AND r.session_id=session_corrections.session_id)");
            Invalidate(transaction);
        }
        ProjectExecute(transaction, "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$key", HostWrapperRepairKey), ("$value", HostWrapperRepairVersion));
        transaction.Commit();
        return (stale.Count, reviews.Count);
    }

    private object? ProjectScalar(Microsoft.Data.Sqlite.SqliteTransaction transaction, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.Transaction = transaction;
        return command.ExecuteScalar();
    }

    private static (string Id, string NewId, string Title, string Body, string? Data) Relabel(string id, string review, string kind, string? environment, string session, string? dataJson)
    {
        var body = Redactor.Clean(LocalObserver.ReviewBody(review), MemoryLimits.MessageCharacters);
        var title = Redactor.Clean(body.Split('\n')[0], 140);
        JsonObject? data = null;
        try { data = dataJson is null ? null : JsonNode.Parse(dataJson) as JsonObject; } catch (System.Text.Json.JsonException) { }
        var sourceRecordId = data?["sourceRecordId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        data ??= new JsonObject { ["schema"] = "memory-v1" };
        data["phase"] = "final"; data["final"] = true; data["finalSource"] = "review_result";
        if (sourceRecordId is null || environment is null) return (id, id, title, body, data.ToJsonString());
        var turn = LocalObserver.ReviewTurn(sourceRecordId);
        data["turnId"] = turn;
        data["exchangeId"] = LocalObserver.ExchangeIdentity(kind, environment, session, turn);
        return (id, LocalObserver.AssistantIdentity(kind, environment, session, turn), title, body, data.ToJsonString());
    }
}
