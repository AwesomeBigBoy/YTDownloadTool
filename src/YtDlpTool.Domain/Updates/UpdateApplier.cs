using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Updates;

public sealed class UpdateApplier
{
    private readonly IUpdateHttpClient _http;
    private readonly SigstoreVerifierOptions _sigstoreOptions;
    private readonly AppPaths _paths;
    private readonly AppLogger? _logger;

    public UpdateApplier(IUpdateHttpClient http, SigstoreVerifierOptions sigstoreOptions, AppPaths paths)
        : this(http, sigstoreOptions, paths, logger: null) { }

    public UpdateApplier(IUpdateHttpClient http, SigstoreVerifierOptions sigstoreOptions, AppPaths paths, AppLogger? logger)
    {
        _http = http;
        _sigstoreOptions = sigstoreOptions;
        _paths = paths;
        _logger = logger;
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        IReadOnlyList<ManifestFileEntry> entries,
        IProgress<UpdateApplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        _logger?.Info("update.apply.start", new Dictionary<string, string>
        {
            ["count"] = entries.Count.ToString()
        });
        var result = await ApplyCoreAsync(entries, progress, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            _logger?.Info("update.apply.success", new Dictionary<string, string>
            {
                ["elapsed_ms"] = elapsed.ToString()
            });
        }
        else
        {
            _logger?.Warn("update.apply.failed", new Dictionary<string, string>
            {
                ["reason"] = result.FailureReason ?? "unknown"
            });
        }
        return result;
    }

    private async Task<UpdateApplyResult> ApplyCoreAsync(
        IReadOnlyList<ManifestFileEntry> entries,
        IProgress<UpdateApplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.UpdateStaging);

        // Defense-in-depth: validate every entry's target path lives strictly inside AppDirectory.
        // Even though manifests are Sigstore-signed, a compromised signer or future bug
        // must not be able to overwrite arbitrary filesystem locations via `..\..\` traversal
        // or absolute paths like `C:\Windows\...`.
        var appDirCanonical = Path.GetFullPath(_paths.AppDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.Name.Contains('/') || entry.Name.Contains('\\'))
                return new UpdateApplyResult(false, $"無效的元件名稱：{entry.Name}");
            string resolvedTarget;
            try
            {
                resolvedTarget = Path.GetFullPath(Path.Combine(appDirCanonical, entry.TargetRelativePath));
            }
            catch (Exception ex)
            {
                return new UpdateApplyResult(false, $"目標路徑解析失敗：{ex.Message}");
            }
            if (!resolvedTarget.StartsWith(appDirCanonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(resolvedTarget, appDirCanonical, StringComparison.OrdinalIgnoreCase))
                return new UpdateApplyResult(false, $"目標路徑超出安裝目錄：{entry.TargetRelativePath}");
        }

        // Step 1+2: download all + verify hashes
        var staged = new List<(ManifestFileEntry Entry, string StagedPath)>();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var staged_path = Path.Combine(_paths.UpdateStaging, entry.Name + ".new");
            progress?.Report(new UpdateApplyProgress(UpdateApplyStage.Downloading, entry.Name, 0, i + 1, entries.Count));

