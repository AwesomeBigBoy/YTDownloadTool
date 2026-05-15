using System.Diagnostics;
using System.IO;
using System.Windows;
using YtDlpTool.Dialogs;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;
using YtDlpTool.Process;

namespace YtDlpTool;

public sealed class YtDlpDownloadExecutor : IDownloadExecutor
{
    private readonly YtDlpRunner _ytDlp;
    private readonly FfmpegRunner _ffmpeg;
    private readonly AppLogger? _logger;

    public YtDlpDownloadExecutor(YtDlpRunner ytDlp, FfmpegRunner ffmpeg, AppLogger? logger = null)
    {
        _ytDlp = ytDlp;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

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

        // Conflict probe applies to the FINAL output (post-cut for clips, the single
        // file otherwise). For the two-pass clip path the temp fullvideo file uses
        // a different stem so it never collides with anything the user might see.
        var probableOutput = ProbeProbableOutputPath(job, sanitizedStem);
        var conflictResolution = FilenameConflictResolution.Overwrite;
        var resolvedStem = sanitizedStem;
        var forceOverwriteFinal = false;
        if (probableOutput is not null && File.Exists(probableOutput))
        {
            conflictResolution = await ShowConflictDialogOnUiThreadAsync(probableOutput).ConfigureAwait(false);
            if (conflictResolution == FilenameConflictResolution.Cancel)
                return new DownloadExecutionResult(false, null, null, WasCancelled: true);
            if (conflictResolution == FilenameConflictResolution.AutoRename)
                resolvedStem = NextAvailableStem(job.SaveDirectory, sanitizedStem, Path.GetExtension(probableOutput));
            else if (conflictResolution == FilenameConflictResolution.Overwrite)
                forceOverwriteFinal = true;
        }

        if (job.ClipRange is not null)
            return await ExecuteClipTwoPassAsync(job, resolvedStem, forceOverwriteFinal, progress, cancellationToken)
                .ConfigureAwait(false);

        return await ExecuteSinglePassAsync(job, resolvedStem, forceOverwriteFinal, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Non-clip download: hand the whole thing to yt-dlp, verify a media file
    /// landed, scrub sidecars. This is the pre-v1.1.12 behaviour preserved
    /// verbatim for the no-clip case.
    /// </summary>
    private async Task<DownloadExecutionResult> ExecuteSinglePassAsync(
        DownloadJob job,
        string sanitizedStem,
        bool forceOverwrite,
        IProgress<DownloadProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        var request = new DownloadRequest(
            Url: job.Url,
            Mode: job.Mode,
            ChosenFormat: job.ChosenFormat,
            SubtitleLanguageCodes: job.SubtitleLanguageCodes,
            ClipRange: null,
            SaveDirectory: job.SaveDirectory,
            SanitizedFileStem: sanitizedStem,
            ForceOverwrite: forceOverwrite);

        var processProgress = new Progress<ProgressReport>(p =>
            progress.Report(new DownloadProgressSnapshot(p.Percent, p.BytesPerSecond, p.Eta)));

        var result = await _ytDlp.DownloadAsync(request, processProgress, cancellationToken).ConfigureAwait(false);

        if (result.WasCancelled)
        {
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

        // v1.1.6: confirm an actual media file landed in the save directory; yt-dlp
        // can return exit 0 after producing only sidecars under some failure modes.
        var probe = MediaOutputProbe.VerifyMediaOutputExists(job.SaveDirectory, request.SanitizedFileStem);
        if (!probe.found)
        {
            foreach (var leftover in probe.sidecarPaths)
            {
                try { File.Delete(leftover); } catch { /* best effort */ }
            }
            return new DownloadExecutionResult(false, null,
                new MappedError(ErrorCategory.UnknownError,
                    "下載未產生影音檔案，僅有附件（字幕/縮圖）。請嘗試其他格式或畫質。",
                    "E-NOMEDIA1", false, ""),
                WasCancelled: false);
        }

        CleanupOrphanSidecars(result.OutputFilePath);
        return new DownloadExecutionResult(true, result.OutputFilePath, null, false);
    }

    /// <summary>
    /// Clip download (v1.1.12): two passes — yt-dlp downloads the FULL video to a
    /// temp file using its proven full-download codepath, then ffmpeg stream-copies
    /// the requested time range into the user-visible output. Eliminates yt-dlp's
    /// --download-sections entirely (which needs a JavaScript runtime to resolve
    /// the section URL on current YouTube videos and hangs silently without one).
    /// </summary>
    private async Task<DownloadExecutionResult> ExecuteClipTwoPassAsync(
        DownloadJob job,
        string finalStem,
        bool forceOverwriteFinal,
        IProgress<DownloadProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        var clipRange = job.ClipRange!;
        var tempStem = finalStem + ".fullvideo";
        var finalExt = ExtensionForMode(job) ?? ".mp4";
        var finalPath = Path.Combine(job.SaveDirectory, finalStem + finalExt);

        var pass1Request = new DownloadRequest(
            Url: job.Url,
            Mode: job.Mode,
            ChosenFormat: job.ChosenFormat,
            SubtitleLanguageCodes: Array.Empty<string>(),    // would be wrong-length on a cut
            ClipRange: null,                                 // explicitly not yt-dlp's job anymore
            SaveDirectory: job.SaveDirectory,
            SanitizedFileStem: tempStem,
            EmbedThumbnail: false,                           // thumbnail re-embed after cut would re-encode
            ForceOverwrite: true);                           // temp stem so safe to clobber

        // Map pass 1 (yt-dlp full download) to 0-85% of overall progress.
        var pass1Progress = new Progress<ProgressReport>(p =>
            progress.Report(new DownloadProgressSnapshot(
                Math.Min(85.0, p.Percent * 0.85), p.BytesPerSecond, p.Eta)));

        var pass1Sw = Stopwatch.StartNew();
        var pass1 = await _ytDlp.DownloadAsync(pass1Request, pass1Progress, cancellationToken).ConfigureAwait(false);
        pass1Sw.Stop();

        if (pass1.WasCancelled)
        {
            TryDeleteByStem(job.SaveDirectory, tempStem);
            MappedError? diag = null;
            if (!string.IsNullOrEmpty(pass1.ErrorStderr))
                diag = new MappedError(ErrorCategory.UnknownError, "", "E-CANCEL-DIAG",
                    false, RawDetails: pass1.ErrorStderr);
            return new DownloadExecutionResult(false, null, diag, true);
        }
        if (!pass1.IsSuccess)
        {
            TryDeleteByStem(job.SaveDirectory, tempStem);
            return new DownloadExecutionResult(false, null,
                ErrorMapper.Map(pass1.ErrorStderr ?? ""), false);
        }

        // Verify the full video file actually exists on disk before ffmpeg tries to read it.
        var fullProbe = MediaOutputProbe.VerifyMediaOutputExists(job.SaveDirectory, tempStem);
        if (!fullProbe.found)
        {
            foreach (var leftover in fullProbe.sidecarPaths)
            {
                try { File.Delete(leftover); } catch { }
            }
            return new DownloadExecutionResult(false, null,
                new MappedError(ErrorCategory.UnknownError,
                    "下載未產生影音檔案，僅有附件（字幕/縮圖）。請嘗試其他格式或畫質。",
                    "E-NOMEDIA1", false, ""),
                WasCancelled: false);
        }

        var fullPath = pass1.OutputFilePath;
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            // Fall back to discovering the actual file on disk — yt-dlp's progress
            // parser is regex-based and can miss the Destination line on certain
            // edge cases (e.g. when merger renames the output).
            fullPath = FindFirstMediaFile(job.SaveDirectory, tempStem);
            if (fullPath is null)
                return new DownloadExecutionResult(false, null,
                    new MappedError(ErrorCategory.UnknownError,
                        "下載成功但找不到輸出檔案，無法進行剪輯。",
                        "E-NOFILE1", false, ""),
                    WasCancelled: false);
        }

        _logger?.Info("clip.fullvideo.completed", new Dictionary<string, string>
        {
            ["elapsed_ms"] = pass1Sw.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        // Refuse to overwrite the final file unless the conflict dialog said so.
        // The conflict probe checks the FINAL path's existence at job start, so
        // by the time we get here either no conflict existed or the user agreed
        // to overwrite / auto-rename (which already updated finalStem). The
        // forceOverwriteFinal flag is forwarded to ffmpeg via -y which we always
        // pass; the variable is kept for parity with the single-pass branch and
        // future audit logging.
        _ = forceOverwriteFinal;

        progress.Report(new DownloadProgressSnapshot(92.0, null, null));

        var cutSw = Stopwatch.StartNew();
        var cut = await _ffmpeg.CutAsync(
            inputPath: fullPath,
            outputPath: finalPath,
            range: clipRange,
            mode: job.Mode,
            progress: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cutSw.Stop();

        // Always delete the temp full video; it's not user-visible and would
        // double-occupy disk if left behind. Sidecars (.jpg/.webp/.ytdl) that
        // yt-dlp may have produced are scrubbed via CleanupOrphanSidecars.
        TryDelete(fullPath);
        CleanupOrphanSidecars(fullPath);

        if (cut.WasCancelled)
        {
            TryDelete(finalPath);
            return new DownloadExecutionResult(false, null,
                new MappedError(ErrorCategory.UnknownError,
                    "剪輯已取消", "E-CUT-CANCEL", false, cut.ErrorMessage),
                WasCancelled: true);
        }
        if (!cut.IsSuccess)
        {
            TryDelete(finalPath);
            var raw = (cut.ErrorMessage ?? "").Trim();
            if (raw.Length > 500) raw = raw.Substring(0, 500);
            return new DownloadExecutionResult(false, null,
                new MappedError(ErrorCategory.UnknownError,
                    "剪輯失敗：ffmpeg 無法處理下載的影片，請改試其他格式或畫質。",
                    "E-CUT-FAIL", false, raw),
                WasCancelled: false);
        }

        _logger?.Info("clip.cut.completed", new Dictionary<string, string>
        {
            ["elapsed_ms"] = cutSw.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        // Final sanity check on the cut output.
        if (!File.Exists(finalPath) || new FileInfo(finalPath).Length == 0)
        {
            TryDelete(finalPath);
            return new DownloadExecutionResult(false, null,
                new MappedError(ErrorCategory.UnknownError,
                    "剪輯完成但輸出檔案為空。請嘗試其他格式或畫質。",
                    "E-CUT-EMPTY", false, ""),
                WasCancelled: false);
        }

        progress.Report(new DownloadProgressSnapshot(100.0, null, null));
        CleanupOrphanSidecars(finalPath);
        return new DownloadExecutionResult(true, finalPath, null, false);
    }

    /// <summary>
    /// Deletes any files in <paramref name="dir"/> matching <paramref name="stem"/>.*
    /// — used to scrub leftover yt-dlp .part/.ytdl/media files from a failed first
    /// pass before returning the executor result.
    /// </summary>
    private static void TryDeleteByStem(string dir, string stem)
    {
        if (!Directory.Exists(dir) || string.IsNullOrEmpty(stem)) return;
        try
        {
            foreach (var path in Directory.GetFiles(dir, stem + ".*"))
            {
                try { File.Delete(path); } catch { }
            }
        }
        catch { /* best effort */ }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string? FindFirstMediaFile(string dir, string stem)
    {
        if (!Directory.Exists(dir)) return null;
        try
        {
            foreach (var p in Directory.GetFiles(dir, stem + ".*"))
            {
                var ext = Path.GetExtension(p);
                if (MediaOutputProbe.MediaExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    return p;
            }
        }
        catch { }
        return null;
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

    private static string? ProbeProbableOutputPath(DownloadJob job, string stem)
    {
        var ext = ExtensionForMode(job);
        if (ext is null) return null;
        try
        {
            return Path.Combine(job.SaveDirectory, stem + ext);
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
