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
    private readonly ConflictDecisions _conflicts;
    // v1.5.0: the output names of the downloads in flight (one executor serves the queue).
    private readonly OutputNames _names = new();

    public YtDlpDownloadExecutor(YtDlpRunner ytDlp, FfmpegRunner ffmpeg, AppLogger? logger = null,
        ConflictDecisions? conflicts = null)
    {
        _ytDlp = ytDlp;
        _ffmpeg = ffmpeg;
        _logger = logger;
        _conflicts = conflicts ?? new ConflictDecisions();
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

        // v1.5.0: the quality goes into the name — 「標題 [1080p].mp4」, 「標題 [129kbps].m4a」 —
        // so a 1080p and a 720p copy of one video sit side by side instead of colliding.
        var sanitizedStem = FileNameSanitizer.WithLabel(job.Title, QualityOptions.FileNameLabel(job.Mode, job.ChosenFormat));
        var finalExt = ExtensionForMode(job) ?? ".mp4";
        bool Finished(string stem) => File.Exists(Path.Combine(job.SaveDirectory, stem + finalExt));

        // v1.5.0: the name is this download's until it ends (OutputNames). When another
        // download in this folder is writing the same name right now — the same video queued
        // twice, two videos with the same title — this one takes the next free name, as
        // 自動改名 would; the 檔案已存在 probe below only sees finished files.
        var resolvedStem = sanitizedStem;
        var claim = _names.TryClaim(job.SaveDirectory, sanitizedStem);
        try
        {
            if (claim is null)
            {
                (resolvedStem, claim) = _names.ClaimFree(job.SaveDirectory, sanitizedStem, Finished);
                _logger?.Info("download.name_in_use", new Dictionary<string, string>
                {
                    ["job_hash"] = job.Id.ToString("N")[..8],
                    ["suffix"] = resolvedStem[sanitizedStem.Length..],
                });
            }
            // Conflict probe applies to the FINAL output path. The intermediate
            // ".media" / ".cut" stems are deliberately disjoint so they never
            // collide with anything the user might see.
            else if (Finished(sanitizedStem))
            {
                var probableOutput = Path.Combine(job.SaveDirectory, sanitizedStem + finalExt);
                // v1.5.0: tell the queue's no-progress watchdog that we are waiting for the
                // user, not stalled — otherwise a dialog left open > 90 s got the job cancelled
                // and reported as 「下載卡住」. The ordinary report afterwards resumes it.
                progress.Report(new DownloadProgressSnapshot(0, null, null, WaitingForUser: true));
                // v1.5.0: one dialog at a time, and a batch's "the same for the rest" answers
                // its other downloads without asking (ConflictDecisions). Waiting for another
                // download's dialog is waiting for the user too.
                var conflictResolution = await _conflicts.DecideAsync(job.BatchId,
                    offerForTheRest => ShowConflictDialogOnUiThreadAsync(probableOutput, offerForTheRest),
                    cancellationToken).ConfigureAwait(false);
                // Cancelled while its dialog was up or waiting its turn: start nothing.
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new DownloadProgressSnapshot(0, null, null));
                if (conflictResolution == FilenameConflictResolution.Cancel)
                    return new DownloadExecutionResult(false, null, null, WasCancelled: true);
                if (conflictResolution == FilenameConflictResolution.AutoRename)
                {
                    claim.Dispose();
                    (resolvedStem, claim) = _names.ClaimFree(job.SaveDirectory, sanitizedStem, Finished);
                }
                // 覆蓋 keeps the name this download already holds; ffmpeg's mux/cut always
                // pass -y, and the .media path deletes the old file only after success.
            }

            var finalPath = Path.Combine(job.SaveDirectory, resolvedStem + finalExt);
            return await RunPipelineAsync(job, resolvedStem, finalPath, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            claim?.Dispose();
        }
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
        //
        // v1.5.0: with nothing to do after yt-dlp (no clip, no subtitles) it writes the
        // final name itself. The ".media" detour ended in a rename of the whole file,
        // which endpoint security on managed PCs holds ~6 s for an .mp4 (measured
        // 2026-11-04, 下載→已完成 of a 19-s clip: 42.8 s; an .m4a moves in ~50 ms).
        //   - Not when the final file exists (覆蓋): the old file stays until the new one
        //     is complete, as before.
        //   - Not for 音訊: converting to MP3 first downloads "<title>.webm" and deletes it
        //     afterwards — under the final stem that could be a file of the user's.
        //     The work files of 影片 / 無聲影片 are "<title>.f137.mp4" and ".part" files,
        //     which DownloadLeftovers tells apart from anything else.
        var direct = !hasClip && !hasSubs && job.Mode != DownloadMode.AudioOnly && !File.Exists(finalPath);
        var mediaStem = direct ? resolvedStem : resolvedStem + ".media";

        // v1.5.0: a clean start under this name. yt-dlp no longer gets --force-overwrites,
        // which also switches --continue off — so a 403 retry or a throttle re-extract
        // started over from byte 0 — and what an earlier run left here (possibly a
        // different video with the same title) must not be resumed into this one.
        ClearLeftovers(job.SaveDirectory, mediaStem, direct);

        // ----- Step 1: download media only -----
        progress.Report(new DownloadProgressSnapshot(0, null, null));
        var mediaProgressMax = hasClip ? 65.0 : 80.0;
        // Inline, like the queue's own progress: forwarded on yt-dlp's reader thread, in order.
        var mediaProgress = new InlineProgress<ProgressReport>(p =>
            progress.Report(new DownloadProgressSnapshot(
                Math.Min(mediaProgressMax, p.Percent * mediaProgressMax / 100.0),
                p.BytesPerSecond, p.Eta, Processing: p.Processing)));

        // v1.1.32: EmbedThumbnail OFF for VideoOnly mode (its ffprobe step failed on
        // installs without ffprobe.exe). v1.5.0: off for 影片 too — the cover now goes
        // into audio files only; see CoverArt for why.
        var embedThumbnail = CoverArt.Embeds(job.Mode, hasClip);
        var mediaRequest = new DownloadRequest(
            Url: job.Url,
            Mode: job.Mode,
            ChosenFormat: job.ChosenFormat,
            SubtitleLanguageCodes: Array.Empty<string>(),
            ClipRange: null,
            SaveDirectory: job.SaveDirectory,
            SanitizedFileStem: mediaStem,
            EmbedThumbnail: embedThumbnail,
            // Cleared above instead: --force-overwrites implies --no-continue.
            ForceOverwrite: false);

        var mediaSw = Stopwatch.StartNew();
        var mediaResult = await _ytDlp.DownloadAsync(mediaRequest, mediaProgress, cancellationToken).ConfigureAwait(false);
        mediaSw.Stop();

        if (mediaResult.WasCancelled)
        {
            ClearLeftovers(job.SaveDirectory, mediaStem, direct);
            if (direct) DeleteUnfinished(finalPath, mediaResult.OutputFilePath, mediaStem);
            MappedError? diag = null;
            if (!string.IsNullOrEmpty(mediaResult.ErrorStderr))
                diag = new MappedError(ErrorCategory.UnknownError, "", "E-CANCEL-DIAG",
                    false, RawDetails: mediaResult.ErrorStderr);
            return new DownloadExecutionResult(false, null, diag, true);
        }
        if (!mediaResult.IsSuccess)
        {
            ClearLeftovers(job.SaveDirectory, mediaStem, direct);
            if (direct) DeleteUnfinished(finalPath, mediaResult.OutputFilePath, mediaStem);
            return new DownloadExecutionResult(false, null,
                ErrorMapper.Map(mediaResult.ErrorStderr ?? ""), false);
        }

        // Verify a real media file actually landed before continuing. Under the final
        // name only that file counts: a stem-wide probe would also see the user's own
        // "<title>.m4a" next to a missing "<title>.mp4".
        var directMedia = direct ? DirectOutput(finalPath, mediaResult.OutputFilePath, mediaStem) : null;
        if (direct && directMedia is null)
        {
            ClearLeftovers(job.SaveDirectory, mediaStem, direct);
            return new DownloadExecutionResult(false, null,
                new MappedError(ErrorCategory.UnknownError,
                    "下載未產生影音檔案，僅有附件（字幕/縮圖）。請嘗試其他格式或畫質。",
                    "E-NOMEDIA1", false, ""),
                WasCancelled: false);
        }
        var mediaProbe = direct
            ? (found: true, sidecarPaths: (IReadOnlyList<string>)Array.Empty<string>())
            : MediaOutputProbe.VerifyMediaOutputExists(job.SaveDirectory, mediaStem);
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

        var mediaPath = directMedia ?? mediaResult.OutputFilePath;
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
            // v1.5.0: not under the final name (direct): nothing writes a thumbnail there
            // (CoverArt: 音訊 only, which never goes direct), and "<title>.jpg" next to
            // it would be the user's.
            if (!direct) CleanupOrphanSidecars(currentMediaPath);
            try
            {
                // Written under the final name already (direct): nothing to move.
                if (!string.Equals(currentMediaPath, finalPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(currentMediaPath, finalPath);
                }
            }
            catch
            {
                // Fall back: report the intermediate name so the user can still find the file.
                finalPath = currentMediaPath;
            }
        }

        progress.Report(new DownloadProgressSnapshot(100.0, null, null));
        if (!direct) CleanupOrphanSidecars(finalPath);
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

    /// <summary>
    /// yt-dlp's work files under <paramref name="stem"/>. Under a ".media" stem every file
    /// is ours; under the final name only the work files are (DownloadLeftovers), never a
    /// finished "<title>.mp4" or anything else of the user's.
    /// </summary>
    private static void ClearLeftovers(string dir, string stem, bool direct)
    {
        if (direct) DownloadLeftovers.Delete(dir, stem);
        else TryDeleteByStem(dir, stem);
    }

    /// <summary>
    /// v1.5.0: a download under its final name that did not finish — cancelled, failed, or
    /// stopped by the watchdog. yt-dlp merges into the final name before its post-processors
    /// run, so a file may already be there; it is not one to keep, and OutputNames guarantees
    /// no other download of this app writes that name (direct also means it did not exist
    /// when this download started).
    /// </summary>
    private static void DeleteUnfinished(string finalPath, string? reported, string stem)
    {
        TryDelete(finalPath);
        if (reported is not null &&
            string.Equals(Path.GetDirectoryName(reported), Path.GetDirectoryName(finalPath), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileNameWithoutExtension(reported), stem, StringComparison.OrdinalIgnoreCase) &&
            MediaOutputProbe.MediaExtensions.Contains(Path.GetExtension(reported), StringComparer.OrdinalIgnoreCase))
            TryDelete(reported);
    }

    /// <summary>The finished file of a download written under its final name, or null.</summary>
    private static string? DirectOutput(string finalPath, string? reported, string stem)
    {
        if (File.Exists(finalPath)) return finalPath;
        // yt-dlp picked another container than ExtensionForMode expects.
        return reported is not null && File.Exists(reported) &&
               string.Equals(Path.GetFileNameWithoutExtension(reported), stem, StringComparison.OrdinalIgnoreCase) &&
               MediaOutputProbe.MediaExtensions.Contains(Path.GetExtension(reported), StringComparer.OrdinalIgnoreCase)
            ? reported
            : null;
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

    private static string? ExtensionForMode(DownloadJob job) => job.Mode switch
    {
        DownloadMode.AudioOnly =>
            (job.ChosenFormat.Extension is "m4a" or "mp4") ? ".m4a" : ".mp3",
        DownloadMode.VideoOnly =>
            "." + (string.IsNullOrEmpty(job.ChosenFormat.Extension) ? "mp4" : job.ChosenFormat.Extension),
        DownloadMode.AudioAndVideo => ".mp4",
        _ => null
    };

    private static Task<(FilenameConflictResolution, bool)> ShowConflictDialogOnUiThreadAsync(
        string conflictingPath, bool offerForTheRest)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null)
            return Task.FromResult((FilenameConflictResolution.Overwrite, false));
        return app.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new FilenameConflictDialog(conflictingPath, offerForTheRest)
            {
                Owner = app.MainWindow
            };
            dlg.ShowDialog();
            return (dlg.Resolution, dlg.ForTheRest);
        }).Task;
    }
}
