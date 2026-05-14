using System.Text.Json.Serialization;

namespace YtDlpTool.Process;

public sealed class YtDlpMetadataDto
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Uploader { get; set; }
    public double? Duration { get; set; }
    public string? Thumbnail { get; set; }
    public YtDlpFormatDto[]? Formats { get; set; }
    public Dictionary<string, YtDlpSubtitleDto[]>? Subtitles { get; set; }
    [JsonPropertyName("automatic_captions")]
    public Dictionary<string, YtDlpSubtitleDto[]>? AutomaticCaptions { get; set; }
}

public sealed class YtDlpFormatDto
{
    [JsonPropertyName("format_id")] public string? FormatId { get; set; }
    public string? Ext { get; set; }
    public string? Vcodec { get; set; }
    public string? Acodec { get; set; }
    public int? Height { get; set; }
    public long? Filesize { get; set; }
    [JsonPropertyName("filesize_approx")] public long? FilesizeApprox { get; set; }
    public double? Abr { get; set; }
}

public sealed class YtDlpSubtitleDto
{
    public string? Ext { get; set; }
    public string? Url { get; set; }
}
