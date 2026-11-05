namespace YtDlpTool.Domain.Models;

public sealed record VideoFormat(
    string FormatId,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    string Extension,
    long? FileSizeBytes,
    int? AudioBitrateKbps,
    // v1.5.0: optional so every existing positional construction keeps compiling.
    // QualityOptions labels a vertical video (a 1080x1920 Short) by its shorter side.
    int? Width = null,
    // v1.5.0: yt-dlp's dynamic_range — "SDR", "HDR10", "HLG"…; null when not reported.
    string? DynamicRange = null);
