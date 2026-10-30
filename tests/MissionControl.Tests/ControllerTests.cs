using System.Text.Json;

namespace MissionControl.Tests;

public sealed class ControllerTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MissionControl-MemoryController-" + Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(directory); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private static JsonElement Args(object? value = null) => JsonSerializer.SerializeToElement(value ?? new { }, AppController.Json);
    private static async Task<AppSnapshot> Snapshot(AppController app) => Assert.IsType<AppSnapshot>(await app.DispatchAsync("snapshot", Args()));
    private string Work => Path.Combine(directory, "work");
    private string Sessions => Path.Combine(directory, "claude-projects");

    // Synthetic Claude Code history in an explicitly approved project, collected by the normal live pipeline.
    private async Task SeedAsync(AppController app, params string[] messages)
    {
        Directory.CreateDirectory(Work); Directory.CreateDirectory(Sessions);
        await app.DispatchAsync("saveProject", Args(new { project = new ProjectInfo("work", "Work", "blue", "Test-only approval", Work) }));
        await app.DispatchAsync("projects.approve", Args(new { projectId = "work", approved = true }));
        Append(messages);
        await app.DispatchAsync("saveSource", Args(new { source = new SourceConfig("test-claude", "claude", "Synthetic Claude history", Sessions, true) }));
    }
    private void Append(params string[] messages) => File.AppendAllLines(Path.Combine(Sessions, "session.jsonl"), messages.Select(text => JsonSerializer.Serialize(new
    {
        type = "user", uuid = text, sessionId = "session", cwd = Work, timestamp = DateTimeOffset.UtcNow.ToString("O"), message = new { role = "user", content = "Synthetic message " + text }
    })));
    private static async Task<AppSnapshot> Until(AppController app, int records)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var snapshot = await Snapshot(app);
        while (snapshot.TotalRecords != records && DateTime.UtcNow < deadline) { await Task.Delay(50); snapshot = await Snapshot(app); }
        Assert.Equal(records, snapshot.TotalRecords);
        return snapshot;
    }

    [Fact]
    public async Task A_new_workspace_starts_empty_with_every_discovered_source_disabled()
    {
        await using var app = new AppController(directory); await app.InitializeAsync();
        var snapshot = await Snapshot(app);
        Assert.Empty(snapshot.Projects); Assert.Equal(0, snapshot.TotalRecords); Assert.All(snapshot.Sources, s => Assert.False(s.Enabled));
        await Assert.ThrowsAsync<ArgumentException>(() => app.DispatchAsync("mode", Args(new { mode = "demo" })));
        await Assert.ThrowsAsync<ArgumentException>(() => app.DispatchAsync("demoReplay", Args()));
    }

    [Fact]
    public async Task Approved_history_replays_available_messages_once_across_a_paused_process_restart()
    {
        await using (var app = new AppController(directory))
        {
            await app.InitializeAsync();
            await SeedAsync(app, "first", "second");
            Assert.All((await Until(app, 2)).Records, r => Assert.Equal("work", r.ProjectId));
            await app.DispatchAsync("control", Args(new { action = "pause" }));
            Append("offline");
            Assert.Equal(2, (await Snapshot(app)).TotalRecords);
        }
        await using var restarted = new AppController(directory);
        await restarted.InitializeAsync();
        Assert.Equal("paused", (await Snapshot(restarted)).Monitoring);
        await restarted.DispatchAsync("control", Args(new { action = "resume" }));
        await Until(restarted, 3);
        await restarted.DispatchAsync("control", Args(new { action = "refresh" }));
        await Task.Delay(500);
        Assert.Equal(3, (await Snapshot(restarted)).TotalRecords);
    }

    [Fact]
    public async Task Exclusion_purges_history_and_reapproval_recovers_available_originals()
    {
        await using var app = new AppController(directory); await app.InitializeAsync();
        await SeedAsync(app, "first", "second");
        await Until(app, 2);
        await app.DispatchAsync("projects.approve", Args(new { projectId = "work", approved = false }));
        Assert.DoesNotContain((await Snapshot(app)).Projects, p => p.Id == "work");
        Assert.Empty(Assert.IsType<QueryResult>(await app.DispatchAsync("query", Args(new { projectId = "work" }))).Records);
        await app.DispatchAsync("projects.approve", Args(new { projectId = "work", approved = true }));
        await Until(app, 2);
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
            await app.InitializeAsync(); await SeedAsync(app, "noted"); recordId = (await Until(app, 1)).Records.First().Id;
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
