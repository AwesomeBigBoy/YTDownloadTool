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
    string RecentStdout = "",
    // v1.1.19: elapsed ms from Process.Start to the first byte of stdout/stderr.
    // null when the child wrote nothing before exit/timeout. Distinguishes
    // "stuck before any output" (null or huge) from "stuck after some output"
    // (small + non-zero) in bug reports — without it, a 30s timeout with
    // empty stdout/stderr is ambiguous between "Python startup hung" and
    // "network call hung after deps loaded".
    long? TimeToFirstOutputMs = null,
    // v1.1.19: total bytes the child wrote (uncapped count, used for diagnostics
    // even when StdoutByteLimit/StderrByteLimit fired and we killed the process).
    long StdoutBytes = 0,
    long StderrBytes = 0,
    // v1.1.19: PID of the child for log correlation. 0 when Process.Start failed.
    int Pid = 0);
