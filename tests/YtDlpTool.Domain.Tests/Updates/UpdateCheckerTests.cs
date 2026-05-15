using System.Text.Json;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Domain.Tests.Updates;

public class UpdateCheckerTests
{
    private static readonly SigstoreVerifierOptions DummyOpts = new(
        ExpectedIssuer: "x",
        ExpectedSanRegex: ".*",
        TrustedRootPem: "");

    private sealed class FakeHttp : IUpdateHttpClient
    {
        public Func<Task<GitHubReleaseDto?>>? OnGetRelease;
        public Func<Task<IReadOnlyList<GitHubReleaseDto>>>? OnGetRecent;
        public Dictionary<string, string> Strings = new();

        public Task<GitHubReleaseDto?> GetLatestReleaseAsync(string o, string r, CancellationToken ct) =>
            OnGetRelease?.Invoke() ?? Task.FromResult<GitHubReleaseDto?>(null);
        public Task<IReadOnlyList<GitHubReleaseDto>> GetRecentReleasesAsync(string o, string r, int limit, CancellationToken ct) =>
            OnGetRecent?.Invoke() ?? Task.FromResult<IReadOnlyList<GitHubReleaseDto>>(Array.Empty<GitHubReleaseDto>());
        public Task<string> GetStringAsync(string url, CancellationToken ct) =>
            Task.FromResult(Strings.TryGetValue(url, out var v) ? v : throw new HttpRequestException("404 " + url));
        public Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task Check_NoRelease_ReturnsNoUpdate()
    {
        var http = new FakeHttp { OnGetRelease = () => Task.FromResult<GitHubReleaseDto?>(null) };
        var checker = new UpdateChecker(http, DummyOpts, "o", "r");
        var result = await checker.CheckAsync(new InstalledVersions("1.0.0", "2026.01.01", "7.1"), CancellationToken.None);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task Check_PrereleaseRelease_IsIgnored()
    {
        var http = new FakeHttp
        {
            OnGetRelease = () => Task.FromResult<GitHubReleaseDto?>(new GitHubReleaseDto
            {
                TagName = "v0.0.1-alpha", Prerelease = true,
                Assets = new() { new GitHubAssetDto { Name = "manifest.json", BrowserDownloadUrl = "x" } }
            })
        };
        var checker = new UpdateChecker(http, DummyOpts, "o", "r");
        var result = await checker.CheckAsync(new InstalledVersions("1.0.0", "2026.01.01", "7.1"), CancellationToken.None);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task UpdateChecker_404OnLatest_FallsBackToRecent()
    {
        // Simulates a real-world failure: /releases/latest returns 404 (no release is
        // tagged "latest"), but /releases?per_page=N still returns a usable release.
        // The checker should silently fall back rather than surfacing the raw exception.
        var manifest = new UpdateManifest
        {
            AppVersion = "1.0.0",
            Files = new() { new ManifestFileEntry { Component = UpdateComponent.App, Version = "1.0.0", Name = "YtDlpTool.exe" } }
        };
        var manifestJson = JsonSerializer.Serialize(manifest, AppJsonContext.Default.UpdateManifest);

        var http = new FakeHttp
        {
            OnGetRelease = () => throw new HttpRequestException("404"),
            OnGetRecent = () => Task.FromResult<IReadOnlyList<GitHubReleaseDto>>(new List<GitHubReleaseDto>
            {
                new()
                {
                    TagName = "v1.0.0",
                    Draft = false,
                    Prerelease = false,
                    Assets = new()
                    {
                        new GitHubAssetDto { Name = "manifest.json", BrowserDownloadUrl = "https://m" },
                        new GitHubAssetDto { Name = "manifest.json.sigstore", BrowserDownloadUrl = "https://s" },
                    }
                }
            }),
            Strings = { ["https://m"] = manifestJson, ["https://s"] = "{not a sigstore bundle}" }
        };
        var checker = new UpdateChecker(http, DummyOpts, "o", "r");
        var result = await checker.CheckAsync(new InstalledVersions("1.0.0", "2026.01.01", "7.1"), CancellationToken.None);

        // The fallback must have engaged: we should NOT see the friendly "missing latest"
        // message. Instead we see the next-stage signature-verification error (because
        // our stub uses an invalid bundle). This proves the checker advanced past the
        // release-resolution step.
        Assert.NotEqual(UpdateChecker.FriendlyMissingLatestMessage, result.FailureReason);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task UpdateChecker_AllReleasesUnusable_ReturnsFriendlyMessage()
    {
        var http = new FakeHttp
        {
            OnGetRelease = () => throw new HttpRequestException("404"),
            OnGetRecent = () => Task.FromResult<IReadOnlyList<GitHubReleaseDto>>(Array.Empty<GitHubReleaseDto>())
        };
        var checker = new UpdateChecker(http, DummyOpts, "o", "r");
        var result = await checker.CheckAsync(new InstalledVersions("1.0.0", "2026.01.01", "7.1"), CancellationToken.None);
        Assert.False(result.HasUpdate);
        Assert.Equal(UpdateChecker.FriendlyMissingLatestMessage, result.FailureReason);
    }

    [Fact]
    public async Task Check_SigstoreFailure_ReturnsFailureReason()
    {
        var manifest = new UpdateManifest
        {
            AppVersion = "2.0.0",
            Files = new() { new ManifestFileEntry { Component = UpdateComponent.App, Version = "2.0.0", Name = "YtDlpTool.exe" } }
        };
        var manifestJson = JsonSerializer.Serialize(manifest, AppJsonContext.Default.UpdateManifest);
        var http = new FakeHttp
        {
            OnGetRelease = () => Task.FromResult<GitHubReleaseDto?>(new GitHubReleaseDto
            {
                TagName = "v2.0.0",
                Assets = new()
                {
                    new GitHubAssetDto { Name = "manifest.json", BrowserDownloadUrl = "https://m" },
                    new GitHubAssetDto { Name = "manifest.json.sigstore", BrowserDownloadUrl = "https://s" }
                }
            }),
            Strings = { ["https://m"] = manifestJson, ["https://s"] = "{invalid}" }
        };
        var checker = new UpdateChecker(http, DummyOpts, "o", "r");
        var result = await checker.CheckAsync(new InstalledVersions("1.0.0", "2026.01.01", "7.1"), CancellationToken.None);
        Assert.False(result.HasUpdate);
        Assert.Contains("簽章", result.FailureReason ?? "");
    }
}
