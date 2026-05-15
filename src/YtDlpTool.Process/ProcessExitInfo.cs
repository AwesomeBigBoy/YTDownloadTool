namespace YtDlpTool.Process;

public sealed record ProcessExitInfo(
    int ExitCode,
    string Stderr,
    bool TimedOut,
    bool Cancelled,
    bool StdoutLimitExceeded,
    bool StderrLimitExceeded,
    // Fix B (v1.1.8): last 30 stdout lines, joined with '\n'. yt-dlp emits its
    // [download]/[ExtractAudio]/[Merger]/[Metadata] progress on stdout, so a stuck
    // or failing job that produced empty stderr still carries useful diagnostics
    // here. Empty string when the child wrote nothing to stdout.
    string RecentStdout = "");
