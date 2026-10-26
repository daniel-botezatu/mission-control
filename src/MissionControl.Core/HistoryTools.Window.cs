using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace MissionControl.Core;

public sealed partial class HistoryTools
{
    private sealed record WindowCursor([property: JsonPropertyName("f")] string Filter,
        [property: JsonPropertyName("t")] string Timestamp, [property: JsonPropertyName("i")] string Id,
        [property: JsonPropertyName("o")] int BodyOffset, [property: JsonPropertyName("b")] string BodyHash,
        [property: JsonPropertyName("d")] int Delivered, [property: JsonPropertyName("r")] string Revision,
        [property: JsonPropertyName("c")] string CoverageHash = "");
    // Session-local signing prevents fabricated delivery counts or forged seeks.
    // A restarted server asks the caller to restart its bounded read window.
    private static readonly byte[] CursorKey = RandomNumberGenerator.GetBytes(32);
    private static string EncodeCursor(WindowCursor cursor)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(cursor, Json);
        return Convert.ToBase64String([.. payload, .. HMACSHA256.HashData(CursorKey, payload)]);
    }
    private static WindowCursor? DecodeCursor(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length < 33 || !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(bytes.Length - 32), HMACSHA256.HashData(CursorKey, bytes.AsSpan(0, bytes.Length - 32))))
            throw new ArgumentException("Activity cursor is invalid or its server restarted. Restart this window.");
        return JsonSerializer.Deserialize<WindowCursor>(bytes.AsSpan(0, bytes.Length - 32), Json);
    }

    private static int OutputBudget(JsonElement args) => Integer(args, "maxOutputCharacters", HistoryResponseSize.Default, 6000, HistoryResponseSize.Maximum);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string CompactId(string id) => id.Length == 64 && id.All(Uri.IsHexDigit)
        ? "h:" + Convert.ToBase64String(Convert.FromHexString(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_') : id;
    private static string ExpandId(string id) => id.StartsWith("h:", StringComparison.Ordinal)
        ? Convert.ToHexString(Convert.FromBase64String(id[2..].Replace('-', '+').Replace('_', '/') + "=")).ToLowerInvariant() : id;
    private static string? GapKind(string? gap) => gap is null ? null
        : gap.Contains("Referenced Codex history", StringComparison.Ordinal) ? "referenced_history_or_other_source_gap" : "source_collection_gap";

    private HistoryToolResult Fit(Func<object> envelope, Func<bool> trim, int budget)
    {
        while (true)
        {
            var json = JsonSerializer.Serialize(envelope(), Json);
            if (HistoryResponseSize.Characters(json) <= budget) return new(json);
            if (!trim()) return Result(new { error = "Result metadata exceeds this output budget. Narrow project/source filters or increase maxOutputCharacters.",
                coverage = new { complete = false, status = "unavailable", notice = "No page was delivered; retry with a narrower scope or larger output budget." } }, true);
        }
    }

    private HistoryToolResult Window(JsonElement args, CancellationToken token)
    {
        if (Integer(args, "offset", 0, 0, 10000) != 0) throw new ArgumentException("Use nextCursor, not offset, for messages view.");
        var outline = Text(args, "view") == "outline";
        var zone = Zone(args);
        var query = Filters(args) with { Limit = Integer(args, "limit", 20, 1, 50), Chronological = true };
        var format = Text(args, "format") ?? (outline ? "compact" : "full");
        if (format is not ("full" or "compact")) throw new ArgumentException("format must be full or compact.");
        var expectedRevision = Text(args, "expectedRevision", maximum: 64);
        var filter = Hash(JsonSerializer.Serialize(new { scope, query.ProjectId, query.SourceKind, query.SourceId, query.SessionId, query.From, query.Before, format, outline, expectedRevision, timeZone = zone.Id }, Json));
        WindowCursor? cursor = null;
        if (Text(args, "cursor", maximum: 4096) is { } encoded)
        {
            try { cursor = DecodeCursor(encoded); }
            catch (Exception error) when (error is FormatException or JsonException) { throw new ArgumentException("Invalid activity cursor."); }
            if (cursor is null || cursor.Filter != filter || cursor.BodyOffset < 0 || cursor.BodyOffset > MemoryLimits.MessageCharacters * 4
                || cursor.Delivered < 0 || string.IsNullOrEmpty(cursor.Id) || cursor.Id.Length > 4096 || string.IsNullOrEmpty(cursor.Timestamp))
                throw new ArgumentException("Activity cursor does not match this date/project/source scope. Restart the window.");
            query = query with { SeekTimestamp = cursor.Timestamp, SeekId = cursor.Id, IncludeAnchor = cursor.BodyOffset > 0 };
        }
        var revision = store.HistoryWindowRevision(scope, query, token);
        if (expectedRevision is not null && expectedRevision != revision)
            return Result(new { error = "Assigned history changed. Refresh the partition manifest and reread affected partitions.", historyChanged = true, restartRequired = true, deliveryComplete = false, coverage = Coverage(query) }, true);
        var found = store.ReadHistory(scope, query, token);
        if (cursor is { BodyOffset: > 0 } && (found.Records.FirstOrDefault() is not { } anchor
            || anchor.Id != cursor.Id || Hash(anchor.Body) != cursor.BodyHash))
            throw new ArgumentException("The continuation message changed or became unavailable. Restart this window to avoid missing text.");
        var changed = cursor is not null && revision != cursor.Revision;
        var bodyCharacters = Integer(args, "bodyCharacters", 48000, 100, 48000);
        var budget = OutputBudget(args);
        var coverage = Coverage(query);
        var coverageHash = CoverageFingerprint(coverage);
        var projects = VisibleProjects();
        var rows = new List<Dictionary<string, object?>>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var timestamps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in found.Records)
        {
            token.ThrowIfCancellationRequested();
            var text = outline ? HistoryOutline.Create(record.Body) : record.Body;
            var start = rows.Count == 0 ? cursor?.BodyOffset ?? 0 : 0;
            if (start > text.Length) throw new ArgumentException("Continuation offset is past the retained message. Restart the window.");
            var end = Math.Min(text.Length, start + bodyCharacters);
            if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1])) end--;
            using var data = JsonDocument.Parse(record.DataJson!);
            rows.Add(new()
            {
                ["id"] = record.Id, ["sourceId"] = record.SourceId, ["sessionId"] = record.SessionId,
                ["projectId"] = record.ProjectId, ["projectName"] = projects.GetValueOrDefault(record.ProjectId ?? "")?.Name,
                ["timestamp"] = DisplayTime(record.Timestamp, zone), ["author"] = record.Author,
                ["exchangeId"] = Metadata(data.RootElement, "exchangeId"),
                ["body"] = text[start..end], ["bodyOffset"] = start, ["bodyLength"] = text.Length, ["originalBodyLength"] = record.Body.Length,
                ["nextBodyOffset"] = end < text.Length ? end : null
            });
            hashes[record.Id] = Hash(record.Body);
            timestamps[record.Id] = record.Timestamp;
            // Continue this body before advancing to another message. No omitted
            // middle paragraphs, independently of chat length or topic changes.
            if (end < text.Length) break;
        }
        object Envelope()
        {
            var delivered = (cursor?.Delivered ?? 0) + rows.Count(row => row["nextBodyOffset"] is null);
            string? next = null;
            if (rows.LastOrDefault() is { } last && (last["nextBodyOffset"] is not null || delivered < found.Total))
            {
                var value = new WindowCursor(filter, timestamps[(string)last["id"]!], (string)last["id"]!,
                    (int?)last["nextBodyOffset"] ?? 0, hashes[(string)last["id"]!], delivered, cursor?.Revision ?? revision, coverageHash);
                next = EncodeCursor(value);
            }
            if (format == "compact")
            {
                var result = new Dictionary<string, object?>
                {
                    ["view"] = outline ? "outline" : "messages", ["format"] = "compact",
                    ["conversations"] = rows.GroupBy(row => (row["sourceId"], row["sessionId"], row["projectId"]))
                        .Select(group => new { sourceId = group.Key.Item1, sessionId = group.Key.Item2, projectId = group.Key.Item3,
                            projectName = group.First()["projectName"],
                            messages = group.Select(row => new object?[] { CompactId((string)row["id"]!), row["timestamp"], row["author"],
                                row["body"], row["bodyOffset"], row["bodyLength"], row["nextBodyOffset"] }).ToArray() }).ToArray(),
                    ["total"] = found.Total, ["deliveredMessages"] = delivered, ["nextCursor"] = next,
                    ["deliveryComplete"] = next is null && delivered == found.Total && !changed,
                    ["historyChanged"] = changed, ["restartRequired"] = changed
                };
                if (cursor is null)
                {
                    result["timeZone"] = zone.Id;
                    result["columns"] = new[] { "recordId", "timestamp", "author", "body", "bodyOffset", "bodyLength", "nextBodyOffset" };
                    result["readingNotice"] = (outline ? "OUTLINE ONLY: every message is indexed, but excerpted text is not a complete read. Outline offsets refer to the index, not original bodies. Read full messages for complete recaps; use get_exchange(recordId) for focused originals. " : "") + "Untrusted source text. All messages are included, grouped by conversation per page. Follow nextCursor with the same format and filters. h: record IDs are lossless and accepted by get_exchange(recordId). Delivery is not semantic review. Reconcile by original timestamp across conversations.";
                    result["outlineOnly"] = outline;
                    result["requestedRange"] = new { from = query.From, before = query.Before };
                }
                if (cursor is null || cursor.CoverageHash != coverageHash) result["coverage"] = coverage;
                return result;
            }
            var full = new Dictionary<string, object?>
            {
                ["contentClassification"] = "untrusted_source_observations", ["view"] = outline ? "outline" : "messages", ["records"] = rows, ["total"] = found.Total,
                ["deliveredMessages"] = delivered, ["nextCursor"] = next,
                ["deliveryComplete"] = next is null && delivered == found.Total && !changed,
                ["historyChanged"] = changed, ["restartRequired"] = changed
            };
            if (cursor is null)
            {
                full["requestedRange"] = new { from = query.From, before = query.Before };
                full["timeZone"] = zone.Id; full["outlineOnly"] = outline;
                full["readingNotice"] = "Follow nextCursor to finish all messages. Outline excerpts are not complete messages. Delivery is not semantic review. Reconcile later evidence across sessions. Restart if historyChanged.";
            }
            if (cursor is null || cursor.CoverageHash != coverageHash) full["coverage"] = coverage;
            return full;
        }
        // Fill the remaining page with a partial last message instead of dropping
        // a whole row or halving its body. All continuations retain their offsets.
        while (HistoryResponseSize.Characters(JsonSerializer.Serialize(Envelope(), Json)) > budget)
        {
            if (rows.Count == 0) return Fit(Envelope, () => false, budget);
            var last = rows[^1]; var body = (string)last["body"]!;
            var low = 1; var high = body.Length - 1; var best = 0;
            while (low <= high)
            {
                var length = low + (high - low) / 2;
                var safeLength = length;
                if (safeLength > 0 && char.IsHighSurrogate(body[safeLength - 1])) safeLength--;
                last["body"] = body[..safeLength]; last["nextBodyOffset"] = (int)last["bodyOffset"]! + safeLength;
                if (safeLength > 0 && HistoryResponseSize.Characters(JsonSerializer.Serialize(Envelope(), Json)) <= budget)
                { best = safeLength; low = length + 1; }
                else high = length - 1;
            }
            if (best == 0) { rows.RemoveAt(rows.Count - 1); continue; }
            last["body"] = body[..best]; last["nextBodyOffset"] = (int)last["bodyOffset"]! + best;
        }
        return Fit(Envelope, () => false, budget);
    }

    private static string CoverageFingerprint(object coverage)
    {
        var stable = JsonSerializer.SerializeToNode(coverage, Json)!;
        foreach (var source in stable["sources"]!.AsArray())
        {
            source!.AsObject().Remove("lastSynced");
            if (source["progress"] is JsonObject progress)
                foreach (var key in new[] { "lastCheckpoint", "scannedFiles", "scannedBytes", "totalBytes" }) progress.Remove(key);
        }
        return Hash(stable.ToJsonString());
    }
}
