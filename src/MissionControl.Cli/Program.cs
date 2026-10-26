using System.Diagnostics;
using System.Text.Json;
using MissionControl.Core;

var command = args.FirstOrDefault() ?? "snapshot";
string? Option(string name) { var index = Array.IndexOf(args, name); return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException(name + " requires a value."); }
var dataDirectory = Path.GetFullPath(Option("--data-dir") ?? ".local/preview-data");
var json = AppController.Json;
void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, json));
if (command == "compact")
{
    var database = Path.Combine(dataDirectory, "live", "workspace.db");
    if (!File.Exists(database)) throw new ArgumentException("An existing live workspace is required.");
    using var store = new WorkspaceStore(database);
    Print(store.CompactStorage()); return;
}
if (command == "benchmark-history") { await RetrievalBenchmark.RunAsync(dataDirectory, Option("--dataset") ?? "fixtures/retrieval-benchmark.json"); return; }
if (command is "stress" or "seed-dashboard")
{
    var dashboard = command == "seed-dashboard";
    var count = args.Length > 1 && int.TryParse(args[1], out var requested) ? Math.Clamp(requested, 100, 100000) : 20000;
    var database = Path.Combine(dataDirectory, dashboard ? "demo/workspace.db" : "large.db");
    if (File.Exists(database)) throw new InvalidOperationException("Use a fresh disposable data directory.");
    using var store = new WorkspaceStore(database);
    if (dashboard) await new DemoWorkspace(Path.Combine(dataDirectory, "demo"), store).InitializeAsync();
    var root = Path.Combine(dataDirectory, "benchmark-project"); Directory.CreateDirectory(root);
    store.SaveProject(new("benchmark", "Benchmark", "#5470cc", "Synthetic history only", root)); store.SetProjectApproval("benchmark", true);
    store.SaveSource(new("benchmark", "codex", "Synthetic history", Path.Combine(dataDirectory, "sessions"), true));
    var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var timer = Stopwatch.StartNew();
    for (var offset = 0; offset < count; offset += 128)
        store.CommitBatch(Enumerable.Range(offset, Math.Min(128, count - offset)).Select(i => new EvidenceRecord($"benchmark-{i}", "benchmark", "synthetic.jsonl", $"session-{i % 100}", "benchmark", i % 2 == 0 ? "codex" : "claude", "message", $"Activity {i}", new string('x', 350) + $" evidence {i}", "user", DateTimeOffset.UtcNow.AddSeconds(-count + i).ToString("O"), DateTimeOffset.UtcNow.ToString("O"), WorkingDirectory: root, DataJson: JsonSerializer.Serialize(new { schema = "memory-v1", phase = "user", exchangeId = $"exchange-{i}", sourceRecordId = i.ToString() }))).ToArray());
    timer.Stop(); var queryTimer = Stopwatch.StartNew(); var page = store.Query(new(Search: "evidence", Limit: 100)); queryTimer.Stop(); process.Refresh();
    Print(new { scenario = "synthetic-history", requested = count, imported = store.Count(), elapsedSeconds = timer.Elapsed.TotalSeconds, cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds, process.WorkingSet64, process.PrivateMemorySize64, store.DatabaseBytes, sqliteVersion = store.SQLiteVersion, queryMilliseconds = queryTimer.Elapsed.TotalMilliseconds, page.Total });
    return;
}
if (command is "collect" or "approve")
{
    // Run inside the source environment (including WSL); never watch a Windows UNC mirror.
    using var store = new WorkspaceStore(Path.Combine(dataDirectory, "live", "workspace.db"));
    if (command == "approve")
    {
        var root = Path.GetFullPath(Option("--root") ?? throw new ArgumentException("approve requires --root PATH; this is explicit consent to index that project."));
        if (!Directory.Exists(root)) throw new ArgumentException("Project directory does not exist.");
        var discovery = new GitWorkspaceDiscovery().Discover(root);
        var id = "approved-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root))).ToLowerInvariant()[..16];
        store.SaveProject(new(id, Path.GetFileName(root), "#5470cc", "Explicit CLI approval", root, Repositories: discovery?.Repositories.Select(r => new ProjectRepository(r.CheckoutPath, r.GitHubRepositories)).ToArray()));
        if (discovery is not null) store.ApplyProjectDiscovery(discovery);
        store.SetProjectApproval(id, true); Print(new { approved = id, root }); return;
    }
    foreach (var source in LocalObserver.DiscoverSources()) if (!store.GetSources().Any(existing => existing.Id == source.Id)) store.SaveSource(source with { Enabled = true });
    await using var observer = new LocalObserver(store);
    await observer.StartAsync();
    if (args.Contains("--watch"))
    {
        Console.Error.WriteLine("Collecting approved local history. Ctrl+C stops collection.");
        var stopped = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.TrySetResult(); };
        await stopped.Task;
    }
    await observer.PauseAsync(); Print(new { records = store.Count(), sources = store.GetStatuses() }); return;
}
await using var controller = new AppController(dataDirectory);
await controller.InitializeAsync();
var method = command switch { "snapshot" => "snapshot", "pause" => "monitoring.pause", "resume" => "monitoring.resume", "append" => "demoReplay", _ => throw new ArgumentException("Commands: snapshot, pause, resume, append, approve --root PATH, collect [--watch], stress [count], seed-dashboard [count]. Use --data-dir PATH.") };
await controller.DispatchAsync(method, JsonSerializer.SerializeToElement(command == "append" ? new { action = "append" } : (object)new { }));
Print((await controller.DispatchAsync("snapshot", JsonSerializer.SerializeToElement(new { })))!);

