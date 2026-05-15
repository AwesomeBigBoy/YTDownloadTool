using System.Net.Http;
using System.Text.Json;
using YtDlpTool.Domain.Logging;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;

namespace YtDlpTool.Domain.Updates;

public sealed record InstalledVersions(string App, string YtDlp, string Ffmpeg);

public sealed class UpdateChecker
{
    private readonly IUpdateHttpClient _http;
    private readonly SigstoreVerifierOptions _sigstoreOptions;
    private readonly string _owner;
    private readonly string _repo;
    // v1.1.6: optional structured logger so users can see *why* the check fell back
    // to the generic "找不到最新版本" message (HTTPS inspection, missing manifest
    // asset on the latest release, sigstore verify failure, etc.) by inspecting
    // the most recent log file from Settings → 顯示診斷詳情.
    private readonly AppLogger? _logger;

    /// <summary>
    /// Friendly Chinese message surfaced when GitHub has no release marked "latest" AND
    /// none of the recent releases carry a manifest.json asset. We deliberately do NOT
    /// pass the raw HttpRequestException up to the UI ("response status code does not
    /// indicate success: 404") because users have no idea what to do with that.
    /// </summary>
    public const string FriendlyMissingLatestMessage =
        "找不到最新版本：請至 GitHub Releases 確認該 repo 的最新 release 已標示為 latest";

    /// <summary>
    /// How many recent releases to enumerate when /releases/latest doesn't yield a
    /// usable release. The pre-v1.1.5 cap was 10; bumped to 30 because real-world
    /// repos can ship a flurry of manually-created GitHub-UI releases (no manifest
    /// asset) that push the first usable workflow-built release past the 10-mark.
    /// </summary>
    private const int RecentReleasesScanLimit = 30;

    public UpdateChecker(
        IUpdateHttpClient http,
        SigstoreVerifierOptions sigstoreOptions,
        string owner,
        string repo,
        AppLogger? logger = null)
    {
        _http = http;
        _sigstoreOptions = sigstoreOptions;
        _owner = owner;
        _repo = repo;
        _logger = logger;
    }

