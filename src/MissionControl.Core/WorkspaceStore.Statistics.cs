using System.Globalization;

namespace MissionControl.Core;

public sealed partial class WorkspaceStore
{
    /// <summary>Body-free counters for the visible dashboard; no collector or external requests are started.</summary>
    public CollectionStatistics Statistics(string monitoring)
    {
        lock (gate)
        {
            var entries = 0; var sessions = 0; var users = 0; var finals = 0; var events = 0;
            using (var command = Command($"""
                SELECT COUNT(*),SUM(r.kind='message' AND r.author='user'),
                    SUM(r.kind='message' AND r.author='assistant'),SUM(r.kind='lifecycle')
                FROM evidence r WHERE {ApprovalPredicate} GROUP BY r.source_id,r.session_id
                """))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    sessions++; entries += reader.GetInt32(0); users += reader.GetInt32(1);
                    finals += reader.GetInt32(2); events += reader.GetInt32(3);
                }
            var pageSize = Convert.ToInt64(Scalar("PRAGMA page_size"), CultureInfo.InvariantCulture);
            var pages = Convert.ToInt64(Scalar("PRAGMA page_count"), CultureInfo.InvariantCulture);
            var free = Convert.ToInt64(Scalar("PRAGMA freelist_count"), CultureInfo.InvariantCulture);
            var sources = GetStatuses().Where(source => source.Enabled).ToArray();
            return new(monitoring, entries, sessions, users, finals, events,
                DatabaseBytesUnsafe(), Math.Max(0, pages - free) * pageSize, StorageLimitBytes,
                Scalar("SELECT MAX(updated_at) FROM checkpoints") as string,
                sources.Any(source => source.State is "catching-up" or "reconciling" || source.Pending > 0),
                sources.Sum(source => source.Pending), sources.Sum(source => source.Progress?.PendingBytes ?? 0),
                sources.Sum(source => source.Progress?.ScannedBytes ?? 0), sources.Sum(source => source.Progress?.TotalBytes ?? 0));
        }
    }
}
