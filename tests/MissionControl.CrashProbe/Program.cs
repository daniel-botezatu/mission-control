using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MissionControl.Core;

if (args.Length != 2 || args[0] is not ("crash" or "verify")) throw new ArgumentException("Use crash|verify DISPOSABLE_DIRECTORY.");
var crashing = args[0] == "crash";
var directory = Path.GetFullPath(args[1]);
var marker = Path.Combine(directory, "crash-probe.owned");
const string ownership = "MISSION_CONTROL_DISPOSABLE_CRASH_PROBE_MEMORY_V1";
if (crashing)
{
    if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new InvalidOperationException("Use a new disposable probe directory.");
    Directory.CreateDirectory(directory); File.WriteAllText(marker, ownership);
}
else if (!File.Exists(marker) || File.ReadAllText(marker) != ownership) throw new InvalidOperationException("Not an owned disposable crash-probe database.");
using var store = new WorkspaceStore(Path.Combine(directory, "probe.db"));
var connection = (SqliteConnection)(typeof(WorkspaceStore).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(store) ?? throw new InvalidOperationException("Update test harness for the writer connection."));
if (crashing)
{
    var root = Path.Combine(directory, "project"); Directory.CreateDirectory(root);
    store.SaveProject(new("project", "Crash fixture", "#5470cc", "Synthetic", root)); store.SetProjectApproval("project", true);
    store.SaveSource(new("probe", "codex", "Disposable crash probe", directory, false));
    EvidenceRecord Record(string id) => new(id, "probe", "probe.jsonl", "probe-session", "project", "codex", "message", id, "Synthetic " + id, "user", DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"), WorkingDirectory: root, DataJson: "{\"schema\":\"memory-v1\",\"phase\":\"user\"}");
    store.CommitBatch([Record("baseline")], new("probe", "probe.jsonl", 1, "prefix", "anchor", 1, 1));
    // Test-only TEMP trigger on the real transaction; never shipped in the app.
    connection.CreateFunction<int>("test_abrupt_exit", () => { Environment.Exit(73); return 0; });
    using var trigger = connection.CreateCommand();
    trigger.CommandText = "CREATE TEMP TRIGGER crash_after_insert AFTER INSERT ON evidence WHEN new.id='uncommitted' BEGIN SELECT test_abrupt_exit(); END;"; trigger.ExecuteNonQuery();
    store.CommitBatch([Record("uncommitted")], new("probe", "probe.jsonl", 2, "prefix", "anchor", 2, 2));
    throw new InvalidOperationException("Crash injection did not run.");
}
using var integrity = connection.CreateCommand(); integrity.CommandText = "PRAGMA integrity_check";
var result = new { healthy = Equals(integrity.ExecuteScalar(), "ok"), records = store.Count(), uncommittedRecordAbsent = store.GetRecord("uncommitted") is null, offset = store.GetCheckpoint("probe", "probe.jsonl")?.Offset, ftsCount = store.Query(new(Search: "uncommitted")).Total, sqlite = store.SQLiteVersion };
Console.WriteLine(JsonSerializer.Serialize(result, AppController.Json));
if (!result.healthy || result.records != 1 || !result.uncommittedRecordAbsent || result.offset != 1 || result.ftsCount != 0) Environment.ExitCode = 1;
