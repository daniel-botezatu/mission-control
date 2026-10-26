using MissionControl.Core;
using ModelContextProtocol.Server;

namespace MissionControl.Mcp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["filter-tool-output"]) return await FilterToolOutputAsync();
        BoundedLineStream? input = null;
        try
        {
            var options = McpOptions.Parse(args);
            if (!File.Exists(options.DatabasePath))
            {
                await Console.Error.WriteLineAsync("Mission Control MCP: workspace database does not exist. Open Mission Control first, or select an existing --data-dir and --live/--demo workspace.");
                return 2;
            }
            // Validation only: opening this process must never create, migrate, collect, or touch a vault.
            using (WorkspaceStore.OpenReadOnly(options.DatabasePath)) { }
            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, action) => { action.Cancel = true; shutdown.Cancel(); };
            input = new BoundedLineStream(Console.OpenStandardInput());
            await using var transport = new StreamServerTransport(input, Console.OpenStandardOutput(), "mission-control-memory");
            await using var server = McpServer.Create(transport, new WorkspaceMcpServer(options).CreateOptions());
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            if (input.LimitExceeded) throw new InvalidDataException();
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (ArgumentException error)
        {
            await Console.Error.WriteLineAsync("Mission Control MCP: " + error.Message);
            return 2;
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync(input?.LimitExceeded == true
                ? "Mission Control MCP disconnected because a request exceeded the 256 KiB frame limit. No request content was logged."
                : "Mission Control MCP could not read the workspace. Open the current Mission Control app to upgrade an older database; newer schemas require a matching MCP build. The MCP process makes no database changes.");
            return 1;
        }
    }

    // Claude Code PostToolUse hook: removes secret-looking values from results that bypass Mission
    // Control (such as Slack). It never blocks a result; on any failure the original passes through.
    private static async Task<int> FilterToolOutputAsync()
    {
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            if (text.Length > ToolOutputFilter.MaximumInputBytes) return 0; // too large to check fully; leave it unchanged
            if (ToolOutputFilter.Filter(text) is { } reply)
            {
                await using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
                await output.WriteAsync(reply);
            }
        }
        catch (Exception) { /* fail open: Slack keeps working */ }
        return 0;
    }
}
