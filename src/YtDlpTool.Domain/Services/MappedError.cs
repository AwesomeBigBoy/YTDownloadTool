namespace YtDlpTool.Domain.Services;

public sealed record MappedError(
    ErrorCategory Category,
    string UserMessage,
    string ErrorCode,
    bool CanRetry,
    // Truncated raw stderr (≤ 500 chars, newlines collapsed) so AppLogger can record
    // the actual yt-dlp failure detail. Stays out of the user-facing UserMessage to
    // avoid leaking English/technical text into the toast.
    string? RawDetails = null);
