using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Updates;

public sealed class UpdateApplier
{
    private readonly IUpdateHttpClient _http;
    private readonly SigstoreVerifierOptions _sigstoreOptions;
    private readonly AppPaths _paths;

    public UpdateApplier(IUpdateHttpClient http, SigstoreVerifierOptions sigstoreOptions, AppPaths paths)
    {
        _http = http;
        _sigstoreOptions = sigstoreOptions;
        _paths = paths;
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        IReadOnlyList<ManifestFileEntry> entries,
        IProgress<UpdateApplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.UpdateStaging);

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
            progress?.Report(new UpdateApplyProgress(UpdateApplyStage.RolledBack, "", 0, 0, entries.Count));
            return new UpdateApplyResult(false, $"套用失敗已還原：{ex.Message}");
        }

        // Step 5: delete backups
        foreach (var (_, backup) in applied)
            if (!string.IsNullOrEmpty(backup) && File.Exists(backup))
                try { File.Delete(backup); } catch { }

        progress?.Report(new UpdateApplyProgress(UpdateApplyStage.Done, "", 100, entries.Count, entries.Count));
        return new UpdateApplyResult(true, null);
    }

    private static void Cleanup(IEnumerable<(ManifestFileEntry, string)> staged)
    {
        foreach (var (_, p) in staged)
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
