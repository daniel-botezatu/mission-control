using System.Text.Json;

namespace MissionControl.Core;

public sealed partial class HistoryTools
{
    private HistoryToolResult Partitions(JsonElement args, CancellationToken token)
    {
        var query = Filters(args);
        if (query.From is null || query.Before is null || Text(args, "cursor") is not null)
            throw new ArgumentException("Partitions require from/before and use nextOffset, not cursor.");
        var target = Integer(args, "partitionBytes", 80000, 10000, 160000);
        var offset = Integer(args, "offset", 0, 0, int.MaxValue);
        var limit = Integer(args, "limit", 50, 1, 50);
        var zone = Zone(args);
        var revision = store.HistoryWindowRevision(scope, query, token);
        var manifestRevision = Hash(JsonSerializer.Serialize(new { scope, query, target, revision, timeZone = zone.Id }, Json));
        var projects = VisibleProjects();
        var buckets = store.HistoryTimeBuckets(scope, query, token);
        var partitions = new List<object>();
        foreach (var session in buckets.GroupBy(b => (b.SourceId, b.SessionId, b.ProjectId)))
        {
            var rows = session.ToArray();
            var start = 0;
            while (start < rows.Length)
            {
                var end = start;
                long bytes = 0;
                var count = 0;
                // Never separate equal timestamps: half-open time windows then
                // partition every record exactly once, even when IDs interleave.
                do { bytes += rows[end].Bytes; count += rows[end].Messages; end++; }
                while (end < rows.Length && bytes + rows[end].Bytes <= target);
                var from = start == 0 ? query.From : rows[start].Timestamp;
                var before = end == rows.Length ? query.Before : rows[end].Timestamp;
                var slice = query with { SourceId = session.Key.SourceId, SessionId = session.Key.SessionId,
                    ProjectId = session.Key.ProjectId, From = from, Before = before };
                var sliceRevision = store.HistoryWindowRevision(scope, slice, token);
                var read = new Dictionary<string, object?>
                {
                    ["view"] = "messages", ["format"] = "compact", ["sourceId"] = slice.SourceId,
                    ["sessionId"] = slice.SessionId, ["from"] = from, ["before"] = before,
                    ["timeZone"] = zone.Id, ["expectedRevision"] = sliceRevision,
                    ["bodyCharacters"] = 48000, ["maxOutputCharacters"] = 48000, ["limit"] = 50
                };
                if (slice.ProjectId is not null) read["projectId"] = slice.ProjectId;
                if (slice.SourceKind is not null) read["sourceKind"] = slice.SourceKind;
                partitions.Add(new { partitionId = Hash(JsonSerializer.Serialize(read, Json)),
                    sourceId = slice.SourceId, sessionId = slice.SessionId, projectId = slice.ProjectId,
                    projectName = projects.GetValueOrDefault(slice.ProjectId ?? "")?.Name,
                    messageCount = count, bodyBytes = bytes, oversized = bytes > target, readArguments = read });
                start = end;
            }
        }
        var page = partitions.Skip(offset).Take(limit).ToList();
        var coverage = offset == 0 ? Coverage(query) : null;
        var fullRead = new Dictionary<string, object?>
        {
            ["view"] = "messages", ["format"] = "compact", ["from"] = query.From, ["before"] = query.Before,
            ["timeZone"] = zone.Id, ["expectedRevision"] = revision, ["bodyCharacters"] = 48000,
            ["maxOutputCharacters"] = 48000, ["limit"] = 50
        };
        if (query.ProjectId is not null) fullRead["projectId"] = query.ProjectId;
        if (query.SourceId is not null) fullRead["sourceId"] = query.SourceId;
        if (query.SessionId is not null) fullRead["sessionId"] = query.SessionId;
        if (query.SourceKind is not null) fullRead["sourceKind"] = query.SourceKind;
        object Envelope() => new { view = "partitions", manifestRevision, partitions = page,
            total = partitions.Count, totalMessages = buckets.Sum(b => b.Messages), totalBodyBytes = buckets.Sum(b => b.Bytes),
            fullReadArguments = offset == 0 ? fullRead : null,
            nextOffset = offset + page.Count < partitions.Count ? (int?)(offset + page.Count) : null,
            coverage, timeZone = zone.Id, requestedRange = new { from = query.From, before = query.Before },
            notice = "Body-free assignments, not summaries. Read every partition fully, including continuations. Check manifestRevision across pages and once more before merging; changed history requires reassignment. oversized means equal-timestamp messages exceed the target and must still all be read. Bytes are UTF-8 storage size, not tokens. Lifecycle-only sessions remain in view=sessions." };
        return Fit(Envelope, () => { if (page.Count <= 1) return false; page.RemoveAt(page.Count - 1); return true; }, OutputBudget(args));
    }
}
