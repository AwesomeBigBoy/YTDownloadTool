using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;
using YtDlpTool.Process;

namespace YtDlpTool;

public sealed class YtDlpDownloadExecutor : IDownloadExecutor
{
    private readonly YtDlpRunner _runner;
    public YtDlpDownloadExecutor(YtDlpRunner runner) => _runner = runner;

    public async Task<DownloadExecutionResult> ExecuteAsync(
        DownloadJob job,
        IProgress<DownloadProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        var request = new DownloadRequest(
            Url: job.Url,
            Mode: job.Mode,
            ChosenFormat: job.ChosenFormat,
            SubtitleLanguageCodes: job.SubtitleLanguageCodes,
            ClipRange: job.ClipRange,
            SaveDirectory: job.SaveDirectory,
            SanitizedFileStem: FileNameSanitizer.Sanitize(job.Title));

        var processProgress = new Progress<ProgressReport>(p =>
            progress.Report(new DownloadProgressSnapshot(p.Percent, p.BytesPerSecond, p.Eta)));

        var result = await _runner.DownloadAsync(request, processProgress, cancellationToken).ConfigureAwait(false);

        if (result.WasCancelled)
            return new DownloadExecutionResult(false, null, null, true);
        if (!result.IsSuccess)
        {
            var mapped = ErrorMapper.Map(result.ErrorStderr ?? "");
            return new DownloadExecutionResult(false, null, mapped, false);
        }
        return new DownloadExecutionResult(true, result.OutputFilePath, null, false);
    }
}
