namespace YtDlpTool.Process;

public sealed record ProcessExitInfo(
    int ExitCode,
    string Stderr,
    bool TimedOut,
    bool Cancelled,
    bool StdoutLimitExceeded,
    bool StderrLimitExceeded);
