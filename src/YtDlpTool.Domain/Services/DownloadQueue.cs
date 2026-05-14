using System.Collections.Concurrent;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public sealed class DownloadQueue : IDisposable
{
    private readonly IDownloadExecutor _executor;
    private readonly Action<QueueEvent> _onEvent;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentQueue<DownloadJob> _pending = new();
    private readonly SemaphoreSlim _slot;
    private readonly CancellationTokenSource _shutdown = new();
    private int _maxConcurrency;

    public DownloadQueue(IDownloadExecutor executor, int maxConcurrency, Action<QueueEvent> onEvent)
    {
        if (maxConcurrency < 1 || maxConcurrency > 5)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "must be 1..5");
        _executor = executor;
        _onEvent = onEvent;
        _maxConcurrency = maxConcurrency;
        _slot = new SemaphoreSlim(maxConcurrency, 5);
    }

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            if (value < 1 || value > 5) throw new ArgumentOutOfRangeException(nameof(value));
            var delta = value - _maxConcurrency;
            _maxConcurrency = value;
            if (delta > 0) _slot.Release(delta);
            // Shrinking concurrency takes effect as in-flight jobs complete.
        }
    }

    public void Enqueue(DownloadJob job)
    {
        _onEvent(new JobEnqueuedEvent(job));
        _pending.Enqueue(job);
        _ = TryStartNextAsync();
    }

    public bool Cancel(Guid jobId)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        // Pending cancellation — rebuild queue without that job.
        var keep = new List<DownloadJob>();
        DownloadJob? removed = null;
        while (_pending.TryDequeue(out var j))
            if (j.Id == jobId) removed = j;
            else keep.Add(j);
        foreach (var j in keep) _pending.Enqueue(j);
        if (removed is not null)
        {
            removed.MarkCancelled();
            _onEvent(new JobCancelledEvent(removed));
            return true;
        }
        return false;
    }

    private async Task TryStartNextAsync()
    {
        if (_shutdown.IsCancellationRequested) return;
        if (!await _slot.WaitAsync(0).ConfigureAwait(false)) return;
        if (!_pending.TryDequeue(out var job)) { _slot.Release(); return; }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _running[job.Id] = cts;

        _ = RunJobAsync(job, cts);
        _ = TryStartNextAsync(); // try to fill remaining slots
    }

    private async Task RunJobAsync(DownloadJob job, CancellationTokenSource cts)
    {
        try
        {
            job.MarkDownloading();
            _onEvent(new JobStartedEvent(job));

            var progress = new Progress<DownloadProgressSnapshot>(snap =>
            {
                job.ReportProgress(snap.Percent, snap.BytesPerSecond, snap.Eta);
                _onEvent(new JobProgressEvent(job, snap));
            });

            DownloadExecutionResult result;
            try
            {
                result = await _executor.ExecuteAsync(job, progress, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new DownloadExecutionResult(false, null, null, WasCancelled: true);
            }

            if (result.WasCancelled)
            {
                job.MarkCancelled();
                _onEvent(new JobCancelledEvent(job));
            }
            else if (result.IsSuccess && result.OutputFilePath is not null)
            {
                job.MarkCompleted(result.OutputFilePath);
                _onEvent(new JobCompletedEvent(job, result.OutputFilePath));
            }
            else
            {
                var err = result.Error ?? new MappedError(ErrorCategory.UnknownError, "下載失敗", "E-UNKNOWN", false);
                job.MarkFailed(err.UserMessage, err.ErrorCode);
                _onEvent(new JobFailedEvent(job, err));
            }
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
            cts.Dispose();
            _slot.Release();
            _ = TryStartNextAsync();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var cts in _running.Values) try { cts.Cancel(); } catch { }
        _shutdown.Dispose();
        _slot.Dispose();
    }
}
