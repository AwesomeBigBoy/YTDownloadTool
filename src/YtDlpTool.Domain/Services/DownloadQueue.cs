using System.Collections.Concurrent;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public sealed class DownloadQueue : IDisposable
{
    private readonly IDownloadExecutor _executor;
    private readonly Action<QueueEvent> _onEvent;
    private readonly AppLogger? _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, int> _retryCounts = new();
    private readonly ConcurrentQueue<DownloadJob> _pending = new();
    private readonly SemaphoreSlim _slot;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _max429Retries;
    private readonly TimeSpan _rateLimitRetryDelay;
    private int _maxConcurrency;

    private const int DefaultMax429Retries = 1;
    private static readonly TimeSpan DefaultRateLimitRetryDelay = TimeSpan.FromSeconds(30);

    public DownloadQueue(IDownloadExecutor executor, int maxConcurrency, Action<QueueEvent> onEvent)
        : this(executor, maxConcurrency, onEvent, logger: null,
               max429Retries: DefaultMax429Retries,
               rateLimitRetryDelay: DefaultRateLimitRetryDelay)
    { }

    public DownloadQueue(IDownloadExecutor executor, int maxConcurrency, Action<QueueEvent> onEvent, AppLogger? logger)
        : this(executor, maxConcurrency, onEvent, logger,
               max429Retries: DefaultMax429Retries,
               rateLimitRetryDelay: DefaultRateLimitRetryDelay)
    { }

    // Test-only ctor: lets tests shorten the rate-limit retry delay so they don't sleep 30s.
    public DownloadQueue(
        IDownloadExecutor executor,
        int maxConcurrency,
        Action<QueueEvent> onEvent,
        AppLogger? logger,
        int max429Retries,
        TimeSpan rateLimitRetryDelay)
    {
        if (maxConcurrency < 1 || maxConcurrency > 10)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "must be 1..10");
        _executor = executor;
        _onEvent = onEvent;
        _logger = logger;
        _maxConcurrency = maxConcurrency;
        _slot = new SemaphoreSlim(maxConcurrency, 10);
        _max429Retries = max429Retries;
        _rateLimitRetryDelay = rateLimitRetryDelay;
    }

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            if (value < 1 || value > 10) throw new ArgumentOutOfRangeException(nameof(value));
            var delta = value - _maxConcurrency;
            _maxConcurrency = value;
            if (delta > 0) _slot.Release(delta);
            // Shrinking concurrency takes effect as in-flight jobs complete.
        }
    }

    public void Enqueue(DownloadJob job)
    {
        _logger?.Info("download.queued", new Dictionary<string, string>
        {
            ["mode"] = job.Mode.ToString(),
            ["format"] = job.ChosenFormat.Height is { } h
                ? h.ToString() + "p"
                : (job.ChosenFormat.AudioBitrateKbps is { } b ? b.ToString() + "kbps" : "?"),
            ["has_clip"] = (job.ClipRange is not null).ToString(),
            ["sub_count"] = job.SubtitleLanguageCodes.Count.ToString()
        });
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
            _logger?.Info("download.cancelled", new Dictionary<string, string>
            {
                ["job_hash"] = AppLogger.HashSuffix(removed.Url)
            });
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
        var jobHash = AppLogger.HashSuffix(job.Url);
        var startedAt = DateTime.UtcNow;
        try
        {
            job.MarkDownloading();
            _logger?.Info("download.started", new Dictionary<string, string> { ["job_hash"] = jobHash });
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

            // 429 retry: if rate-limited and within retry budget, wait and retry once.
            if (!result.IsSuccess
                && !result.WasCancelled
                && result.Error?.Category == ErrorCategory.RateLimited)
            {
                var attempts = _retryCounts.AddOrUpdate(job.Id, 1, (_, n) => n + 1);
                if (attempts <= _max429Retries && !cts.IsCancellationRequested)
                {
                    _logger?.Info("download.rate_limited",
                        new Dictionary<string, string> { ["job_hash"] = jobHash });
                    // Re-emit a "rate-limited, retrying" event so UI can show waiting state.
                    _onEvent(new JobProgressEvent(job,
                        new DownloadProgressSnapshot(job.Progress, null, _rateLimitRetryDelay)));
                    try { await Task.Delay(_rateLimitRetryDelay, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* cancelled during wait → fall through */ }

                    if (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            result = await _executor.ExecuteAsync(job, progress, cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            result = new DownloadExecutionResult(false, null, null, WasCancelled: true);
                        }
                    }
                }
            }

            if (result.WasCancelled)
            {
                job.MarkCancelled();
                _logger?.Info("download.cancelled", new Dictionary<string, string> { ["job_hash"] = jobHash });
                _onEvent(new JobCancelledEvent(job));
            }
            else if (result.IsSuccess && result.OutputFilePath is not null)
            {
                job.MarkCompleted(result.OutputFilePath);
                var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                _logger?.Info("download.completed", new Dictionary<string, string>
                {
                    ["job_hash"] = jobHash,
                    ["elapsed_ms"] = elapsed.ToString()
                });
                _onEvent(new JobCompletedEvent(job, result.OutputFilePath));
            }
            else
            {
                var err = result.Error ?? new MappedError(ErrorCategory.UnknownError, "下載失敗", "E-UNKNOWN", false);
                job.MarkFailed(err.UserMessage, err.ErrorCode);
                _logger?.Warn("download.failed", new Dictionary<string, string>
                {
                    ["job_hash"] = jobHash,
                    ["error_code"] = err.ErrorCode
                });
                _onEvent(new JobFailedEvent(job, err));
            }
        }
        finally
        {
            _retryCounts.TryRemove(job.Id, out _);
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
