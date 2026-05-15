using System.Diagnostics;
using System.IO;
using System.Windows;
using YtDlpTool.Dialogs;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;
using YtDlpTool.Process;

namespace YtDlpTool;

/// <summary>
/// v1.1.13 refactor: ExecuteAsync is now a single ordered pipeline regardless of
/// clip / subs combination. Steps:
///   1. Download MEDIA only (yt-dlp, no --write-subs).
///   2. Download SUBS only (yt-dlp --skip-download), best-effort.
///   3. ffmpeg-cut media (and each sub) when ClipRange is set.
///   4. ffmpeg-mux any subs into the media. On mux failure, keep .vtt sidecars.
/// This eliminates two bugs in v1.1.12: (a) clip+subs lost subtitles because the
/// pre-cut yt-dlp call was forced to drop them, and (b) non-clip+subs failed
/// because --write-subs alongside the media download triggered a heavier
/// YouTube extractor path that needed a JS runtime we don't ship.
/// </summary>
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

        // Conflict probe applies to the FINAL output path. The intermediate
        // ".media" / ".cut" stems are deliberately disjoint so they never
        // collide with anything the user might see.
        var probableOutput = ProbeProbableOutputPath(job, sanitizedStem);
        var resolvedStem = sanitizedStem;
        var forceOverwriteFinal = false;
        if (probableOutput is not null && File.Exists(probableOutput))
        {
            var conflictResolution = await ShowConflictDialogOnUiThreadAsync(probableOutput).ConfigureAwait(false);
            if (conflictResolution == FilenameConflictResolution.Cancel)
                return new DownloadExecutionResult(false, null, null, WasCancelled: true);
            if (conflictResolution == FilenameConflictResolution.AutoRename)
                resolvedStem = NextAvailableStem(job.SaveDirectory, sanitizedStem, Path.GetExtension(probableOutput));
            else if (conflictResolution == FilenameConflictResolution.Overwrite)
                forceOverwriteFinal = true;
        }

        var finalExt = ExtensionForMode(job) ?? ".mp4";
        var finalPath = Path.Combine(job.SaveDirectory, resolvedStem + finalExt);
        // forceOverwriteFinal is intentionally retained for parity with the legacy
        // single-pass branch — we always pass -y to ffmpeg's mux/cut so the flag is
        // not load-bearing, but it documents that the user agreed to overwrite.
        _ = forceOverwriteFinal;

        return await RunPipelineAsync(job, resolvedStem, finalPath, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The unified media+subs+clip pipeline. Each "phase" maps to a specific
    /// progress range so the UI shows steady forward motion: media 0-65 (clip)
    /// or 0-80 (no clip), subs +5, cut 75, mux 92, done 100.
    /// </summary>
    private async Task<DownloadExecutionResult> RunPipelineAsync(
        DownloadJob job,
        string resolvedStem,
        string finalPath,
        IProgress<DownloadProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        var hasClip = job.ClipRange is not null;
        var hasSubs = job.SubtitleLanguageCodes.Count > 0
                      && job.Mode != DownloadMode.AudioOnly;

        // Intermediate stems. ".media" so the raw download never collides with
        // the final file; ".cut" so the ffmpeg-trimmed media never collides
        // with the raw download.
        var mediaStem = resolvedStem + ".media";

        // ----- Step 1: download media only -----
        progress.Report(new DownloadProgressSnapshot(0, null, null));
        var mediaProgressMax = hasClip ? 65.0 : 80.0;
        var mediaProgress = new Progress<ProgressReport>(p =>
            progress.Report(new DownloadProgressSnapshot(
                Math.Min(mediaProgressMax, p.Percent * mediaProgressMax / 100.0),
                p.BytesPerSecond, p.Eta)));

        var mediaRequest = new DownloadRequest(
            Url: job.Url,
            Mode: job.Mode,
            ChosenFormat: job.ChosenFormat,
            SubtitleLanguageCodes: Array.Empty<string>(),
            ClipRange: null,
            SaveDirectory: job.SaveDirectory,
            SanitizedFileStem: mediaStem,
            EmbedThumbnail: !hasClip,    // post-cut thumbnail re-embed would re-encode
            ForceOverwrite: true);       // intermediate stem — safe to clobber

        var mediaSw = Stopwatch.StartNew();
        var mediaResult = await _ytDlp.DownloadAsync(mediaRequest, mediaProgress, cancellationToken).ConfigureAwait(false);
        mediaSw.Stop();

        if (mediaResult.WasCancelled)
        {
            TryDeleteByStem(job.SaveDirectory, mediaStem);
            MappedError? diag = null;
            if (!string.IsNullOrEmpty(mediaResult.ErrorStderr))
                diag = new MappedError(ErrorCategory.UnknownError, "", "E-CANCEL-DIAG",
                    false, RawDetails: mediaResult.ErrorStderr);
            return new DownloadExecutionResult(false, null, diag, true);
        }
        if (!mediaResult.IsSuccess)
        {
            TryDeleteByStem(job.SaveDirectory, mediaStem);
            return new DownloadExecutionResult(false, null,
                ErrorMapper.Map(mediaResult.ErrorStderr ?? ""), false);
        }

        // Verify a real media file actually landed before continuing.
        var mediaProbe = MediaOutputProbe.VerifyMediaOutputExists(job.SaveDirectory, mediaStem);
        if (!mediaProbe.found)
        {
            foreach (var leftover in mediaProbe.sidecarPaths)
            {
                try { File.Delete(leftover); } catch { /* best effort */ }
            }
            return new DownloadExecutionResult(false, null,
                new MappedError(ErrorCategory.UnknownError,
                    "下載未產生影音檔案，僅有附件（字幕/縮圖）。請嘗試其他格式或畫質。",
                    "E-NOMEDIA1", false, ""),
                WasCancelled: false);
        }

        var mediaPath = mediaResult.OutputFilePath;
        if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
        {
            // yt-dlp's progress parser may miss the Destination: line on certain
            // edge cases (merger rename, etc.). Fall back to file-system probe.
            mediaPath = FindFirstMediaFile(job.SaveDirectory, mediaStem);
            if (mediaPath is null)
                return new DownloadExecutionResult(false, null,
                    new MappedError(ErrorCategory.UnknownError,
                        "下載成功但找不到輸出檔案，無法進行後續處理。",
                        "E-NOFILE1", false, ""),
                    WasCancelled: false);
        }

        _logger?.Info("media.download.completed", new Dictionary<string, string>
        {
            ["elapsed_ms"] = mediaSw.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        // ----- Step 2: download subs only (best-effort) -----
        IReadOnlyList<string> subFiles = Array.Empty<string>();
        if (hasSubs)
        {
            progress.Report(new DownloadProgressSnapshot(mediaProgressMax + 5, null, null));
            var subRes = await _ytDlp.DownloadSubtitlesOnlyAsync(
                job.Url, job.SubtitleLanguageCodes, job.SaveDirectory, mediaStem, cancellationToken)
                .ConfigureAwait(false);
            if (subRes.IsSuccess && subRes.SubtitleFilePaths.Count > 0)
            {
                subFiles = subRes.SubtitleFilePaths;
                _logger?.Info("subs.download.completed", new Dictionary<string, string>
                {
                    ["count"] = subFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            }
            else
            {
                _logger?.Warn("subs.download.failed", new Dictionary<string, string>
                {
                    ["reason"] = TrimDiag(subRes.ErrorMessage) ?? "no files"
                });
                // continue without subs — best effort
            }
        }

        // ----- Step 3: ffmpeg cut media + each sub (clip only) -----
        var currentMediaPath = mediaPath;
        var currentSubFiles = subFiles;
        if (hasClip)
        {
            var clipRange = job.ClipRange!;
            var cutStem = resolvedStem + ".cut";
            var cutMediaPath = Path.Combine(job.SaveDirectory, cutStem + Path.GetExtension(currentMediaPath));

            progress.Report(new DownloadProgressSnapshot(75, null, null));
            var cutSw = Stopwatch.StartNew();
            var cut = await _ffmpeg.CutAsync(
                inputPath: currentMediaPath,
                outputPath: cutMediaPath,
                range: clipRange,
                mode: job.Mode,
                progress: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            cutSw.Stop();

            if (cut.WasCancelled)
            {
                TryDelete(currentMediaPath);
                TryDelete(cutMediaPath);
                foreach (var sf in currentSubFiles) TryDelete(sf);
                return new DownloadExecutionResult(false, null,
                    new MappedError(ErrorCategory.UnknownError,
                        "剪輯已取消", "E-CUT-CANCEL", false, cut.ErrorMessage),
                    WasCancelled: true);
            }
            if (!cut.IsSuccess)
            {
                TryDelete(currentMediaPath);
                TryDelete(cutMediaPath);
                foreach (var sf in currentSubFiles) TryDelete(sf);
                var raw = (cut.ErrorMessage ?? "").Trim();
                if (raw.Length > 500) raw = raw.Substring(0, 500);
                return new DownloadExecutionResult(false, null,
                    new MappedError(ErrorCategory.UnknownError,
                        "剪輯失敗：ffmpeg 無法處理下載的影片，請改試其他格式或畫質。",
                        "E-CUT-FAIL", false, raw),
                    WasCancelled: false);
            }

            // Verify the cut output is non-empty before tossing the raw media.
            if (!File.Exists(cutMediaPath) || new FileInfo(cutMediaPath).Length == 0)
            {
                TryDelete(currentMediaPath);
                TryDelete(cutMediaPath);
                foreach (var sf in currentSubFiles) TryDelete(sf);
                return new DownloadExecutionResult(false, null,
                    new MappedError(ErrorCategory.UnknownError,
                        "剪輯完成但輸出檔案為空。請嘗試其他格式或畫質。",
                        "E-CUT-EMPTY", false, ""),
                    WasCancelled: false);
            }

            TryDelete(currentMediaPath);
            CleanupOrphanSidecars(currentMediaPath);
            currentMediaPath = cutMediaPath;

            _logger?.Info("clip.cut.completed", new Dictionary<string, string>
            {
                ["elapsed_ms"] = cutSw.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

            // Cut each subtitle to the same range. Failures here drop that
            // individual subtitle but never fail the whole pipeline — the user
            // still gets the cut media.
            if (currentSubFiles.Count > 0)
            {
                var cutSubs = new List<string>();
                foreach (var sf in currentSubFiles)
                {
                    var dir = Path.GetDirectoryName(sf) ?? job.SaveDirectory;
                    var lang = FfmpegRunner.ExtractLangFromFilename(Path.GetFileName(sf));
                    var subExt = Path.GetExtension(sf);
                    var cutSubName = string.IsNullOrEmpty(lang)
                        ? cutStem + subExt
                        : cutStem + "." + lang + subExt;
                    var cutSubPath = Path.Combine(dir, cutSubName);

                    var subCut = await _ffmpeg.CutSubtitleAsync(sf, cutSubPath, clipRange, cancellationToken)
                        .ConfigureAwait(false);
                    if (subCut.IsSuccess && File.Exists(cutSubPath))
                    {
                        cutSubs.Add(cutSubPath);
                    }
                    else
                    {
                        _logger?.Warn("subs.cut.failed", new Dictionary<string, string>
                        {
                            ["file"] = Path.GetFileName(sf),
                            ["reason"] = TrimDiag(subCut.ErrorMessage) ?? ""
                        });
                    }
                    TryDelete(sf);
                }
                currentSubFiles = cutSubs;
            }
        }

        // ----- Step 4: mux subs into media (if any) -----
        if (currentSubFiles.Count > 0)
        {
            progress.Report(new DownloadProgressSnapshot(92, null, null));
            // Scrub intermediate sidecars BEFORE the mux replaces things — otherwise
            // a ".media.jpg" left over from --embed-thumbnail's sidecar write would
            // outlive the rename and clutter the user's folder.
            CleanupOrphanSidecars(currentMediaPath);
            // ffmpeg refuses to overwrite without -y; BuildMuxArgs already passes -y.
            // We still pre-delete the final path so a stale file with the same name
            // doesn't confuse downstream consumers if mux silently produces a zero-byte file.
            TryDelete(finalPath);

            var muxResult = await _ffmpeg.MuxSubtitlesAsync(
                currentMediaPath, currentSubFiles, finalPath, cancellationToken).ConfigureAwait(false);

            if (muxResult.IsSuccess && File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
            {
                TryDelete(currentMediaPath);
                foreach (var sf in currentSubFiles) TryDelete(sf);
                _logger?.Info("subs.mux.completed", new Dictionary<string, string>
                {
                    ["count"] = currentSubFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            }
            else
            {
                // Mux failed — keep cut media as final + leave sidecars next to it
                // so the user still has the bits, just in two files.
                _logger?.Warn("subs.mux.failed", new Dictionary<string, string>
                {
                    ["reason"] = TrimDiag(muxResult.ErrorMessage) ?? ""
                });
                TryDelete(finalPath); // remove any partial output
                try
                {
                    File.Move(currentMediaPath, finalPath);
                }
                catch
                {
                    // If even the rename fails, fall back to reporting the
                    // intermediate path so the user knows where the file is.
                    finalPath = currentMediaPath;
                }

                // Rename sidecars so they sit alongside the renamed media: the
                // intermediate ".cut.<lang>.vtt" / ".media.<lang>.vtt" prefix
                // becomes "<final-stem>.<lang>.vtt". On failure, leave the
                // sidecar at its current name.
                var finalStem = Path.GetFileNameWithoutExtension(finalPath);
                var finalDir = Path.GetDirectoryName(finalPath) ?? job.SaveDirectory;
                foreach (var sf in currentSubFiles)
                {
                    if (!File.Exists(sf)) continue;
                    var lang = FfmpegRunner.ExtractLangFromFilename(Path.GetFileName(sf));
                    var subExt = Path.GetExtension(sf);
                    var renamed = string.IsNullOrEmpty(lang)
                        ? Path.Combine(finalDir, finalStem + subExt)
                        : Path.Combine(finalDir, finalStem + "." + lang + subExt);
                    try
                    {
                        if (!string.Equals(renamed, sf, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(renamed)) File.Delete(renamed);
                            File.Move(sf, renamed);
                        }
                    }
                    catch { /* leave sidecar where it lies */ }
                }
            }
        }
        else
        {
            // No subs — just rename the (possibly cut) media to finalPath.
            // Scrub intermediate sidecars (e.g. ".media.jpg" from --embed-thumbnail)
            // BEFORE the move so they don't get left orphaned under the old stem.
            CleanupOrphanSidecars(currentMediaPath);
            try
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(currentMediaPath, finalPath);
            }
            catch
            {
                // Fall back: report the intermediate name so the user can still find the file.
                finalPath = currentMediaPath;
            }
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
    /// Trims a diagnostic blob to a length sensible for log files. Returns null
    /// when the input is null/empty so callers can fall back to a default reason.
    /// </summary>
    private static string? TrimDiag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > 500 ? trimmed.Substring(0, 500) : trimmed;
    }

    /// <summary>
    /// Deletes likely-orphan sidecar files (thumbnail variants, yt-dlp temp files) that
    /// share the same stem as the successfully-downloaded output. Each deletion is
    /// wrapped in its own try/catch because none of them are load-bearing. NOTE: this
    /// intentionally does NOT touch .vtt/.srt sidecars — those may have been left
    /// behind on purpose when ffmpeg-mux failed.
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
