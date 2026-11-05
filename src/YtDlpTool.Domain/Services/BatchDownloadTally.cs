namespace YtDlpTool.Domain.Services;

/// <summary>How a batch's downloads ended, for its one summary toast.</summary>
public sealed record BatchTally(int Completed, int Failed);

/// <summary>
/// v1.5.0: a 50-video playlist used to mean 50 toasts. The downloads a batch queued report
/// once, together, when the last of them has ended and nothing more is being added.
/// Downloads queued any other way — and a 重試 of a batch download — keep their own toast.
/// </summary>
public sealed class BatchDownloadTally
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _pending = new();
    private int _completed, _failed;
    private bool _adding;

    public void AddingStarted()
    {
        lock (_gate) _adding = true;
    }

    public void Expect(Guid jobId)
    {
        lock (_gate) _pending.Add(jobId);
    }

    public bool IsBatchJob(Guid jobId)
    {
        lock (_gate) return _pending.Contains(jobId);
    }

    /// <returns>The summary when this was the batch's last download; otherwise null.</returns>
    public BatchTally? Finish(Guid jobId, bool succeeded)
    {
        lock (_gate)
        {
            if (!_pending.Remove(jobId)) return null;
            if (succeeded) _completed++;
            else _failed++;
            return TakeSummaryIfDone();
        }
    }

    /// <summary>A cancelled batch download: no longer awaited, and not counted.</summary>
    public BatchTally? Forget(Guid jobId)
    {
        lock (_gate)
        {
            return _pending.Remove(jobId) ? TakeSummaryIfDone() : null;
        }
    }

    /// <returns>The summary when every download the batch queued has already ended.</returns>
    public BatchTally? AddingFinished()
    {
        lock (_gate)
        {
            _adding = false;
            return TakeSummaryIfDone();
        }
    }

    private BatchTally? TakeSummaryIfDone()
    {
        if (_adding || _pending.Count > 0 || _completed + _failed == 0) return null;
        var summary = new BatchTally(_completed, _failed);
        _completed = _failed = 0;
        return summary;
    }
}
