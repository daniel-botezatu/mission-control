using System.Text;
using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

public sealed class RewriteRecoveryTests
{
    [Fact]
    public async Task Same_length_interior_rewrite_with_changed_mtime_replays_and_updates_stable_evidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "mission-control-rewrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WorkspaceStore? store = null;
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRoot);
            var path = Path.Combine(sourceRoot, "session.jsonl");
            var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ");
            object Record(string id, string body) => new { type = "user", uuid = id, sessionId = "session", cwd = root, timestamp, message = new { role = "user", content = body } };
            string Middle() => store!.Query(new(Limit: 10)).Records.Single(record => record.Body.Length < 100).Body;
            string Content(string middle) => string.Join('\n', new[]
            {
                Record("header", new string('h', 1000)),
                Record("middle", middle),
                Record("footer", new string('f', 1000))
            }.Select(record => JsonSerializer.Serialize(record))) + "\n";
            var before = Content("beforewatermelon");
            var after = Content("afterstrawberry!");
            Assert.Equal(Encoding.UTF8.GetByteCount(before), Encoding.UTF8.GetByteCount(after));
            Assert.Equal(before[..128], after[..128]);
            Assert.Equal(before[^128..], after[^128..]);
            await File.WriteAllTextAsync(path, before, new UTF8Encoding(false));
            var firstModified = DateTime.UtcNow.AddMinutes(-2);
            File.SetLastWriteTimeUtc(path, firstModified);
            store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
            store.SaveProject(new("approved", "Approved project", "blue", "", root));
            store.SetProjectApproval("approved", true);
            store.SaveSource(new("synthetic", "claude", "Synthetic transcript", sourceRoot, true));
            await using var observer = new LocalObserver(store);
            await observer.StartAsync();
            Assert.Equal(3, store.Count());
            Assert.Equal("beforewatermelon", Middle());
            await observer.PauseAsync();
            await File.WriteAllTextAsync(path, after, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, firstModified.AddMinutes(1));
            await observer.ResumeAsync();
            Assert.Equal(3, store.Count());
            Assert.Equal("afterstrawberry!", Middle());
            Assert.Empty(store.Query(new(Search: "beforewatermelon")).Records);
            Assert.Single(store.Query(new(Search: "afterstrawberry")).Records);
            Assert.Equal(new FileInfo(path).Length, store.GetCheckpoints("synthetic").Single().Offset);
            Assert.Null(Assert.Single(store.GetStatuses()).Gap);
            await observer.PauseAsync();
            await observer.ResumeAsync();
            Assert.Null(Assert.Single(store.GetStatuses()).Gap);
        }
        finally { store?.Dispose(); Directory.Delete(root, true); }
    }
}
