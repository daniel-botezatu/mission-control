using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MissionControl.Core;

namespace MissionControl.Backend;

/// <summary>A bounded pipe protocol; only the parent desktop shell owns these anonymous pipes.</summary>
internal sealed class BackendProtocol(IBackendApplication application, Stream input, Stream output)
{
    internal const int MaximumInputBytes = 262144;
    internal const int MaximumReplyBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _ordinary = new(14, 14);
    private readonly SemaphoreSlim _all = new(16, 16);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _requests = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
    });
    private Task _initialized = Task.CompletedTask;
    private volatile bool _stopping;

    public async Task<int> RunAsync()
    {
        string? shutdownId = null;
        var failed = false;
        application.Changed += OnChanged;
        _initialized = Task.Run(InitializeAsync);
        var changes = PublishChangesAsync();
        try
        {
            await foreach (var line in ReadLinesAsync(input, _lifetime.Token))
            {
                using var document = Parse(line);
                var root = document.RootElement;
                var id = root.GetProperty("id").GetString()!;
                var method = root.GetProperty("method").GetString()!;
                var args = root.TryGetProperty("args", out var arguments) ? arguments.Clone() : JsonSerializer.SerializeToElement(new { });
                if (method == "shutdown")
                {
                    lock (_gate) if (_ids.Contains(id)) throw new InvalidDataException("Duplicate active request identity.");
                    shutdownId = id;
                    break;
                }
                var urgent = method is "monitoring.pause"
                    || method == "control" && args.TryGetProperty("action", out var action)
                        && action.ValueKind == JsonValueKind.String && action.GetString() == "pause";
                lock (_gate)
                {
                    if (!_ids.Add(id)) throw new InvalidDataException("Duplicate active request identity.");
                }
                if ((!urgent && !_ordinary.Wait(0)))
                {
                    lock (_gate) _ids.Remove(id);
                    await WriteAsync(new { id, error = "The backend is busy. Wait for an active request to finish." });
                    continue;
                }
                if (!_all.Wait(0))
                {
                    if (!urgent) _ordinary.Release();
                    lock (_gate) _ids.Remove(id);
                    await WriteAsync(new { id, error = "The backend is busy. Wait for an active request to finish." });
                    continue;
                }
                // AppController has synchronous SQLite sections. Keep the pipe reader available for cancellation.
                var request = Task.Run(() => ExecuteAsync(id, method, args, urgent));
                lock (_gate) _requests.Add(request);
                _ = request.ContinueWith(completed => { lock (_gate) _requests.Remove(completed); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { failed = _initialized.IsFaulted; }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException)
        {
            failed = true;
            await TryWriteAsync(new { @event = "fatal", error = "The backend pipe received an invalid or oversized request, or was disconnected." });
        }
        finally
        {
            _stopping = true;
            application.Changed -= OnChanged;
            application.RequestStop();
            _lifetime.Cancel();
            _changes.Writer.TryComplete();
            try { await changes; } catch (OperationCanceledException) { }
            try
            {
                // Do not dispose SQLite while dispatches still use it. A wedged native call ends the process instead.
                try { await _initialized.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (Exception) when (_initialized.IsCompleted) { failed = true; }
                Task[] pending;
                lock (_gate) pending = _requests.ToArray();
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30));
                await application.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception) { failed = true; }
            if (shutdownId is not null)
                await TryWriteAsync(failed ? new { id = shutdownId, error = "Backend shutdown could not finish cleanly." } as object
                    : new { id = shutdownId, result = new { stopped = true } });
        }
        return failed ? 1 : 0;
    }

    private async Task InitializeAsync()
    {
        try
        {
            await application.InitializeAsync();
            if (!_stopping) await WriteAsync(new { @event = "ready", protocolVersion = 1 });
        }
        catch (Exception)
        {
            await TryWriteAsync(new { @event = "fatal", error = "Mission Control could not initialize its local workspace. The existing settings and credentials were not reset." });
            _lifetime.Cancel();
            throw;
        }
    }

    private async Task ExecuteAsync(string id, string method, JsonElement args, bool urgent)
    {
        try
        {
            await _initialized;
            if (_stopping) throw new OperationCanceledException();
            var result = await application.DispatchAsync(method, args);
            await WriteAsync(new { id, result });
        }
        catch (Exception error)
        {
            await TryWriteAsync(new { id, error = SafeError(error) });
        }
        finally
        {
            if (!urgent) _ordinary.Release();
            _all.Release();
            lock (_gate) _ids.Remove(id);
        }
    }

    private void OnChanged() => _changes.Writer.TryWrite(true);

    private async Task PublishChangesAsync()
    {
        try
        {
            await _initialized;
            while (await _changes.Reader.WaitToReadAsync(_lifetime.Token))
            {
                await Task.Delay(150, _lifetime.Token);
                while (_changes.Reader.TryRead(out _)) { }
                if (!_stopping) await WriteAsync(new { @event = "changed" });
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { _lifetime.Cancel(); }
    }

    private async Task WriteAsync(object message)
    {
        var bytes = SerializeReply(message);
        await _write.WaitAsync();
        try
        {
            await output.WriteAsync(bytes);
            await output.WriteAsync("\n"u8.ToArray());
            await output.FlushAsync();
        }
        finally { _write.Release(); }
    }

    private async Task TryWriteAsync(object message)
    {
        try { await WriteAsync(message); }
        catch (IOException) { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { _lifetime.Cancel(); }
    }

    internal static byte[] SerializeReply(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, AppController.Json);
        if (bytes.Length <= MaximumReplyBytes) return bytes;
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("id", out var id))
            throw new InvalidDataException("An event exceeded the pipe reply limit.");
        return JsonSerializer.SerializeToUtf8Bytes(new { id, error = "This response exceeds the dashboard transfer limit. Narrow the query and open individual evidence records." }, AppController.Json);
    }

    private static string SafeError(Exception error) => error switch
    {
        OperationCanceledException => "The operation was cancelled.",
        ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception => Redactor.Clean(error.Message, 1000),
        _ => "The local operation could not finish. Check the source status and try again."
    };

    internal static JsonDocument Parse(byte[] line)
    {
        var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 128 }
            && root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() is { Length: > 0 and <= 100 }
            && (!root.TryGetProperty("args", out var args) || args.ValueKind == JsonValueKind.Object)) return document;
        document.Dispose();
        throw new InvalidDataException("Invalid pipe request envelope.");
    }

    internal static async IAsyncEnumerable<byte[]> ReadLinesAsync(Stream source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[8192];
        using var line = new MemoryStream();
        while (true)
        {
            var length = await source.ReadAsync(buffer, cancellationToken);
            if (length == 0)
            {
                if (line.Length != 0) throw new InvalidDataException("Incomplete pipe request.");
                yield break;
            }
            for (var index = 0; index < length; index++)
            {
                if (buffer[index] == '\n')
                {
                    yield return line.ToArray();
                    line.SetLength(0);
                }
                else
                {
                    if (line.Length >= MaximumInputBytes) throw new InvalidDataException("Pipe request exceeded the byte limit.");
                    line.WriteByte(buffer[index]);
                }
            }
        }
    }
}
