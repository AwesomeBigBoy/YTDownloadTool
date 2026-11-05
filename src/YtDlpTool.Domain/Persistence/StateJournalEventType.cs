namespace YtDlpTool.Domain.Persistence;

public enum StateJournalEventType
{
    JobEnqueued, JobStarted, JobProgress, JobCompleted, JobFailed, JobCancelled,
    // v1.5.0: a playlist / multi-link link waiting to be parsed and queued (BatchJournal),
    // and that link leaving the batch.
    LinkPending, LinkSettled,
}
