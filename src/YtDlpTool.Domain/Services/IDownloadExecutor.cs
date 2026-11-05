using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public interface IDownloadExecutor
{
    Task<DownloadExecutionResult> ExecuteAsync(
        DownloadJob job,
        IProgress<DownloadProgressSnapshot> progress,
        CancellationToken cancellationToken);
}

/// <param name="WaitingForUser">
/// v1.5.0: the executor is blocked on the user (the filename-conflict dialog), not on the
/// network. DownloadQueue's no-progress watchdog pauses until the next ordinary report.
/// </param>
/// <param name="Processing">v1.5.0: the bytes are in; yt-dlp's post-processors run (ProgressReport.Processing).</param>
public sealed record DownloadProgressSnapshot(double Percent, long? BytesPerSecond, TimeSpan? Eta, bool WaitingForUser = false,
    bool Processing = false);
