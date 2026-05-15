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
    IReadOnlyDictionary<string, string>? ExtraEnv = null,
    // v1.1.22: when true, ProcessSandbox spawns the executable indirectly via
    // %SystemRoot%\System32\cmd.exe /c. Use case: endpoint security software (e.g. endpoint security software
    // Behavior Monitoring) may flag the combination of "unsigned GUI parent
    // spawning a PyInstaller-frozen child that self-extracts to TEMP" as
    // malware-like, and suspend the child long enough that our 30s watchdog
    // kills it before it ever runs. Funnelling the spawn through cmd.exe
    // (which IS Microsoft-signed and trusted by most endpoint security software) makes
    // yt-dlp's immediate parent a trusted process, which on some AV engines
    // is enough to skip behavior heuristics.
    bool WrapInCmdShell = false,
    // v1.1.23: when true, ProcessSandbox does NOT redirect stdout / stderr / stdin
    // and forces CreateNoWindow=false. The child gets a real console window with
    // genuine TTY stdio handles. This is the fix for the managed environment
    // failure mode where endpoint security software Web Reputation drops the network
    // payload of any process whose stdout is a pipe (the typical "headless
    // malware" pattern) but allows the same binary when its stdout is a real
    // console. Cost: visible console flash for ~1-2 s per spawn; we lose the
    // pipe-based output capture, so callers must arrange for yt-dlp to write
    // its results to a file via --write-info-json / --output instead of stdout.
    bool NoIoRedirection = false);