            try
            {
                await _http.DownloadAsync(entry.DownloadUrl, staged_path,
                    new Progress<double>(p =>
                        progress?.Report(new UpdateApplyProgress(UpdateApplyStage.Downloading, entry.Name, p, i + 1, entries.Count))),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Cleanup(staged);
                return new UpdateApplyResult(false, $"下載 {entry.Name} 失敗：{ex.Message}");
            }

            progress?.Report(new UpdateApplyProgress(UpdateApplyStage.VerifyingHash, entry.Name, 100, i + 1, entries.Count));
            if (!Sha256Verifier.VerifyFile(staged_path, entry.Sha256))
            {
                Cleanup(staged);
                File.Delete(staged_path);
                return new UpdateApplyResult(false, $"{entry.Name} 內容校驗失敗");
            }

            // Step 3: per-file Sigstore signature (defence in depth)
            progress?.Report(new UpdateApplyProgress(UpdateApplyStage.VerifyingSignature, entry.Name, 100, i + 1, entries.Count));
            try
            {
                var sigJson = await _http.GetStringAsync(entry.SignatureUrl, cancellationToken).ConfigureAwait(false);
                var fileBytes = await File.ReadAllBytesAsync(staged_path, cancellationToken).ConfigureAwait(false);
                var sigResult = SigstoreVerifier.Verify(fileBytes, sigJson, _sigstoreOptions);
                if (!sigResult.IsValid)
                {
                    Cleanup(staged);
                    File.Delete(staged_path);
                    return new UpdateApplyResult(false, $"{entry.Name} 簽章驗證失敗：{sigResult.FailureReason}");
                }
            }
            catch (Exception ex)
            {
                Cleanup(staged);
                File.Delete(staged_path);
                return new UpdateApplyResult(false, $"{entry.Name} 簽章下載失敗：{ex.Message}");
            }

            staged.Add((entry, staged_path));
        }

        // Step 4: atomically replace each file with rollback support
        var applied = new List<(string Live, string Backup)>();
        try
        {
            foreach (var (entry, stagedPath) in staged)
            {
                var livePath = Path.Combine(_paths.AppDirectory, entry.TargetRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                var backupPath = livePath + ".old";

                if (File.Exists(livePath))
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Move(livePath, backupPath);
                    applied.Add((livePath, backupPath));
                }
                else
                {
                    applied.Add((livePath, ""));
                }
                File.Move(stagedPath, livePath);
                progress?.Report(new UpdateApplyProgress(UpdateApplyStage.Applying, entry.Name, 100, applied.Count, entries.Count));
            }
        }
        catch (Exception ex)
        {
            foreach (var (live, backup) in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(live)) File.Delete(live);
                    if (!string.IsNullOrEmpty(backup) && File.Exists(backup))
                        File.Move(backup, live);
                }
                catch { }
            }
            // Rollback path: same scrub as the success path so a failed apply doesn't
            // leave .new files dangling in staging that confuse the next attempt.
            ScrubStaging();
            progress?.Report(new UpdateApplyProgress(UpdateApplyStage.RolledBack, "", 0, 0, entries.Count));
            return new UpdateApplyResult(false, $"套用失敗已還原：{ex.Message}");
        }

        // Step 5: delete backups
        foreach (var (_, backup) in applied)
            if (!string.IsNullOrEmpty(backup) && File.Exists(backup))
                try { File.Delete(backup); } catch { }

        // Step 6: scrub the staging directory. Even after a clean apply, the move/rename
        // sequence above can leave behind .partial/.tmp/.new files when yt-dlp resumes a
        // half-finished download in a future session, or when an antivirus held a file
        // briefly open. We don't want any breadcrumbs left in the user's data folder, so
        // wipe the whole staging dir and recreate it empty.
        ScrubStaging();
        _logger?.Info("update.cleanup", new Dictionary<string, string>
        {
            ["staging_emptied"] = "true"
        });

        progress?.Report(new UpdateApplyProgress(UpdateApplyStage.Done, "", 100, entries.Count, entries.Count));
        return new UpdateApplyResult(true, null);
    }

    /// <summary>
    /// Best-effort recursive delete of <see cref="AppPaths.UpdateStaging"/>. We swallow
    /// every exception because cleanup is never load-bearing: if a stale file is held
    /// open by another process the next apply will overwrite the .new with the same path.
    /// </summary>
    private void ScrubStaging()
    {
        try
        {
            if (Directory.Exists(_paths.UpdateStaging))
                Directory.Delete(_paths.UpdateStaging, recursive: true);
        }
        catch { }
        try { Directory.CreateDirectory(_paths.UpdateStaging); } catch { }
    }

    private static void Cleanup(IEnumerable<(ManifestFileEntry, string)> staged)
    {
        foreach (var (_, p) in staged)
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
