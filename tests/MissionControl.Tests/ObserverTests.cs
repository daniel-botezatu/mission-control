using System.Text;
using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

public sealed class ObserverTests
{
    [Fact]
    public async Task Dashboard_refresh_is_queued_while_catchup_is_still_running_and_pause_stops_it()
    {
        await using var fixture = new WorkspaceFixture();
        await Append(fixture.File("queued.jsonl"), Message("queued"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Observer.WorkingDirectoryObserved += _ => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        try
        {
            await fixture.Observer.ResumeAsync(waitForCatchup: false).WaitAsync(TimeSpan.FromSeconds(2));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => { for (var i = 0; i < 1000; i++) fixture.Observer.RequestReconciliation(); }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("catching-up", Assert.Single(fixture.Store.GetStatuses()).State);
            fixture.Observer.RequestPause(); release.Set();
            await fixture.Observer.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("paused", fixture.Observer.MonitoringState);
            Assert.NotNull(Find(fixture.Store, "queued"));
        }
        finally { release.Set(); }
    }
    [Fact]
    public void DiscoverySuggestsOnlyDisabledSources()
    {
        var sources = LocalObserver.DiscoverSources();
        Assert.Equal(3, sources.Count);
        Assert.All(sources, source => Assert.False(source.Enabled));
        Assert.Contains(sources, source => source.Id == "local-codex-archived");
    }

    [Fact]
    public async Task ReplayRecoversExactlyOnceAcrossPauseAndCompleteRestart()
    {
        var root = NewRoot();
        try
        {
            var sources = Path.Combine(root, "sources"); Directory.CreateDirectory(sources);
            var file = Path.Combine(sources, "events.jsonl");
            await Append(file, Message("initial"));
            using (var store = new WorkspaceStore(Path.Combine(root, "workspace.db")))
            {
                ApproveSynthetic(store);
                store.SaveSource(new("synthetic", "claude", "Synthetic replay", sources, true, TestPlatform.Environment));
                await using var observer = new LocalObserver(store);
                await observer.StartAsync();
                Assert.Equal(1, store.Count());
                store.SaveCorrection(new(Find(store, "initial")!.Id, Task: "User-linked task", Dismissed: true));
                await observer.PauseAsync();
                await Append(file, Message("offline"), Message("initial"));
                await observer.ReconcileAsync();
                Assert.Equal(1, store.Count());
                Assert.Equal("paused", observer.MonitoringState);
            }
            using (var store = new WorkspaceStore(Path.Combine(root, "workspace.db")))
            {
                ApproveSynthetic(store);
                await using var observer = new LocalObserver(store);
                await observer.ResumeAsync();
                Assert.Equal(2, store.Count());
                Assert.Equal("current", Assert.Single(store.GetStatuses()).State);
                Assert.Equal("User-linked task", Assert.Single(store.GetCorrections()).Task);
                Assert.True(Assert.Single(store.GetCorrections()).Dismissed);
                await observer.ReconcileAsync();
                Assert.Equal(2, store.Count());
                var records = store.Query(new(Limit: 20)).Records;
                Assert.All(records, record => Assert.Equal("synthetic", record.SourceId));
                Assert.All(records, record => Assert.Equal(DateTimeOffset.Parse(At), DateTimeOffset.Parse(record.Timestamp)));
                Assert.All(records, record => Assert.False(string.IsNullOrWhiteSpace(record.IngestedAt)));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PartialFinalRecordIsNotCheckpointedUntilNewlineArrives()
    {
        await using var fixture = new WorkspaceFixture();
        var file = fixture.File("partial.jsonl");
        await Append(file, Message("first"));
        var completeLength = new FileInfo(file).Length;
        var encoded = JsonSerializer.Serialize(Message("second"));
        await File.AppendAllTextAsync(file, encoded[..20]);
        await fixture.Observer.StartAsync();
        Assert.Equal(1, fixture.Store.Count());
        Assert.Equal(completeLength, fixture.Store.GetCheckpoint("synthetic", "partial.jsonl")!.Offset);
        Assert.Equal("catching-up", Assert.Single(fixture.Store.GetStatuses()).State);
        await File.AppendAllTextAsync(file, encoded[20..] + "\n");
        await fixture.Observer.ReconcileAsync();
        Assert.Equal(2, fixture.Store.Count());
        Assert.Equal(new FileInfo(file).Length, fixture.Store.GetCheckpoint("synthetic", "partial.jsonl")!.Offset);
        Assert.Equal("current", Assert.Single(fixture.Store.GetStatuses()).State);
    }

    [Fact]
    public async Task TruncationAndReplacementReplayWithoutDuplicates()
    {
        await using var fixture = new WorkspaceFixture();
        var file = fixture.File("rotation.jsonl");
        await Append(file, Message("first"), Message("second"));
        await fixture.Observer.StartAsync();
        await fixture.Observer.PauseAsync();
        await File.WriteAllTextAsync(file, "");
        await Append(file, Message("first"));
        await fixture.Observer.ResumeAsync();
        Assert.Equal(2, fixture.Store.Count());
        // Both messages remain retained after the available source is replayed.
        Assert.Null(Assert.Single(fixture.Store.GetStatuses()).Gap);
        await fixture.Observer.PauseAsync();
        File.Move(file, fixture.File("rotation-old.jsonl"));
        await Append(file, Message("replacement"));
        await fixture.Observer.ResumeAsync();
        Assert.Equal(3, fixture.Store.Count());
        await fixture.Observer.ReconcileAsync();
        Assert.Equal(3, fixture.Store.Count());
    }

    [Fact]
    public async Task BoundedCatchupContinuesPastOneBatch()
    {
        await using var fixture = new WorkspaceFixture();
        var file = fixture.File("many.jsonl");
        await Append(file, Enumerable.Range(0, 750).Select(index => Message("record-" + index)).ToArray());
        await fixture.Observer.StartAsync();
        // Records/checkpoints commit before the worker publishes its completed source status.
        await Until(() => fixture.Store.Count() == 750 && Assert.Single(fixture.Store.GetStatuses()).State == "current");
        Assert.Equal(750, fixture.Store.Query(new(Limit: 1000)).Total);
        Assert.Equal("current", Assert.Single(fixture.Store.GetStatuses()).State);
        var checkpoint = fixture.Store.GetCheckpoint("synthetic", "many.jsonl")!;
        Assert.Equal(new FileInfo(file).Length, checkpoint.Offset);
    }

    [Fact]
    public async Task CodexMetadataSurvivesRestartAndDoesNotDuplicateEventText()
    {
        var root = NewRoot();
        try
        {
            var sources = Path.Combine(root, "sources"); Directory.CreateDirectory(sources);
            var file = Path.Combine(sources, "rollout-test.jsonl");
            await Append(file,
                Codex("session_meta", new { id = "session", cwd = Path.Combine(TestPlatform.Work, "alpha"), cli_version = "0.160.1", git = new { branch = "worktree-branch" } }),
                Codex("event_msg", new { type = "user_message", message = "Duplicate persisted text" }),
                Codex("response_item", new { type = "message", id = "user-1", role = "user", content = new[] { new { type = "input_text", text = "Duplicate persisted text" } } }));
            using (var store = new WorkspaceStore(Path.Combine(root, "workspace.db")))
            {
                ApproveSynthetic(store);
                store.SaveSource(new("codex", "codex", "Codex", sources, true, TestPlatform.Environment));
                await using var observer = new LocalObserver(store); await observer.StartAsync();
                Assert.Equal(1, store.Count());
            }
            await Append(file, Codex("response_item", new { type = "message", id = "assistant-1", role = "assistant", phase = "final_answer", content = new[] { new { type = "output_text", text = "Resume later" } } }));
            using (var store = new WorkspaceStore(Path.Combine(root, "workspace.db")))
            {
                ApproveSynthetic(store);
                await using var observer = new LocalObserver(store); await observer.StartAsync();
                Assert.Equal(2, store.Count());
                Assert.All(store.Query(new(Limit: 20)).Records, record => { Assert.Equal("session", record.SessionId); Assert.Equal("worktree-branch", record.Branch); });
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MalformedAndUnsupportedRecordsLeaveVisibleDurableGap()
    {
        await using var fixture = new WorkspaceFixture();
        var file = fixture.File("broken.jsonl");
        await File.WriteAllTextAsync(file, "{invalid json}\n");
        await Append(file, Message("valid"));
        await fixture.Observer.StartAsync();
        Assert.Equal(1, fixture.Store.Count());
        Assert.Equal("gap", Assert.Single(fixture.Store.GetStatuses()).State);
        Assert.Contains("Malformed", Assert.Single(fixture.Store.GetStatuses()).Gap);
        await fixture.Observer.PauseAsync(); await fixture.Observer.ResumeAsync();
        Assert.Equal("gap", Assert.Single(fixture.Store.GetStatuses()).State);
    }

    [Fact]
    public async Task OversizedRecordsAreBoundedAndFollowingRecordsStillRecover()
    {
        await using var fixture = new WorkspaceFixture();
        var file = fixture.File("oversized.jsonl");
        await File.WriteAllTextAsync(file, "{\"payload\":\"" + new string('x', 5 * 1024 * 1024) + "\"}\n");
        await Append(file, Message("after-large"));
        await fixture.Observer.StartAsync();
        await Until(() => fixture.Store.Count() == 1);
        Assert.Contains("Oversized", Assert.Single(fixture.Store.GetStatuses()).Gap);
        Assert.Equal(new FileInfo(file).Length, fixture.Store.GetCheckpoint("synthetic", "oversized.jsonl")!.Offset);
    }

    [Fact]
    public async Task DisabledAndUnsupportedEnvironmentsAreNeverRead()
    {
        await using var fixture = new WorkspaceFixture();
        await Append(fixture.File("available.jsonl"), Message("not-imported"));
        fixture.Store.SaveSource(new("synthetic", "claude", "Disabled", fixture.SourceRoot, false, TestPlatform.Environment));
        fixture.Store.SaveSource(new("wsl", "claude", "WSL", fixture.SourceRoot, true, "wsl"));
        fixture.Store.SaveSource(new("missing", "claude", "Missing", Path.Combine(fixture.SourceRoot, "missing"), true, TestPlatform.Environment));
        await fixture.Observer.StartAsync();
        Assert.Equal(0, fixture.Store.Count());
        Assert.Equal("disabled", fixture.Store.GetStatuses().Single(source => source.Id == "synthetic").State);
        Assert.Equal("unsupported", fixture.Store.GetStatuses().Single(source => source.Id == "wsl").State);
        Assert.Equal("unavailable", fixture.Store.GetStatuses().Single(source => source.Id == "missing").State);
    }

    [Fact]
    public async Task InvalidTimestampsDoNotPoisonLaterRecordsOrKillWorker()
    {
        await using var fixture = new WorkspaceFixture();
        var file = fixture.File("dates.jsonl");
        await Append(file, Message("invalid-date", "not-a-date"), Message("valid-date"));
        await fixture.Observer.StartAsync();
        Assert.Equal(1, fixture.Store.Count());
        Assert.NotNull(Find(fixture.Store, "valid-date"));
        Assert.Null(Find(fixture.Store, "invalid-date"));
        Assert.Equal("gap", Assert.Single(fixture.Store.GetStatuses()).State);
        await Append(file, Message("later-date"));
        await Until(() => fixture.Store.Count() == 2);
        Assert.NotNull(Find(fixture.Store, "later-date"));
    }

    [Fact]
    public async Task OfflineDeletionOfACompletelyReadFileKeepsHistoryWithoutAGapAcrossRestarts()
    {
        // A host clean-up (such as Claude Code's 30-day expiry) removing a file that was already read in
        // full loses nothing; a file deleted with unread bytes is still reported (CompressedRolloutTests).
        var root = NewRoot();
        try
        {
            var sourceRoot = Path.Combine(root, "sources"); Directory.CreateDirectory(sourceRoot);
            var file = Path.Combine(sourceRoot, "gone.jsonl"); await Append(file, Message("retained"));
            using (var store = new WorkspaceStore(Path.Combine(root, "workspace.db")))
            {
                ApproveSynthetic(store);
                store.SaveSource(new("synthetic", "claude", "Synthetic", sourceRoot, true, TestPlatform.Environment));
                await using var observer = new LocalObserver(store); await observer.StartAsync();
            }
            File.Delete(file);
            for (var restart = 0; restart < 2; restart++)
            {
                using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
            ApproveSynthetic(store);
                await using var observer = new LocalObserver(store); await observer.StartAsync();
                Assert.Equal(1, store.Count());
                Assert.NotEqual("gap", Assert.Single(store.GetStatuses()).State);
                Assert.Null(Assert.Single(store.GetStatuses()).Gap);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PauseInterruptsInitialCatchupBeforeAllFilesAreRead()
    {
        await using var fixture = new WorkspaceFixture();
        for (var file = 0; file < 80; file++)
            await Append(fixture.File($"batch-{file}.jsonl"), Enumerable.Range(0, 80).Select(index => Message($"{file}-{index}")).ToArray());
        var startup = fixture.Observer.StartAsync();
        await Task.Delay(5);
        await fixture.Observer.PauseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await startup;
        Assert.Equal("paused", fixture.Observer.MonitoringState);
        Assert.True(fixture.Store.Count() < 6400, "Pause should cancel initial catch-up rather than wait for every file.");
        var stoppedAt = fixture.Store.Count();
        await Task.Delay(400);
        Assert.Equal(stoppedAt, fixture.Store.Count());
        await fixture.Observer.ResumeAsync();
        await Until(() => fixture.Store.Count() == 6400);
    }

    [Fact]
    public async Task NativeRecordIdentitySurvivesArchiveMoveAndOverlappingRoots()
    {
        var root = NewRoot();
        try
        {
            var active = Path.Combine(root, "active"); var archive = Path.Combine(root, "archive");
            Directory.CreateDirectory(active); Directory.CreateDirectory(archive);
            var path = Path.Combine(active, "rollout-session.jsonl");
            await Append(path,
                Codex("session_meta", new { id = "stable-session", cwd = Path.Combine(TestPlatform.Work, "project"), cli_version = "0.160.1" }),
                Codex("response_item", new { type = "message", id = "upstream-message", role = "assistant", phase = "final_answer", content = new[] { new { type = "output_text", text = "Archive me" } } }));
            using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
            ApproveSynthetic(store);
            store.SaveSource(new("active", "codex", "Active", active, true, TestPlatform.Environment));
            store.SaveSource(new("archive", "codex", "Archived", archive, true, TestPlatform.Environment));
            await using var observer = new LocalObserver(store); await observer.StartAsync();
            var recordId = Assert.Single(store.Query(new(Limit: 20)).Records).Id;
            store.SaveCorrection(new(recordId, Task: "Preserved archive correction"));
            await observer.PauseAsync();
            File.Move(path, Path.Combine(archive, "rollout-session.jsonl"));
            await observer.ResumeAsync();
            Assert.Equal(1, store.Count());
            Assert.Equal("archive", store.GetRecord(recordId)!.SourceId);
            Assert.Equal("Preserved archive correction", Assert.Single(store.GetCorrections()).Task);
            store.SaveSource(new("overlap", "codex", "Overlap", archive, true, TestPlatform.Environment));
            await observer.ResumeAsync();
            Assert.Equal(1, store.Count());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LaterPauseWinsRepeatedConcurrentResumeRaces()
    {
        await using var fixture = new WorkspaceFixture();
        await fixture.Observer.StartAsync();
        for (var index = 0; index < 16; index++)
        {
            var resume = fixture.Observer.ResumeAsync();
            fixture.Observer.RequestPause();
            var pause = fixture.Observer.PauseAsync();
            await Task.WhenAll(resume, pause);
            Assert.Equal("paused", fixture.Observer.MonitoringState);
        }
        var stoppedAt = fixture.Store.Count();
        await Append(fixture.File("after-race.jsonl"), Message("after-race"));
        await Task.Delay(450);
        Assert.Equal(stoppedAt, fixture.Store.Count());
        await fixture.Observer.ResumeAsync();
        Assert.NotNull(Find(fixture.Store, "after-race"));
    }

    [Fact]
    public async Task AppearingSourceInstallsLiveWatcherAfterReconciliation()
    {
        await using var fixture = new WorkspaceFixture();
        var lateRoot = Path.Combine(fixture.SourceRoot, "late");
        fixture.Store.SaveSource(new("synthetic", "claude", "Late source", lateRoot, true, TestPlatform.Environment));
        await fixture.Observer.StartAsync();
        Assert.Equal("unavailable", Assert.Single(fixture.Store.GetStatuses()).State);
        Directory.CreateDirectory(lateRoot);
        var path = Path.Combine(lateRoot, "late.jsonl"); await Append(path, Message("initial-late"));
        await fixture.Observer.ReconcileAsync();
        Assert.Equal(1, fixture.Store.Count());
        await Append(path, Message("watched-late"));
        await Until(() => fixture.Store.Count() == 2);
    }

    [SymlinkFact]
    public async Task ReparseLeavesDirectoriesAndRootsAreNotTraversed()
    {
        var root = NewRoot();
        try
        {
            var selected = Path.Combine(root, "selected"); var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(selected); Directory.CreateDirectory(outside);
            var outsideFile = Path.Combine(outside, "evidence.jsonl"); await Append(outsideFile, Message("outside"));
            // Explicit opt-in run fails clearly if the current account cannot create symlinks.
            Directory.CreateSymbolicLink(Path.Combine(selected, "nested"), outside);
            File.CreateSymbolicLink(Path.Combine(selected, "leaf.jsonl"), outsideFile);
            var linkedRoot = Path.Combine(root, "linked-root"); Directory.CreateSymbolicLink(linkedRoot, outside);
            using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
            ApproveSynthetic(store);
            store.SaveSource(new("synthetic", "claude", "Selected", selected, true, TestPlatform.Environment));
            store.SaveSource(new("linked", "claude", "Linked root", linkedRoot, true, TestPlatform.Environment));
            await using var observer = new LocalObserver(store); await observer.StartAsync();
            Assert.Equal(0, store.Count());
            Assert.Equal("gap", store.GetStatuses().Single(source => source.Id == "synthetic").State);
            Assert.Equal("unsupported", store.GetStatuses().Single(source => source.Id == "linked").State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("sk-proj-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("xox" + "b-123456789012-abcdefghijklmnopqrst")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nsecret body\n-----END PRIVATE KEY-----")]
    [InlineData("postgres://developer:secret-password@localhost/database")]
    public void SecretFilteringIsBoundedAndRemovesKnownPatterns(string secret)
    {
        var clean = Redactor.Clean("Evidence " + secret);
        Assert.Contains("REDACTED", clean);
        Assert.DoesNotContain(secret, clean);
        Assert.Contains("truncated", Redactor.Clean(new string('a', 1000), 64));
    }

    private static object Codex(string type, object payload) => new { timestamp = "2026-10-01T10:00:00Z", type, payload };
    private static readonly string At = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ");
    // A Claude Code user message in the approved synthetic project, identified by its source uuid.
    private static object Message(string id, string? timestamp = null) => new { type = "user", uuid = id, sessionId = "session", cwd = Path.GetTempPath(), timestamp = timestamp ?? At, message = new { role = "user", content = "Synthetic body " + id } };
    private static EvidenceRecord? Find(WorkspaceStore store, string id) => store.Query(new(Limit: 100)).Records.SingleOrDefault(record => record.Body == "Synthetic body " + id);
    private static void ApproveSynthetic(WorkspaceStore store)
    {
        store.SaveProject(new("synthetic", "Synthetic", "blue", "Test-only explicit consent", Path.GetTempPath()));
        if (!store.GetProjects().Single(p => p.Id == "synthetic").IsApproved) store.SetProjectApproval("synthetic", true);
        store.SaveProject(new("native", "Native", "blue", "Test-only explicit consent", TestPlatform.Work));
        if (!store.GetProjects().Single(p => p.Id == "native").IsApproved) store.SetProjectApproval("native", true);
    }
    private static Task Append(string path, params object[] records) => File.AppendAllTextAsync(path, string.Join("\n", records.Select(record => JsonSerializer.Serialize(record))) + "\n", new UTF8Encoding(false));
    private static string NewRoot() { var root = Path.Combine(Path.GetTempPath(), "mission-control-observer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(condition(), "Observer did not finish bounded catch-up before the test deadline.");
    }
    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly string root = NewRoot();
        public string SourceRoot { get; }
        public WorkspaceStore Store { get; }
        public LocalObserver Observer { get; }
        public WorkspaceFixture(string kind = "claude")
        {
            SourceRoot = Path.Combine(root, "sources"); Directory.CreateDirectory(SourceRoot);
            Store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
            ApproveSynthetic(Store);
            Store.SaveSource(new("synthetic", kind, "Synthetic source", SourceRoot, true, TestPlatform.Environment));
            Observer = new(Store);
        }
        public string File(string name) => Path.Combine(SourceRoot, name);
        public async ValueTask DisposeAsync() { await Observer.DisposeAsync(); Store.Dispose(); Directory.Delete(root, true); }
    }
}

public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("MISSION_CONTROL_TEST_SYMLINKS") != "1")
            Skip = "Windows symlink integration is opt-in: set MISSION_CONTROL_TEST_SYMLINKS=1 with Developer Mode or symlink privilege. Current environment probe failed with Win32 1314.";
    }
}
