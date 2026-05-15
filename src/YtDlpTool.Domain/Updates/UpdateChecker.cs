using System.Net.Http;
using System.Text.Json;
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

    /// <summary>
    /// Friendly Chinese message surfaced when GitHub has no release marked "latest" AND
    /// none of the recent releases carry a manifest.json asset. We deliberately do NOT
    /// pass the raw HttpRequestException up to the UI ("response status code does not
    /// indicate success: 404") because users have no idea what to do with that.
    /// </summary>
    public const string FriendlyMissingLatestMessage =
        "找不到最新版本：請至 GitHub Releases 確認該 repo 的最新 release 已標示為 latest";

    public UpdateChecker(IUpdateHttpClient http, SigstoreVerifierOptions sigstoreOptions, string owner, string repo)
    {
        _http = http;
        _sigstoreOptions = sigstoreOptions;
        _owner = owner;
        _repo = repo;
    }

    public async Task<UpdateAvailability> CheckAsync(
        InstalledVersions installed,
        CancellationToken cancellationToken)
    {
        try
        {
            var release = await ResolveLatestUsableReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release is null)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), FriendlyMissingLatestMessage);

            var manifestAsset = release.Assets?.FirstOrDefault(a => a.Name == "manifest.json");
            var manifestSigAsset = release.Assets?.FirstOrDefault(a => a.Name == "manifest.json.sigstore");
            if (manifestAsset?.BrowserDownloadUrl is null || manifestSigAsset?.BrowserDownloadUrl is null)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), FriendlyMissingLatestMessage);

            string manifestJson;
            string manifestSigJson;
            try
            {
                manifestJson = await _http.GetStringAsync(manifestAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
                manifestSigJson = await _http.GetStringAsync(manifestSigAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), FriendlyMissingLatestMessage);
            }

            var sigVerify = SigstoreVerifier.Verify(
                artifactBytes: System.Text.Encoding.UTF8.GetBytes(manifestJson),
                bundleJson: manifestSigJson,
                options: _sigstoreOptions);

            if (!sigVerify.IsValid)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), $"簽章驗證失敗：{sigVerify.FailureReason}");

            var manifest = JsonSerializer.Deserialize(manifestJson, AppJsonContext.Default.UpdateManifest);
            if (manifest is null)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), "manifest 解析失敗");

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

            return new UpdateAvailability(newer.Count > 0, manifest, newer, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Any other error during the check (DNS, TLS, broken JSON, ...) shouldn't
            // bubble a raw exception to the UI; show a friendly fallback instead.
            return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), FriendlyMissingLatestMessage);
        }
    }

    /// <summary>
    /// Two-stage release resolver: try /releases/latest first (the GitHub-recommended path,
    /// honours the "latest" flag the maintainer sets) and fall back to /releases?per_page=N
    /// when that 404s or returns null. The fallback picks the first non-draft non-prerelease
    /// entry whose assets include a manifest.json so we never pretend a release is usable
    /// when the artefacts we need are missing.
    /// </summary>
    private async Task<GitHubReleaseDto?> ResolveLatestUsableReleaseAsync(CancellationToken ct)
    {
        GitHubReleaseDto? release = null;
        try
        {
            release = await _http.GetLatestReleaseAsync(_owner, _repo, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            release = null;
        }

        if (release is not null && !release.Draft && !release.Prerelease
            && release.Assets is not null
            && release.Assets.Any(a => a.Name == "manifest.json"))
        {
            return release;
        }

        // Fallback path: enumerate recent releases and pick the first usable one.
        IReadOnlyList<GitHubReleaseDto> recent;
        try
        {
            recent = await _http.GetRecentReleasesAsync(_owner, _repo, 10, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        return recent.FirstOrDefault(r =>
            !r.Draft
            && !r.Prerelease
            && r.Assets is not null
            && r.Assets.Any(a => a.Name == "manifest.json"));
    }
}
