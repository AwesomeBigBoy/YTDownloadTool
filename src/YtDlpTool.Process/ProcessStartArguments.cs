namespace YtDlpTool.Process;

public sealed record ProcessStartArguments(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    long StdoutByteLimit = 10 * 1024 * 1024,
    long StderrByteLimit = 1 * 1024 * 1024,
    // Explicit env vars to inject into the child. Applied AFTER the parent
    // environment is inherited and after PATH / PYTHON encoding overrides in
    // ProcessSandbox, so callers can force a specific value regardless of
    // what the parent process's env contains.
    IReadOnlyDictionary<string, string>? ExtraEnv = null);
