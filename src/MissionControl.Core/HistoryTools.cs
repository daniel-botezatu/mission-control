using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MissionControl.Core;

/// <summary>Small read-only memory API. It returns observations; reasoning belongs to the calling host.</summary>
public sealed partial class HistoryTools(WorkspaceStore store, HistoryScope? restrictions = null, TimeZoneInfo? defaultTimeZone = null)
{
    private readonly HistoryScope scope = restrictions ?? new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static IReadOnlyList<HistoryToolDefinition> Definitions { get; } = CreateDefinitions();

    public Task<HistoryToolResult> CallAsync(string name, JsonElement arguments, CancellationToken token = default)
        => Task.Run(() => Dispatch(name, arguments, token), token);

    private HistoryToolResult Dispatch(string name, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var definition = Definitions.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException("Unknown history tool.");
            if (args.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(args.GetRawText()) > 16384)
                throw new ArgumentException("Arguments must be an object of at most 16 KiB.");
            var schema = definition.InputSchema;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in args.EnumerateObject())
                if (!seen.Add(property.Name) || !schema.GetProperty("properties").TryGetProperty(property.Name, out _))
                    throw new ArgumentException("Unknown or duplicate history tool argument.");
            foreach (var required in schema.GetProperty("required").EnumerateArray())
                if (!args.TryGetProperty(required.GetString()!, out _)) throw new ArgumentException("A required history tool argument is missing.");
            var result = store.ReadSnapshot(() => name switch
            {
                "search_history" => Search(args, token),
                "get_session_digest" => Digest(args, token),
                "get_exchange" => Exchange(args, token),
                "get_project_activity" => Activity(args, token),
                "get_session_details" => Details(args, token),
                _ => throw new ArgumentException("Unknown history tool.")
            });
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or SqliteException)
        {
            token.ThrowIfCancellationRequested();
            return Result(new { error = error is SqliteException ? "History could not be read. Narrow the query or retry after indexing completes." : error.Message, coverage = Coverage() }, true);
        }
    }

    private HistoryToolResult Search(JsonElement args, CancellationToken token)
    {
        var query = Filters(args) with { Query = Text(args, "query", required: true, maximum: 512), Mode = Text(args, "mode") ?? "auto", Limit = Integer(args, "limit", 10, 1, 30), Offset = Integer(args, "offset", 0, 0, 10000) };
        if (query.Mode is not ("auto" or "all" or "any" or "phrase")) throw new ArgumentException("Search mode must be auto, all, any, or phrase.");
        return Page(store.ReadHistory(scope, query, token), query.Offset, Integer(args, "excerptCharacters", 800, 100, 2000), 0, token, OutputBudget(args), Zone(args));
    }

    private HistoryToolResult Digest(JsonElement args, CancellationToken token)
    {
        var order = Text(args, "order") ?? "asc";
        if (order is not ("asc" or "desc")) throw new ArgumentException("Digest order must be asc or desc.");
        var query = Filters(args) with { SourceId = Text(args, "sourceId", true), SessionId = Text(args, "sessionId", true), Limit = Integer(args, "limit", 20, 1, 50), Offset = Integer(args, "offset", 0, 0, 10000), Chronological = order == "asc" };
        return Page(store.ReadHistory(scope, query, token), query.Offset, Integer(args, "excerptCharacters", 800, 100, 2000), 0, token, OutputBudget(args), Zone(args));
    }

    private HistoryToolResult Exchange(JsonElement args, CancellationToken token)
    {
        var exchangeId = Text(args, "exchangeId");
        var recordId = Text(args, "recordId");
        if ((exchangeId is null) == (recordId is null)) throw new ArgumentException("Provide exactly one of exchangeId or recordId.");
        if (recordId is not null)
        {
            var record = store.GetRecord(ExpandId(recordId)) ?? throw new ArgumentException("No accessible record matches this ID.");
            using var metadata = JsonDocument.Parse(record.DataJson ?? "{}");
            exchangeId = Metadata(metadata.RootElement, "exchangeId") ?? throw new ArgumentException("This record has no retained exchange.");
            // Resolve in the original session; the caller can only narrow it further.
            if (Text(args, "sourceId") is { } source && source != record.SourceId || Text(args, "sessionId") is { } session && session != record.SessionId)
                throw new ArgumentException("Record does not match the requested session.");
            var resolved = new HistoryQuery(ExchangeId: exchangeId, SourceId: record.SourceId, SessionId: record.SessionId,
                Limit: Integer(args, "limit", 10, 1, 20), Offset: Integer(args, "offset", 0, 0, 10000), Chronological: true);
            return Page(store.ReadHistory(scope, resolved, token), resolved.Offset, Integer(args, "bodyCharacters", 12000, 100, 48000), Integer(args, "bodyOffset", 0, 0, MemoryLimits.MessageCharacters + 12), token, OutputBudget(args), Zone(args));
        }
        var query = new HistoryQuery(ExchangeId: exchangeId, SourceId: Text(args, "sourceId"), SessionId: Text(args, "sessionId"), Limit: Integer(args, "limit", 10, 1, 20), Offset: Integer(args, "offset", 0, 0, 10000), Chronological: true);
        return Page(store.ReadHistory(scope, query, token), query.Offset, Integer(args, "bodyCharacters", 12000, 100, 48000), Integer(args, "bodyOffset", 0, 0, MemoryLimits.MessageCharacters + 12), token, OutputBudget(args), Zone(args));
    }

    private HistoryToolResult Activity(JsonElement args, CancellationToken token)
    {
        var view = Text(args, "view") ?? "sessions";
        if (view == "partitions") return Partitions(args, token);
        if (view is "messages" or "outline") return Window(args, token);
        if (view != "sessions") throw new ArgumentException("Activity view must be sessions, partitions, messages or outline.");
        if (Text(args, "cursor") is not null) throw new ArgumentException("A cursor is only supported in messages view.");
        var query = Filters(args) with { Limit = Integer(args, "limit", 20, 1, 50), Offset = Integer(args, "offset", 0, 0, int.MaxValue) };
        var found = store.ReadHistorySessions(scope, query, token);
        var projects = VisibleProjects();
        var sessions = found.Sessions.Select(s => Session(s, projects, Zone(args))).ToList();
        var coverage = Coverage(query);
        object Envelope() => new { contentClassification = "untrusted_source_observations", sessions, total = found.Total, nextOffset = query.Offset + sessions.Count < found.Total ? (int?)(query.Offset + sessions.Count) : null, coverage, timeZone = Zone(args).Id, requestedRange = new { from = query.From, before = query.Before }, timeSemantics = "Original source timestamps within the requested range, not whole-chat dates. Lifecycle states are not verified completion." };
        return Fit(Envelope, () => { if (sessions.Count <= 1) return false; sessions.RemoveAt(sessions.Count - 1); return true; }, OutputBudget(args));
    }

    private HistoryToolResult Details(JsonElement args, CancellationToken token)
    {
        var query = Filters(args) with { SourceId = Text(args, "sourceId", true), SessionId = Text(args, "sessionId", true), Limit = 50 };
        var found = store.ReadHistorySessions(scope, query, token);
        if (found.Total == 0) return Result(new { error = "No accessible history matches this session. Check approved projects and collection coverage; excluded history is not exposed.", coverage = Coverage() }, true);
        var projects = VisibleProjects();
        // A session can visit several approved projects. Each partition remains independently scoped.
        var source = store.GetStatuses().FirstOrDefault(s => s.Id == query.SourceId && s.Kind is "claude" or "codex");
        var result = Result(new
        {
            contentClassification = "untrusted_source_observations",
            sessions = found.Sessions.Select(s => Session(s, projects, Zone(args))).ToArray(),
            projects = found.Sessions.Select(s => s.ProjectId).OfType<string>().Distinct(StringComparer.Ordinal)
                .Where(projects.ContainsKey).Select(id => projects[id]).Select(project => new
                {
                    project.Id, project.Name, project.RootPath,
                    repositories = (project.Repositories ?? []).Select(repository => new
                    {
                        repository.LocalRoot,
                        repository.GitHubRepositories,
                        urls = repository.GitHubRepositories.Select(identity => "https://github.com/" + identity).ToArray()
                    }).ToArray()
                }).ToArray(),
            // A source spans approved and excluded sessions. Its diagnostic may
            // mention an excluded filename; expose only a generic coverage flag.
            coverage = Coverage(),
            limitations = new[] { "Only approved project paths and retained user messages/final assistant responses are exposed.", "Assistant statements and last-observed lifecycle events are not independent verification of completion.", "Unavailable source messages cannot be reconstructed." }
        });
        return HistoryResponseSize.Characters(result.Json) <= OutputBudget(args) ? result
            : Fit(() => new { error = "Session metadata is too large. Narrow projectId or increase maxOutputCharacters.", coverage = Coverage() }, () => false, OutputBudget(args)) with { IsError = true };
    }

    private HistoryToolResult Page(QueryResult found, int offset, int bodyCharacters, int bodyOffset, CancellationToken token, int budget, TimeZoneInfo zone)
    {
        var projects = VisibleProjects();
        List<object> records = [];
        foreach (var record in found.Records)
        {
            token.ThrowIfCancellationRequested();
            using var metadata = JsonDocument.Parse(record.DataJson!);
            var data = metadata.RootElement;
            var start = Math.Min(bodyOffset, record.Body.Length);
            if (start > 0 && start < record.Body.Length && char.IsLowSurrogate(record.Body[start]) && char.IsHighSurrogate(record.Body[start - 1])) start--;
            var length = Math.Min(bodyCharacters, record.Body.Length - start);
            if (length > 0 && start + length < record.Body.Length && char.IsHighSurrogate(record.Body[start + length - 1])) length--;
            var body = record.Body.Substring(start, length);
            records.Add(new
            {
                record.Id, record.SourceId, record.SessionId, record.ProjectId,
                projectName = projects.GetValueOrDefault(record.ProjectId ?? "")?.Name,
                record.SourceKind, timestamp = DisplayTime(record.Timestamp, zone), record.Author, record.Title, body,
                exchangeId = Metadata(data, "exchangeId"), turnId = Metadata(data, "turnId"),
                final = data.TryGetProperty("final", out var final) && final.ValueKind == JsonValueKind.True,
                finalSource = Metadata(data, "finalSource"), record.Branch,
                reference = new { recordId = record.Id, record.SourceId, record.SessionId, record.StreamId, sourceRecordId = Metadata(data, "sourceRecordId"), sourceByteOffset = data.TryGetProperty("sourceByteOffset", out var byteOffset) && byteOffset.ValueKind == JsonValueKind.Number && byteOffset.TryGetInt64(out var number) ? (long?)number : null },
                bodyOffset = start, bodyLength = record.Body.Length,
                nextBodyOffset = start + length < record.Body.Length ? (int?)(start + length) : null
            });
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(records, Json)) > 60000)
            {
                records.RemoveAt(records.Count - 1);
                break;
            }
        }
        // Rebuild a too-large single record with a smaller body so paging always
        // makes progress, including long JSON-escaped/non-ASCII messages.
        var coverage = Coverage();
        object Envelope() => new { contentClassification = "untrusted_source_observations", records, total = found.Total, nextOffset = offset + records.Count < found.Total ? (int?)(offset + records.Count) : null, coverage, searchNotice = found.SearchNotice, timeZone = zone.Id, timeSemantics = "Original source timestamps; from inclusive, before exclusive.", limitations = "Excerpts are not complete messages. Follow nextBodyOffset with get_exchange. Assistant statements are unverified observations." };
        if (records.Count == 1 && HistoryResponseSize.Characters(JsonSerializer.Serialize(Envelope(), Json)) > budget && bodyCharacters > 100)
            return Page(found, offset, Math.Max(100, bodyCharacters / 2), bodyOffset, token, budget, zone);
        var fitted = Fit(Envelope, () => { if (records.Count <= 1) return false; records.RemoveAt(records.Count - 1); return true; }, budget);
        return fitted.IsError && bodyCharacters > 100 ? Page(found, offset, Math.Max(100, bodyCharacters / 2), bodyOffset, token, budget, zone) : fitted;
    }

    private object Coverage(HistoryQuery? query = null)
    {
        query ??= new();
        var sources = store.GetStatuses().Where(s => s.Kind is "claude" or "codex" && (scope.SourceId is null || s.Id == scope.SourceId)
            && (query.SourceId is null || s.Id == query.SourceId) && (query.SourceKind is null || s.Kind == query.SourceKind)).ToArray();
        var affected = store.HistoryGapSessions(scope, query);
        var selected = sources.Where(s => s.Enabled).ToArray();
        var complete = selected.Length > 0 && selected.All(s => s.State is "current" or "synced" && s.Pending == 0 && s.Gap is null);
        var projects = VisibleProjects().Values.OrderBy(p => p.Name).ToArray();
        return new
        {
            complete, status = complete ? "current" : "partial",
            notice = complete ? "Selected sources are caught up. Coverage is limited to approved projects and retained user/final messages."
                : "History coverage is partial. Empty results do not prove no work occurred. Check source progress and approved projects.",
            approvedProjects = projects.Take(12).Select(p => new { p.Id, p.Name }).ToArray(), approvedProjectCount = projects.Length,
            approvedProjectsOmitted = Math.Max(0, projects.Length - 12), sourceCount = sources.Length,
            sources = sources.Take(12).Select(s => new { s.Id, s.Kind, s.Enabled, s.State, s.LastSynced, s.Pending,
                progress = s.Progress, gap = s.Gap is null ? null : "Source has a collection gap; inspect diagnostics in Mission Control.",
                gapKind = GapKind(s.Gap), requestedRangeImpact = s.Gap is null ? "none_reported" : "unknown" }).ToArray(),
            sourcesOmitted = Math.Max(0, sources.Length - 12),
            affectedSessions = affected.Take(12).ToArray(), affectedSessionsOmitted = Math.Max(0, affected.Count - 12),
            scopeNotice = "Only approved projects are in scope. Affected sessions match the requested window and operator scope; missing-message dates may still be unknown. Deliberate exclusions are not gaps."
        };
    }

    private HistoryQuery Filters(JsonElement args)
    {
        var projectId = Text(args, "projectId");
        var projectName = Text(args, "projectName", maximum: 200);
        if (projectId is not null && projectName is not null) throw new ArgumentException("Use projectId or projectName, not both.");
        if (projectName is not null)
        {
            var matches = VisibleProjects().Values.Where(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Project name is unavailable or ambiguous; use an approved project ID from a search result.");
            projectId = matches[0].Id;
        }
        var sourceKind = Text(args, "sourceKind");
        if (sourceKind is not null and not ("claude" or "codex")) throw new ArgumentException("sourceKind must be claude or codex.");
        var from = Date(args, "from"); var before = Date(args, "before");
        if (from is not null && before is not null && string.CompareOrdinal(from, before) >= 0) throw new ArgumentException("before must be after from.");
        return new(ProjectId: projectId, SourceKind: sourceKind, SourceId: Text(args, "sourceId"), SessionId: Text(args, "sessionId"), From: from, Before: before);
    }

    private Dictionary<string, ProjectInfo> VisibleProjects() => store.GetProjects().Where(p => p.IsApproved && (scope.ProjectId is null || p.Id == scope.ProjectId)).ToDictionary(p => p.Id, StringComparer.Ordinal);

    private static object Session(HistorySession session, IReadOnlyDictionary<string, ProjectInfo> projects, TimeZoneInfo zone) => new
    {
        session.SourceId, session.SessionId, session.SourceKind, session.ProjectId,
        projectName = projects.GetValueOrDefault(session.ProjectId ?? "")?.Name,
        firstTimestamp = DisplayTime(session.FirstTimestamp, zone), lastTimestamp = DisplayTime(session.LastTimestamp, zone), session.Messages, session.UserMessages, session.AssistantResponses,
        session.Branch, session.WorkingDirectory,
        lastObserved = session.LastObservedState is null ? null : new { state = session.LastObservedState, recordId = session.StateRecordId, timestamp = DisplayTime(session.StateTimestamp, zone), verifiedCompletion = false }
    };

    private static string? Metadata(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Text(JsonElement args, string name, bool required = false, int maximum = 4096)
    {
        if (!args.TryGetProperty(name, out var value)) { if (required) throw new ArgumentException("A required history tool argument is missing."); return null; }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximum || value.GetString() == "null")
            throw new ArgumentException("Text arguments must be nonempty strings within their documented limits; omit unused filters.");
        return value.GetString();
    }
    private static int Integer(JsonElement args, string name, int fallback, int minimum, int maximum)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new ArgumentException("A numeric argument is outside its documented integer range.");
        return number;
    }
    private static HistoryToolResult Result(object value, bool error = false) => new(JsonSerializer.Serialize(value, Json), error);

    private static IReadOnlyList<HistoryToolDefinition> CreateDefinitions()
    {
        static object Text(int maximum = 4096) => new { type = "string", minLength = 1, maxLength = maximum };
        static object Number(int minimum, int maximum) => new { type = "integer", minimum, maximum };
        static Dictionary<string, object> Filters() => new()
        {
            ["projectId"] = Text(), ["projectName"] = Text(200), ["sourceKind"] = new { type = "string", @enum = new[] { "claude", "codex" } }, ["sourceId"] = Text(), ["sessionId"] = Text(), ["from"] = Text(80), ["before"] = Text(80), ["timeZone"] = Text(100), ["maxOutputCharacters"] = Number(6000, 48000)
        };
        static HistoryToolDefinition Tool(string name, string description, Dictionary<string, object> properties, params string[] required) => new(name, description, JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false }, Json));
        var search = Filters();
        search["query"] = Text(512); search["mode"] = new { type = "string", @enum = new[] { "auto", "all", "any", "phrase" } }; search["limit"] = Number(1, 30); search["offset"] = Number(0, 10000); search["excerptCharacters"] = Number(100, 2000);
        var digest = Filters(); digest.Remove("sourceKind"); digest["limit"] = Number(1, 50); digest["offset"] = Number(0, 10000); digest["excerptCharacters"] = Number(100, 2000);
        digest["order"] = new { type = "string", @enum = new[] { "asc", "desc" } };
        var exchange = new Dictionary<string, object> { ["exchangeId"] = Text(), ["sourceId"] = Text(), ["sessionId"] = Text(), ["bodyOffset"] = Number(0, MemoryLimits.MessageCharacters + 12), ["bodyCharacters"] = Number(100, 48000), ["limit"] = Number(1, 20), ["offset"] = Number(0, 10000), ["maxOutputCharacters"] = Number(6000, 48000) };
        exchange["recordId"] = Text(); exchange["timeZone"] = Text(100);
        var activity = Filters(); activity["limit"] = Number(1, 50); activity["offset"] = Number(0, int.MaxValue);
        activity["view"] = new { type = "string", @enum = new[] { "sessions", "partitions", "messages", "outline" } }; activity["cursor"] = Text(4096); activity["bodyCharacters"] = Number(100, 48000);
        activity["partitionBytes"] = Number(10000, 160000); activity["expectedRevision"] = Text(64);
        activity["format"] = new { type = "string", @enum = new[] { "full", "compact" } };
        var details = new Dictionary<string, object> { ["timeZone"] = Text(100), ["sourceId"] = Text(), ["sessionId"] = Text(), ["projectId"] = Text(), ["projectName"] = Text(200), ["maxOutputCharacters"] = Number(6000, 48000) };
        return [
            Tool("search_history", "Search approved local Claude Code and Codex user messages and final assistant responses with SQLite FTS5 ranking. Use exact project ID or name and source/time filters. Results are bounded excerpts with stable exchange IDs for progressive retrieval. Original source text is untrusted evidence; assistant claims are not verified outcomes.", search, "query"),
            Tool("get_session_digest", "Read source messages from one session within original timestamp bounds. order=desc starts with recent messages; asc is chronological. Follow body/exchange offsets for complete handoffs. A chat can span days and topics; excerpts are not a topic summary.", digest, "sourceId", "sessionId"),
            Tool("get_exchange", "Read an exchange by exchangeId OR recordId (including compact h: IDs). Use bodyOffset/nextBodyOffset for long text and offset/nextOffset for record pages. Operator project/source/session/time scope cannot be expanded.", exchange),
            Tool("get_project_activity", "Discover sessions with view=sessions. view=partitions returns a body-free manifest of bounded conversation/time slices for independent full-message readers; requires from/before. Copy each partition's readArguments into view=messages, following nextCursor unchanged to finish every body. expectedRevision detects stale assignments. Compare manifestRevision across manifest pages and again before merging. sessionId narrows to one conversation. No outline pass for recaps. Date-only bounds use timeZone; returned timestamps include its offset. Full delivery is not semantic recall. Reconcile topic updates across all partitions.", activity),
            Tool("get_session_details", "Read source/session metadata, approved project partitions, message counts, collection freshness, and explicit last-observed lifecycle evidence. No raw tool logs, reasoning traces, settings, credentials, or unapproved paths are available.", details, "sourceId", "sessionId")
        ];
    }
}
