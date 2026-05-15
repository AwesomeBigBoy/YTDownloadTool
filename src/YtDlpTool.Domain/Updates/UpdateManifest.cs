using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Updates;

// build-manifest.ps1 emits camelCase keys via PowerShell ConvertTo-Json. Our shared
// AppJsonContext uses default PascalCase property policy, so the manifest's Files
// array was deserialising as empty — causing the "empty-files" status across every
// release in update.check.evaluate. Explicit JsonPropertyName attributes lock the
// JSON shape to camelCase regardless of the context's global policy.
public sealed class UpdateManifest
{
    [JsonPropertyName("manifestVersion")] public string ManifestVersion { get; set; } = "1";
    [JsonPropertyName("publishedAt")]     public DateTimeOffset PublishedAt { get; set; }
    [JsonPropertyName("appVersion")]      public string AppVersion { get; set; } = "";
    [JsonPropertyName("ytDlpVersion")]    public string YtDlpVersion { get; set; } = "";
    [JsonPropertyName("ffmpegVersion")]   public string FfmpegVersion { get; set; } = "";
    [JsonPropertyName("files")]           public List<ManifestFileEntry> Files { get; set; } = new();
}
