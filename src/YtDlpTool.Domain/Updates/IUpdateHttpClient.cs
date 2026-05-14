namespace YtDlpTool.Domain.Updates;

public interface IUpdateHttpClient
{
    Task<GitHubReleaseDto?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct);
    Task<string> GetStringAsync(string url, CancellationToken ct);
    Task DownloadAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct);
}
