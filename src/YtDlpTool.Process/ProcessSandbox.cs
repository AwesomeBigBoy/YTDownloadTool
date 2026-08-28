using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace YtDlpTool.Process;

public static class ProcessSandbox
{
    private static readonly TimeSpan KillGrace = TimeSpan.FromMilliseconds(800);

    // Last 30 stdout lines kept in a ring buffer for diagnostics when stderr is empty —
    // yt-dlp emits [download]/[ExtractAudio]/[Merger]/[Metadata] progress on stdout and
    // a stuck job's only useful payload is the recent tail.
    private const int RecentStdoutCapacity = 30;

    public static async Task<ProcessExitInfo> RunAsync(
        ProcessStartArguments args,
        Action<ProcessStdoutLine>? onStdout = null,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo
        {
            FileName = args.ExecutablePath,
            WorkingDirectory = args.WorkingDirectory ?? Path.GetDirectoryName(args.ExecutablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Closed immediately after Start. Without redirection the child inherits the
            // parent's stdin handle, which for a GUI parent (no attached console) is invalid;
            // PyInstaller-frozen Python (yt-dlp) can then block on the first isatty probe.
            // Closing the redirected pipe gives the child a clean EOF on first read.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args.Arguments) info.ArgumentList.Add(a);

        ConfigureSandboxedEnvironment(info, args.ExecutablePath, args.ExtraEnv);

        using var process = new System.Diagnostics.Process { StartInfo = info, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        long stdoutBytes = 0, stderrBytes = 0;
        var stdoutLimitExceeded = false;
        var stderrLimitExceeded = false;
        var recentStdout = new ConcurrentQueue<string>();

        // Sentinel -1 = "no output received". Stamped from the first non-null line on
        // whichever stream fires first. Lets callers distinguish "stuck before any output"
        // from "stuck after some bytes" — see ProcessExitInfo.TimeToFirstOutputMs.
        long firstOutputMs = -1;
        var startStopwatch = Stopwatch.StartNew();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Interlocked.CompareExchange(ref firstOutputMs, startStopwatch.ElapsedMilliseconds, -1);
            var bytes = Encoding.UTF8.GetByteCount(e.Data);
            if (Interlocked.Add(ref stdoutBytes, bytes) > args.StdoutByteLimit)
            {
                stdoutLimitExceeded = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                return;
            }
            recentStdout.Enqueue(e.Data);
            while (recentStdout.Count > RecentStdoutCapacity)
                recentStdout.TryDequeue(out string? _);
            onStdout?.Invoke(new ProcessStdoutLine(e.Data, DateTime.UtcNow));
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Interlocked.CompareExchange(ref firstOutputMs, startStopwatch.ElapsedMilliseconds, -1);
            var bytes = Encoding.UTF8.GetByteCount(e.Data);
            if (Interlocked.Add(ref stderrBytes, bytes) > args.StderrByteLimit)
            {
                stderrLimitExceeded = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                return;
            }
            lock (stderr) stderr.AppendLine(e.Data);
        };

        // Process.Start can throw before the child even runs — most often when the
        // executable file is missing (AV quarantine) or AppLocker / WDAC blocks it.
        // Surface a friendly diagnostic via ProcessExitInfo.Stderr so the rest of the
        // failure pipeline (ErrorMapper, log lines) can act on it instead of an
        // opaque Win32Exception bubbling to the UI.
        // v1.3.6: the watchdog below only starts counting AFTER Process.Start returns,
        // so a slow CreateProcess (AV scanning an unsigned binary, a cold page-in from
        // a network share) is invisible in the log and shows up only as "the 30s
        // timeout actually took 38s". Measure it explicitly.
        long startMs;
        try
        {
            if (!process.Start())
                return new ProcessExitInfo(-1, "Process.Start returned false (no further detail)", false, false, false, false);
            startMs = startStopwatch.ElapsedMilliseconds;
        }
        catch (System.ComponentModel.Win32Exception wex)
        {
            var hint = wex.NativeErrorCode switch
            {
                2    => "找不到可執行檔（檔案可能被防毒隔離或缺失）",
                5    => "存取被拒（AppLocker / WDAC 可能阻擋此程式執行）",
                740  => "需要提升權限（Elevation required）",
                1260 => "AppLocker 群組原則拒絕執行此程式",
                _    => $"Win32 錯誤 {wex.NativeErrorCode}"
            };
            return new ProcessExitInfo(-1, $"Process.Start failed: {hint} — {wex.Message}", false, false, false, false);
        }
        catch (Exception ex)
        {
            return new ProcessExitInfo(-1, $"Process.Start failed: {ex.GetType().Name}: {ex.Message}", false, false, false, false);
        }

        // Close stdin immediately so the child sees EOF on first read.
        try { process.StandardInput.Close(); } catch { }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var cancelTask = WaitForCancellationAsync(process, cancellationToken);
        var timeoutTask = args.Timeout is { } t
            ? Task.Delay(t, CancellationToken.None)
            : Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);

        var winner = await Task.WhenAny(exitTask, cancelTask, timeoutTask).ConfigureAwait(false);

        bool timedOut = winner == timeoutTask && !exitTask.IsCompleted;
        bool cancelled = winner == cancelTask && !exitTask.IsCompleted;

        if (timedOut || cancelled)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await exitTask.ConfigureAwait(false);
        }

        return new ProcessExitInfo(
            ExitCode: process.HasExited ? process.ExitCode : -1,
            Stderr: stderr.ToString(),
            TimedOut: timedOut,
            Cancelled: cancelled,
            StdoutLimitExceeded: stdoutLimitExceeded,
            StderrLimitExceeded: stderrLimitExceeded,
            RecentStdout: string.Join('\n', recentStdout),
            TimeToFirstOutputMs: firstOutputMs == -1 ? null : firstOutputMs,
            StdoutBytes: Interlocked.Read(ref stdoutBytes),
            StderrBytes: Interlocked.Read(ref stderrBytes),
            Pid: SafePid(process),
            StartMs: startMs);
    }

