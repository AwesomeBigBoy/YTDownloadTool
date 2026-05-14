namespace YtDlpTool.Domain.Updates;

public sealed record UpdateApplyResult(bool IsSuccess, string? FailureReason);
