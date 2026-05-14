namespace YtDlpTool.Domain.Persistence;

public enum StateJournalEventType { JobEnqueued, JobStarted, JobProgress, JobCompleted, JobFailed, JobCancelled }
