namespace YtDlpTool.Domain.Updates;

public sealed class UpdateManifest
{
    public string ManifestVersion { get; set; } = "1";
    public DateTimeOffset PublishedAt { get; set; }
    public string AppVersion { get; set; } = "";
    public string YtDlpVersion { get; set; } = "";
    public string FfmpegVersion { get; set; } = "";
    public List<ManifestFileEntry> Files { get; set; } = new();
}
