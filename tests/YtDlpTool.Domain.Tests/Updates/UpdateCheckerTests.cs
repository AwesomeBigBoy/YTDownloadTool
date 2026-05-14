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
        public Dictionary<string, string> Strings = new();

        public Task<GitHubReleaseDto?> GetLatestReleaseAsync(string o, string r, CancellationToken ct) =>
            OnGetRelease?.Invoke() ?? Task.FromResult<GitHubReleaseDto?>(null);
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