    public async Task<UpdateAvailability> CheckAsync(
        InstalledVersions installed,
        CancellationToken cancellationToken)
    {
        Log("update.check.start", new Dictionary<string, string>
        {
            ["owner"] = _owner,
            ["repo"] = _repo
        });
        try
        {
            // Try /releases/latest first — if it returns a release that has BOTH a
            // manifest.json and a manifest.json.sigstore asset, we attempt that one.
            // If it doesn't, or the attempted release ends up with zero updatable
            // files (e.g. unparseable manifest or empty Files list), fall through
            // to a scan of recent releases.
            GitHubReleaseDto? primary = null;
            try
            {
                primary = await _http.GetLatestReleaseAsync(_owner, _repo, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Log("update.check.primary", new Dictionary<string, string>
                {
                    ["tag"] = "none",
                    ["usable"] = "false",
                    ["http_error"] = ex.Message
                });
                primary = null;
            }

            UpdateAvailability? lastTerminalFailure = null;
            if (primary is not null)
            {
                var primaryUsable = IsUsableRelease(primary);
                Log("update.check.primary", new Dictionary<string, string>
                {
                    ["tag"] = primary.TagName ?? "none",
                    ["usable"] = primaryUsable ? "true" : "false"
                });
                if (primaryUsable)
                {
                    var primaryResult = await TryEvaluateReleaseAsync(primary, installed, cancellationToken).ConfigureAwait(false);
                    if (primaryResult.ShouldReturn)
                    {
                        Log("update.check.result", new Dictionary<string, string>
                        {
                            ["status"] = primaryResult.Availability!.HasUpdate ? "update_available" : "up_to_date"
                        });
                        return primaryResult.Availability!;
                    }
                    // Record terminal failure (signature / manifest-parse) so the recent
                    // fallback can still surface it if no later release is verifiable.
                    if (primaryResult.Availability is not null)
                        lastTerminalFailure = primaryResult.Availability;
                    // Otherwise fall through to recent-release fallback.
                }
            }
            else
            {
                // Already logged the HTTP error above; nothing further to record.
            }

            // Fallback: enumerate recent releases (newest first). Pick the first that
            //  - is not draft, not prerelease,
            //  - has BOTH manifest.json AND manifest.json.sigstore assets,
            //  - and evaluates to a non-trivial UpdateAvailability (i.e. parseable
            //    manifest + signature verifies + has Files entries).
            IReadOnlyList<GitHubReleaseDto> recent;
            try
            {
                recent = await _http.GetRecentReleasesAsync(_owner, _repo, RecentReleasesScanLimit, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Log("update.check.recent_scan", new Dictionary<string, string>
                {
                    ["count"] = "0",
                    ["usable_count"] = "0",
                    ["http_error"] = ex.Message
                });
                var fallback = lastTerminalFailure ?? NoUpdate(FriendlyMissingLatestMessage);
                LogResult(fallback, lastTerminalFailure is not null);
                return fallback;
            }

            var usableCount = recent.Count(IsUsableRelease);
            Log("update.check.recent_scan", new Dictionary<string, string>
            {
                ["count"] = recent.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["usable_count"] = usableCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

            foreach (var candidate in recent)
            {
                if (!IsUsableRelease(candidate)) continue;
                // Skip the primary if we already tried it above — no point repeating
                // the same network calls and signature failure for the same tag.
                if (primary is not null && candidate.TagName == primary.TagName) continue;

                var attempt = await TryEvaluateReleaseAsync(candidate, installed, cancellationToken).ConfigureAwait(false);
                if (attempt.ShouldReturn)
                {
                    Log("update.check.result", new Dictionary<string, string>
                    {
                        ["status"] = attempt.Availability!.HasUpdate ? "update_available" : "up_to_date"
                    });
                    return attempt.Availability!;
                }
                // Record the most recent terminal failure (e.g. signature failure)
                // in case nothing else works; better than the generic missing-latest.
                if (attempt.Availability is not null)
                    lastTerminalFailure = attempt.Availability;
            }

            var result = lastTerminalFailure ?? NoUpdate(FriendlyMissingLatestMessage);
            LogResult(result, lastTerminalFailure is not null);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any other error during the check (DNS, TLS, broken JSON, ...) shouldn't
            // bubble a raw exception to the UI; show a friendly fallback instead.
            Log("update.check.result", new Dictionary<string, string>
            {
                ["status"] = "fallback_friendly_msg",
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message
            });
            return NoUpdate(FriendlyMissingLatestMessage);
        }
    }

    private void LogResult(UpdateAvailability result, bool hadTerminalFailure)
    {
        string status;
        if (result.HasUpdate) status = "update_available";
        else if (result.FailureReason == FriendlyMissingLatestMessage) status = "fallback_friendly_msg";
        else if (hadTerminalFailure && !string.IsNullOrEmpty(result.FailureReason))
            status = "specific_failure: " + result.FailureReason;
        else status = "up_to_date";
        Log("update.check.result", new Dictionary<string, string> { ["status"] = status });
    }

    private void Log(string category, IReadOnlyDictionary<string, string> fields)
    {
        _logger?.Info(category, fields);
    }

    /// <summary>
    /// "Usable" means non-draft, non-prerelease, and carries BOTH the manifest and its
    /// sigstore bundle as release assets. A release that has only manifest.json (or
    /// only the sigstore bundle) cannot be verified, so we treat it as not usable and
    /// move on to the next candidate.
    /// </summary>
    private static bool IsUsableRelease(GitHubReleaseDto r)
    {
        if (r.Draft || r.Prerelease) return false;
        if (r.Assets is null) return false;
        var hasManifest = r.Assets.Any(a => a.Name == "manifest.json" && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));
        var hasSigstore = r.Assets.Any(a => a.Name == "manifest.json.sigstore" && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));
        return hasManifest && hasSigstore;
    }

    private readonly record struct ReleaseAttempt(bool ShouldReturn, UpdateAvailability? Availability);

    /// <summary>
    /// Attempt to evaluate a single release. Returns ShouldReturn=true when the result
    /// is conclusive enough to surface to the user immediately (parseable manifest with
    /// at least one Files entry, regardless of whether it's newer). Returns
    /// ShouldReturn=false to indicate "this release didn't yield a usable answer, try
    /// the next candidate" — used for HTTP fetch failures, sigstore failures, manifest
    /// parse failures, and empty-Files manifests.
    /// </summary>
    private async Task<ReleaseAttempt> TryEvaluateReleaseAsync(
        GitHubReleaseDto release,
        InstalledVersions installed,
        CancellationToken ct)
    {
        var tag = release.TagName ?? "(notag)";
        var manifestAsset = release.Assets?.FirstOrDefault(a => a.Name == "manifest.json");
        var manifestSigAsset = release.Assets?.FirstOrDefault(a => a.Name == "manifest.json.sigstore");
        if (manifestAsset?.BrowserDownloadUrl is null || manifestSigAsset?.BrowserDownloadUrl is null)
        {
            LogEvaluate(tag, "manifest-fetch-failed");
            return new ReleaseAttempt(false, null);
        }

        string manifestJson;
        string manifestSigJson;
        try
        {
            manifestJson = await _http.GetStringAsync(manifestAsset.BrowserDownloadUrl, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            LogEvaluate(tag, "manifest-fetch-failed");
            return new ReleaseAttempt(false, null);
        }
        try
        {
            manifestSigJson = await _http.GetStringAsync(manifestSigAsset.BrowserDownloadUrl, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            LogEvaluate(tag, "sig-fetch-failed");
            return new ReleaseAttempt(false, null);
        }

        var sigVerify = SigstoreVerifier.Verify(
            artifactBytes: System.Text.Encoding.UTF8.GetBytes(manifestJson),
            bundleJson: manifestSigJson,
            options: _sigstoreOptions);

        if (!sigVerify.IsValid)
        {
            LogEvaluate(tag, "sig-verify-failed: " + (sigVerify.FailureReason ?? ""));
            // Signature failure is terminal-ish: bubble it as the lastTerminalFailure
            // so we don't mask a real signing issue with the generic missing-latest
            // message, but allow the loop to keep scanning in case an earlier release
            // does verify cleanly.
            return new ReleaseAttempt(false,
                NoUpdate($"簽章驗證失敗：{sigVerify.FailureReason}"));
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestJson, AppJsonContext.Default.UpdateManifest);
        }
        catch (JsonException)
        {
            LogEvaluate(tag, "manifest-parse-failed");
            return new ReleaseAttempt(false, NoUpdate("manifest 解析失敗"));
        }
        if (manifest is null || manifest.Files.Count == 0)
        {
            LogEvaluate(tag, "empty-files");
            // Empty Files list = manifest is structurally valid but useless. Treat
            // as "try the next release" rather than declaring victory with zero files.
            return new ReleaseAttempt(false, null);
        }

        var newer = new List<ManifestFileEntry>();
        foreach (var f in manifest.Files)
        {
            var localVersion = f.Component switch
            {
                UpdateComponent.App => installed.App,
                UpdateComponent.YtDlp => installed.YtDlp,
                UpdateComponent.Ffmpeg => installed.Ffmpeg,
                _ => ""
            };
            if (InstalledVersionProbe.IsRemoteNewer(localVersion, f.Version))
                newer.Add(f);
        }

        LogEvaluate(tag, newer.Count > 0 ? "ok-has-update" : "ok-no-update");
        return new ReleaseAttempt(true, new UpdateAvailability(newer.Count > 0, manifest, newer, null));
    }

    private void LogEvaluate(string tag, string status)
    {
        _logger?.Info("update.check.evaluate", new Dictionary<string, string>
        {
            ["tag"] = tag,
            ["status"] = status
        });
    }

    private static UpdateAvailability NoUpdate(string? failureReason) =>
        new(false, null, Array.Empty<ManifestFileEntry>(), failureReason);
}
