using System.IO;
using System.Windows;
using YtDlpTool.Dialogs;
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
        // Materialize the save directory just-in-time. yt-dlp does not reliably create
        // missing parent paths in --output templates; AppHost no longer pre-creates the
        // default folder so we ensure it exists right before launching the process.
        try { Directory.CreateDirectory(job.SaveDirectory); } catch { }

        var sanitizedStem = FileNameSanitizer.Sanitize(job.Title);
        var request = new DownloadRequest(
            Url: job.Url,
            Mode: job.Mode,
            ChosenFormat: job.ChosenFormat,
            SubtitleLanguageCodes: job.SubtitleLanguageCodes,
            ClipRange: job.ClipRange,
            SaveDirectory: job.SaveDirectory,
            SanitizedFileStem: sanitizedStem);

        // Best-effort conflict probe: only the most common output extension per mode is checked.
        var probableOutput = ProbeProbableOutputPath(job, request);
        if (probableOutput is not null && File.Exists(probableOutput))
        {
            var resolution = await ShowConflictDialogOnUiThreadAsync(probableOutput).ConfigureAwait(false);
            if (resolution == FilenameConflictResolution.Cancel)
                return new DownloadExecutionResult(false, null, null, WasCancelled: true);
            if (resolution == FilenameConflictResolution.AutoRename)
            {
                var newStem = NextAvailableStem(job.SaveDirectory, sanitizedStem, Path.GetExtension(probableOutput));
                request = request with { SanitizedFileStem = newStem };
            }
            // Overwrite = leave request alone; yt-dlp will overwrite.
        }

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

    private static string? ProbeProbableOutputPath(DownloadJob job, DownloadRequest request)
    {
        var ext = ExtensionForMode(job);
        if (ext is null) return null;
        try
        {
            return Path.Combine(request.SaveDirectory, request.SanitizedFileStem + ext);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtensionForMode(DownloadJob job) => job.Mode switch
    {
        DownloadMode.AudioOnly =>
            (job.ChosenFormat.Extension is "m4a" or "mp4") ? ".m4a" : ".mp3",
        DownloadMode.VideoOnly =>
            "." + (string.IsNullOrEmpty(job.ChosenFormat.Extension) ? "mp4" : job.ChosenFormat.Extension),
        DownloadMode.AudioAndVideo => ".mp4",
        _ => null
    };

    private static string NextAvailableStem(string saveDir, string stem, string extension)
    {
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{stem}_{i}";
            var candidatePath = Path.Combine(saveDir, candidate + extension);
            if (!File.Exists(candidatePath)) return candidate;
        }
        // Fallback: timestamp-suffix
        return $"{stem}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static Task<FilenameConflictResolution> ShowConflictDialogOnUiThreadAsync(string conflictingPath)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null)
            return Task.FromResult(FilenameConflictResolution.Overwrite);
        return app.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new FilenameConflictDialog(conflictingPath)
            {
                Owner = app.MainWindow
            };
            dlg.ShowDialog();
            return dlg.Resolution;
        }).Task;
    }
}
