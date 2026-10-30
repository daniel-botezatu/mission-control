using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MissionControl.Core;

public sealed class StorageCapacityException() : InvalidOperationException(
    "Workspace storage budget reached. Collection is suspended without advancing checkpoints. Increase the budget or remove old data in Settings, then resume.");

public sealed partial class WorkspaceStore
{
    public int LookbackDays => Math.Clamp(int.TryParse(GetSetting("collection.lookbackDays"), out var days) ? days : 30, 1, 36500);
    // Delegated prompts are not human requests. Index the parent conversation only.
    public bool IncludeSubagents => false;

    public string? MatchProject(string? cwd)
    {
        lock (gate) return MatchProjectUnsafe(cwd, null);
    }

    private void ReassignProjects()
    {
        using var transaction = connection.BeginTransaction();
        ReassignProjectEvidenceUnsafe(transaction);
        Invalidate(transaction);
        transaction.Commit();
    }

    public void SaveSessionProject(string sourceId, string sessionId, string? projectId)
    {
        ValidateIdentity(sourceId, nameof(sourceId)); ValidateIdentity(sessionId, nameof(sessionId));
        if (projectId is not null) ValidateIdentity(projectId, nameof(projectId));
        lock (gate)
        {
            if (Scalar("SELECT 1 FROM evidence WHERE source_id=$source AND session_id=$session LIMIT 1", ("$source", sourceId), ("$session", sessionId)) is null)
                throw new ArgumentException("This session no longer exists.");
            using var transaction = connection.BeginTransaction();
            using var command = Command(projectId is null
                ? "DELETE FROM session_corrections WHERE source_id=$source AND session_id=$session"
                : "INSERT INTO session_corrections(source_id,session_id,project_id) VALUES($source,$session,$project) ON CONFLICT(source_id,session_id) DO UPDATE SET project_id=excluded.project_id",
                ("$source", sourceId), ("$session", sessionId), ("$project", projectId));
            command.Transaction = transaction; command.ExecuteNonQuery();
            Invalidate(transaction); transaction.Commit();
        }
    }

    /// <summary>
    /// Filter and group conversations before pagination. Rows carry the newest activity identity
    /// and update time, with a human request title/preview instead of an individual tool event.
    /// </summary>
    public QueryResult QuerySessions(QueryOptions options, bool fuzzy = true)
    {
        lock (gate)
        {
            var (where, parameters) = Predicate(options);
            where += (where.Length == 0 ? "WHERE " : " AND ") + "r.source_kind IN ('claude','codex')";
            var from = " FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id " + where;
            var total = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM (SELECT r.source_id,r.session_id" + from + " GROUP BY r.source_id,r.session_id)", parameters), CultureInfo.InvariantCulture);
            if (total == 0 && fuzzy && !string.IsNullOrWhiteSpace(options.Search))
            {
                var (visible, visibleParameters) = Predicate(options with { Search = null });
                if (CorrectSearch(options.Search, visible, visibleParameters) is { } corrected)
                    return QuerySessions(options with { Search = corrected }, fuzzy: false) with { SearchNotice = "Used spelling-tolerant search. Verify matching excerpts." };
            }
            var bounds = parameters.Concat(new (string, object?)[] { ("$limit", Math.Clamp(options.Limit, 1, 100)), ("$offset", Math.Max(0, options.Offset)) }).ToArray();
            // Search selects conversations; their current activity still determines ordering.
            // Keep project/source scoping when looking beyond the matching message.
            var (scope, _) = Predicate(options with { Search = null, Since = null, Person = null, Kind = null, State = null });
            scope += (scope.Length == 0 ? "WHERE " : " AND ") + "r.source_kind IN ('claude','codex')";
            var summaryProjection = Projection.Replace("r.body", "substr(r.body,1,512) AS body").Replace("r.data_json", "NULL AS data_json");
            var records = ReadRecords($"WITH matching AS (SELECT r.source_id,r.session_id{from} GROUP BY r.source_id,r.session_id), ranked AS (SELECT {summaryProjection},ROW_NUMBER() OVER(PARTITION BY r.source_id,r.session_id ORDER BY r.changed_at DESC,r.id) AS position FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id JOIN matching m ON m.source_id=r.source_id AND m.session_id=r.session_id {scope}) SELECT {string.Join(',', RecordColumns)} FROM ranked WHERE position=1 ORDER BY changed_at DESC,id LIMIT $limit OFFSET $offset", bounds);
            var summaries = records.Select(record =>
            {
                var scoped = options with { Search = null, Since = null, Person = null, Kind = "message", State = null, SourceId = record.SourceId, SessionId = record.SessionId };
                var (humanWhere, humanParameters) = Predicate(scoped);
                humanWhere += " AND lower(r.author)='user' AND trim(r.body)<>''";
                var humanFrom = " FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id " + humanWhere;
                var title = Scalar("SELECT COALESCE(NULLIF(trim(r.title),''),substr(r.body,1,160))" + humanFrom + " ORDER BY r.timestamp,r.id LIMIT 1", humanParameters) as string;
                var preview = Scalar("SELECT substr(r.body,1,512)" + humanFrom + " ORDER BY r.timestamp DESC,r.id LIMIT 1", humanParameters) as string;
                return record with
                {
                    Title = string.IsNullOrWhiteSpace(title) ? (record.SourceKind == "claude" ? "Claude conversation" : "Codex conversation") : Redactor.Clean(title, 160),
                    Body = preview ?? "No human message is available in the retained conversation."
                };
            }).ToArray();
            return new(summaries, total);
        }
    }

    public UserCorrection? GetCorrection(string id) => GetCorrections([id]).FirstOrDefault();

}
