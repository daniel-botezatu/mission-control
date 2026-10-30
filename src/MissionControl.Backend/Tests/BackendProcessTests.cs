using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace MissionControl.Backend.Tests;

public sealed class BackendProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_backend_exits_after_graceful_shutdown_or_parent_pipe_closure(bool closePipe)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MissionControl-backend-process-tests", Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(BackendProtocol).Assembly.Location);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(directory);
        using var process = Process.Start(start)!;
        try
        {
            await ReadUntil(process, root => root.TryGetProperty("event", out var name) && name.GetString() == "ready");
            await process.StandardInput.WriteLineAsync("{\"id\":\"snapshot\",\"method\":\"snapshot\",\"args\":{}}");
            await process.StandardInput.FlushAsync();
            var snapshot = await ReadUntil(process, root => root.TryGetProperty("id", out var id) && id.GetString() == "snapshot");
            Assert.False(snapshot.GetProperty("result").TryGetProperty("mode", out _));
            Assert.Equal("running", snapshot.GetProperty("result").GetProperty("monitoring").GetString());
            Assert.True(File.Exists(Path.Combine(directory, "live", "workspace.db")));
            Assert.False(Directory.Exists(Path.Combine(directory, "demo")));
            if (closePipe) process.StandardInput.Close();
            else
            {
                await process.StandardInput.WriteLineAsync("{\"id\":\"quit\",\"method\":\"shutdown\",\"args\":{}}");
                await process.StandardInput.FlushAsync();
                var shutdown = await ReadUntil(process, root => root.TryGetProperty("id", out var id) && id.GetString() == "quit");
                Assert.True(shutdown.GetProperty("result").GetProperty("stopped").GetBoolean());
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await process.StandardError.ReadToEndAsync());
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<JsonElement> ReadUntil(Process process, Func<JsonElement, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            using var document = JsonDocument.Parse(line);
            Assert.False(document.RootElement.TryGetProperty("event", out var name) && name.GetString() == "fatal", line);
            if (predicate(document.RootElement)) return document.RootElement.Clone();
        }
    }
}
