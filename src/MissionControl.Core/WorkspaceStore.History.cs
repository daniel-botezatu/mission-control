using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MissionControl.Core;

public sealed partial class WorkspaceStore
{
    // Safe source extraction is an admission condition, not an optional display filter.
    private const string MemoryMessages = "r.kind='message' AND lower(r.author) IN ('user','assistant') AND CASE WHEN json_valid(r.data_json) THEN json_extract(r.data_json,'$.schema')='memory-v1' AND (lower(r.author)='user' OR (json_extract(r.data_json,'$.final')=1 AND json_extract(r.data_json,'$.phase')='final')) ELSE 0 END";
    private const string MemoryLifecycle = "r.kind='lifecycle' AND CASE WHEN json_valid(r.data_json) THEN json_extract(r.data_json,'$.schema')='memory-v1' ELSE 0 END";

    internal QueryResult ReadHistory(HistoryScope scope, HistoryQuery query, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var (where, parameters, match) = HistoryPredicate(scope, query);
            var joins = " FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id";
            if (match is not null) joins += " JOIN evidence_fts ON evidence_fts.rowid=r.rowid";
            using var count = Command("SELECT COUNT(*)" + joins + where, parameters.ToArray());
            count.CommandTimeout = 10;
            using var cancelCount = token.Register(count.Cancel);
            var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (total == 0 && query.Mode == "auto" && query.Query is not null)
            {
                var (visible, visibleParameters, _) = HistoryPredicate(scope, query with { Query = null });
                var corrected = CorrectSearch(query.Query, visible, visibleParameters.ToArray());
                if (corrected is not null) return ReadHistory(scope, query with { Query = corrected, Mode = "all" }, token)
                    with { SearchNotice = "No exact match. Used spelling-tolerant search; verify the returned excerpts." };
            }
            parameters.Add(("$historyLimit", query.Limit));
            parameters.Add(("$historyOffset", query.Offset));
            // Count describes the complete requested window, while a continuation
            // seeks by original timestamp/ID so new records cannot shift offsets.
            if (query.SeekTimestamp is not null && query.SeekId is not null)
            {
                where += " AND (r.timestamp>$seekTime OR (r.timestamp=$seekTime AND r.id" + (query.IncludeAnchor ? ">=" : ">") + "$seekId))";
                parameters.Add(("$seekTime", query.SeekTimestamp)); parameters.Add(("$seekId", query.SeekId));
            }
            var order = query.Chronological ? "r.timestamp,r.id" : match is null ? "r.timestamp DESC,r.id" : "bm25(evidence_fts,2.0,1.0),r.timestamp DESC,r.id";
            using var command = Command($"SELECT {Projection}{joins}{where} ORDER BY {order} LIMIT $historyLimit OFFSET $historyOffset", parameters.ToArray());
            command.CommandTimeout = 10;
            using var cancel = token.Register(command.Cancel);
            List<EvidenceRecord> records = [];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                records.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), NullableString(reader, 4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11), NullableString(reader, 12), NullableString(reader, 13), NullableString(reader, 14), NullableString(reader, 15), NullableString(reader, 16), NullableString(reader, 17), NullableString(reader, 18)));
            }
            token.ThrowIfCancellationRequested();
            return new(records, total);
        }
    }

    internal HistorySessionPage ReadHistorySessions(HistoryScope scope, HistoryQuery query, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var (where, parameters, _) = HistoryPredicate(scope, query with { Query = null, IncludeLifecycle = true });
            var cte = $"WITH visible AS (SELECT r.*,({EffectiveProject}) AS effective_project FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id{where}), sessions AS (SELECT source_id,session_id,source_kind,effective_project,MIN(timestamp) AS first_timestamp,MAX(timestamp) AS last_timestamp,SUM(kind='message') AS messages,SUM(kind='message' AND lower(author)='user') AS users,SUM(kind='message' AND lower(author)='assistant') AS assistants FROM visible GROUP BY source_id,session_id,source_kind,effective_project) ";
            using var count = Command(cte + "SELECT COUNT(*) FROM sessions", parameters.ToArray());
            count.CommandTimeout = 10;
            using var cancelCount = token.Register(count.Cancel);
            var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            parameters.Add(("$historyLimit", query.Limit)); parameters.Add(("$historyOffset", query.Offset));
            using var command = Command(cte + """
                SELECT s.*,
                    (SELECT branch FROM visible v WHERE v.source_id=s.source_id AND v.session_id=s.session_id AND v.effective_project IS s.effective_project AND v.branch IS NOT NULL ORDER BY timestamp DESC,id LIMIT 1),
                    (SELECT working_directory FROM visible v WHERE v.source_id=s.source_id AND v.session_id=s.session_id AND v.effective_project IS s.effective_project ORDER BY timestamp DESC,id LIMIT 1),
                    (SELECT state FROM visible v WHERE v.source_id=s.source_id AND v.session_id=s.session_id AND v.effective_project IS s.effective_project AND v.kind='lifecycle' ORDER BY timestamp DESC,id LIMIT 1),
                    (SELECT id FROM visible v WHERE v.source_id=s.source_id AND v.session_id=s.session_id AND v.effective_project IS s.effective_project AND v.kind='lifecycle' ORDER BY timestamp DESC,id LIMIT 1),
                    (SELECT timestamp FROM visible v WHERE v.source_id=s.source_id AND v.session_id=s.session_id AND v.effective_project IS s.effective_project AND v.kind='lifecycle' ORDER BY timestamp DESC,id LIMIT 1)
                FROM sessions s ORDER BY last_timestamp DESC,source_id,session_id,effective_project LIMIT $historyLimit OFFSET $historyOffset
                """, parameters.ToArray());
            command.CommandTimeout = 10;
            using var cancel = token.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            List<HistorySession> sessions = [];
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                sessions.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), NullableString(reader, 9), NullableString(reader, 10), NullableString(reader, 11), NullableString(reader, 12), NullableString(reader, 13)));
            }
            return new(sessions, total);
        }
    }

    internal string HistoryWindowRevision(HistoryScope scope, HistoryQuery query, CancellationToken token)
    {
        lock (gate)
        {
            var (where, parameters, _) = HistoryPredicate(scope, query with { Query = null });
            // Timestamps/length totals miss equal-length edits and parser replays
            // which intentionally preserve changed_at. Hash the visible contents
            // incrementally, without concatenating an unbounded window in memory.
            using var command = Command("SELECT r.id,r.source_id,r.session_id,r.project_id,r.source_kind,r.timestamp,r.author,r.body,r.data_json FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id" + where + " ORDER BY r.timestamp,r.id", parameters.ToArray());
            command.CommandTimeout = 10;
            using var cancel = token.Register(command.Cancel);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var reader = command.ExecuteReader();
            var prefix = new byte[4];
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var bytes = reader.IsDBNull(i) ? null : Encoding.UTF8.GetBytes(reader.GetString(i));
                    BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes?.Length ?? -1); hash.AppendData(prefix);
                    if (bytes is not null) hash.AppendData(bytes);
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }

    internal IReadOnlyList<object> HistoryGapSessions(HistoryScope scope, HistoryQuery query)
    {
        lock (gate)
        {
            var (where, parameters, _) = HistoryPredicate(scope, query with { Query = null, IncludeLifecycle = true });
            // A Codex continuation's referenced history counts as complete once the collector recorded that the earlier
            // thread was read far enough, or lies outside the lookback; the dashboard applies the same rule.
            const string gap = "CASE WHEN json_valid(cp.metadata_json) AND NOT (COALESCE(json_extract(cp.metadata_json,'$.Gap'),json_extract(cp.metadata_json,'$.gap')) LIKE 'Referenced Codex history:%'"
                + " AND (COALESCE(json_extract(cp.metadata_json,'$.HistoryBaseRead'),json_extract(cp.metadata_json,'$.historyBaseRead'),0)=1"
                + " OR COALESCE(json_extract(cp.metadata_json,'$.HistoryBaseOutsideLookback'),json_extract(cp.metadata_json,'$.historyBaseOutsideLookback'),0)=1))"
                + " THEN COALESCE(json_extract(cp.metadata_json,'$.Gap'),json_extract(cp.metadata_json,'$.gap')) END";
            using var command = Command("SELECT DISTINCT r.source_id,r.session_id,r.project_id," + gap
                + " FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id JOIN checkpoints cp ON cp.source_id=r.source_id AND cp.stream_id=r.stream_id"
                + where + " AND " + gap + " IS NOT NULL ORDER BY r.source_id,r.session_id,r.project_id LIMIT 101", parameters.ToArray());
            using var reader = command.ExecuteReader();
            List<object> result = [];
            while (reader.Read())
            {
                var message = reader.GetString(3);
                if (message == "Source file was truncated, replaced, or rewritten; available complete records were replayed safely.") continue;
                result.Add(new { sourceId = reader.GetString(0), sessionId = reader.GetString(1), projectId = NullableString(reader, 2),
                    kind = message.Contains("Referenced Codex history", StringComparison.Ordinal) ? "referenced_history" : "source_record_gap",
                    missingMessageDates = "unknown" });
            }
            return result;
        }
    }

    private static (string Where, List<(string, object?)> Parameters, string? Match) HistoryPredicate(HistoryScope scope, HistoryQuery query)
    {
        // Predicate is the shared storage privacy boundary: it checks current approval and cwd.
        var (approved, baseParameters) = Predicate(new());
        var terms = new List<string> { approved.Length == 0 ? "0=1" : approved[6..], "r.source_kind IN ('claude','codex')", "r.state IS NOT 'deleted'", query.IncludeLifecycle ? $"(({MemoryMessages}) OR ({MemoryLifecycle}))" : MemoryMessages };
        var parameters = baseParameters.ToList();
        void Exact(string field, string parameter, string? value)
        {
            if (value is null) return;
            terms.Add(field + "=" + parameter); parameters.Add((parameter, value));
        }
        Exact("(" + EffectiveProject + ")", "$historyScopeProject", scope.ProjectId);
        Exact("(" + EffectiveProject + ")", "$historyProject", query.ProjectId);
        Exact("r.source_id", "$historyScopeSource", scope.SourceId); Exact("r.source_id", "$historySource", query.SourceId);
        Exact("r.session_id", "$historyScopeSession", scope.SessionId); Exact("r.session_id", "$historySession", query.SessionId);
        Exact("r.source_kind", "$historyKind", query.SourceKind);
        Exact("json_extract(r.data_json,'$.exchangeId')", "$historyExchange", query.ExchangeId);
        void Time(string? value, string name, string comparison)
        {
            if (value is null) return;
            terms.Add("r.timestamp" + comparison + name); parameters.Add((name, Timestamp(value)));
        }
        Time(scope.Since, "$historyScopeFrom", ">="); Time(scope.Until, "$historyScopeBefore", "<");
        Time(query.From, "$historyFrom", ">="); Time(query.Before, "$historyBefore", "<");
        string? match = null;
        if (query.Query is not null)
        {
            // Quote every token: user text never becomes raw FTS operators or column selectors.
            var words = Regex.Matches(query.Query, @"[\p{L}\p{N}_]+", RegexOptions.None, TimeSpan.FromMilliseconds(50)).Select(m => m.Value[..Math.Min(64, m.Length)]).Take(16).ToArray();
            match = query.Mode == "phrase" ? "\"" + string.Join(' ', words) + "\"" : string.Join(query.Mode == "any" ? " OR " : " AND ", words.Distinct(StringComparer.OrdinalIgnoreCase).Select(word => query.Mode == "auto" ? HistorySearch.Alternatives(word) : HistorySearch.Term(word)));
            if (words.Length == 0) { terms.Add("0=1"); match = null; }
            else { terms.Add("evidence_fts MATCH $historyFts"); parameters.Add(("$historyFts", match)); }
        }
        return (" WHERE " + string.Join(" AND ", terms), parameters, match);
    }
}
