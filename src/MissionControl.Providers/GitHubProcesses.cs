using System.Diagnostics;
using System.Text;

namespace MissionControl.Providers;

internal sealed class GitHubProcesses(Stream input, Stream output) : IGitHubProcesses
{
    public async Task<string> ReadTokenAsync(ProcessStartInfo command, CancellationToken token)
    {
        using var process = Process.Start(command) ?? throw new InvalidOperationException();
        using var terminate = token.Register(() => Terminate(process));
        process.StandardInput.Close();
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        var secret = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                if (secret.Length + count > 4096) throw new InvalidDataException();
                secret.Append(buffer, 0, count);
                Array.Clear(buffer);
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException();
            return secret.ToString();
        }
        finally { Array.Clear(buffer); secret.Clear(); Terminate(process); }
    }

    public async Task<int> RelayServerAsync(ProcessStartInfo command, CancellationToken token)
    {
        using var process = Process.Start(command) ?? throw new InvalidOperationException();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var terminate = lifetime.Token.Register(() => Terminate(process));
        var forwardInput = ForwardInputAsync(process, lifetime.Token);
        var forwardOutput = process.StandardOutput.BaseStream.CopyToAsync(output, lifetime.Token);
        // Upstream diagnostics can include URLs or credential material; never relay or persist stderr.
        var discardErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, lifetime.Token);
        try
        {
            var exit = process.WaitForExitAsync(token);
            var first = await Task.WhenAny(exit, forwardInput, forwardOutput).ConfigureAwait(false);
            if (first != exit)
            {
                await first.ConfigureAwait(false);
                // A host that closes stdin expects the whole local server tree to leave promptly.
                try { await exit.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
                catch (TimeoutException) { Terminate(process); await exit.ConfigureAwait(false); }
            }
            else await exit.ConfigureAwait(false);
            await Task.WhenAll(forwardOutput, discardErrors).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            lifetime.Cancel();
            Terminate(process);
            // Console stdin may not observe cancellation; do not hold the launcher alive waiting on that read.
            _ = forwardInput.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task ForwardInputAsync(Process process, CancellationToken token)
    {
        try { await input.CopyToAsync(process.StandardInput.BaseStream, token).ConfigureAwait(false); }
        finally { try { process.StandardInput.Close(); } catch (InvalidOperationException) { } }
    }

    private static void Terminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
