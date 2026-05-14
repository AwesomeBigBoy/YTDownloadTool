using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Persistence;

public sealed class JobSnapshot
{
    public Guid Id { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public DownloadMode Mode { get; set; }
    public string FormatId { get; set; } = "";
    public int? FormatHeight { get; set; }
    public string FormatExt { get; set; } = "";
    public List<string> SubtitleLanguageCodes { get; set; } = new();
    public string? ClipStart { get; set; }
    public string? ClipEnd { get; set; }
    public string SaveDirectory { get; set; } = "";
}
