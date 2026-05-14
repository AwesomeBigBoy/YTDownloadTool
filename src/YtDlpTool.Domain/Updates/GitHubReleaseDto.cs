using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Updates;

public sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAssetDto>? Assets { get; set; }
}

public sealed class GitHubAssetDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
