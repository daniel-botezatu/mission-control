namespace MissionControl.Providers;

internal static class Program
{
    internal const string Failure = "Mission Control GitHub could not start. Verify the pinned server installation and sign in once with `gh auth login --hostname github.com` in your terminal. No credential or upstream diagnostic was logged.";

    private static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, action) => { action.Cancel = true; shutdown.Cancel(); };
        try
        {
            var status = await GitHubLauncher.RunAsync(GitHubOptions.Parse(args), new GitHubProcesses(Console.OpenStandardInput(), Console.OpenStandardOutput()), shutdown.Token).ConfigureAwait(false);
            if (status != 0) await Console.Error.WriteLineAsync(Failure);
            return status;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
        catch (Exception) { await Console.Error.WriteLineAsync(Failure); return 2; }
    }
}
