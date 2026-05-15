namespace YtDlpTool.Process;

public sealed record ProcessStartArguments(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    long StdoutByteLimit = 10 * 1024 * 1024,
    long StderrByteLimit = 1 * 1024 * 1024,
    // Explicit env vars to inject into the child. Applied AFTER ProcessSandbox's
    // env whitelist so callers can force a specific value (e.g., the SSL_CERT_FILE
    // path that lets yt-dlp's Python trust site-installed CAs installed via GPO).
    IReadOnlyDictionary<string, string>? ExtraEnv = null);
