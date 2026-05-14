namespace YtDlpTool.Domain.Services;

public sealed record DownloadExecutionResult(
    bool IsSuccess,
    string? OutputFilePath,
    MappedError? Error,
    bool WasCancelled);
