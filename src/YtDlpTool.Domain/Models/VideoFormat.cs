namespace YtDlpTool.Domain.Models;

public sealed record VideoFormat(
    string FormatId,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    string Extension,
    long? FileSizeBytes,
    int? AudioBitrateKbps);
