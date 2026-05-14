using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;

namespace YtDlpTool.Domain.Services;

public static class JournaledQueue
{
    public static Action<QueueEvent> Wrap(StateJournal journal, Action<QueueEvent> downstream)
    {
        return evt =>
        {
            journal.Append(ToJournalEvent(evt));
            downstream(evt);
        };
    }

    private static StateJournalEvent ToJournalEvent(QueueEvent evt) => evt switch
    {
        JobEnqueuedEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobEnqueued,
            JobId = e.Job.Id,
            Snapshot = ToSnapshot(e.Job)
        },
        JobStartedEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobStarted,
            JobId = e.Job.Id
        },
        JobProgressEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobProgress,
            JobId = e.Job.Id,
            ProgressPercent = e.Progress.Percent
        },
        JobCompletedEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobCompleted,
            JobId = e.Job.Id
        },
        JobFailedEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobFailed,
            JobId = e.Job.Id,
            FailureCode = e.Error.ErrorCode
        },
        JobCancelledEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobCancelled,
            JobId = e.Job.Id
        },
        _ => throw new InvalidOperationException($"unknown event type {evt.GetType().Name}")
    };

    private static JobSnapshot ToSnapshot(DownloadJob job) => new()
    {
        Id = job.Id,
        Url = job.Url,
        Title = job.Title,
        ThumbnailUrl = job.ThumbnailUrl,
        Mode = job.Mode,
        FormatId = job.ChosenFormat.FormatId,
        FormatHeight = job.ChosenFormat.Height,
        FormatExt = job.ChosenFormat.Extension,
        SubtitleLanguageCodes = job.SubtitleLanguageCodes.ToList(),
        ClipStart = job.ClipRange is null ? null : job.ClipRange.Start.ToString(@"hh\:mm\:ss"),
        ClipEnd = job.ClipRange is null ? null : job.ClipRange.End.ToString(@"hh\:mm\:ss"),
        SaveDirectory = job.SaveDirectory
    };
}
