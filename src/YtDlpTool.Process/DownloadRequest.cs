using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

public sealed record DownloadRequest(
    string Url,
    DownloadMode Mode,
    VideoFormat ChosenFormat,
    IReadOnlyList<string> SubtitleLanguageCodes,
    TimeRange? ClipRange,
    string SaveDirectory,
    string SanitizedFileStem,
    bool EmbedThumbnail = true,
    bool ForceOverwrite = false);
