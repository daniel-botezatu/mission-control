using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace MissionControl.Core;

/// <summary>Local collection and consent boundary. The AI host owns reasoning and external services.</summary>
public sealed class AppController : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string directory;
    private readonly ILegacyCredentialStore? retiredCredentials;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object desiredGate = new();
    private WorkspaceStore demoStore = null!, liveStore = null!;
    private LocalObserver demoObserver = null!, liveObserver = null!;
    private ProjectDiscoveryService demoProjects = null!, liveProjects = null!;
    private DemoWorkspace demo = null!;
    private string mode = "demo";
    private volatile bool paused, stopping;
    private bool disposed;
    private readonly CancellationTokenSource retentionStop = new();
    private Task? retentionLoop;
    public event Action? SourceChanged;
    private WorkspaceStore Store => mode == "demo" ? demoStore : liveStore;
    private LocalObserver Observer => mode == "demo" ? demoObserver : liveObserver;
    private ProjectDiscoveryService Projects => mode == "demo" ? demoProjects : liveProjects;
    public bool IsDemo => mode == "demo";
    public AppController(string dataDir, ILegacyCredentialStore? retiredCredentials = null)
    { directory = Path.GetFullPath(dataDir); this.retiredCredentials = retiredCredentials; }

    public async Task InitializeAsync()
    {
        WorkspacePaths.CreatePrivateDirectory(directory);
        liveStore = new(Path.Combine(directory, "live", "workspace.db"));
        demoStore = new(Path.Combine(directory, "demo", "workspace.db"));
        foreach (var source in LocalObserver.DiscoverSources())
            if (!liveStore.GetSources().Any(s => s.Id == source.Id)) liveStore.SaveSource(source with { Enabled = false });
        CleanupRetiredCredentials();
        demo = new(Path.Combine(directory, "demo"), demoStore); await demo.InitializeAsync();
        liveObserver = new(liveStore); demoObserver = new(demoStore);
        liveProjects = new(liveStore); demoProjects = new(demoStore);
        liveObserver.WorkingDirectoryObserved += liveProjects.Observe; demoObserver.WorkingDirectoryObserved += demoProjects.Observe;
        liveObserver.Changed += NotifyChanged; demoObserver.Changed += NotifyChanged;
        liveProjects.Changed += NotifyChanged; demoProjects.Changed += NotifyChanged;
        retentionLoop = Task.Run(() => RetentionLoopAsync(retentionStop.Token));
        mode = liveStore.GetSetting("app.mode", "demo") == "live" ? "live" : "demo";
        paused = paused || liveStore.GetSetting("app.paused", "false") == "true";
        if (!paused) await StartActiveAsync();
    }

    public async Task<object?> DispatchAsync(string method, JsonElement args)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (method == "monitoring.pause" || method == "control" && Text(args, "action") == "pause")
        { RequestPause(); await StopCollectorsAsync(); NotifyChanged(); return new { ok = true }; }
        if (method == "monitoring.resume" || method == "control" && Text(args, "action") == "resume")
            lock (desiredGate) { paused = false; liveStore.SetSetting("app.paused", "false"); }
        switch (method)
        {
            case "snapshot": return await Task.Run(() => Store.Snapshot(mode, Observer.MonitoringState));
            case "collection.stats": return await Task.Run(() => Store.Statistics(mode, Observer.MonitoringState));
            case "projects": return Store.GetProjects();
            case "openLoops.local": return await Task.Run(() => LocalOpenLoops(DateTimeOffset.UtcNow));
            case "sources": return Store.GetSources();
            case "query": return await Task.Run(() => Store.Query(Query(args), previews: true));
            case "sessions": return await Task.Run(() => Store.QuerySessions(Query(args)));
            case "conversation":
                if (Text(args, "sessionId") is not { Length: > 0 }) throw new ArgumentException("Select a session.");
                return await Task.Run(() => Store.Query(Query(args), previews: true));
            case "record": return RecordPage(args);
            case "recordCorrection": return Store.GetCorrection(Required(args, "id"));
            case "correction": Store.SaveCorrection(Read<UserCorrection>(args.GetProperty("correction"))); NotifyChanged(); return new { saved = true };
            case "undoCorrection": Store.DeleteCorrection(Required(args, "recordId")); NotifyChanged(); return new { saved = true };
            case "sessionProject": Store.SaveSessionProject(Required(args, "sourceId"), Required(args, "sessionId"), Text(args, "projectId")); NotifyChanged(); return new { saved = true };
            case "markRead": Store.SetSetting("dashboard.last-read", DateTimeOffset.UtcNow.ToString("O")); NotifyChanged(); return new { ok = true };
            case "settings": return new { lookbackDays = Store.LookbackDays, includeSubagents = Store.IncludeSubagents, storageLimitMb = Store.StorageLimitBytes / (1024 * 1024), retentionDays = IntSetting("retention.days", 90), startWithWindows = liveStore.GetSetting("startWithWindows", "false") == "true", telemetry = false, databaseBytes = Store.DatabaseBytes, dataDirectory = directory, lastReadAt = Store.GetSetting("dashboard.last-read"), retiredCredentials = liveStore.GetRetiredCredentialKeys().Count + demoStore.GetRetiredCredentialKeys().Count, privacy = "Only explicitly approved local projects are indexed or returned. Unknown projects are excluded. Secret filtering is best effort. No model, GitHub or Slack client runs in the desktop. Repository metadata and branch reminders are read locally." };
            case "mcpConfig":
                var binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "mission-control-mcp.exe" : "mission-control-mcp");
                var quote = (string value) => WorkspacePaths.QuoteShellArgument(value, OperatingSystem.IsWindows());
                var command = $"{quote(binary)} --data-dir {quote(directory)} --live --time-zone {quote(TimeZoneInfo.Local.Id)}";
                return new { executablePath = binary, dataDirectory = directory, claudeCommand = "claude mcp add --transport stdio mission-control -- " + command, codexCommand = "codex mcp add mission-control -- " + command, configurationPath = Path.Combine(WorkspacePaths.ResourcesDirectory(AppContext.BaseDirectory), "integrations") };
            case "legacy.cleanup": CleanupRetiredCredentials(); NotifyChanged(); return new { remaining = liveStore.GetRetiredCredentialKeys().Count + demoStore.GetRetiredCredentialKeys().Count };
        }
        await lifecycle.WaitAsync();
        try
        {
            switch (method)
            {
                case "mode":
                    var next = Required(args, "mode"); if (next is not ("demo" or "live")) throw new ArgumentException("Choose demo or live.");
                    await StopCollectorsAsync(); mode = next; liveStore.SetSetting("app.mode", mode); await StartActiveAsync(); break;
                case "control": await ControlAsync(Required(args, "action")); break;
                case "monitoring.resume": await ControlAsync("resume"); break;
                case "sources.reconcile": case "monitoring.wake":
                    if (!paused) { Observer.RequestReconciliation(); await Projects.RescanAsync(); } break;
                case "projects.discover":
                    if (paused) throw new InvalidOperationException("Resume monitoring before discovering project folders.");
                    await Projects.RescanAsync(); return new { queued = true };
                case "projects.approve":
                    await Task.WhenAll(Observer.PauseAsync(), Projects.PauseAsync());
                    var purged = Store.SetProjectApproval(Required(args, "projectId"), Bool(args, "approved"));
                    await StartActiveAsync(); NotifyChanged(); return new { saved = true, purged };
                case "projects.purgeExcluded":
                    var excluded = Store.PurgeExcludedHistory(); NotifyChanged(); return new { removed = excluded };
                case "history.clear":
                    await Task.WhenAll(Observer.PauseAsync(), Projects.PauseAsync());
                    RequestPause(); Store.ClearAllHistory(); break;
                case "saveProject":
                    var project = Read<ProjectInfo>(args.GetProperty("project"));
                    if (string.IsNullOrWhiteSpace(project.Id) || project.Id.Length > 100 || string.IsNullOrWhiteSpace(project.Name)) throw new ArgumentException("Project identity and name are required.");
                    if (project.RootPath is { Length: > 0 } root && !Path.IsPathFullyQualified(root)) throw new ArgumentException("Use an absolute project folder path.");
                    Store.SaveProject(project); break;
                case "saveSource":
                    if (mode == "demo") throw new InvalidOperationException("Switch to Live to configure local collection.");
                    var source = Read<SourceConfig>(args.GetProperty("source"));
                    if (source.Kind is not ("claude" or "codex")) throw new ArgumentException("Only local Claude Code and Codex sources are supported.");
                    if (string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 100 || source.Name.Length > 120 || !Path.IsPathFullyQualified(source.RootPath)) throw new ArgumentException("A source identity, name and absolute session folder are required.");
                    await Task.WhenAll(Observer.PauseAsync(), Projects.PauseAsync()); Store.SaveSource(source); await StartActiveAsync(); break;
                case "deleteSource":
                    await Task.WhenAll(Observer.PauseAsync(), Projects.PauseAsync()); Store.DeleteSource(Required(args, "id")); await StartActiveAsync(); break;
                case "demoReplay":
                    if (mode != "demo") throw new InvalidOperationException("Replay is available only in Demo."); await demo.AppendChangesAsync(); break;
                case "saveSettings":
                    if (args.TryGetProperty("lookbackDays", out var days)) Store.SetSetting("collection.lookbackDays", Math.Clamp(days.GetInt32(), 1, 36500).ToString());
                    if (args.TryGetProperty("storageLimitMb", out var storage)) Store.SetSetting("storage.limitMb", Math.Clamp(storage.GetInt32(), 64, 8192).ToString());
                    if (args.TryGetProperty("retentionDays", out var retention)) Store.SetSetting("retention.days", Math.Clamp(retention.GetInt32(), 7, 3650).ToString());
                    if (args.TryGetProperty("startWithWindows", out var startup)) liveStore.SetSetting("startWithWindows", startup.GetBoolean() ? "true" : "false"); break;
                case "retention":
                    var retainedDays = Math.Clamp(Int(args, "days", IntSetting("retention.days", 90)), 7, 3650);
                    Store.SetSetting("retention.days", retainedDays.ToString()); var removed = Store.ApplyRetention(retainedDays); NotifyChanged(); return new { removed };
                default: throw new ArgumentException("Unsupported local memory operation.");
            }
        }
        finally { lifecycle.Release(); }
        NotifyChanged(); return new { ok = true };
    }

    private async Task ControlAsync(string action)
    {
        if (action == "resume") await StartActiveAsync();
        else if (action == "refresh") { if (paused) throw new InvalidOperationException("Monitoring is paused."); Observer.RequestReconciliation(); await Projects.RescanAsync(); }
        else throw new ArgumentException("Unknown monitoring action.");
    }
    public void RequestPause() { lock (desiredGate) { paused = true; RequestCollectorStop(); liveStore?.SetSetting("app.paused", "true"); } }
    public void RequestStop() { lock (desiredGate) { stopping = true; paused = true; RequestCollectorStop(); } }
    private void RequestCollectorStop() { demoObserver?.RequestPause(); liveObserver?.RequestPause(); demoProjects?.RequestPause(); liveProjects?.RequestPause(); }
    private Task StartActiveAsync() { lock (desiredGate) return paused || stopping ? Task.CompletedTask : Task.WhenAll(Projects.ResumeAsync(), Observer.ResumeAsync(waitForCatchup: mode == "demo")); }
    private Task StopCollectorsAsync() => Task.WhenAll(demoObserver.PauseAsync(), liveObserver.PauseAsync(), demoProjects.PauseAsync(), liveProjects.PauseAsync());
    private void NotifyChanged() => SourceChanged?.Invoke();
    private int IntSetting(string key, int fallback) => int.TryParse(Store.GetSetting(key), out var value) ? value : fallback;

    /// <summary>Read on request only: unpushed branches of approved projects' repositories and drafts left unsent.</summary>
    private object LocalOpenLoops(DateTimeOffset now)
    {
        var repositories = Store.GetProjects().Where(project => project.IsApproved).SelectMany(project => project.Repositories ?? []).ToArray();
        var branches = GitBranchLoops.Read(repositories, now, TimeSpan.FromDays(30));
        return new { branches = branches.Branches, branchesOmitted = branches.Omitted, drafts = Store.UnsentDrafts(now.AddDays(-7), 5), checkedAt = now.ToString("O") };
    }

    private object? RecordPage(JsonElement args)
    {
        var record = Store.GetRecord(Required(args, "id"));
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
    private void CleanupRetiredCredentials()
    {
        if (retiredCredentials is null) return;
        foreach (var store in new[] { liveStore, demoStore }) foreach (var reference in store.GetRetiredCredentialKeys())
        {
            var split = reference.Split('\n', 2);
            if (split.Length != 2 || split[0] is not ("live" or "demo" or "live-integrations")) continue;
            var logical = split[1];
            if (logical != "*" && !logical.StartsWith("remote-api-key:", StringComparison.Ordinal) && !logical.StartsWith("remote:", StringComparison.Ordinal)) continue;
            var prefix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant() + "\n" + split[0])))[..24] + ":";
            try { if (logical == "*") retiredCredentials.DeleteScope(prefix); else retiredCredentials.Delete(prefix + logical); store.MarkRetiredCredentialDeleted(reference); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { }
        }
    }
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
                try { if (liveStore.ApplyRetentionIfDue(TimeSpan.FromDays(1)) is > 0) NotifyChanged(); }
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
        if (demoObserver is not null) { demoObserver.Changed -= NotifyChanged; demoObserver.WorkingDirectoryObserved -= demoProjects.Observe; await demoObserver.DisposeAsync(); }
        if (liveObserver is not null) { liveObserver.Changed -= NotifyChanged; liveObserver.WorkingDirectoryObserved -= liveProjects.Observe; await liveObserver.DisposeAsync(); }
        if (demoProjects is not null) { demoProjects.Changed -= NotifyChanged; await demoProjects.DisposeAsync(); }
        if (liveProjects is not null) { liveProjects.Changed -= NotifyChanged; await liveProjects.DisposeAsync(); }
        demoStore?.Dispose(); liveStore?.Dispose(); lifecycle.Dispose();
    }
}

