namespace YtDlpTool.Domain.Services;

public sealed record MappedError(
    ErrorCategory Category,
    string UserMessage,
    string ErrorCode,
    bool CanRetry);
