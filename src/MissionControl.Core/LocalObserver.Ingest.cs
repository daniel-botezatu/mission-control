namespace MissionControl.Core;

public sealed partial class LocalObserver
{
    /// <summary>Wake the normal read-only collector for one allowed source file.</summary>
    public async Task IngestNowAsync(string sourceId, string transcriptPath, CancellationToken token = default)
    {
        var source = LocalSources().SingleOrDefault(item => item.Id == sourceId && item.Enabled)
            ?? throw new ArgumentException("SOURCE_NOT_ENABLED");
        var path = Path.GetFullPath(transcriptPath);
        CancellationTokenSource generation;
        CancellationToken runningToken;
        lock (_state)
        {
            if (_run is null || _run.IsCancellationRequested || !_desiredRunning)
                throw new InvalidOperationException("COLLECTOR_NOT_RUNNING");
            generation = _run;
            runningToken = _run.Token;
        }
        if (Unsupported(source) is not null || !Allowed(source, path) || !Eligible(source, path))
            throw new ArgumentException("TRANSCRIPT_OUTSIDE_APPROVED_SOURCE");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, runningToken);
        token = linked.Token;
        await _serial.WaitAsync(token);
        try
        {
            lock (_state)
                if (_run != generation || token.IsCancellationRequested || !_desiredRunning)
                    throw new OperationCanceledException("COLLECTOR_RUN_CHANGED", token);
            // ProcessFileAsync and CommitBatch retain the normal project-approval,
            // parser, checkpoint, redaction and source-boundary checks.
            await ProcessFileAsync(source, path, token);
            UpdateStatus(source);
        }
        finally { _serial.Release(); }
        PublishProgress();
        Changed?.Invoke();
    }
}
