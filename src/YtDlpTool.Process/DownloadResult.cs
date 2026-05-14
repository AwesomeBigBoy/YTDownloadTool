namespace YtDlpTool.Process;

public sealed record DownloadResult(
    bool IsSuccess,
    string? OutputFilePath,
    string? ErrorStderr,
    bool WasCancelled);
