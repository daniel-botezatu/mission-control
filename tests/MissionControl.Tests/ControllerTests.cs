using System.Text.Json;

namespace MissionControl.Tests;

public sealed class ControllerTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MissionControl-MemoryController-" + Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(directory); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private static JsonElement Args(object? value = null) => JsonSerializer.SerializeToElement(value ?? new { }, AppController.Json);
    private static async Task<AppSnapshot> Snapshot(AppController app) => Assert.IsType<AppSnapshot>(await app.DispatchAsync("snapshot", Args()));

    [Fact]
    public async Task CustomGitHubCliPathPersistsAndRejectsRelativeOrMissingExecutables()
    {
        var cli = Path.Combine(directory, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
        File.WriteAllText(cli, "synthetic path fixture, never executed");
        await using (var app = new AppController(directory))
        {
            await app.InitializeAsync();
            await app.DispatchAsync("saveSettings", Args(new { githubCliPath = cli }));
            Assert.Equal(cli, app.GitHubCliPath);
            await Assert.ThrowsAsync<ArgumentException>(() => app.DispatchAsync("saveSettings", Args(new { githubCliPath = "./gh" })));
            Assert.Equal(cli, app.GitHubCliPath);
        }
        await using var restarted = new AppController(directory); await restarted.InitializeAsync();
        Assert.Equal(cli, restarted.GitHubCliPath);
        await restarted.DispatchAsync("saveSettings", Args(new { githubCliPath = "" }));
        Assert.Equal("", restarted.GitHubCliPath);
    }

    [Fact]
    public async Task Approved_demo_replays_available_messages_once_across_a_paused_process_restart()
    {
        await using (var app = new AppController(directory))
        {
            await app.InitializeAsync();
            var initial = await Snapshot(app);
            Assert.Equal(DemoWorkspace.InitialRecordCount, initial.TotalRecords);
            Assert.Equal(5, initial.Projects.Count); Assert.All(initial.Projects, p => Assert.True(p.IsApproved));
            Assert.All(initial.Records, r => Assert.Contains(r.SourceKind, new[] { "claude", "codex" }));
            await app.DispatchAsync("control", Args(new { action = "pause" }));
            await app.DispatchAsync("demoReplay", Args(new { action = "append" }));
            Assert.Equal(DemoWorkspace.InitialRecordCount, (await Snapshot(app)).TotalRecords);
        }
        await using var restarted = new AppController(directory);
        await restarted.InitializeAsync();
        Assert.Equal("paused", (await Snapshot(restarted)).Monitoring);
        await restarted.DispatchAsync("control", Args(new { action = "resume" }));
        Assert.Equal(DemoWorkspace.RecoveredRecordCount, (await Snapshot(restarted)).TotalRecords);
        await restarted.DispatchAsync("control", Args(new { action = "refresh" }));
        Assert.Equal(DemoWorkspace.RecoveredRecordCount, (await Snapshot(restarted)).TotalRecords);
    }

    [Fact]
    public async Task Exclusion_purges_history_and_reapproval_recovers_available_originals_without_enabling_live_sources()
    {
        await using var app = new AppController(directory); await app.InitializeAsync();
        var initial = await Snapshot(app); var project = initial.Projects.First();
        Assert.Contains(initial.Records, r => r.ProjectId == project.Id);
        await app.DispatchAsync("projects.approve", Args(new { projectId = project.Id, approved = false }));
        Assert.DoesNotContain((await Snapshot(app)).Projects, p => p.Id == project.Id);
        Assert.Empty(Assert.IsType<QueryResult>(await app.DispatchAsync("query", Args(new { projectId = project.Id }))).Records);
        await app.DispatchAsync("projects.approve", Args(new { projectId = project.Id, approved = true }));
        Assert.Equal(DemoWorkspace.InitialRecordCount, (await Snapshot(app)).TotalRecords);
        await app.DispatchAsync("mode", Args(new { mode = "live" }));
        var live = await Snapshot(app); Assert.Empty(live.Projects); Assert.Equal(0, live.TotalRecords); Assert.All(live.Sources, s => Assert.False(s.Enabled));
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("saveAi")]
    [InlineData("testAi")]
    [InlineData("remote.connect")]
    [InlineData("remote.refresh")]
    public async Task Removed_services_are_not_reachable_through_RPC(string method)
    {
        await using var app = new AppController(directory); await app.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => app.DispatchAsync(method, Args()));
    }

    [Fact]
    public async Task Existing_local_preferences_and_manual_notes_survive_restart_and_quit_does_not_persist_pause()
    {
        string recordId;
        await using (var app = new AppController(directory))
        {
            await app.InitializeAsync(); recordId = (await Snapshot(app)).Records.First().Id;
            await app.DispatchAsync("saveSettings", Args(new { retentionDays = 180, lookbackDays = 120, storageLimitMb = 512 }));
            await app.DispatchAsync("correction", Args(new { correction = new UserCorrection(recordId, Note: "An explicit local note") }));
            app.RequestStop();
        }
        await using var restarted = new AppController(directory); await restarted.InitializeAsync();
        Assert.Equal("running", (await Snapshot(restarted)).Monitoring);
        var settings = JsonSerializer.SerializeToElement(await restarted.DispatchAsync("settings", Args()), AppController.Json);
        Assert.Equal(180, settings.GetProperty("retentionDays").GetInt32());
        Assert.Equal("An explicit local note", Assert.IsType<UserCorrection>(await restarted.DispatchAsync("recordCorrection", Args(new { id = recordId }))).Note);
    }
}
