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
    private readonly TimeSpan _noProgressTimeout;
    private readonly TimeSpan _watchdogInterval;
    private int _maxConcurrency;

    private const int DefaultMax429Retries = 1;
    private static readonly TimeSpan DefaultRateLimitRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultNoProgressTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromSeconds(5);

    public DownloadQueue(IDownloadExecutor executor, int maxConcurrency, Action<QueueEvent> onEvent)
        : this(executor, maxConcurrency, onEvent, logger: null,
               max429Retries: DefaultMax429Retries,
               rateLimitRetryDelay: DefaultRateLimitRetryDelay,
               noProgressTimeout: DefaultNoProgressTimeout,
               watchdogInterval: DefaultWatchdogInterval)
    { }

    public DownloadQueue(IDownloadExecutor executor, int maxConcurrency, Action<QueueEvent> onEvent, AppLogger? logger)
        : this(executor, maxConcurrency, onEvent, logger,
               max429Retries: DefaultMax429Retries,
               rateLimitRetryDelay: DefaultRateLimitRetryDelay,
               noProgressTimeout: DefaultNoProgressTimeout,
               watchdogInterval: DefaultWatchdogInterval)
    { }

    // Test-only ctor: lets tests shorten the rate-limit retry delay so they don't sleep 30s.
    public DownloadQueue(
        IDownloadExecutor executor,
        int maxConcurrency,
        Action<QueueEvent> onEvent,
        AppLogger? logger,
        int max429Retries,
        TimeSpan rateLimitRetryDelay)
        : this(executor, maxConcurrency, onEvent, logger,
               max429Retries, rateLimitRetryDelay,
               noProgressTimeout: DefaultNoProgressTimeout,
               watchdogInterval: DefaultWatchdogInterval)
    { }

    // Full ctor: lets tests also shorten the no-progress watchdog timings.
    public DownloadQueue(
        IDownloadExecutor executor,
        int maxConcurrency,
        Action<QueueEvent> onEvent,
        AppLogger? logger,
        int max429Retries,
        TimeSpan rateLimitRetryDelay,
        TimeSpan noProgressTimeout,
        TimeSpan watchdogInterval)
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
        _noProgressTimeout = noProgressTimeout;
        _watchdogInterval = watchdogInterval;
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
            ["job_hash"] = JobHash(job),
            ["url_hash"] = AppLogger.HashSuffix(job.Url),
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

    /// <summary>
    /// Per-job hash derived from the job's Guid (first 8 hex chars). Stable for the
    /// life of the job and unique even when the same URL is enqueued repeatedly,
    /// which makes log correlation across download.queued / started / progress /
    /// completed / failed straightforward. Cross-job correlation by URL still uses
    /// AppLogger.HashSuffix(url) in a separate url_hash field.
    /// </summary>
    private static string JobHash(DownloadJob job) => job.Id.ToString("N").Substring(0, 8);

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
                ["job_hash"] = JobHash(removed),
                ["url_hash"] = AppLogger.HashSuffix(removed.Url)
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
        var jobHash = JobHash(job);
        var urlHash = AppLogger.HashSuffix(job.Url);
        var startedAt = DateTime.UtcNow;
        // Stuck-download watchdog: track the wall-clock time of the last progress report
        // and a flag the watchdog flips when it's the one who cancelled the job.
        long lastProgressTicks = DateTime.UtcNow.Ticks;
        var stuckCancelled = 0; // 0 = not stuck; 1 = watchdog tripped
        // Fix E: gate all terminal event emissions so a watchdog-trip racing with a
        // real executor failure doesn't produce two JobFailedEvents in a row.
        var terminalEmitted = 0;
        using var watchdogStop = new CancellationTokenSource();
        try
        {
            job.MarkDownloading();
            _logger?.Info("download.started", new Dictionary<string, string>
            {
                ["job_hash"] = jobHash,
                ["url_hash"] = urlHash
            });
            _onEvent(new JobStartedEvent(job));

            var progress = new Progress<DownloadProgressSnapshot>(snap =>
            {
                Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                job.ReportProgress(snap.Percent, snap.BytesPerSecond, snap.Eta);
                _onEvent(new JobProgressEvent(job, snap));
            });

            // Start the watchdog: every `_watchdogInterval`, check the gap since the last
            // progress report. If it exceeds `_noProgressTimeout`, mark this job as stuck
            // and cancel its CTS so the executor unwinds.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!watchdogStop.IsCancellationRequested && !cts.IsCancellationRequested)
                    {
                        try { await Task.Delay(_watchdogInterval, watchdogStop.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        var last = new DateTime(Interlocked.Read(ref lastProgressTicks), DateTimeKind.Utc);
                        if (DateTime.UtcNow - last > _noProgressTimeout)
                        {
                            Interlocked.Exchange(ref stuckCancelled, 1);
                            _logger?.Warn("download.stuck", new Dictionary<string, string>
                            {
                                ["job_hash"] = jobHash,
                                ["url_hash"] = urlHash,
                                ["timeout_s"] = ((int)_noProgressTimeout.TotalSeconds).ToString()
                            });
                            try { cts.Cancel(); } catch { }
                            return;
                        }
                    }
                }
                catch { /* watchdog should never throw out */ }
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

            // If the watchdog cancelled the job, rewrite the result so the UI sees a failure
            // with a useful message instead of a generic "cancelled". Preserve any
            // diagnostics the executor attached (Fix B v1.1.8: stderr + stdout-tail) so
            // the download.failed log isn't empty even when stderr was silent.
            if (result.WasCancelled && Interlocked.CompareExchange(ref stuckCancelled, 0, 0) == 1)
            {
                result = new DownloadExecutionResult(
                    false,
                    null,
                    new MappedError(ErrorCategory.NetworkError,
                        "下載卡住，請選擇其他畫質或音質重新下載",
                        "E-STUCK01",
                        true,
                        RawDetails: result.Error?.RawDetails),
                    WasCancelled: false);
            }

            // 429 retry: if rate-limited and within retry budget, wait and retry once.
            if (!result.IsSuccess
                && !result.WasCancelled
                && result.Error?.Category == ErrorCategory.RateLimited)
            {
                var attempts = _retryCounts.AddOrUpdate(job.Id, 1, (_, n) => n + 1);
                if (attempts <= _max429Retries && !cts.IsCancellationRequested)
                {
                    _logger?.Info("download.rate_limited", new Dictionary<string, string>
                    {
                        ["job_hash"] = jobHash,
                        ["url_hash"] = urlHash
                    });
                    // Re-emit a "rate-limited, retrying" event so UI can show waiting state.
                    _onEvent(new JobProgressEvent(job,
                        new DownloadProgressSnapshot(job.Progress, null, _rateLimitRetryDelay)));
                    try { await Task.Delay(_rateLimitRetryDelay, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* cancelled during wait → fall through */ }

                    if (!cts.IsCancellationRequested)
                    {
                        Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                        try
                        {
                            result = await _executor.ExecuteAsync(job, progress, cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            result = new DownloadExecutionResult(false, null, null, WasCancelled: true);
                        }
                    }

                    // Re-check the stuck flag after the retry: the watchdog may have
                    // fired during the second executor call. Without this re-check the
                    // job emits as a generic cancellation instead of E-STUCK01.
                    if (result.WasCancelled && Interlocked.CompareExchange(ref stuckCancelled, 0, 0) == 1)
                    {
                        result = new DownloadExecutionResult(
                            false,
                            null,
                            new MappedError(ErrorCategory.NetworkError,
                                "下載卡住，請選擇其他畫質或音質重新下載",
                                "E-STUCK01",
                                true,
                                RawDetails: result.Error?.RawDetails),
                            WasCancelled: false);
                    }
                }
            }

            // Stop the watchdog BEFORE emitting any terminal event. Without this, a
            // late watchdog tick can race ahead and produce a second cancel/failure
            // event after the executor has already returned a real result. The
            // terminalEmitted guard below is the belt-and-braces against that race.
            try { watchdogStop.Cancel(); } catch { }

            if (result.WasCancelled)
            {
                if (Interlocked.Exchange(ref terminalEmitted, 1) == 0)
                {
                    job.MarkCancelled();
                    _logger?.Info("download.cancelled", new Dictionary<string, string>
                    {
                        ["job_hash"] = jobHash,
                        ["url_hash"] = urlHash
                    });
                    _onEvent(new JobCancelledEvent(job));
                }
            }
            else if (result.IsSuccess && result.OutputFilePath is not null)
            {
                if (Interlocked.Exchange(ref terminalEmitted, 1) == 0)
                {
                    job.MarkCompleted(result.OutputFilePath);
                    var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                    _logger?.Info("download.completed", new Dictionary<string, string>
                    {
                        ["job_hash"] = jobHash,
                        ["url_hash"] = urlHash,
                        ["elapsed_ms"] = elapsed.ToString()
                    });
                    _onEvent(new JobCompletedEvent(job, result.OutputFilePath));
                }
            }
            else
            {
                if (Interlocked.Exchange(ref terminalEmitted, 1) == 0)
                {
                    var err = result.Error ?? new MappedError(ErrorCategory.UnknownError, "下載失敗", "E-UNKNOWN", false);
                    job.MarkFailed(err.UserMessage, err.ErrorCode);
                    // Log the real failure detail (truncated raw stderr) alongside the code so
                    // bug reports actually contain the yt-dlp diagnosis instead of just
                    // the bucket name. This is a local-only log; we don't strip URLs.
                    // Fix 2: always emit the details field, with "(empty)" as a
                    // sentinel when stderr was empty/null. The field had previously
                    // collapsed to "" on rule matches that didn't propagate RawDetails,
                    // making bug reports useless. ErrorMapper now sets RawDetails
                    // on every code path, but keep the sentinel so a future regression
                    // still surfaces in logs instead of vanishing.
                    _logger?.Warn("download.failed", new Dictionary<string, string>
                    {
                        ["job_hash"] = jobHash,
                        ["url_hash"] = urlHash,
                        ["error_code"] = err.ErrorCode,
                        ["category"] = err.Category.ToString(),
                        ["details"] = string.IsNullOrEmpty(err.RawDetails) ? "(empty)" : err.RawDetails
                    });
                    _onEvent(new JobFailedEvent(job, err));
                }
            }
        }
        finally
        {
            try { watchdogStop.Cancel(); } catch { }
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
