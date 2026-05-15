namespace YtDlpTool.Process;

/// <summary>
/// v1.1.13: result of a standalone <c>yt-dlp --skip-download --write-subs</c>
/// invocation. <see cref="IsSuccess"/> reflects yt-dlp's exit code; the
/// <see cref="SubtitleFilePaths"/> list is what the file system actually
/// contains after the call (yt-dlp may serve fewer languages than requested).
/// On failure <see cref="ErrorMessage"/> carries the combined diagnostic blob
/// in the same shape as <see cref="DownloadResult.ErrorStderr"/>.
/// </summary>
public sealed record SubtitleDownloadResult(
    bool IsSuccess,
    IReadOnlyList<string> SubtitleFilePaths,
    string? ErrorMessage);
