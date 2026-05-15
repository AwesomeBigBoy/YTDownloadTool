using System.Security.Cryptography;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Security;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Domain.Tests.Updates;

public class UpdateApplierTests : IDisposable
{
    private readonly string _appDir;
    private readonly AppPaths _paths;

    public UpdateApplierTests()
    {
        _appDir = Path.Combine(Path.GetTempPath(), "ytdlp-app-" + Guid.NewGuid());
        Directory.CreateDirectory(_appDir);
        _paths = AppPaths.ResolveForAppDirectory(_appDir);
        _paths.EnsureDataDirectoriesExist();
    }

    public void Dispose() => Directory.Delete(_appDir, recursive: true);

    private sealed class StubHttp : IUpdateHttpClient
    {
        public Dictionary<string, byte[]> Files = new();
        public Dictionary<string, string> Strings = new();

        public Task<GitHubReleaseDto?> GetLatestReleaseAsync(string o, string r, CancellationToken ct) =>
            Task.FromResult<GitHubReleaseDto?>(null);
        public Task<IReadOnlyList<GitHubReleaseDto>> GetRecentReleasesAsync(string o, string r, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GitHubReleaseDto>>(Array.Empty<GitHubReleaseDto>());
        public Task<string> GetStringAsync(string url, CancellationToken ct) =>
            Task.FromResult(Strings[url]);
        public async Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllBytesAsync(dest, Files[url], ct);
            p?.Report(100);
        }
    }

    [Fact]
    public async Task Apply_HashMismatch_AbortsAndCleansUp()
    {
        var http = new StubHttp();
        var content = System.Text.Encoding.UTF8.GetBytes("totally legit");
        http.Files["https://fake/yt-dlp.exe"] = content;
        http.Strings["https://fake/yt-dlp.exe.sigstore"] = "{}";

        var entry = new ManifestFileEntry
        {
            Name = "yt-dlp.exe",
            Component = UpdateComponent.YtDlp,
            DownloadUrl = "https://fake/yt-dlp.exe",
            SignatureUrl = "https://fake/yt-dlp.exe.sigstore",
            Sha256 = new string('0', 64), // intentionally wrong
            TargetRelativePath = "bin\\yt-dlp.exe",
            Version = "2026.05.14"
        };

        var applier = new UpdateApplier(http,
            new SigstoreVerifierOptions("x", ".*", ""), _paths);
        var result = await applier.ApplyAsync(new[] { entry }, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("校驗失敗", result.FailureReason!);
        Assert.False(File.Exists(Path.Combine(_paths.UpdateStaging, "yt-dlp.exe.new")));
    }

    [Fact]
    public async Task Apply_PathTraversal_Rejected()
    {
        var http = new StubHttp();
        var entry = new ManifestFileEntry
        {
            Name = "yt-dlp.exe",
            Component = UpdateComponent.YtDlp,
            DownloadUrl = "https://fake/yt-dlp.exe",
            SignatureUrl = "https://fake/yt-dlp.exe.sigstore",
            Sha256 = new string('0', 64),
            TargetRelativePath = "..\\..\\Windows\\evil.exe",
            Version = "2026.05.14"
        };

        var applier = new UpdateApplier(http,
            new SigstoreVerifierOptions("x", ".*", ""), _paths);
        var result = await applier.ApplyAsync(new[] { entry }, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("超出安裝目錄", result.FailureReason!);
    }

    [Fact]
    public async Task Apply_AbsolutePath_Rejected()
    {
        var http = new StubHttp();
        var entry = new ManifestFileEntry
        {
            Name = "yt-dlp.exe",
            Component = UpdateComponent.YtDlp,
            DownloadUrl = "https://fake/yt-dlp.exe",
            SignatureUrl = "https://fake/yt-dlp.exe.sigstore",
            Sha256 = new string('0', 64),
            TargetRelativePath = "C:\\Windows\\evil.exe",
            Version = "2026.05.14"
        };

        var applier = new UpdateApplier(http,
            new SigstoreVerifierOptions("x", ".*", ""), _paths);
        var result = await applier.ApplyAsync(new[] { entry }, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("超出安裝目錄", result.FailureReason!);
    }

    [Fact]
    public async Task Apply_NameWithSeparator_Rejected()
    {
        var http = new StubHttp();
        var entry = new ManifestFileEntry
        {
            Name = "evil/file.exe",
            Component = UpdateComponent.YtDlp,
            DownloadUrl = "https://fake/yt-dlp.exe",
            SignatureUrl = "https://fake/yt-dlp.exe.sigstore",
            Sha256 = new string('0', 64),
            TargetRelativePath = "bin\\yt-dlp.exe",
            Version = "2026.05.14"
        };

        var applier = new UpdateApplier(http,
            new SigstoreVerifierOptions("x", ".*", ""), _paths);
        var result = await applier.ApplyAsync(new[] { entry }, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("無效的元件名稱", result.FailureReason!);
    }

    [Fact]
    public async Task Apply_SigFailure_AbortsAfterHash()
    {
        var http = new StubHttp();
        var content = System.Text.Encoding.UTF8.GetBytes("totally legit");
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        http.Files["https://fake/yt-dlp.exe"] = content;
        http.Strings["https://fake/yt-dlp.exe.sigstore"] = "{invalid sigstore bundle}";

        var entry = new ManifestFileEntry
        {
            Name = "yt-dlp.exe",
            Component = UpdateComponent.YtDlp,
            DownloadUrl = "https://fake/yt-dlp.exe",
            SignatureUrl = "https://fake/yt-dlp.exe.sigstore",
            Sha256 = sha,
            TargetRelativePath = "bin\\yt-dlp.exe",
            Version = "2026.05.14"
        };

        var applier = new UpdateApplier(http,
            new SigstoreVerifierOptions("x", ".*", ""), _paths);
        var result = await applier.ApplyAsync(new[] { entry }, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("簽章", result.FailureReason!);
    }
}
