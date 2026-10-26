using System.Text.Json;
using MissionControl.Core;
using Xunit;

namespace MissionControl.Backend.Tests;

public sealed class BackendPersistenceTests
{
    [Fact]
    public async Task Desktop_reuses_existing_data_preferences_and_requires_explicit_project_approval()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MissionControl-backend-preserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using (var original = new AppController(directory))
            {
                await original.InitializeAsync(); await original.DispatchAsync("mode", Args(new { mode = "live" }));
                await original.DispatchAsync("saveProject", Args(new { project = new ProjectInfo("saved", "Saved project", "blue", "Keep this", directory) }));
                await original.DispatchAsync("saveSettings", Args(new { retentionDays = 180 }));
                await original.DispatchAsync("control", Args(new { action = "pause" }));
            }
            var options = BackendOptions.Parse(["--data-dir", directory]);
            await using var backend = new BackendApplication(options);
            await backend.InitializeAsync();
            var snapshot = Assert.IsType<AppSnapshot>(await backend.DispatchAsync("snapshot", Args()));
            Assert.Equal("live", snapshot.Mode); Assert.Equal("paused", snapshot.Monitoring); Assert.Empty(snapshot.Projects);
            var projects = Assert.IsAssignableFrom<IReadOnlyList<ProjectInfo>>(await backend.DispatchAsync("projects", Args()));
            Assert.Equal("Keep this", Assert.Single(projects).Description); Assert.False(projects[0].IsApproved);
            var settings = JsonSerializer.SerializeToElement(await backend.DispatchAsync("settings", Args()), AppController.Json);
            Assert.Equal(180, settings.GetProperty("retentionDays").GetInt32());
            await Assert.ThrowsAsync<ArgumentException>(() => backend.DispatchAsync("remote.connections", Args()));
            await Assert.ThrowsAsync<ArgumentException>(() => backend.DispatchAsync("ask", Args()));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static JsonElement Args(object? value = null) => JsonSerializer.SerializeToElement(value ?? new { }, AppController.Json);
}
