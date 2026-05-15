namespace YtDlpTool.Process;

public sealed record ProcessStartArguments(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    long StdoutByteLimit = 10 * 1024 * 1024,
    long StderrByteLimit = 1 * 1024 * 1024,
    // v1.1.17: explicit env vars to inject into the child. Applied AFTER the
    // PassThroughEnvVars whitelist in ProcessSandbox, so callers can force a
    // specific value regardless of what the parent process's env contains.
    // The earlier reliance on Environment.SetEnvironmentVariable round-trip
    // turned out to be unreliable on managed Windows hosts: even after
    // SetEnvironmentVariable returned, the value was not visible to yt-dlp
    // children launched via ProcessStartInfo. Explicit injection bypasses
    // the global-env hop entirely.
    IReadOnlyDictionary<string, string>? ExtraEnv = null);
