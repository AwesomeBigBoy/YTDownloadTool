namespace YtDlpTool.Domain.Models;

public sealed record VideoMetadata(
    string VideoId,
    string Title,
    string Channel,
    TimeSpan Duration,
    string ThumbnailUrl,
    IReadOnlyList<VideoFormat> Formats,
    IReadOnlyList<SubtitleTrack> Subtitles);
