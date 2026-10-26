using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Globalization;

namespace MissionControl.Core;

/// <summary>Reads complete persisted records only. Does not connect to or control coding agents.</summary>
public sealed class LocalObserver(WorkspaceStore store) : IAsyncDisposable
{
    private const int MaximumLineBytes = 4 * 1024 * 1024;
    private const int MaximumPassBytes = 16 * 1024 * 1024;
    // Tool/progress envelopes must not consume the same tiny budget as retained
    // human exchanges. Keep commits bounded while scanning past omitted logs.
    private const int MaximumPassRecords = 8192;
    // Replays old streams once to replace broad tool/intermediate indexing with memory-only rows.
    private static int ParserVersion(string kind) => kind == "codex" ? 6 : 5;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _state = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalidWatchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _gaps = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Stream), string> _streamGaps = [];
    private readonly HashSet<(string Source, string Path)> _queued = [];
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private Channel<(string Source, string Path)>? _events;
    private CancellationTokenSource? _run;
    private Task? _worker;
    private Task? _timer;
    private Task? _catchup;
    private volatile bool _capacityBlocked;
    private int _overflow;
    private bool _disposed;
    private bool _desiredRunning;
    public string MonitoringState { get; private set; } = "paused";
    public event Action? Changed;
    public event Action<string?>? WorkingDirectoryObserved;
    public event Action<string>? WorkspaceObserved;

    public static IReadOnlyList<SourceConfig> DiscoverSources()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var codex = Environment.GetEnvironmentVariable("CODEX_HOME");
        var environment = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";
        return [
            new("local-claude", "claude", "Claude Code · local persisted sessions", Path.Combine(string.IsNullOrWhiteSpace(claude) ? Path.Combine(home, ".claude") : claude, "projects"), Environment: environment),
            new("local-codex", "codex", "Codex · local persisted sessions", Path.Combine(string.IsNullOrWhiteSpace(codex) ? Path.Combine(home, ".codex") : codex, "sessions"), Environment: environment),
            new("local-codex-archived", "codex", "Codex · archived persisted sessions", Path.Combine(string.IsNullOrWhiteSpace(codex) ? Path.Combine(home, ".codex") : codex, "archived_sessions"), Environment: environment)
        ];
    }

    public Task StartAsync()
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _desiredRunning = true;
        }
        return StartCoreAsync();
    }

    private async Task StartCoreAsync(bool waitForCatchup = true)
    {
        await _lifecycle.WaitAsync();
        try
        {
            lock (_state)
            {
                if (_disposed || !_desiredRunning || _run is not null) return;
                _run = new();
            }
            _capacityBlocked = false;
            // Paths are deduplicated before enqueueing. A backlog continuation
            // cannot overflow a small notification buffer and trigger a rescan.
            _events = Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
            MonitoringState = "running";
            foreach (var source in LocalSources().Where(source => source.Enabled))
            {
                store.SetSourceStatus(source.Id, "catching-up");
                if (Unsupported(source) is not null || !Directory.Exists(source.RootPath)) continue;
                EnsureWatcher(source);
            }
            var token = _run.Token;
            _worker = WorkerAsync(token);
            _timer = TimerAsync(token);
            _catchup = Task.Run(() => ReconcileAsync(token), token);
            try { if (waitForCatchup) await _catchup; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        finally { _lifecycle.Release(); }
    }

    public Task ResumeAsync(bool waitForCatchup = true)
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _desiredRunning = true;
        }
        return ResumeCoreAsync(waitForCatchup);
    }

    private async Task ResumeCoreAsync(bool waitForCatchup)
    {
        await StopCoreAsync();
        // A later RequestPause wins even if it happened while this resume was stopping the prior run.
        await StartCoreAsync(waitForCatchup);
    }

    public void RequestPause()
    {
        lock (_state)
        {
            _desiredRunning = false;
            _run?.Cancel();
        }
    }

    public Task PauseAsync()
    {
        RequestPause();
        return StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_run is null) { MonitoringState = "paused"; return; }
            MonitoringState = "paused";
            lock (_state) _run.Cancel();
            lock (_state)
            {
                foreach (var watcher in _watchers.Values) watcher.Dispose();
                _watchers.Clear(); _invalidWatchers.Clear();
            }
            _events?.Writer.TryComplete();
            try { await Task.WhenAll(_worker ?? Task.CompletedTask, _timer ?? Task.CompletedTask, _catchup ?? Task.CompletedTask); }
            catch (OperationCanceledException) { }
            await _serial.WaitAsync();
            try
            {
                _pending.Clear();
                lock (_state) _queued.Clear();
                foreach (var source in LocalSources().Where(source => source.Enabled)) store.SetSourceStatus(source.Id, "paused", Gap(source.Id));
            }
            finally { _serial.Release(); }
            lock (_state) { _run.Dispose(); _run = null; }
            _worker = null; _timer = null; _catchup = null; _events = null;
            Interlocked.Exchange(ref _overflow, 0);
            Changed?.Invoke();
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken runningToken;
        lock (_state)
        {
            if (_run is null || _run.IsCancellationRequested || !_desiredRunning) return;
            runningToken = _run.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runningToken);
        var token = linked.Token;
        await _reconcile.WaitAsync(token);
        try
        {
            var discoveredFiles = new List<(SourceConfig Source, string Path, DateTime Modified)>();
            var availableSources = new List<SourceConfig>();
            foreach (var source in LocalSources())
            {
                token.ThrowIfCancellationRequested();
                if (_capacityBlocked) break;
                lock (_state) _gaps.Remove(source.Id); // retry transient discovery/read errors on every reconciliation
                if (!source.Enabled) { store.SetSourceStatus(source.Id, "disabled"); continue; }
                var unsupported = Unsupported(source);
                if (unsupported is not null) { store.SetSourceStatus(source.Id, "unsupported", unsupported); continue; }
                if (!Directory.Exists(source.RootPath))
                {
                    RemoveWatcher(source.Id);
                    store.SetSourceStatus(source.Id, "unavailable", "Selected source folder is missing or inaccessible."); continue;
                }
                EnsureWatcher(source);
                store.SetSourceStatus(source.Id, "catching-up", Gap(source.Id));
                availableSources.Add(source);
                try
                {
                    var discovered = EnumerateFiles(source, token).Take(50001).ToArray();
                    if (discovered.Length > 50000) AddGap(source.Id, "Discovery limit reached (50,000 files); select a narrower source folder.");
                    foreach (var file in discovered.Take(50000).Where(file => Eligible(source, file)))
                    {
                        token.ThrowIfCancellationRequested();
                        if (Allowed(source, file)) discoveredFiles.Add((source, file, File.GetLastWriteTimeUtc(file)));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    AddGap(source.Id, "Some source folders could not be enumerated; history coverage is partial.");
                }
            }
            // A large older source must not consume the budget before today's work in another source.
            // Status is a synced write over all checkpoints, so a full scan refreshes it at most once a
            // second; each source gets an exact status when the scan ends below.
            var statusWritten = Environment.TickCount64;
            foreach (var file in discoveredFiles.OrderByDescending(file => file.Modified))
            {
                token.ThrowIfCancellationRequested();
                if (_capacityBlocked) break;
                await _serial.WaitAsync(token);
                try
                {
                    await ProcessFileAsync(file.Source, file.Path, token);
                    if (Environment.TickCount64 - statusWritten >= 1000) { UpdateStatus(file.Source); statusWritten = Environment.TickCount64; }
                }
                finally { _serial.Release(); }
                PublishProgress();
                await Task.Yield();
            }
            foreach (var source in availableSources)
            {
                await _serial.WaitAsync(token);
                try
                {
                    foreach (var checkpoint in store.GetCheckpoints(source.Id))
                    {
                        token.ThrowIfCancellationRequested();
                        var priorPath = Path.GetFullPath(Path.Combine(source.RootPath, checkpoint.StreamId.Replace('/', Path.DirectorySeparatorChar)));
                        if (!Allowed(source, priorPath)) { AddGap(source.Id, "A persisted stream now resolves outside the supported source boundary."); continue; }
                        // A rollout Codex compressed is still the same conversation, not a missing one. A file the
                        // host deleted after it was completely read (for example Claude Code's 30-day clean-up) loses
                        // nothing: every retained message is still here. Only unread bytes are missing history.
                        if (!RolloutFiles.Exists(priorPath) && !CompletelyRead(checkpoint))
                            AddGap(source.Id, "A source file was deleted before Mission Control finished reading it; its unread part cannot be recovered.");
                    }
                    UpdateStatus(source);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    AddGap(source.Id, "Some previously collected files could not be checked; history coverage is partial.");
                    UpdateStatus(source);
                }
                finally { _serial.Release(); }
            }
        }
        finally { _reconcile.Release(); Changed?.Invoke(); }
    }

    /// <summary>Coalesces a dashboard refresh into the owned worker instead of waiting for a full replay.</summary>
    public void RequestReconciliation()
    {
        lock (_state)
        {
            if (_run is null || _run.IsCancellationRequested || !_desiredRunning) return;
            Queue("", "");
        }
    }

    private void Queue(string source, string path)
    {
        lock (_state)
        {
            if (_run is null || _run.IsCancellationRequested || _capacityBlocked || !_queued.Add((source, path))) return;
            if (_queued.Count > 150000 || _events?.Writer.TryWrite((source, path)) != true)
            { _queued.Remove((source, path)); Interlocked.Exchange(ref _overflow, 1); }
        }
    }

    private long _lastPublished;
    private void PublishProgress()
    {
        var now = Environment.TickCount64;
        if (now - _lastPublished < 250) return;
        _lastPublished = now; Changed?.Invoke();
    }

    private void RemoveWatcher(string sourceId)
    {
        lock (_state)
        {
            if (_watchers.Remove(sourceId, out var watcher)) watcher.Dispose();
            _invalidWatchers.Remove(sourceId);
        }
    }

    private void EnsureWatcher(SourceConfig source)
    {
        lock (_state)
        {
            if (_run is null || _run.IsCancellationRequested || !_desiredRunning) return;
            if (_invalidWatchers.Remove(source.Id) && _watchers.Remove(source.Id, out var old)) old.Dispose();
            if (_watchers.ContainsKey(source.Id)) return;
            try
            {
                var watcher = new FileSystemWatcher(source.RootPath, "*.jsonl")
                {
                    IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16384
                };
                watcher.Filters.Add("*.jsonl" + RolloutFiles.CompressedSuffix);
                watcher.Changed += (_, e) => Queue(source.Id, e.FullPath);
                watcher.Created += (_, e) => Queue(source.Id, e.FullPath);
                watcher.Deleted += (_, e) => Queue(source.Id, e.FullPath);
                watcher.Renamed += (_, e) => { Queue(source.Id, e.OldFullPath); Queue(source.Id, e.FullPath); };
                watcher.Error += (_, _) =>
                {
                    lock (_state) _invalidWatchers.Add(source.Id);
                    Interlocked.Exchange(ref _overflow, 1); Queue("", "");
                };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(source.Id, watcher);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { AddGap(source.Id, "Filesystem notifications unavailable; conservative reconciliation remains enabled."); }
        }
    }

    private async Task WorkerAsync(CancellationToken token)
    {
        var channel = _events!;
        try
        {
            while (await channel.Reader.WaitToReadAsync(token))
            {
                await Task.Delay(25, token);
                var paths = new HashSet<(string Source, string Path)>();
                while (channel.Reader.TryRead(out var item))
                {
                    paths.Add(item);
                    lock (_state) _queued.Remove(item);
                }
                if (Interlocked.Exchange(ref _overflow, 0) == 1 || paths.Contains(("", "")))
                {
                    await ReconcileAsync(token);
                    paths.Remove(("", ""));
                }
                var sources = LocalSources().Where(source => source.Enabled).ToDictionary(source => source.Id);
                foreach (var item in paths.OrderByDescending(item => File.Exists(item.Path) ? File.GetLastWriteTimeUtc(item.Path) : DateTime.MinValue))
                {
                    token.ThrowIfCancellationRequested();
                    if (!sources.TryGetValue(item.Source, out var source) || Unsupported(source) is not null || !Allowed(source, item.Path)) continue;
                    await _serial.WaitAsync(token);
                    try
                    {
                        await ProcessFileAsync(source, item.Path, token);
                        UpdateStatus(source);
                    }
                    finally { _serial.Release(); }
                    PublishProgress();
                    await Task.Yield();
                }
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task TimerAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try { while (await timer.WaitForNextTickAsync(token)) { Interlocked.Exchange(ref _overflow, 1); Queue("", ""); } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private IEnumerable<SourceConfig> LocalSources() => store.GetSources().Where(source => source.Kind is "claude" or "codex" or "fixture");

    private async Task ProcessFileAsync(SourceConfig source, string path, CancellationToken token)
    {
        if (_capacityBlocked) return;
        if (!_pending.TryGetValue(source.Id, out var pending)) _pending[source.Id] = pending = new(StringComparer.Ordinal);
        pending.Remove(path);
        // name.jsonl and Codex's compressed name.jsonl.zst are one conversation: one stream and checkpoint.
        path = RolloutFiles.Existing(path) ?? path;
        var compressed = RolloutFiles.IsCompressed(path);
        var streamId = Path.GetRelativePath(Path.GetFullPath(source.RootPath), Path.GetFullPath(RolloutFiles.Logical(path))).Replace('\\', '/');
        if (OperatingSystem.IsWindows()) streamId = streamId.ToLowerInvariant();
        if (!File.Exists(path)) { AddGap(source.Id, "A previously observed source file disappeared; available records remain cached."); return; }
        try
        {
            if (!Eligible(source, path)) return;
            var info = new FileInfo(path);
            var previous = store.GetCheckpoint(source.Id, streamId);
            var metadata = previous?.MetadataJson is { } metadataJson ? JsonSerializer.Deserialize<Metadata>(metadataJson, _json) ?? new() : new Metadata();
            if (source.Kind != "fixture" && (metadata.ParserVersion != ParserVersion(source.Kind) || metadata.LookbackDays != store.LookbackDays || metadata.IncludeSubagents != store.IncludeSubagents))
            {
                // A parser or scope change re-reads the file. Bytes already observed are reinterpreted,
                // not new activity, so their records keep the observed-change time they already had.
                var replayBelow = Math.Max(previous?.Offset ?? 0, metadata.ReplayBelow);
                previous = null;
                metadata = new() { ReplayBelow = replayBelow };
            }
            // Earlier versions persisted a successful replay as a permanent gap.
            // It is an informational recovery, not evidence that messages are missing.
            if (metadata.Gap == "Source file was truncated, replaced, or rewritten; available complete records were replayed safely.")
            {
                metadata.Gap = null;
                if (previous is not null) store.CommitBatch([], previous with { MetadataJson = JsonSerializer.Serialize(metadata) });
            }
            SetStreamGap(source.Id, streamId, metadata.Gap);
            // A checkpoint records decompressed sizes; an unchanged compressed file is recognised by its own size and time.
            var unchanged = compressed
                ? metadata.CompressedLength == info.Length && metadata.CompressedTicks == info.LastWriteTimeUtc.Ticks
                : previous is not null && previous.Length == info.Length && previous.ModifiedTicks == info.LastWriteTimeUtc.Ticks && metadata.CreationTicks == info.CreationTimeUtc.Ticks;
            if (previous is not null && unchanged && (previous.Offset == previous.Length || metadata.WaitingForTail))
            {
                if (metadata.WaitingForTail) pending.Add(path);
                return;
            }
            await using var file = RolloutFiles.Open(path);
            var offset = previous?.Offset ?? 0;
            // Compressing or restoring a rollout changes its timestamp but not its content, which the
            // prefix and anchor checks below still verify. Only a plain-to-plain rewrite uses the heuristic.
            var sameLengthRewrite = previous is not null && previous.Offset > 0 && previous.Length == file.Length
                && previous.ModifiedTicks != info.LastWriteTimeUtc.Ticks && !compressed && metadata.CompressedLength is null;
            if (previous is not null && (sameLengthRewrite || file.Length < offset || !PrefixMatches(file, previous.Fingerprint) || Anchor(file, offset) != previous.Anchor))
            {
                offset = 0;
                metadata = new();
                SetStreamGap(source.Id, streamId, null);
            }
            metadata.ParserVersion = ParserVersion(source.Kind); metadata.LookbackDays = store.LookbackDays; metadata.IncludeSubagents = store.IncludeSubagents;
            if (offset >= metadata.ReplayBelow) metadata.ReplayBelow = 0;
            var reinterpreting = metadata.ReplayBelow > 0;
            file.Position = offset;
            var records = new List<EvidenceRecord>();
            var approvals = new Dictionary<string, bool>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var buffer = new byte[65536];
            using var line = new MemoryStream();
            var cursor = offset;
            var excludedSession = source.Kind == "codex" && metadata.Subagent && !metadata.IncludeSubagents;
            var committed = excludedSession ? file.Length : offset;
            var consumed = 0;
            var completeLines = 0;
            var discarding = metadata.Discarding;
            var reachedEnd = excludedSession;
            while (!excludedSession && consumed < MaximumPassBytes && completeLines < MaximumPassRecords && records.Count < 256 && !(reinterpreting && committed >= metadata.ReplayBelow))
            {
                token.ThrowIfCancellationRequested();
                var read = await file.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumPassBytes - consumed)), token);
                if (read == 0) { reachedEnd = true; break; }
                for (var index = 0; index < read;)
                {
                    token.ThrowIfCancellationRequested();
                    var newline = buffer.AsSpan(index, read - index).IndexOf((byte)'\n');
                    var segment = newline < 0 ? read - index : newline;
                    if (!discarding)
                    {
                        if (line.Length + segment <= MaximumLineBytes) line.Write(buffer, index, segment);
                        else
                        {
                            discarding = true;
                            if (!OmittableOversized(source.Kind, line.GetBuffer().AsSpan(0, (int)line.Length)))
                                metadata.Gap = "Oversized source record (>4 MiB) omitted; the original source remains unchanged.";
                            line.SetLength(0);
                        }
                    }
                    var advance = segment + (newline < 0 ? 0 : 1);
                    index += advance; consumed += advance; cursor += advance;
                    if (newline >= 0)
                    {
                        if (!discarding && line.Length > 0)
                        {
                            try
                            {
                                var lineBytes = line.GetBuffer().AsMemory(0, (int)line.Length);
                                if (lineBytes.Length >= 3 && lineBytes.Span[0] == 0xef && lineBytes.Span[1] == 0xbb && lineBytes.Span[2] == 0xbf) lineBytes = lineBytes[3..];
                                using var document = JsonDocument.Parse(lineBytes);
                                records.AddRange(Parse(source, streamId, path, document.RootElement, metadata, info.LastWriteTimeUtc, committed, approvals));
                            }
                            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
                            { metadata.Gap = "Malformed or unsupported complete JSONL record skipped; history coverage is partial."; }
                        }
                        committed = cursor; completeLines++; line.SetLength(0); discarding = false;
                        if (source.Kind == "codex" && metadata.Subagent && !metadata.IncludeSubagents)
                        {
                            metadata.Gap = null;
                            // The session header establishes provenance for the whole Codex file.
                            // Retain its policy-aware checkpoint without scanning excluded tool bodies.
                            excludedSession = true; committed = file.Length; reachedEnd = true; break;
                        }
                        if (completeLines >= MaximumPassRecords || records.Count >= 256 || (reinterpreting && committed >= metadata.ReplayBelow)) break;
                    }
                }
            }
            if (discarding) committed = cursor; // explicit oversized-record gap; keep discard state across bounded passes
            metadata.Discarding = discarding;
            metadata.CreationTicks = info.CreationTimeUtc.Ticks;
            metadata.CompressedLength = compressed ? info.Length : null;
            metadata.CompressedTicks = compressed ? info.LastWriteTimeUtc.Ticks : null;
            metadata.WaitingForTail = reachedEnd && line.Length > 0;
            if (!metadata.ApprovedHistory && !string.IsNullOrWhiteSpace(metadata.Cwd) && !store.CanIndexWorkingDirectory(metadata.Cwd)) metadata.Gap = null;
            var length = file.Length;
            if (committed > length)
            {
                AddGap(source.Id, "Source file changed length during a read; the uncommitted batch will be retried.");
                pending.Add(path); Queue(source.Id, path); return;
            }
            var fingerprint = Prefix(file, Math.Min(128, (int)Math.Min(committed, 128)));
            // A pending final candidate can cross a bounded batch or restart. Never retain its
            // text when the originating cwd is no longer approved, even inside a checkpoint.
            if (source.Kind != "fixture" && !store.CanIndexWorkingDirectory(metadata.Cwd)) metadata.PendingAssistant = null;
            var checkpoint = new StreamCheckpoint(source.Id, streamId, committed, fingerprint, Anchor(file, committed), length, info.LastWriteTimeUtc.Ticks, JsonSerializer.Serialize(metadata));
            token.ThrowIfCancellationRequested();
            store.CommitBatch(records, checkpoint, reinterpreting);
            SetStreamGap(source.Id, streamId, metadata.Gap);
            if (!excludedSession)
                foreach (var cwd in records.Select(record => record.WorkingDirectory).Append(metadata.Cwd).Where(cwd => !string.IsNullOrWhiteSpace(cwd)).Distinct(StringComparer.Ordinal))
                    WorkingDirectoryObserved?.Invoke(cwd);
            if (metadata.WaitingForTail) pending.Add(path);
            if (!reachedEnd && committed > offset && committed < length)
            {
                pending.Add(path);
                Queue(source.Id, path);
            }
        }
        catch (StorageCapacityException)
        {
            _capacityBlocked = true;
            MonitoringState = "storage-full";
            foreach (var enabled in LocalSources().Where(s => s.Enabled))
            {
                RemoveWatcher(enabled.Id);
                store.SetSourceStatus(enabled.Id, "storage-full", "Storage budget reached. Increase it or remove old data in Settings, then resume.");
            }
            _pending.Clear();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { AddGap(source.Id, "A source file could not be read safely; retry on reconciliation."); }
        catch (ZstdSharp.ZstdException)
        { AddGap(source.Id, "A compressed Codex rollout could not be decompressed; the original remains unchanged."); }
        catch (InvalidOperationException error)
        { AddGap(source.Id, Redactor.Clean("Collection could not commit; checkpoint was not advanced. " + error.Message, 300)); }
        catch (ArgumentException)
        { AddGap(source.Id, "Source or checkpoint validation failed; the batch was not committed."); }
    }

    private IEnumerable<EvidenceRecord> Parse(SourceConfig source, string stream, string path, JsonElement root, Metadata metadata, DateTime fileTime, long byteOffset, Dictionary<string, bool> approvals)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (source.Kind == "fixture")
        {
            var fixture = root.Deserialize<EvidenceRecord>(_json) ?? throw new JsonException("Expected normalized fixture");
            if (string.IsNullOrWhiteSpace(fixture.Id) || string.IsNullOrWhiteSpace(fixture.SessionId) || fixture.Id.Length > 4096 || fixture.SessionId.Length > 4096 || fixture.ProjectId?.Length > 4096
                || !ValidTimestamp(fixture.Timestamp)) throw new JsonException("Invalid fixture identity or timestamp");
            yield return fixture with { SourceId = source.Id, StreamId = stream, IngestedAt = now, Body = Redactor.Clean(fixture.Body), Title = Redactor.Clean(fixture.Title, 160), DataJson = fixture.DataJson is null ? null : Redactor.Clean(fixture.DataJson) };
            yield break;
        }
        var type = Text(root, "type");
        if (type.Length == 0) { metadata.Gap = "Unsupported transcript schema; records lack a recognized envelope type."; yield break; }
        JsonElement payload;
        if (source.Kind == "codex")
        {
            if (!root.TryGetProperty("payload", out payload) || payload.ValueKind != JsonValueKind.Object)
            { metadata.Gap = "Legacy or unknown Codex schema is unsupported; only verified envelope records are imported."; yield break; }
            if (type == "session_meta")
            {
                if (payload.TryGetProperty("source", out var origin) && origin.ValueKind == JsonValueKind.Object && origin.TryGetProperty("subagent", out _)) metadata.Subagent = true;
                metadata.Session = MetadataValue(Text(payload, "id"), 256, metadata, identity: true);
                metadata.Cwd = WorkingDirectory(Text(payload, "cwd"), metadata);
                metadata.Version = MetadataValue(Text(payload, "cli_version"), 64, metadata);
                metadata.Timestamp = MetadataValue(Text(payload, "timestamp"), 64, metadata);
                if (payload.TryGetProperty("git", out var git)) metadata.Branch = MetadataValue(Text(git, "branch"), 256, metadata);
                // Paginated rollouts still contain ordinary ordinal-stamped
                // user/final response envelopes. A label alone is not a gap.
                if (payload.TryGetProperty("history_base", out var basis) && basis.ValueKind != JsonValueKind.Null)
                    metadata.Gap = "Referenced Codex history: only messages present in this source file are available.";
                yield break;
            }
            if (type == "turn_context")
            {
                if (Text(payload, "cwd") is { Length: > 0 } cwd) metadata.Cwd = WorkingDirectory(cwd, metadata);
                if (Text(payload, "turn_id") is { Length: > 0 } turn) SetTurn(metadata, MetadataValue(turn, 256, metadata, true));
                yield break;
            }
        }
        else
        {
            payload = root;
            if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True) metadata.Subagent = true;
            if (Text(root, "sessionId") is { Length: > 0 } id) metadata.Session = MetadataValue(id, 256, metadata, true);
            if (Text(root, "cwd") is { Length: > 0 } cwd) metadata.Cwd = WorkingDirectory(cwd, metadata);
            if (Text(root, "gitBranch") is { Length: > 0 } branch) metadata.Branch = MetadataValue(branch, 256, metadata);
            if (Text(root, "version") is { Length: > 0 } version) metadata.Version = MetadataValue(version, 64, metadata);
        }
        if (metadata.Subagent && !metadata.IncludeSubagents) { metadata.PendingAssistant = null; yield break; }
        if (source.Kind == "codex" && (type is "history_revert" or "history_reset"
            || type == "event_msg" && Text(payload, "type") is "thread_rollback" or "thread_rolled_back" or "history_reverted"))
        { metadata.Gap = "Source records a history rollback. Original activity is retained; the current conversation projection may differ."; yield break; }
        // Most transcript bytes are tools, progress and reasoning. Reject those
        // envelopes before filesystem approval checks and timestamp parsing.
        // Retained messages still pass both admission and commit-time consent.
        if (source.Kind == "codex")
        {
            if (type == "response_item" && Text(payload, "type") != "message") yield break;
            if (type == "response_item" && (Text(payload, "role") != "user" && !(Text(payload, "role") == "assistant" && Text(payload, "phase") == "final_answer"))) yield break;
            if (type == "event_msg" && Text(payload, "type") is not ("task_started" or "turn_started" or "task_complete" or "turn_complete" or "turn_aborted" or "error")) yield break;
            if (type is not ("response_item" or "event_msg")) yield break;
        }
        else
        {
            if (type is not ("user" or "assistant") && !(type == "system" && Text(root, "subtype") == "turn_duration")) yield break;
            if (type == "assistant" && root.TryGetProperty("message", out var assistant)
                && (TranscriptContent.HasBlock(assistant, "tool_use") || Text(assistant, "stop_reason") is "tool_use" or "max_tokens"))
            { metadata.PendingAssistant = null; yield break; }
        }
        // Discovery may observe a cwd. The full text is not extracted or placed in a queue until
        // the persisted allowlist approves that exact native path or one of its ancestors.
        if (!string.IsNullOrWhiteSpace(metadata.Cwd)) WorkspaceObserved?.Invoke(metadata.Cwd);
        var currentCwd = metadata.Cwd ?? "";
        if (!approvals.TryGetValue(currentCwd, out var approved)) approvals[currentCwd] = approved = store.CanIndexWorkingDirectory(metadata.Cwd);
        // This cache lasts one bounded read only. CommitBatch rechecks current
        // approval and path safety for every retained record, and checkpoint
        // sanitization rechecks a pending final before persisting its text.
        if (!approved) { metadata.PendingAssistant = null; yield break; }
        var session = string.IsNullOrEmpty(metadata.Session) ? Path.GetFileNameWithoutExtension(path) : metadata.Session;
        var timestamp = Text(root, "timestamp");
        var fallbackTimestamp = timestamp.Length == 0;
        if (fallbackTimestamp) { timestamp = metadata.Timestamp ?? fileTime.ToString("O"); metadata.Gap ??= "Some records lack source timestamps; session/file timestamps are shown as a fallback."; }
        if (!ValidTimestamp(timestamp)) { metadata.Gap = "Record has an invalid source timestamp; it was skipped while later records remain recoverable."; yield break; }
        if (DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow.AddDays(-metadata.LookbackDays)) yield break;
        var identity = Text(payload, "uuid");
        if (identity.Length == 0) identity = Text(payload, "id");
        if (identity.Length == 0) identity = Hash(root.GetRawText());
        identity = MetadataValue(identity, 256, metadata, true);
        var project = store.MatchProject(metadata.Cwd);
        metadata.ApprovedHistory = true;
        EvidenceRecord Record(string body, string author, string sourceRecordId, string phase, string? finalSource = null,
            string? originalTimestamp = null, string? turn = null, string? kind = null, string? state = null, long? originalByteOffset = null, string? originalSourceType = null)
        {
            var turnId = turn ?? metadata.Turn ?? sourceRecordId;
            var exchangeId = ExchangeIdentity(source.Kind, source.Environment, session, turnId);
            // Assistant identity belongs to the turn, not its duplicated response/event envelope.
            // User IDs retain the earlier parser's empty-suffix newline so migration replay
            // upgrades retained human rows and their corrections instead of duplicating them.
            var id = author == "assistant" ? AssistantIdentity(source.Kind, source.Environment, session, turnId)
                : Hash($"{source.Kind}\n{source.Environment}\n{session}\n{sourceRecordId}" + (author == "user" ? "\n" : ""));
            var clean = Redactor.Clean(body, kind == "lifecycle" ? 512 : MemoryLimits.MessageCharacters);
            return new(id, source.Id, stream, session, project,
                source.Kind, kind ?? "message", Redactor.Clean(clean.Split('\n')[0], 140), clean, author, originalTimestamp ?? timestamp, now,
                Empty(metadata.Cwd), Empty(metadata.Branch), state ?? "unknown", DataJson: JsonSerializer.Serialize(new
                {
                    schema = "memory-v1", phase, turnId, exchangeId, sourceRecordId, sourceByteOffset = originalByteOffset ?? byteOffset, final = author == "assistant", finalSource,
                    writerVersion = metadata.Version, sourceType = originalSourceType ?? type, timestampFallback = fallbackTimestamp, subagent = metadata.Subagent
                }));
        }
        EvidenceRecord? Flush(string reason)
        {
            var pending = metadata.PendingAssistant; metadata.PendingAssistant = null;
            if (pending is null || pending.Cwd != metadata.Cwd || string.IsNullOrWhiteSpace(pending.Text)) return null;
            metadata.FinalEmitted = true;
            return Record(pending.Text, "assistant", pending.RecordId, "final", reason, pending.Timestamp, pending.Turn, originalByteOffset: pending.ByteOffset, originalSourceType: "assistant");
        }
        if (source.Kind == "claude")
        {
            if (type == "system" && Text(root, "subtype") == "turn_duration")
            {
                if (Flush("turn_boundary") is { } final) yield return final;
                yield return Record("Assistant turn ended (source turn-duration event).", "system", identity, "lifecycle", kind: "lifecycle", state: "last-observed: turn_complete");
                yield break;
            }
            if (type is not ("user" or "assistant")) yield break;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            { metadata.Gap = "Message record lacks its expected content object."; yield break; }
            if (type == "user")
            {
                // tool_result envelopes have role=user but are not a person's words.
                // A compaction summary is written by Claude Code, not the person; it is flagged explicitly.
                if (TranscriptContent.HasBlock(message, "tool_result") || IsTrue(root, "isMeta") || IsTrue(root, "isCompactSummary")) yield break;
                var body = TranscriptContent.HumanRequest(TranscriptContent.MessageText(message));
                if (TranscriptContent.IsInterruption(body))
                {
                    metadata.PendingAssistant = null; metadata.TurnCompleted = true;
                    yield return Record("User interrupted the assistant turn (source marker).", "system", identity, "lifecycle", kind: "lifecycle", state: "last-observed: turn_aborted");
                    yield break;
                }
                if (!TranscriptContent.IsHumanText(body)) yield break;
                if (Flush("turn_boundary") is { } prior) yield return prior;
                SetTurn(metadata, identity);
                yield return Record(body, "user", identity, "user");
            }
            else
            {
                if (TranscriptContent.HasBlock(message, "tool_use") || Text(message, "stop_reason") is "tool_use" or "max_tokens") { metadata.PendingAssistant = null; yield break; }
                var body = TranscriptContent.MessageText(message);
                if (string.IsNullOrWhiteSpace(body)) yield break;
                metadata.Turn ??= "orphan:" + identity;
                var messageId = Text(message, "id");
                if (messageId.Length == 0) messageId = identity;
                var pending = metadata.PendingAssistant;
                // Claude may persist separate text blocks from one streamed message. Retain
                // only that message's bounded text; another message supersedes commentary.
                var continuesMessage = pending is not null && pending.MessageId == messageId;
                if (continuesMessage)
                {
                    // Persisted streaming snapshots can repeat or extend the
                    // whole text, rather than append a new text block.
                    if (pending!.Text.StartsWith(body, StringComparison.Ordinal)) body = pending.Text;
                    else if (!body.StartsWith(pending.Text, StringComparison.Ordinal)) body = pending.Text + "\n" + body;
                }
                // A candidate lives only inside this bounded parse pass.
                // Redact on final emission / checkpoint persistence, avoiding
                // repeatedly filtering commentary that is discarded by a tool.
                metadata.PendingAssistant = new() { Text = body[..Math.Min(body.Length, MemoryLimits.MessageCharacters)], RecordId = continuesMessage ? pending!.RecordId : identity, MessageId = messageId,
                    Timestamp = continuesMessage ? pending!.Timestamp : timestamp, Turn = metadata.Turn, Cwd = metadata.Cwd, ByteOffset = continuesMessage ? pending!.ByteOffset : byteOffset };
                if (Text(message, "stop_reason") == "end_turn" && Flush("stop_reason") is { } final) yield return final;
            }
        }
        else if (type == "response_item")
        {
            if (Text(payload, "type") != "message") yield break;
            var role = Text(payload, "role");
            if (role == "user")
            {
                var body = TranscriptContent.HumanRequest(TranscriptContent.MessageText(payload));
                // Codex delivers a requested code review as a user-role envelope. Its text is the
                // reviewer model's output, so it is kept as an AI-written result in its own turn.
                if (TranscriptContent.ReviewResult(body) is { } review)
                {
                    yield return Record(ReviewBody(review), "assistant", identity, "final", "review_result", turn: ReviewTurn(identity));
                    yield break;
                }
                if (!TranscriptContent.IsHumanText(body)) yield break;
                if (metadata.Turn is null || metadata.TurnCompleted) SetTurn(metadata, identity);
                yield return Record(body, "user", identity, "user");
            }
            else if (role == "assistant" && Text(payload, "phase") == "final_answer")
            {
                var body = TranscriptContent.MessageText(payload);
                if (string.IsNullOrWhiteSpace(body)) yield break;
                metadata.Turn ??= "orphan:" + identity;
                metadata.FinalEmitted = true;
                yield return Record(body, "assistant", identity, "final", "phase");
            }
        }
        else if (type == "event_msg")
        {
            var eventType = Text(payload, "type");
            if (eventType is "task_started" or "turn_started")
            {
                SetTurn(metadata, Text(payload, "turn_id") is { Length: > 0 } turn ? MetadataValue(turn, 256, metadata, true) : identity);
                yield return Record(eventType.Replace('_', ' '), "system", identity, "lifecycle", kind: "lifecycle", state: "last-observed: " + eventType);
            }
            else if (eventType is "task_complete" or "turn_complete")
            {
                if (metadata.Turn is null && Text(payload, "turn_id") is { Length: > 0 } turn) SetTurn(metadata, MetadataValue(turn, 256, metadata, true));
                var last = Text(payload, "last_agent_message");
                if (!metadata.FinalEmitted && !string.IsNullOrWhiteSpace(last))
                {
                    metadata.Turn ??= "orphan:" + identity;
                    yield return Record(last, "assistant", identity, "final", "task_complete");
                    metadata.FinalEmitted = true;
                }
                metadata.TurnCompleted = true;
                var hasError = payload.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
                yield return Record(hasError ? "Turn completed with an explicitly recorded error." : "Turn completed (source event; implementation correctness is unverified).",
                    "system", identity, "lifecycle", kind: "lifecycle", state: hasError ? "last-observed: error" : "last-observed: " + eventType);
            }
            else if (eventType is "turn_aborted" or "error")
            {
                metadata.PendingAssistant = null; metadata.TurnCompleted = true;
                yield return Record(eventType.Replace('_', ' '), "system", identity, "lifecycle", kind: "lifecycle", state: "last-observed: " + eventType);
            }
            // user_message/agent_message duplicate response_item; tools/reasoning are omitted.
        }
    }

    private static void SetTurn(Metadata metadata, string turn)
    {
        if (metadata.Turn == turn) return;
        metadata.Turn = turn; metadata.FinalEmitted = false; metadata.TurnCompleted = false; metadata.PendingAssistant = null;
    }

    private static bool OmittableOversized(string kind, ReadOnlySpan<byte> prefix)
    {
        // Inspect envelope discriminators only. A huge omitted tool result is
        // not missing human history; arbitrary text never decides its type.
        var reader = new Utf8JsonReader(prefix, isFinalBlock: false, state: default);
        string? envelope = null;
        var message = false; var content = false;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var depth = reader.CurrentDepth;
                if (depth == 1 && reader.ValueTextEquals("message")) message = true;
                if (message && depth == 2 && reader.ValueTextEquals("content")) content = true;
                if (!reader.ValueTextEquals("type")) continue;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) continue;
                var type = reader.GetString();
                if (depth == 1)
                {
                    envelope = type;
                    if (kind == "codex" && type is "world_state" or "token_usage_record" or "compacted" or "inter_agent_communication_metadata") return true;
                }
                else if (kind == "codex" && depth == 2 && envelope == "response_item"
                    && type is "function_call_output" or "function_call" or "custom_tool_call_output" or "custom_tool_call" or "reasoning" or "agent_message") return true;
                else if (kind == "claude" && content && depth == 4 && envelope is "user" or "assistant"
                    && type is "tool_result" or "tool_use" or "thinking") return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static string Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool IsTrue(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string WorkingDirectory(string value, Metadata metadata)
    {
        if (value.Length <= 1024) return value;
        metadata.Gap = "Oversized working-directory metadata was rejected; no conversation payload was indexed.";
        return "";
    }
    private static string MetadataValue(string value, int maximum, Metadata metadata, bool identity = false)
    {
        if (value.Length <= maximum) return value;
        metadata.Gap = "Oversized session metadata was bounded; attribution details may be partial.";
        return identity ? "bounded-" + Hash(value) : value[..maximum];
    }
    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Read to its last byte, with no partial final line still waiting for the rest of the record.
    private bool CompletelyRead(StreamCheckpoint checkpoint)
    {
        if (checkpoint.Offset < checkpoint.Length) return false;
        try { return !(checkpoint.MetadataJson is { } json && JsonSerializer.Deserialize<Metadata>(json, _json)?.WaitingForTail == true); }
        catch (JsonException) { return false; }
    }

    // Shared with the stored-row repair, so a re-labelled review result keeps the identity a later replay gives it.
    internal static string ExchangeIdentity(string kind, string environment, string session, string turn) => Hash($"{kind}\n{environment}\n{session}\n{turn}");
    internal static string AssistantIdentity(string kind, string environment, string session, string turn) => Hash($"{kind}\n{environment}\n{session}\nfinal:{turn}");
    internal static string ReviewTurn(string sourceRecordId) => "review:" + sourceRecordId;
    internal static string ReviewBody(string results) => "Code review result:\n" + results;
    private static string Prefix(Stream file, int count)
    {
        var bytes = new byte[count]; file.Position = 0; var read = file.Read(bytes, 0, count);
        return read + ":" + Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, read)));
    }
    private static bool PrefixMatches(Stream file, string fingerprint) => int.TryParse(fingerprint.Split(':')[0], out var count) && count >= 0 && count <= 128 && Prefix(file, count) == fingerprint;
    private static string Anchor(Stream file, long offset)
    {
        var count = (int)Math.Min(offset, 128); var bytes = new byte[count]; file.Position = offset - count; var read = file.Read(bytes, 0, count);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, read)));
    }
    private static bool ValidTimestamp(string? timestamp) => !string.IsNullOrWhiteSpace(timestamp) && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    private static string? Unsupported(SourceConfig source)
    {
        var native = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        if (source.Environment != native && !(OperatingSystem.IsLinux() && source.Environment == "wsl")
            || source.RootPath.StartsWith("\\\\", StringComparison.Ordinal) || source.RootPath.StartsWith("//", StringComparison.Ordinal))
            return "Cross-environment/UNC observation is unsupported. Run the collector inside WSL for WSL-native history, or select a native local folder.";
        if (source.Kind is not ("claude" or "codex" or "fixture")) return "This source adapter is not implemented.";
        try
        {
            for (var current = Path.GetFullPath(source.RootPath); !string.IsNullOrWhiteSpace(current); current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return "Reparse-point source roots or ancestors are unsupported; choose the actual native source directory.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return "Selected source boundary cannot be validated."; }
        return null;
    }
    private IEnumerable<string> EnumerateFiles(SourceConfig source, CancellationToken token)
    {
        var includeSubagents = store.IncludeSubagents;
        var directories = new Stack<(string Path, int Depth)>();
        directories.Push((source.RootPath, 0));
        var visited = 0;
        while (directories.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 10000) { AddGap(source.Id, "Directory discovery limit reached; select a narrower source folder."); yield break; }
            foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                { AddGap(source.Id, "A reparse point was excluded from the selected source; target history was not read."); continue; }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!includeSubagents && Path.GetFileName(path).Equals("subagents", StringComparison.OrdinalIgnoreCase)) continue;
                    if (directory.Depth >= 12 || directories.Count >= 10000) AddGap(source.Id, "Directory depth/queue limit reached; select a narrower source folder.");
                    else directories.Push((path, directory.Depth + 1));
                }
                else if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) yield return path;
                // While both spellings exist, the plain file is the conversation; skip its compressed copy.
                else if (RolloutFiles.IsCompressed(path) && !File.Exists(RolloutFiles.Logical(path))) yield return path;
            }
        }
    }
    private bool Eligible(SourceConfig source, string path)
    {
        if (source.Kind == "fixture") return true;
        if (!store.IncludeSubagents && (path.Replace('\\', '/').Contains("/subagents/", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).StartsWith("agent-", StringComparison.OrdinalIgnoreCase))) return false;
        return File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddDays(-store.LookbackDays);
    }

    private static bool Allowed(SourceConfig source, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(source.RootPath), Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
        try
        {
            var boundary = Path.GetFullPath(source.RootPath).TrimEnd(Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(path);
            while (candidate.Length >= boundary.Length)
            {
                if ((File.Exists(candidate) || Directory.Exists(candidate)) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) return false;
                if (candidate.Equals(boundary, StringComparison.OrdinalIgnoreCase)) break;
                candidate = Path.GetDirectoryName(candidate) ?? "";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        var name = Path.GetFileName(RolloutFiles.Logical(path));
        if (!name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || name.Contains("auth", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase)) return false;
        return source.Kind != "codex" || name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase);
    }
    private void AddGap(string source, string gap)
    {
        lock (_state)
        {
            if (!_gaps.TryGetValue(source, out var gaps)) _gaps[source] = gaps = new(StringComparer.Ordinal);
            if (gaps.Count < 6) gaps.Add(gap);
        }
    }
    private string? Gap(string source)
    {
        lock (_state)
        {
            var messages = (_gaps.GetValueOrDefault(source) ?? []).Concat(_streamGaps.Where(p => p.Key.Source == source).Select(p => p.Value)).Distinct().Take(6).ToArray();
            return messages.Length == 0 ? null : string.Join(" ", messages);
        }
    }
    private void SetStreamGap(string source, string stream, string? gap)
    {
        lock (_state)
        {
            if (gap is null) _streamGaps.Remove((source, stream));
            else _streamGaps[(source, stream)] = gap;
        }
    }
    private void UpdateStatus(SourceConfig source)
    {
        if (_capacityBlocked) return;
        var pending = _pending.TryGetValue(source.Id, out var paths) ? paths.Count : 0;
        var gap = Gap(source.Id);
        store.SetSourceStatus(source.Id, pending > 0 ? "catching-up" : gap is not null ? "gap" : "current", gap, pending);
    }
    public async ValueTask DisposeAsync()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true; _desiredRunning = false; _run?.Cancel();
        }
        await StopCoreAsync(); _serial.Dispose(); _reconcile.Dispose(); _lifecycle.Dispose();
    }
    private sealed class PendingAssistant
    {
        public string Text { get; set; } = "";
        public string RecordId { get; set; } = "";
        public string MessageId { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string? Turn { get; set; }
        public string? Cwd { get; set; }
        public long ByteOffset { get; set; }
    }
    private sealed class Metadata
    {
        public int ParserVersion { get; set; }
        // Size and time of the .jsonl.zst file last read for this stream; null when read from the plain file.
        public long? CompressedLength { get; set; }
        public long? CompressedTicks { get; set; }
        public long ReplayBelow { get; set; }
        public int LookbackDays { get; set; }
        public bool IncludeSubagents { get; set; }
        public string? Session { get; set; }
        public string? Cwd { get; set; }
        public string? Branch { get; set; }
        public string? Version { get; set; }
        public string? Timestamp { get; set; }
        public string? Gap { get; set; }
        public bool ApprovedHistory { get; set; }
        public bool Subagent { get; set; }
        public string? Turn { get; set; }
        public bool FinalEmitted { get; set; }
        public bool TurnCompleted { get; set; }
        public PendingAssistant? PendingAssistant { get; set; }
        public bool Discarding { get; set; }
        public bool WaitingForTail { get; set; }
        public long CreationTicks { get; set; }
    }
}
