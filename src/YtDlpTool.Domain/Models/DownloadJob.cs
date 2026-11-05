namespace YtDlpTool.Domain.Models;

public sealed class DownloadJob
{
    public Guid Id { get; }
    public string Url { get; }
    public string Title { get; }
    public string ThumbnailUrl { get; }
    public DownloadMode Mode { get; }
    public VideoFormat ChosenFormat { get; }
    public IReadOnlyList<string> SubtitleLanguageCodes { get; }
    public TimeRange? ClipRange { get; }
    public string SaveDirectory { get; }

    /// <summary>
    /// v1.5.0: the playlist / multi-link batch this download was queued from (null for a
    /// single video), so 「檔案已存在」 can be answered once for all of it (ConflictDecisions).
    /// </summary>
    public Guid? BatchId { get; init; }

    public JobStatus Status { get; private set; } = JobStatus.Pending;
    public double Progress { get; private set; }
    public long? BytesPerSecond { get; private set; }
    public TimeSpan? Eta { get; private set; }
    public string? FailureReason { get; private set; }
    public string? FailureCode { get; private set; }
    public string? OutputFilePath { get; private set; }

    public DownloadJob(
        string url, string title, string thumbnailUrl,
        DownloadMode mode, VideoFormat chosenFormat,
        IReadOnlyList<string> subtitleLanguageCodes,
        TimeRange? clipRange, string saveDirectory)
    {
        Id = Guid.NewGuid();
        Url = url;
        Title = title;
        ThumbnailUrl = thumbnailUrl;
        Mode = mode;
        ChosenFormat = chosenFormat;
        SubtitleLanguageCodes = subtitleLanguageCodes;
        ClipRange = clipRange;
        SaveDirectory = saveDirectory;
    }

    /// <summary>
    /// v1.5.0: what 「重試」 on a failed row queues — the same request as a new job, so the
    /// queue's per-job bookkeeping (cancellation, 429 retries, log job_hash) starts fresh.
    /// It is not part of its batch any more: a conflict on the retry is asked about again.
    /// </summary>
    public DownloadJob CloneForRetry() =>
        new(Url, Title, ThumbnailUrl, Mode, ChosenFormat, SubtitleLanguageCodes, ClipRange, SaveDirectory);

    public void MarkDownloading() => Status = JobStatus.Downloading;
    public void MarkCancelled()   => Status = JobStatus.Cancelled;

    public void ReportProgress(double percent, long? bps, TimeSpan? eta)
    {
        Progress = percent;
        BytesPerSecond = bps;
        Eta = eta;
    }

    public void MarkCompleted(string outputPath)
    {
        Status = JobStatus.Completed;
        Progress = 100.0;
        OutputFilePath = outputPath;
    }

    public void MarkFailed(string reason, string code)
    {
        Status = JobStatus.Failed;
        FailureReason = reason;
        FailureCode = code;
    }
}
