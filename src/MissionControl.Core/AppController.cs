using System.Text.Json;

namespace MissionControl.Core;

/// <summary>Local collection and consent boundary. The AI host owns reasoning and external services.</summary>
public sealed class AppController : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string directory;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object desiredGate = new();
    private WorkspaceStore store = null!;
    private LocalObserver observer = null!;
    private ProjectDiscoveryService projects = null!;
    private volatile bool paused, stopping;
    private bool disposed;
    private readonly CancellationTokenSource retentionStop = new();
    private Task? retentionLoop;
    public event Action? SourceChanged;
    public AppController(string dataDir)
    { directory = Path.GetFullPath(dataDir); }

    public async Task InitializeAsync()
    {
        WorkspacePaths.CreatePrivateDirectory(directory);
        store = new(Path.Combine(directory, "live", "workspace.db"));
        foreach (var source in LocalObserver.DiscoverSources())
            if (!store.GetSources().Any(s => s.Id == source.Id)) store.SaveSource(source with { Enabled = false });
        observer = new(store); projects = new(store);
        observer.WorkingDirectoryObserved += projects.Observe;
        observer.Changed += NotifyChanged; projects.Changed += NotifyChanged;
        retentionLoop = Task.Run(() => RetentionLoopAsync(retentionStop.Token));
        paused = paused || store.GetSetting("app.paused", "false") == "true";
        if (!paused) await StartActiveAsync();
    }

    public async Task<object?> DispatchAsync(string method, JsonElement args)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (method == "monitoring.pause" || method == "control" && Text(args, "action") == "pause")
        { RequestPause(); await StopCollectorsAsync(); NotifyChanged(); return new { ok = true }; }
        if (method == "monitoring.resume" || method == "control" && Text(args, "action") == "resume")
            lock (desiredGate) { paused = false; store.SetSetting("app.paused", "false"); }
        switch (method)
        {
            case "snapshot": return await Task.Run(() => store.Snapshot(observer.MonitoringState));
            case "collection.stats": return await Task.Run(() => store.Statistics(observer.MonitoringState));
            case "projects": return store.GetProjects();
            case "openLoops.local": return await Task.Run(() => LocalOpenLoops(DateTimeOffset.UtcNow));
            case "sources": return store.GetSources();
            case "query": return await Task.Run(() => store.Query(Query(args), previews: true));
            case "sessions": return await Task.Run(() => store.QuerySessions(Query(args)));
            case "conversation":
                if (Text(args, "sessionId") is not { Length: > 0 }) throw new ArgumentException("Select a session.");
                return await Task.Run(() => store.Query(Query(args), previews: true));
            case "record": return RecordPage(args);
            case "recordCorrection": return store.GetCorrection(Required(args, "id"));
            case "correction": store.SaveCorrection(Read<UserCorrection>(args.GetProperty("correction"))); NotifyChanged(); return new { saved = true };
            case "undoCorrection": store.DeleteCorrection(Required(args, "recordId")); NotifyChanged(); return new { saved = true };
            case "sessionProject": store.SaveSessionProject(Required(args, "sourceId"), Required(args, "sessionId"), Text(args, "projectId")); NotifyChanged(); return new { saved = true };
            case "markRead": store.SetSetting("dashboard.last-read", DateTimeOffset.UtcNow.ToString("O")); NotifyChanged(); return new { ok = true };
            case "settings": return new { lookbackDays = store.LookbackDays, includeSubagents = store.IncludeSubagents, storageLimitMb = store.StorageLimitBytes / (1024 * 1024), retentionDays = IntSetting("retention.days", 90), startWithWindows = store.GetSetting("startWithWindows", "false") == "true", telemetry = false, databaseBytes = store.DatabaseBytes, dataDirectory = directory, lastReadAt = store.GetSetting("dashboard.last-read"), privacy = "Only explicitly approved local projects are indexed or returned. Unknown projects are excluded. Secret filtering is best effort. No model, GitHub or Slack client runs in the desktop. Repository metadata and branch reminders are read locally." };
            case "mcpConfig":
                var binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "mission-control-mcp.exe" : "mission-control-mcp");
                var quote = (string value) => WorkspacePaths.QuoteShellArgument(value, OperatingSystem.IsWindows());
                var command = $"{quote(binary)} --data-dir {quote(directory)} --live --time-zone {quote(TimeZoneInfo.Local.Id)}";
                return new { executablePath = binary, dataDirectory = directory, claudeCommand = "claude mcp add --transport stdio mission-control -- " + command, codexCommand = "codex mcp add mission-control -- " + command, configurationPath = Path.Combine(WorkspacePaths.ResourcesDirectory(AppContext.BaseDirectory), "integrations") };
        }
        await lifecycle.WaitAsync();
        try
        {
            switch (method)
            {
                case "control": await ControlAsync(Required(args, "action")); break;
                case "monitoring.resume": await ControlAsync("resume"); break;
                case "sources.reconcile": case "monitoring.wake":
                    if (!paused) { observer.RequestReconciliation(); await projects.RescanAsync(); } break;
                case "projects.discover":
                    if (paused) throw new InvalidOperationException("Resume monitoring before discovering project folders.");
                    await projects.RescanAsync(); return new { queued = true };
                case "projects.approve":
                    await Task.WhenAll(observer.PauseAsync(), projects.PauseAsync());
                    var purged = store.SetProjectApproval(Required(args, "projectId"), Bool(args, "approved"));
                    await StartActiveAsync(); NotifyChanged(); return new { saved = true, purged };
                case "projects.purgeExcluded":
                    var excluded = store.PurgeExcludedHistory(); NotifyChanged(); return new { removed = excluded };
                case "history.clear":
                    await Task.WhenAll(observer.PauseAsync(), projects.PauseAsync());
                    RequestPause(); store.ClearAllHistory(); break;
                case "saveProject":
                    var project = Read<ProjectInfo>(args.GetProperty("project"));
                    if (string.IsNullOrWhiteSpace(project.Id) || project.Id.Length > 100 || string.IsNullOrWhiteSpace(project.Name)) throw new ArgumentException("Project identity and name are required.");
                    if (project.RootPath is { Length: > 0 } root && !Path.IsPathFullyQualified(root)) throw new ArgumentException("Use an absolute project folder path.");
                    store.SaveProject(project); break;
                case "saveSource":
                    var source = Read<SourceConfig>(args.GetProperty("source"));
                    if (source.Kind is not ("claude" or "codex")) throw new ArgumentException("Only local Claude Code and Codex sources are supported.");
                    if (string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 100 || source.Name.Length > 120 || !Path.IsPathFullyQualified(source.RootPath)) throw new ArgumentException("A source identity, name and absolute session folder are required.");
                    await Task.WhenAll(observer.PauseAsync(), projects.PauseAsync()); store.SaveSource(source); await StartActiveAsync(); break;
                case "deleteSource":
                    await Task.WhenAll(observer.PauseAsync(), projects.PauseAsync()); store.DeleteSource(Required(args, "id")); await StartActiveAsync(); break;
                case "saveSettings":
                    if (args.TryGetProperty("lookbackDays", out var days)) store.SetSetting("collection.lookbackDays", Math.Clamp(days.GetInt32(), 1, 36500).ToString());
                    if (args.TryGetProperty("storageLimitMb", out var storage)) store.SetSetting("storage.limitMb", Math.Clamp(storage.GetInt32(), 64, 8192).ToString());
                    if (args.TryGetProperty("retentionDays", out var retention)) store.SetSetting("retention.days", Math.Clamp(retention.GetInt32(), 7, 3650).ToString());
                    if (args.TryGetProperty("startWithWindows", out var startup)) store.SetSetting("startWithWindows", startup.GetBoolean() ? "true" : "false"); break;
                case "retention":
                    var retainedDays = Math.Clamp(Int(args, "days", IntSetting("retention.days", 90)), 7, 3650);
                    store.SetSetting("retention.days", retainedDays.ToString()); var removed = store.ApplyRetention(retainedDays); NotifyChanged(); return new { removed };
                default: throw new ArgumentException("Unsupported local memory operation.");
            }
        }
        finally { lifecycle.Release(); }
        NotifyChanged(); return new { ok = true };
    }

    private async Task ControlAsync(string action)
    {
        if (action == "resume") await StartActiveAsync();
        else if (action == "refresh") { if (paused) throw new InvalidOperationException("Monitoring is paused."); observer.RequestReconciliation(); await projects.RescanAsync(); }
        else throw new ArgumentException("Unknown monitoring action.");
    }
    public void RequestPause() { lock (desiredGate) { paused = true; RequestCollectorStop(); store?.SetSetting("app.paused", "true"); } }
    public void RequestStop() { lock (desiredGate) { stopping = true; paused = true; RequestCollectorStop(); } }
    private void RequestCollectorStop() { observer?.RequestPause(); projects?.RequestPause(); }
    private Task StartActiveAsync() { lock (desiredGate) return paused || stopping ? Task.CompletedTask : Task.WhenAll(projects.ResumeAsync(), observer.ResumeAsync(waitForCatchup: false)); }
    private Task StopCollectorsAsync() => Task.WhenAll(observer.PauseAsync(), projects.PauseAsync());
    private void NotifyChanged() => SourceChanged?.Invoke();
    private int IntSetting(string key, int fallback) => int.TryParse(store.GetSetting(key), out var value) ? value : fallback;

    /// <summary>Read on request only: unpushed branches of approved projects' repositories and drafts left unsent.</summary>
    private object LocalOpenLoops(DateTimeOffset now)
    {
        var repositories = store.GetProjects().Where(project => project.IsApproved).SelectMany(project => project.Repositories ?? []).ToArray();
        var branches = GitBranchLoops.Read(repositories, now, TimeSpan.FromDays(30));
        return new { branches = branches.Branches, branchesOmitted = branches.Omitted, drafts = store.UnsentDrafts(now.AddDays(-7), 5), checkedAt = now.ToString("O") };
    }

    private object? RecordPage(JsonElement args)
    {
        var record = store.GetRecord(Required(args, "id"));
        if (record is null) return null;
        var offset = Math.Clamp(Int(args, "bodyOffset", 0), 0, record.Body.Length);
        if (offset > 0 && offset < record.Body.Length && char.IsLowSurrogate(record.Body[offset]) && char.IsHighSurrogate(record.Body[offset - 1])) offset--;
        var count = Math.Min(12000, record.Body.Length - offset);
        if (count > 0 && offset + count < record.Body.Length && char.IsHighSurrogate(record.Body[offset + count - 1])) count--;
        return record with { Body = record.Body.Substring(offset, count), BodyLength = record.Body.Length,
            NextBodyOffset = offset + count < record.Body.Length ? offset + count : null };
    }
    private static QueryOptions Query(JsonElement args) => Read<QueryOptions>(args) with { Limit = Math.Clamp(Int(args, "limit", 50), 1, 100), Offset = Math.Max(0, Int(args, "offset", 0)) };
    private static T Read<T>(JsonElement args) => args.Deserialize<T>(Json) ?? throw new ArgumentException("Invalid request.");
    private static string? Text(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement args, string name) => Text(args, name) is { Length: > 0 } value ? value : throw new ArgumentException($"Missing {name}.");
    private static int Int(JsonElement args, string name, int fallback) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static bool Bool(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    // The saved retention period applies by itself once a day, not only when Apply is pressed.
    // It waits for startup catch-up, checks hourly, and does nothing while collection is paused.
    internal static TimeSpan RetentionStartDelay { get; set; } = TimeSpan.FromMinutes(3);
    internal static TimeSpan RetentionCheckInterval { get; set; } = TimeSpan.FromHours(1);
    private async Task RetentionLoopAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(RetentionStartDelay, token);
            using var timer = new PeriodicTimer(RetentionCheckInterval);
            do
            {
                if (paused || stopping) continue;
                try { if (store.ApplyRetentionIfDue(TimeSpan.FromDays(1)) is > 0) NotifyChanged(); }
                catch (Exception error) when (error is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public Task ShutdownAsync() => DisposeAsync().AsTask();
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; RequestStop();
        retentionStop.Cancel();
        if (retentionLoop is not null) await retentionLoop;
        retentionStop.Dispose();
        if (observer is not null) { observer.Changed -= NotifyChanged; observer.WorkingDirectoryObserved -= projects.Observe; await observer.DisposeAsync(); }
        if (projects is not null) { projects.Changed -= NotifyChanged; await projects.DisposeAsync(); }
        store?.Dispose(); lifecycle.Dispose();
    }
}

