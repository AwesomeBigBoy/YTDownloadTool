namespace YtDlpTool.Domain.Services;

public enum ErrorCategory
{
    YouTubeRefused,
    RateLimited,
    NetworkError,
    VideoUnavailable,
    PremiereUpcoming,
    DiskFull,
    FileConflict,
    ComponentMissing,
    UnknownError
}
