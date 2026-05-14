using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public interface IDownloadExecutor
{
    Task<DownloadExecutionResult> ExecuteAsync(
        DownloadJob job,
        IProgress<DownloadProgressSnapshot> progress,
        CancellationToken cancellationToken);
}

public sealed record DownloadProgressSnapshot(double Percent, long? BytesPerSecond, TimeSpan? Eta);
