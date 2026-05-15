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
            else if (resolution == FilenameConflictResolution.Overwrite)
            {
                // yt-dlp refuses overwrites by default — without --force-overwrites the
                // download fails with a message that can incidentally match ComponentMissing.
                request = request with { ForceOverwrite = true };
            }
        }

        var processProgress = new Progress<ProgressReport>(p =>
            progress.Report(new DownloadProgressSnapshot(p.Percent, p.BytesPerSecond, p.Eta)));

        var result = await _runner.DownloadAsync(request, processProgress, cancellationToken).ConfigureAwait(false);

        if (result.WasCancelled)
        {
            // Fix B (v1.1.8): cancellation may have been driven by the no-progress
            // watchdog rather than the user. Forward the combined stderr+stdout-tail
            // diagnostics on the result so DownloadQueue can attach them to the
            // E-STUCK01 MappedError before logging. The category here is a
            // placeholder — DownloadQueue swaps the whole MappedError when it
            // identifies a watchdog-driven cancel.
            MappedError? diag = null;
            if (!string.IsNullOrEmpty(result.ErrorStderr))
                diag = new MappedError(ErrorCategory.UnknownError, "", "E-CANCEL-DIAG",
                    false, RawDetails: result.ErrorStderr);
            return new DownloadExecutionResult(false, null, diag, true);
        }
        if (!result.IsSuccess)
        {
            var mapped = ErrorMapper.Map(result.ErrorStderr ?? "");
            return new DownloadExecutionResult(false, null, mapped, false);
        }

        // v1.1.6: post-success media verification. yt-dlp can return exit code 0 after
        // producing only sidecars (.vtt subtitle + .jpg/.webp thumbnail) when its
        // --download-sections + ffmpeg seek/cut combo fails silently. Confirm an
        // actual media file landed in the save directory; otherwise convert this
        // pseudo-success into a typed UnknownError so the user sees a real message
        // instead of believing the download succeeded.
        var probe = MediaOutputProbe.VerifyMediaOutputExists(job.SaveDirectory, request.SanitizedFileStem);
        if (!probe.found)
        {
            var hint = job.ClipRange is not null
                ? "片段下載未產生影音檔案。請嘗試：降低畫質、改選其他格式、或暫時關閉擷取片段功能後再下載完整影片再自行剪輯。"
                : "下載未產生影音檔案，僅有附件（字幕/縮圖）。請嘗試其他格式或畫質。";
            foreach (var leftover in probe.sidecarPaths)
            {
                try { File.Delete(leftover); } catch { /* best effort */ }
            }
            return new DownloadExecutionResult(false, null,
                new MappedError(ErrorCategory.UnknownError, hint, "E-NOMEDIA1", false, ""),
                WasCancelled: false);
        }

        // Best-effort sidecar scrub: --embed-thumbnail normally removes the .webp/.jpg
        // sidecar after embedding, but failure modes (codec mismatch, ffmpeg crashed
        // mid-mux) leave orphans next to the final media file. Sweep the obvious
        // candidates so the user's downloads folder stays clean.
        CleanupOrphanSidecars(result.OutputFilePath);

        return new DownloadExecutionResult(true, result.OutputFilePath, null, false);
    }

    /// <summary>
    /// Deletes likely-orphan sidecar files (thumbnail variants, yt-dlp temp files) that
    /// share the same stem as the successfully-downloaded output. Each deletion is
    /// wrapped in its own try/catch because none of them are load-bearing.
    /// </summary>
    private static void CleanupOrphanSidecars(string? outputFilePath)
    {
        if (string.IsNullOrEmpty(outputFilePath)) return;
        var dir = Path.GetDirectoryName(outputFilePath);
        var stem = Path.GetFileNameWithoutExtension(outputFilePath);
        if (dir is null || stem is null) return;
        foreach (var ext in new[] { ".webp", ".jpg", ".jpeg", ".png", ".part", ".ytdl", ".temp" })
        {
            try
            {
                var p = Path.Combine(dir, stem + ext);
                if (File.Exists(p)) File.Delete(p);
            }
            catch { /* sidecar cleanup is best-effort */ }
        }
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
