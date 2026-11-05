using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public enum TaskbarState
{
    None,
    Indeterminate,
    Normal,
}

/// <summary>One download-list row as the taskbar sees it.</summary>
public readonly record struct QueueRow(Guid Id, JobStatus Status, double Percent);

/// <summary>
/// v1.5.0: what the taskbar button shows for the download list — the average progress of
/// the current batch, i.e. every download that has been waiting or running since the list
/// was last idle. Finished and failed members count as done, so the bar never runs
/// backwards when one of them ends; cancelled ones leave the batch. Nothing while idle.
/// </summary>
public sealed class TaskbarProgressTracker
{
    private readonly Dictionary<Guid, (double Percent, bool Done)> _batch = new();

    public (TaskbarState State, double Value) Update(IEnumerable<QueueRow> rows)
    {
        var current = rows.ToList();
        var active = current.Where(r => IsActive(r.Status)).ToList();
        if (active.Count == 0)
        {
            _batch.Clear();
            return (TaskbarState.None, 0);
        }

        var present = new HashSet<Guid>();
        foreach (var r in current)
        {
            present.Add(r.Id);
            if (IsActive(r.Status)) _batch[r.Id] = (Math.Clamp(r.Percent, 0, 100), false);
            else if (_batch.ContainsKey(r.Id))
            {
                if (r.Status == JobStatus.Cancelled) _batch.Remove(r.Id);
                else _batch[r.Id] = (100, true);
            }
        }
        // Gone from the list: a finished row the user cleared still counts; a cancelled one
        // (removed as soon as it is cancelled) does not.
        foreach (var gone in _batch.Where(kv => !present.Contains(kv.Key) && !kv.Value.Done).Select(kv => kv.Key).ToList())
            _batch.Remove(gone);

        var value = _batch.Values.Average(v => v.Percent) / 100.0;
        var state = value == 0 && active.All(r => r.Status == JobStatus.Pending)
            ? TaskbarState.Indeterminate
            : TaskbarState.Normal;
        return (state, value);
    }

    /// <summary>Drops a run from the batch — the failed job a 重試 replaces.</summary>
    public void Forget(Guid id) => _batch.Remove(id);

    private static bool IsActive(JobStatus s) => s is JobStatus.Pending or JobStatus.Downloading;
}
