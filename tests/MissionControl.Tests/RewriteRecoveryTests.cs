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
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRoot);
            var path = Path.Combine(sourceRoot, "session.jsonl");
            const string timestamp = "2026-10-16T10:00:00Z";
            EvidenceRecord Record(string id, string body) => new(id, "fixture", "stream", "session", null,
                "claude", "message", id, body, "assistant", timestamp, timestamp, WorkingDirectory: root,
                DataJson: "{\"schema\":\"memory-v1\",\"phase\":\"final\",\"final\":true,\"exchangeId\":\"exchange\"}");
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
            using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
            store.SaveProject(new("approved", "Approved fixture", "blue", "", root));
            store.SetProjectApproval("approved", true);
            store.SaveSource(new("fixture", "fixture", "Synthetic transcript", sourceRoot, true));
            await using var observer = new LocalObserver(store);
            await observer.StartAsync();
            Assert.Equal(3, store.Count());
            Assert.Equal("beforewatermelon", store.GetRecord("middle")!.Body);
            await observer.PauseAsync();
            await File.WriteAllTextAsync(path, after, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, firstModified.AddMinutes(1));
            await observer.ResumeAsync();
            Assert.Equal(3, store.Count());
            Assert.Equal("afterstrawberry!", store.GetRecord("middle")!.Body);
            Assert.Empty(store.Query(new(Search: "beforewatermelon")).Records);
            Assert.Single(store.Query(new(Search: "afterstrawberry")).Records);
            Assert.Equal(new FileInfo(path).Length, store.GetCheckpoints("fixture").Single().Offset);
            Assert.Null(Assert.Single(store.GetStatuses()).Gap);
            await observer.PauseAsync();
            await observer.ResumeAsync();
            Assert.Null(Assert.Single(store.GetStatuses()).Gap);
        }
        finally { Directory.Delete(root, true); }
    }
}
