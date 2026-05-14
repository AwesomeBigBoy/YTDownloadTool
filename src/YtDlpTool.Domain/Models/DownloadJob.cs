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
