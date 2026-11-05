namespace YtDlpTool.Domain.Services;

/// <summary>
/// Limits how often a job's download progress reaches the UI (v1.5.0).
///
/// yt-dlp with --concurrent-fragments 8 prints several progress lines a second per job,
/// and before v1.5.0 every one of them was marshalled to the UI thread with a blocking
/// Dispatcher.Invoke and raised six property changes — on machines where WPF renders in
/// software. Four updates a second is smoother than the eye can use; the 100 % report is
/// never dropped. Called from worker threads, hence the lock.
/// </summary>
public sealed class ProgressThrottle
{
    private readonly TimeSpan _minInterval;
    private readonly Dictionary<Guid, DateTime> _lastPublished = new();
    private readonly object _gate = new();

    public ProgressThrottle(TimeSpan minInterval) => _minInterval = minInterval;

    public bool ShouldPublish(Guid jobId, double percent, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (percent < 100 &&
                _lastPublished.TryGetValue(jobId, out var last) &&
                nowUtc - last < _minInterval)
            {
                return false;
            }
            _lastPublished[jobId] = nowUtc;
            return true;
        }
    }

    public void Forget(Guid jobId)
    {
        lock (_gate) _lastPublished.Remove(jobId);
    }
}
