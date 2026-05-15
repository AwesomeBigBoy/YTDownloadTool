namespace YtDlpTool.Domain.Updates;

public interface IUpdateHttpClient
{
    Task<GitHubReleaseDto?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct);

    /// <summary>
    /// Fallback used when GetLatestReleaseAsync returns null or 404 — fetches up to
    /// <paramref name="limit"/> recent releases so the caller can pick the most recent
    /// non-draft/non-prerelease release whose assets actually contain a manifest. This
    /// makes the updater survive repos that have no release explicitly marked latest.
    /// </summary>
    Task<IReadOnlyList<GitHubReleaseDto>> GetRecentReleasesAsync(string owner, string repo, int limit, CancellationToken ct);

    Task<string> GetStringAsync(string url, CancellationToken ct);
    Task DownloadAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct);
}
