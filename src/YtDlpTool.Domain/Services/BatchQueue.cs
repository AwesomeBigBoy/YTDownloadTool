using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

/// <summary>One link of a batch and how to download it.</summary>
/// <param name="Quality">As QualityOptions.PreferredIndex reads it: "1080p", "best", "M4A", "MP3".</param>
public sealed record BatchWork(string Url, DownloadMode Mode, string Quality)
{
    /// <summary>This link's own identity in the state journal (BatchJournal).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The batch it was confirmed in (one 加入多部影片), handed on to its download.</summary>
    public Guid? BatchId { get; init; }
}

public enum BatchItemOutcome
{
    Added,
    Failed,
}

public sealed record BatchItemResult(BatchItemOutcome Outcome, MappedError? Error = null)
{
    public static readonly BatchItemResult Added = new(BatchItemOutcome.Added);
    public static BatchItemResult Failed(MappedError error) => new(BatchItemOutcome.Failed, error);
}

/// <summary>Where a batch stands; BatchStatusText turns it into the status line.</summary>
/// <param name="Total">Links accepted into the batch (duplicates and links already downloading excluded).</param>
/// <param name="Skipped">Duplicates and links already in the download list.</param>
/// <param name="CommonError">The most frequent reason among the failures.</param>
/// <param name="AbortedOn">Set when repeated failures that hit every video stopped the batch.</param>
public sealed record BatchProgress(
    int Total, int Done, int Added, int Failed, int Skipped,
    bool IsRunning, bool Stopped, MappedError? CommonError, MappedError? AbortedOn);

/// <summary>
/// v1.5.0: the links of a playlist or multi-link paste, parsed and queued one at a time by
/// <c>queueOne</c> (so downloads already running keep the bandwidth). A link is taken once
/// per batch, and not at all while <c>isAlreadyQueued</c> says it is downloading: two jobs
/// for one video would write the same files. Links added while a batch runs join it.
///
/// <see cref="DefaultStopAfter"/> failures in a row with the same code, of a kind that hits
/// every video alike (network, TLS, rate limit, a broken component, a full disk), stop the
/// rest: behind an HTTPS-inspecting proxy every parse fails the same way and each one can
/// take 30 s or more. Failures of single videos (removed, private, age-restricted) do not.
///
/// Call <see cref="Add"/> and <see cref="Stop"/> from one thread — the app uses the UI
/// thread, where every event is then raised too, because nothing here leaves the caller's
/// synchronization context. (An Add from another thread between the loop's last check and
/// its end would be accepted and never run.)
/// </summary>
public sealed class BatchQueue
{
    public const int DefaultStopAfter = 3;

    private static readonly HashSet<ErrorCategory> HitsEveryVideo = new()
    {
        ErrorCategory.NetworkError, ErrorCategory.RateLimited, ErrorCategory.ComponentMissing, ErrorCategory.DiskFull,
    };

