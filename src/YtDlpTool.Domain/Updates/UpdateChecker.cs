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
            var release = await _http.GetLatestReleaseAsync(_owner, _repo, cancellationToken).ConfigureAwait(false);
            if (release is null || release.Draft || release.Prerelease || release.Assets is null)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), "no release");

            var manifestAsset = release.Assets.FirstOrDefault(a => a.Name == "manifest.json");
            var manifestSigAsset = release.Assets.FirstOrDefault(a => a.Name == "manifest.json.sigstore");
            if (manifestAsset?.BrowserDownloadUrl is null || manifestSigAsset?.BrowserDownloadUrl is null)
                return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), "manifest assets missing");

            var manifestJson = await _http.GetStringAsync(manifestAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
            var manifestSigJson = await _http.GetStringAsync(manifestSigAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);

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
        catch (Exception ex)
        {
            return new UpdateAvailability(false, null, Array.Empty<ManifestFileEntry>(), ex.Message);
        }
    }
}
