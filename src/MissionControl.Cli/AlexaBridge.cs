using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using MissionControl.Core;

internal sealed record AlexaSource(string Id, string Tool, string Account, string Root);
internal sealed record AlexaConfiguration(int Port, string TokenFile, IReadOnlyList<AlexaSource> Sources, bool AllowProjectSync = false);

/// <summary>Optional loopback transport over the existing approved, read-only collector.</summary>
internal static class AlexaBridge
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true };

    public static async Task RunAsync(string dataDirectory, string configFile)
    {
        var config = JsonSerializer.Deserialize<AlexaConfiguration>(File.ReadAllText(configFile), Json)
            ?? throw new ArgumentException("INVALID_BRIDGE_CONFIG");
        if (config.Port is < 1024 or > 65535 || config.Sources.Count is < 1 or > 8
            || config.Sources.Select(source => source.Id).Distinct().Count() != config.Sources.Count)
            throw new ArgumentException("INVALID_BRIDGE_CONFIG");
        var token = File.ReadAllText(config.TokenFile).Trim();
        if (token.Length is < 32 or > 256) throw new ArgumentException("INVALID_BRIDGE_TOKEN");
        var expected = Encoding.UTF8.GetBytes("Bearer " + token);
        if (config.AllowProjectSync) AlexaStoreOwnership.Require(dataDirectory);
        using var store = new WorkspaceStore(Path.Combine(dataDirectory, "live", "workspace.db"));
        // The source list does not grant project approval. Setup must explicitly
        // approve project folders through Mission Control's existing approve command.
        foreach (var source in config.Sources)
        {
            if (source.Tool is not ("claude" or "codex") || source.Account is not ("work" or "personal" or "shared")
                || (source.Tool == "claude") != (source.Account == "shared") || !Path.IsPathFullyQualified(source.Root))
                throw new ArgumentException("INVALID_SOURCE_ACCOUNT");
            var existing = store.GetSources().SingleOrDefault(item => item.Id == source.Id);
            if (existing is not null && (existing.Kind != source.Tool || Path.GetFullPath(existing.RootPath) != Path.GetFullPath(source.Root)))
                throw new ArgumentException("SOURCE_IDENTITY_CHANGED");
            store.SaveSource(new(source.Id, source.Tool, "Alexa " + source.Account + " " + source.Tool,
                Path.GetFullPath(source.Root), true));
            var key = "alexa.source-account." + source.Id;
            if (store.GetSetting(key) is { } previous && previous != source.Account)
                throw new ArgumentException("SOURCE_ACCOUNT_CHANGED");
            store.SetSetting(key, source.Account);
        }
        var sources = config.Sources.ToDictionary(source => source.Id);
        store.EnablePushJournal();
        var subscribers = new ConcurrentDictionary<Guid, Channel<string>>();
        void Committed(IReadOnlyList<string> ids)
        {
            foreach (var subscriber in subscribers.Values)
                foreach (var id in ids)
                    if (!subscriber.Writer.TryWrite(id))
                    {
                        // Disconnect an overflowing consumer so it reconciles
                        // with the atomic journal. Never grow a hidden unbounded queue.
                        subscriber.Writer.TryComplete(new IOException("EVENT_BACKLOG_REQUIRES_RECONCILIATION"));
                        break;
                    }
        }
        store.RecordsCommitted += Committed;
        await using var observer = new LocalObserver(store);
        using var scopeLock = new SemaphoreSlim(1, 1);
        long scopeDeadline = 0;
        string? lastScopes = null;
        bool ScopeReady() => !config.AllowProjectSync || Environment.TickCount64 < Interlocked.Read(ref scopeDeadline);
        // A managed bridge never resumes collection with yesterday's approvals.
        if (config.AllowProjectSync) store.SynchronizeAlexaScopes([]);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // No tokens, transcript bodies or query strings in request logs.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, config.Port);
            options.Limits.MaxRequestBodySize = 512 * 1024;
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var actual = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (context.Request.Headers.ContainsKey("Origin") || actual.Length != expected.Length
                || !CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                context.Response.StatusCode = 403;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Path != "/project-scopes" && context.Request.Path != "/health" && !ScopeReady())
            { context.Response.StatusCode = 503; return; }
            await next();
        });
        app.MapGet("/health", () => Results.Json(new { ready = ScopeReady(), read_only = true, project_sync = config.AllowProjectSync }));
        app.MapPost("/project-scopes", async (HttpRequest request, CancellationToken cancellation) =>
        {
            if (!config.AllowProjectSync) return Results.NotFound();
            await scopeLock.WaitAsync(cancellation);
            try
            {
                var scopes = await JsonSerializer.DeserializeAsync<AlexaProjectScope[]>(request.Body, Json, cancellation);
                if (scopes is null) return Results.BadRequest(new { error = "INVALID_PROJECT_SCOPES" });
                var serialized = JsonSerializer.Serialize(scopes, Json);
                if (lastScopes == serialized && ScopeReady())
                {
                    Interlocked.Exchange(ref scopeDeadline, Environment.TickCount64 + 75000);
                    return Results.Json(new { synchronized = true, scopes = scopes.Length });
                }
                // Stop the parser before changing approvals, including its in-memory pending finals.
                await observer.PauseAsync();
                Interlocked.Exchange(ref scopeDeadline, 0);
                store.SynchronizeAlexaScopes(scopes);
                lastScopes = serialized;
                Interlocked.Exchange(ref scopeDeadline, Environment.TickCount64 + 75000);
                await observer.ResumeAsync(waitForCatchup: false);
                return Results.Json(new { synchronized = true, scopes = scopes.Length });
            }
            catch (Exception error) when (error is ArgumentException or JsonException)
            { return Results.BadRequest(new { error = "INVALID_PROJECT_SCOPES" }); }
            finally { scopeLock.Release(); }
        });
        app.MapGet("/projects", () => Results.Json(new { projects = store.GetProjects().Where(project => project.IsApproved)
            .Select(project => new { id = project.Id, root_path = project.RootPath }) }));
        app.MapGet("/observations", (HttpRequest request) =>
        {
            if (!long.TryParse(request.Query["after"], out var after) || after < 0)
                return Results.BadRequest(new { error = "INVALID_CURSOR" });
            var page = store.ReadPushObservations(after);
            var events = page.Where(item => item.Record is not null).Select(item => item.Record!)
                .Where(record => sources.ContainsKey(record.SourceId) && EventType(record) is not null)
                .Select(record => new { event_id = record.Id + ":" + EventType(record) + ":" + record.ChangedAt,
                    type = EventType(record), tool = sources[record.SourceId].Tool, account = sources[record.SourceId].Account,
                    session_id = record.SessionId, project = record.ProjectId, cwd = record.WorkingDirectory,
                    git_branch = record.Branch, timestamp = record.Timestamp, observed_at = record.IngestedAt,
                    state = record.State, ownership = "unknown_external", reconciled = true });
            return Results.Json(new { journal_id = store.PushJournalId, events, next_cursor = page.LastOrDefault()?.Sequence ?? after, has_more = page.Count == 100 });
        });
        app.MapGet("/find_sessions", (HttpRequest request) =>
        {
            string? Value(string key) => request.Query[key].FirstOrDefault();
            var candidates = sources.Values.Where(source => (Value("account") is null || source.Account == Value("account"))
                && (Value("tool") is null || source.Tool == Value("tool")));
            var rows = candidates.SelectMany(source => store.QuerySessions(new(Search: Value("text"),
                ProjectId: Value("project"), SourceId: source.Id, Branch: Value("branch"), Limit: 100), fuzzy: false).Records
                .Select(record => new { tool = source.Tool, account = source.Account, source_id = source.Id,
                    session_id = record.SessionId, project = record.ProjectId, cwd = record.WorkingDirectory,
                    branch = record.Branch, title = record.Title, last_activity = record.ChangedAt ?? record.Timestamp,
                    ownership = "unknown_external", last_observed_state = record.State }))
                .OrderByDescending(record => record.last_activity).Take(100).ToArray();
            return Results.Json(new { sessions = rows });
        });
        app.MapPost("/ingest", async (HttpRequest request, CancellationToken token) =>
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
                var value = body.RootElement;
                var sourceId = value.GetProperty("source_id").GetString() ?? "";
                if (!sources.TryGetValue(sourceId, out var source)) return Results.BadRequest(new { error = "SOURCE_UNKNOWN" });
                var path = value.TryGetProperty("path", out var supplied) ? supplied.GetString() : null;
                if (path is null && value.TryGetProperty("session_id", out var session))
                {
                    var row = store.QuerySessions(new(SourceId: sourceId, SessionId: session.GetString(), Limit: 1)).Records.FirstOrDefault();
                    if (row is not null) path = Path.Combine(source.Root, row.StreamId);
                }
                if (path is null) return Results.BadRequest(new { error = "TRANSCRIPT_PATH_REQUIRED" });
                await observer.IngestNowAsync(sourceId, path, token);
                return Results.Json(new { accepted = true });
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or IOException)
            { return Results.BadRequest(new { error = "INGEST_REJECTED" }); }
        });
        app.MapGet("/events", async (HttpContext context) =>
        {
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true, SingleWriter = false });
            var subscription = Guid.NewGuid();
            subscribers[subscription] = channel;
            var seen = new HashSet<string>();
            try
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await foreach (var id in channel.Reader.ReadAllAsync(context.RequestAborted))
                {
                    var record = store.GetRecord(id); // Recheck project approval after queueing.
                    if (!ScopeReady()) break;
                    if (record is null || !sources.TryGetValue(record.SourceId, out var source)) continue;
                    var type = EventType(record);
                    if (type is null) continue;
                    var key = record.SourceId + ":" + record.SessionId;
                    if (seen.Add(key)) await Send("session_created");
                    if (seen.Count > 4096) seen.Clear();
                    await Send(type);
                    async Task Send(string kind)
                    {
                        var payload = new { event_id = record.Id + ":" + kind + ":" + record.ChangedAt,
                            type = kind, account = source.Account, tool = source.Tool, session_id = record.SessionId,
                            project = record.ProjectId, cwd = record.WorkingDirectory, git_branch = record.Branch,
                            timestamp = record.Timestamp, observed_at = record.IngestedAt,
                            final_answer = kind == "turn_finished" && record.Author == "assistant" ? record.Body : null,
                            ownership = "unknown_external", source_record_id = record.Id, state = record.State };
                        await context.Response.WriteAsync("event: " + kind + "\ndata: " + JsonSerializer.Serialize(payload) + "\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ChannelClosedException) { }
            finally { subscribers.TryRemove(subscription, out _); }
        });
        if (!config.AllowProjectSync) await observer.StartAsync();
        using var leaseCancellation = new CancellationTokenSource();
        var lease = Task.Run(async () =>
        {
            try
            {
                while (config.AllowProjectSync)
                {
                    await Task.Delay(5000, leaseCancellation.Token);
                    await scopeLock.WaitAsync(leaseCancellation.Token);
                    try
                    {
                        if (!ScopeReady()) { await observer.PauseAsync(); store.SynchronizeAlexaScopes([]); }
                    }
                    finally { scopeLock.Release(); }
                }
            }
            catch (OperationCanceledException) { }
        });
        try { await app.RunAsync(); }
        finally
        {
            leaseCancellation.Cancel();
            await lease;
            store.RecordsCommitted -= Committed;
            foreach (var subscriber in subscribers.Values) subscriber.Writer.TryComplete();
            await observer.PauseAsync();
            await app.DisposeAsync();
        }
    }

    private static string? EventType(EvidenceRecord record)
    {
        var state = record.State ?? "";
        if (state.Contains("aborted", StringComparison.OrdinalIgnoreCase)) return "turn_aborted";
        if (state.Contains("error", StringComparison.OrdinalIgnoreCase)) return "turn_error";
        if (state.Contains("started", StringComparison.OrdinalIgnoreCase)) return "turn_started";
        if (state.Contains("complete", StringComparison.OrdinalIgnoreCase)) return "turn_finished";
        if (record.Kind == "message" && record.Author == "user") return "turn_started";
        if (record.Kind == "message" && record.Author == "assistant") return "turn_finished";
        return null;
    }
}
