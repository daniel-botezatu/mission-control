namespace MissionControl.Core;

internal sealed record HistoryTimeBucket(string SourceId, string SessionId, string? ProjectId,
    string Timestamp, int Messages, long Bytes);

public sealed partial class WorkspaceStore
{
    internal IReadOnlyList<HistoryTimeBucket> HistoryTimeBuckets(HistoryScope scope, HistoryQuery query, CancellationToken token)
    {
        lock (gate)
        {
            var (where, parameters, _) = HistoryPredicate(scope, query);
            using var command = Command($"SELECT r.source_id,r.session_id,({EffectiveProject}) AS effective_project,r.timestamp,COUNT(*),SUM(length(CAST(r.body AS BLOB))) FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id{where} GROUP BY r.source_id,r.session_id,effective_project,r.timestamp ORDER BY r.source_id,r.session_id,effective_project,r.timestamp", parameters.ToArray());
            command.CommandTimeout = 10;
            using var cancel = token.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            var result = new List<HistoryTimeBucket>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                result.Add(new(reader.GetString(0), reader.GetString(1), NullableString(reader, 2), reader.GetString(3), reader.GetInt32(4), reader.GetInt64(5)));
            }
            return result;
        }
    }
}
