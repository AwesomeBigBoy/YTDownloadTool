using YtDlpTool.Domain.Persistence;

namespace YtDlpTool.Domain.Services;

/// <summary>
/// v1.5.0: journals the links of a batch until each one leaves it, so links not yet parsed
/// and queued when the app closes are offered again at the next start, together with the
/// interrupted downloads (StateJournal.ReconstructOpenJobs). A link that was queued is
/// covered by its download's own journal entries from then on.
/// </summary>
public static class BatchJournal
{
    public static void Attach(BatchQueue batch, StateJournal journal)
    {
        batch.Accepted += works =>
        {
            foreach (var w in works)
            {
                journal.Append(new StateJournalEvent
                {
                    At = DateTimeOffset.UtcNow,
                    Type = StateJournalEventType.LinkPending,
                    JobId = w.Id,
                    Snapshot = new JobSnapshot { Id = w.Id, Url = w.Url, Mode = w.Mode },
                });
            }
        };
        batch.Settled += w => journal.Append(new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.LinkSettled,
            JobId = w.Id,
        });
    }
}
