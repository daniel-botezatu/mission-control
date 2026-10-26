using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace MissionControl.Core;

/// <summary>Discovers projects when local working directories arrive; performs no idle polling.</summary>
public sealed class ProjectDiscoveryService(
    WorkspaceStore store,
    IGitWorkspaceDiscovery? discovery = null,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private const int MaximumRoots = 1024;
    private const int MaximumPending = 512;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly IGitWorkspaceDiscovery _discovery = discovery ?? GitWorkspaceDiscovery.ForCurrentUser();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _state = new();
    private readonly Dictionary<string, RootState> _roots = new(PathComparer);
    private Run? _run;
    private bool _desiredRunning;
    private bool _disposed;

    public event Action? Changed;

    // Shown in Settings when non-empty; a changed message refreshes the dashboard like a project change.
    private void SetGap(string value)
    {
        if ((store.GetSetting("projects.discovery.gap") ?? "") == value) return;
        store.SetSetting("projects.discovery.gap", value);
        Changed?.Invoke();
    }

    /// <summary>Queues metadata discovery, coalescing frequent messages from the same working directory.</summary>
    public void Observe(string? cwd)
    {
        var path = Normalize(cwd);
        if (path is null) return;
        lock (_state)
        {
            if (_disposed) return;
            Remember(path);
            if (!_desiredRunning || _run is null || _run.Cancellation.IsCancellationRequested) return;
            Queue(_run, path, force: false);
        }
    }

    public Task ResumeAsync()
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _desiredRunning = true;
        }
        return ResumeCoreAsync();
    }

    private async Task ResumeCoreAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            Run? previous;
            lock (_state)
            {
                if (_disposed || !_desiredRunning) return;
                previous = _run;
                if (previous is not null && !previous.Cancellation.IsCancellationRequested && !previous.Worker.IsCompleted) return;
            }
            if (previous is not null) await StopRunAsync(previous);
            lock (_state)
            {
                if (_disposed || !_desiredRunning) return;
                var run = new Run();
                _run = run;
                run.Worker = Task.Run(() => WorkAsync(run));
            }
        }
        finally { _lifecycle.Release(); }
    }

    public void RequestPause()
    {
        lock (_state)
        {
            _desiredRunning = false;
            _run?.Cancellation.Cancel();
        }
    }

    public Task PauseAsync()
    {
        RequestPause();
        return PauseCoreAsync();
    }

    private async Task PauseCoreAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            Run? run;
            lock (_state)
            {
                if (!_disposed && _desiredRunning) return;
                run = _run;
            }
            if (run is not null) await StopRunAsync(run);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Schedules a bounded fresh scan of known working directories; collection must be resumed.</summary>
    public Task RescanAsync()
    {
        Run run;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_desiredRunning || _run is null || _run.Cancellation.IsCancellationRequested)
                throw new InvalidOperationException("Resume collection before rescanning projects.");
            run = _run;
        }
        // A rescan reports its own gaps; a limit message from an earlier pass must not outlive it.
        SetGap("");
        Seed(run);
        return Task.CompletedTask;
    }

    private async Task StopRunAsync(Run run)
    {
        lock (_state)
        {
            run.Cancellation.Cancel();
            run.Signals.Writer.TryComplete();
        }
        try { await run.Worker; }
        finally
        {
            lock (_state)
            {
                if (ReferenceEquals(_run, run)) _run = null;
                run.Cancellation.Dispose();
            }
        }
    }

    private async Task WorkAsync(Run run)
    {
        var token = run.Cancellation.Token;
        try
        {
            try
            {
                // Each run reports its own gaps; an old limit message must not outlive the scan that produced it.
                SetGap("");
                if (store.NormalizeAutomaticProjects(_discovery)) Changed?.Invoke();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageCapacityException
                || error is SqliteException { SqliteErrorCode: 5 or 6 or 10 or 13 })
            {
                SetGap("Earlier automatic project groups could not be repaired. Project discovery can be retried with Rescan.");
            }
            token.ThrowIfCancellationRequested();
            Seed(run);
            while (await run.Signals.Reader.WaitToReadAsync(token))
            {
                while (run.Signals.Reader.TryRead(out _)) { }
                while (true)
                {
                    string path;
                    lock (_state)
                    {
                        token.ThrowIfCancellationRequested();
                        if (run.Pending.Count == 0) break;
                        path = run.Pending.Min!;
                        run.Pending.Remove(path);
                        run.Current = path;
                    }
                    try
                    {
                        var result = _discovery.Discover(path, token);
                        token.ThrowIfCancellationRequested();
                        var changed = result is not null && store.ApplyProjectDiscovery(result);
                        if (result is not null && result.Gaps.Count > 0)
                            SetGap(Redactor.Clean(string.Join(" ", result.Gaps), 1024));
                        lock (_state)
                        {
                            if (_roots.TryGetValue(path, out var root)) root.ScannedAt = _time.GetUtcNow();
                        }
                        if (changed) Changed?.Invoke();
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException or StorageCapacityException
                        || error is SqliteException { SqliteErrorCode: 5 or 6 or 10 or 13 })
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            SetGap("Some local project metadata could not be read or saved. Project discovery can be retried with Rescan.");
                        }
                        catch (SqliteException storageError) when (storageError.SqliteErrorCode is 5 or 6 or 10 or 13) { }
                        lock (_state)
                        {
                            if (_roots.TryGetValue(path, out var root)) root.ScannedAt = _time.GetUtcNow();
                        }
                    }
                    finally
                    {
                        lock (_state) run.Current = null;
                    }
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Seed(Run run)
    {
        var observed = store.GetObservedWorkingDirectories(MaximumPending);
        lock (_state)
        {
            if (!ReferenceEquals(_run, run) || !_desiredRunning || run.Cancellation.IsCancellationRequested) return;
            foreach (var path in observed.Select(Normalize).OfType<string>()) Remember(path);
            foreach (var path in _roots.Keys.Order(RootOrder.Instance).Take(MaximumPending)) Queue(run, path, force: true);
        }
    }

    private void Remember(string path)
    {
        var now = _time.GetUtcNow();
        if (_roots.TryGetValue(path, out var existing)) { existing.LastSeen = now; return; }
        if (_roots.Count == MaximumRoots)
        {
            var victim = _roots.Where(item => _run is null || (!_run.Pending.Contains(item.Key) && !PathComparer.Equals(_run.Current, item.Key)))
                .OrderBy(item => item.Value.LastSeen).Select(item => item.Key).FirstOrDefault();
            if (victim is null) return;
            _roots.Remove(victim);
        }
        _roots[path] = new RootState { LastSeen = now };
    }

    private void Queue(Run run, string path, bool force)
    {
        if (!_roots.TryGetValue(path, out var root) || run.Pending.Contains(path) || PathComparer.Equals(run.Current, path)) return;
        if (!force && root.ScannedAt is { } scanned && _time.GetUtcNow() - scanned < CacheDuration) return;
        if (run.Pending.Count == MaximumPending) return;
        run.Pending.Add(path);
        run.Signals.Writer.TryWrite(true);
    }

    private static string? Normalize(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || cwd.Length > 32768 || cwd.Contains('\0') || !Path.IsPathFullyQualified(cwd)
            || cwd.StartsWith("\\\\", StringComparison.Ordinal) || cwd.StartsWith("//", StringComparison.Ordinal)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_state)
        {
            _disposed = true;
            _desiredRunning = false;
            _run?.Cancellation.Cancel();
        }
        await PauseCoreAsync();
    }

    private sealed class RootState
    {
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset? ScannedAt { get; set; }
    }

    private sealed class RootOrder : IComparer<string>
    {
        public static RootOrder Instance { get; } = new();
        public int Compare(string? left, string? right)
        {
            var order = (left?.Length ?? 0).CompareTo(right?.Length ?? 0);
            return order != 0 ? order : PathComparer.Compare(left, right);
        }
    }

    private sealed class Run
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Channel<bool> Signals { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        public SortedSet<string> Pending { get; } = new(RootOrder.Instance);
        public string? Current { get; set; }
        public Task Worker { get; set; } = Task.CompletedTask;
    }
}