    private static int SafePid(System.Diagnostics.Process p)
    {
        try { return p.Id; }
        catch { return 0; }
    }

    private static async Task WaitForCancellationAsync(System.Diagnostics.Process process, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) { await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None); return; }
        var tcs = new TaskCompletionSource();
        using var reg = ct.Register(() =>
        {
            try { process.CloseMainWindow(); } catch { }
            tcs.TrySetResult();
        });
        await tcs.Task.ConfigureAwait(false);
        try { await Task.Delay(KillGrace, CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    // Python-specific env vars we STRIP from the inherited environment. These are the
    // real hijack vectors for a frozen-Python child: PYTHONPATH / PYTHONHOME can point
    // the interpreter at attacker-controlled modules, PYTHONSTARTUP names a script to
    // execute. Everything else is inherited (see ConfigureSandboxedEnvironment).
    private static readonly string[] StrippedEnvVars =
    {
        "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP",
        "PYTHONEXECUTABLE", "PYTHONUSERBASE", "PYTHONCASEOK",
    };

    // v1.3.6: STOP clearing the child environment.
    //
    // History — read this before "tidying" it back into an allowlist:
    //   v1.1.16  ProcessSandbox shipped with EnvironmentVariables.Clear() + a rebuilt
    //            allowlist, out of paranoia over PATH hijack (spec §5.2).
    //   v1.1.18  91b2a62 removed the Clear(): the allowlist starved PyInstaller's
    //            Python init and yt-dlp.exe hung on cold start with ZERO output on
    //            both pipes — indistinguishable from a network timeout in the log.
    //   v1.1.27  a4c0668 ("revert(arch): drop TTY-mode complications, base on v1.1.16
    //            pipe-mode") put the Clear() back. That revert was justified by a
    //            *different* finding — a user's real root cause turned out to be
    //            broken IPv6 routing, fixed by --force-ipv4 — and the env work got
    //            swept up with the TTY work it was never part of.
    //   v1.3.6   Field log (2026-08, Intel HD 4600 / managed desktop) showed
    //            `yt-dlp.exe --version` — which performs NO network I/O at all —
    //            timing out at 30s with zero bytes under this sandbox, while the same
    //            command succeeded from cmd.exe, and ffmpeg.exe (native, few env
    //            dependencies) succeeded through this very same sandbox. IPv6 cannot
    //            explain a --version hang; the stripped environment can.
    //
    // The allowlist could never be right: it has to enumerate every var that Python,
    // PyInstaller's bootloader, and whatever EDR/AV DLL the machine injects into every
    // new process might need (SystemDrive, ProgramData, ALLUSERSPROFILE, COMSPEC,
    // PATHEXT, PROCESSOR_ARCHITECTURE, NUMBER_OF_PROCESSORS, …). We cannot know that
    // list for someone else's managed desktop. Inherit, then subtract the few vars
    // that are genuinely dangerous.
    private static void ConfigureSandboxedEnvironment(
        ProcessStartInfo info,
        string exePath,
        IReadOnlyDictionary<string, string>? extraEnv)
    {
        var binDir = Path.GetDirectoryName(exePath) ?? "";
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        // Accessing EnvironmentVariables pre-populates it with the parent's full
        // environment. We deliberately do NOT Clear() it.
        foreach (var name in StrippedEnvVars)
            info.EnvironmentVariables.Remove(name);

        // PATH is still rewritten — this is the actual hijack vector spec §5.2 cares
        // about, and overriding it costs the child nothing it needs.
        info.EnvironmentVariables["Path"] = $"{binDir};{systemDir};{Path.Combine(systemRoot, "System32")}";
        info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        info.EnvironmentVariables["PYTHONUTF8"] = "1";

        var tempDir = ResolveChildTempDirectory();
        info.EnvironmentVariables["TEMP"] = tempDir;
        info.EnvironmentVariables["TMP"] = tempDir;

        // Explicit injection from the caller wins over everything — used to force
        // SSL_CERT_FILE / REQUESTS_CA_BUNDLE values regardless of the parent's env.
        if (extraEnv is not null)
        {
            foreach (var kv in extraEnv)
            {
                if (!string.IsNullOrEmpty(kv.Value))
                    info.EnvironmentVariables[kv.Key] = kv.Value;
            }
        }
    }

    /// <summary>
    /// Temp directory handed to the child. Normally just the inherited one, but a
    /// PyInstaller onefile binary extracts its whole ~30 MB bundle here on EVERY run
    /// (fresh _MEIxxxxxx each time), so if %TEMP% lands on a network share — roaming
    /// profile, aggressive folder redirection, a mapped home drive — extraction blows
    /// straight past the 30s watchdog with nothing on either pipe. Fall back to
    /// LocalApplicationData, which folder-redirection GPO leaves alone by convention
    /// (only AppData\Roaming is normally redirected).
    /// </summary>
    internal static string ResolveChildTempDirectory()
    {
        string inherited;
        try { inherited = Path.GetTempPath(); }
        catch { return Path.GetTempPath(); }

        if (!IsNetworkPath(inherited)) return inherited;

        try
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YtDlpTool", "temp");
            if (!IsNetworkPath(local))
            {
                Directory.CreateDirectory(local);
                return local;
            }
        }
        catch { /* fall through — an unusable fallback is worse than the inherited path */ }

        return inherited;
    }

    /// <summary>
    /// True when <paramref name="path"/> lives on a UNC share or a mapped network
    /// drive. Extended-length prefixes (\\?\C:\…, \\.\…) are local despite the
    /// leading backslashes, so they fall through to the drive-type check.
    /// </summary>
    internal static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Strip the extended-length prefix first. Path.GetFullPath PRESERVES it, so
        // leaving it on makes every \\?\C:\… look like UNC to the leading-\\ test.
        // \\?\UNC\server\share is the extended spelling of \\server\share and IS remote.
        var bare = path;
        foreach (var prefix in new[] { @"\\?\", @"\\.\" })
        {
            if (!bare.StartsWith(prefix, StringComparison.Ordinal)) continue;
            bare = bare[prefix.Length..];
            if (bare.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase)) return true;
            break;
        }

        if (bare.StartsWith(@"\\", StringComparison.Ordinal)) return true;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(bare));
            if (string.IsNullOrEmpty(root)) return false;
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }
}