    private readonly Func<BatchWork, CancellationToken, Task<BatchItemResult>> _queueOne;
    private readonly Func<string, bool> _isAlreadyQueued;
    private readonly int _stopAfter;
    private readonly object _gate = new();
    private readonly Queue<BatchWork> _work = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Count, MappedError Error)> _errors = new();
    private readonly List<BatchWork> _afterStop = new();
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private int _total, _done, _added, _failed, _skipped, _streak;
    private string? _streakCode;
    private bool _isRunning, _stopRequested, _stopped;
    private MappedError? _abortedOn;

    public BatchQueue(Func<BatchWork, CancellationToken, Task<BatchItemResult>> queueOne,
        Func<string, bool> isAlreadyQueued, int stopAfterRepeatedFailures = DefaultStopAfter)
    {
        _queueOne = queueOne;
        _isAlreadyQueued = isAlreadyQueued;
        _stopAfter = stopAfterRepeatedFailures;
    }

    /// <summary>After every change of <see cref="Progress"/>.</summary>
    public event Action? Changed;

    /// <summary>Links taken into the batch, after duplicates and links already downloading are left out.</summary>
    public event Action<IReadOnlyList<BatchWork>>? Accepted;

    /// <summary>
    /// An accepted link that left the batch — queued, failed, or dropped by 停止 or a stop
    /// rule. Raised exactly once per accepted link.
    /// </summary>
    public event Action<BatchWork>? Settled;

    /// <summary>Completes when the current batch has finished (completed when idle).</summary>
    public Task Completion
    {
        get { lock (_gate) return _running; }
    }

    public BatchProgress Progress
    {
        get
        {
            lock (_gate)
            {
                var common = _errors.Values.OrderByDescending(e => e.Count).Select(e => e.Error).FirstOrDefault();
                return new BatchProgress(_total, _done, _added, _failed, _skipped,
                    _isRunning, _stopped, common, _abortedOn);
            }
        }
    }

    public void Add(IEnumerable<BatchWork> work)
    {
        CancellationToken? start = null;
        var accepted = new List<BatchWork>();
        lock (_gate)
        {
            if (_isRunning && _stopRequested)
            {
                // Added while a stopped batch is still unwinding its last parse: these
                // belong to a new batch, which starts as soon as that one has ended.
                _afterStop.AddRange(work);
                return;
            }
            if (!_isRunning) Reset();
            foreach (var w in work)
            {
                if (!_seen.Add(w.Url) || _isAlreadyQueued(w.Url))
                {
                    _skipped++;
                    continue;
                }
                _work.Enqueue(w);
                accepted.Add(w);
                _total++;
            }
            if (!_isRunning && _work.Count > 0)
            {
                _isRunning = true;
                _cts = new CancellationTokenSource();
                start = _cts.Token;
            }
        }
        if (accepted.Count > 0) Accepted?.Invoke(accepted);
        Changed?.Invoke();
        if (start is { } token)
        {
            var run = RunAsync(token);
            lock (_gate)
            {
                if (_isRunning) _running = run;
            }
        }
    }

    /// <summary>停止: nothing more is added; the link being parsed is dropped, not counted as failed.</summary>
    public void Stop()
    {
        List<BatchWork> dropped;
        lock (_gate)
        {
            if (!_isRunning) return;
            _stopRequested = true;
            dropped = _work.ToList();
            _work.Clear();
            _cts?.Cancel();
        }
        foreach (var w in dropped) Settled?.Invoke(w);
    }

    private void Reset()
    {
        _total = _done = _added = _failed = _skipped = _streak = 0;
        _streakCode = null;
        _stopRequested = _stopped = false;
        _abortedOn = null;
        _errors.Clear();
        _seen.Clear();
        _work.Clear();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            BatchWork next;
            lock (_gate)
            {
                if (ct.IsCancellationRequested || _work.Count == 0) break;
                next = _work.Dequeue();
            }

            BatchItemResult result;
            try
            {
                result = await _queueOne(next, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Settled?.Invoke(next);
                break;
            }
            catch (Exception ex)
            {
                result = BatchItemResult.Failed(new MappedError(ErrorCategory.UnknownError, ex.Message, "E-BATCH", false));
            }

            List<BatchWork>? abandoned = null;
            bool stoppedMidParse;
            lock (_gate)
            {
                // Stopped while this one was being parsed: it is not a failure.
                stoppedMidParse = ct.IsCancellationRequested;
                if (!stoppedMidParse)
                {
                    _done++;
                    if (result.Outcome == BatchItemOutcome.Added)
                    {
                        _added++;
                        _streak = 0;
                        _streakCode = null;
                    }
                    else
                    {
                        var error = result.Error ?? new MappedError(ErrorCategory.UnknownError, "原因不明", "E-UNKNOWN", false);
                        Count(error);
                        if (_streak >= _stopAfter)
                        {
                            _abortedOn = error;
                            abandoned = _work.ToList();
                            _work.Clear();
                        }
                    }
                }
            }
            Settled?.Invoke(next);
            if (stoppedMidParse) break;
            foreach (var w in abandoned ?? new List<BatchWork>()) Settled?.Invoke(w);
            Changed?.Invoke();
        }

        List<BatchWork>? next2 = null;
        lock (_gate)
        {
            _stopped = _stopRequested;
            _isRunning = false;
            _cts?.Dispose();
            _cts = null;
            if (_afterStop.Count > 0)
            {
                next2 = _afterStop.ToList();
                _afterStop.Clear();
            }
        }
        Changed?.Invoke();
        if (next2 is not null) Add(next2);
    }

    private void Count(MappedError error)
    {
        _failed++;
        _errors[error.ErrorCode] = (_errors.TryGetValue(error.ErrorCode, out var seen) ? seen.Count + 1 : 1, error);
        if (!HitsEveryVideo.Contains(error.Category))
        {
            _streak = 0;
            _streakCode = null;
        }
        else if (error.ErrorCode == _streakCode)
        {
            _streak++;
        }
        else
        {
            _streakCode = error.ErrorCode;
            _streak = 1;
        }
    }
}
