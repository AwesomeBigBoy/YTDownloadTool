namespace YtDlpTool.Domain.Updates;

public sealed class ManifestFileEntry
{
    public string Name { get; set; } = "";
    public UpdateComponent Component { get; set; }
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string SignatureUrl { get; set; } = "";
    public string TargetRelativePath { get; set; } = "";
}
