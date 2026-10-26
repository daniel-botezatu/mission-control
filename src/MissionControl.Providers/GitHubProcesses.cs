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

    public Task<int> RelayServerAsync(Func<CancellationToken, Task<ProcessStartInfo>> start, StartupCache cache, CancellationToken token) =>
        new LazyRelay(input, output, cache).RunAsync(async cancellation =>
        {
            var command = await start(cancellation).ConfigureAwait(false);
            // MCP stdio is UTF-8; the console code page Windows would otherwise use corrupts non-ASCII text.
            command.StandardInputEncoding = command.StandardOutputEncoding = new UTF8Encoding(false);
            return new ProcessUpstream(Process.Start(command) ?? throw new InvalidOperationException());
        }, token);

    private sealed class ProcessUpstream : IUpstream
    {
        private readonly Process process;
        private readonly Task discardErrors;

        internal ProcessUpstream(Process process)
        {
            this.process = process;
            // Upstream diagnostics can include URLs or credential material; never relay or persist stderr.
            discardErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            Exit = WaitAsync();
        }

        public Task<int> Exit { get; }

        public async Task WriteLineAsync(string line, CancellationToken token)
        {
            await process.StandardInput.WriteAsync((line + "\n").AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }

        public Task<string?> ReadLineAsync(CancellationToken token) => process.StandardOutput.ReadLineAsync(token).AsTask();

        public void CloseInput()
        {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
        }

        public async ValueTask DisposeAsync()
        {
            Terminate(process);
            try { await Exit.ConfigureAwait(false); } catch (InvalidOperationException) { }
            process.Dispose();
        }

        private async Task<int> WaitAsync()
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await discardErrors.ConfigureAwait(false); } catch (IOException) { }
            return process.ExitCode;
        }
    }

    private static void Terminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
