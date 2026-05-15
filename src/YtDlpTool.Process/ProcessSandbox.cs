using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace YtDlpTool.Process;

public static class ProcessSandbox
{
    private static readonly TimeSpan KillGrace = TimeSpan.FromMilliseconds(800);

    // Fix B (v1.1.8): when a child hangs or fails with empty stderr the only
    // diagnostic available is what it wrote to stdout. yt-dlp emits its
    // [download]/[ExtractAudio]/[Merger]/[Metadata] lines there. Keeping the
    // last 30 lines in a bounded ring buffer gives enough context for the field
    // bug reports without unbounded memory growth.
    private const int RecentStdoutCapacity = 30;

    public static async Task<ProcessExitInfo> RunAsync(
        ProcessStartArguments args,
        Action<ProcessStdoutLine>? onStdout = null,
        CancellationToken cancellationToken = default)
    {
        // v1.1.22: optional cmd.exe wrapper. See ProcessStartArguments.WrapInCmdShell
        // for the rationale (endpoint software trust). Cmd.exe parses arguments through
        // its own rules; ArgumentList passes each token as a separate argv entry
        // to cmd, which then reconstructs the command line for the real exe.
        string fileName = args.ExecutablePath;
        IEnumerable<string> argSource = args.Arguments;
        if (args.WrapInCmdShell)
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            fileName = Path.Combine(systemRoot, "System32", "cmd.exe");
            // Prepend "/c" + the original exe path. Cmd will treat the rest as
            // arguments to that exe.
            argSource = new[] { "/c", args.ExecutablePath }.Concat(args.Arguments);
        }

        // v1.1.23: TTY mode is the actual fix for the AD-environment hang. Real
        // root cause was endpoint security software's Web Reputation: it drops the
        // application-layer payload for any process whose stdout is a redirected
        // pipe (heuristic: "headless malware-like"), while allowing the same
        // binary when stdout is a real console. We support both modes:
        //
        //   args.NoIoRedirection == false (default, used for ffmpeg etc.):
        //     redirect everything, capture output via pipes. The historical path.
        //
        //   args.NoIoRedirection == true (used for yt-dlp on AD hosts):
        //     don't redirect anything, force CreateNoWindow=false so the child
        //     gets a real console with TTY stdio. Output goes to a visible
        //     console window for ~1-2s. Callers must arrange for the binary to
        //     write its results to a file (--write-info-json / --output) — pipe
        //     capture is no longer available.
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = args.WorkingDirectory ?? Path.GetDirectoryName(args.ExecutablePath),
            UseShellExecute = false,
        };
        if (args.NoIoRedirection)
        {
            info.CreateNoWindow        = false;
            info.RedirectStandardOutput = false;
            info.RedirectStandardError  = false;
            info.RedirectStandardInput  = false;
        }
        else
        {
            // Pre-v1.1.23 path. Kept for ffmpeg invocations where pipe capture
            // is still useful (ffmpeg doesn't trigger endpoint security software's heuristic).
            info.CreateNoWindow        = false; // v1.1.21
            info.RedirectStandardOutput = true;
            info.RedirectStandardError  = true;
            info.RedirectStandardInput  = true; // v1.1.18: close immediately to avoid isatty hang
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding  = Encoding.UTF8;
        }

        foreach (var a in argSource) info.ArgumentList.Add(a);

        ConfigureSandboxedEnvironment(info, args.ExecutablePath, args.ExtraEnv);

        using var process = new System.Diagnostics.Process { StartInfo = info, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        long stdoutBytes = 0, stderrBytes = 0;
        var stdoutLimitExceeded = false;
        var stderrLimitExceeded = false;
        // Bounded ring of recent stdout lines for ProcessExitInfo.RecentStdout.
        // ConcurrentQueue + manual TryDequeue keeps Count <= RecentStdoutCapacity
        // under concurrent OutputDataReceived callbacks without locking.
        var recentStdout = new ConcurrentQueue<string>();

        // v1.1.19: track time-to-first-output so the caller can tell "stuck before
        // any output" from "stuck after Python started talking". Stamped from the
        // first non-null stdout OR stderr line, whichever lands first. Sentinel
        // -1 means "no output received yet" — converted to null at return time.
        long firstOutputMs = -1;
        var startStopwatch = System.Diagnostics.Stopwatch.StartNew();

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
        // We surface a friendly diagnostic via ProcessExitInfo.Stderr so the rest of
        // the failure pipeline (ErrorMapper, log lines) can act on it instead of an
        // opaque Win32Exception that just bubbles to the UI.
        try
        {
            if (!process.Start())
                return new ProcessExitInfo(-1, "Process.Start returned false (no further detail)", false, false, false, false);
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

        if (!args.NoIoRedirection)
        {
            // v1.1.18: close child's stdin immediately so it gets EOF on first read.
            // Pairs with RedirectStandardInput=true above — see comment there.
            try { process.StandardInput.Close(); } catch { }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        // else: NoIoRedirection mode — no pipes to manage. yt-dlp writes its
        // output to a file the caller specified (--write-info-json / --output).

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
            Pid: SafePid(process));
    }

    private static int SafePid(System.Diagnostics.Process p)
    {
        // Process.Id throws if the process has exited and the OS has reaped it.
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

    private static void ConfigureSandboxedEnvironment(
        ProcessStartInfo info,
        string exePath,
        IReadOnlyDictionary<string, string>? extraEnv)
    {
        // v1.1.18: do NOT clear the child's environment.
        //
        // Prior versions cleared it and rebuilt from a small whitelist
        // (proxy, SSL, USERPROFILE, APPDATA, ...). The intent was paranoia
        // hardening per spec §5.2, but the production failure mode in
        // managed environments turned out to be exactly this: yt-dlp's
        // PyInstaller-frozen Python depends on environment vars we did not
        // anticipate (likely PROCESSOR_*, COMSPEC, SESSIONNAME or similar),
        // and missing them made yt-dlp hang during startup before producing
        // any stdout/stderr. The same bare yt-dlp.exe invocation from a
        // plain CMD — inheriting full user env — returned JSON immediately.
        //
        // We keep two security-relevant overrides:
        //   1. PATH is replaced with bin + System32, since user-PATH hijack
        //      is the actual attack vector we care about (spec §5.2).
        //   2. PYTHON encoding hints make yt-dlp output deterministic UTF-8
        //      regardless of the user's locale.
        // ExtraEnv (e.g., SSL_CERT_FILE injection from YtDlpRunner) is then
        // applied on top so callers can still force specific values.

        var binDir = Path.GetDirectoryName(exePath) ?? "";
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        info.EnvironmentVariables["Path"] = $"{binDir};{systemDir};{Path.Combine(systemRoot, "System32")}";
        info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        info.EnvironmentVariables["PYTHONUTF8"] = "1";

        if (extraEnv is not null)
        {
            foreach (var kv in extraEnv)
            {
                if (!string.IsNullOrEmpty(kv.Value))
                    info.EnvironmentVariables[kv.Key] = kv.Value;
            }
        }
    }
}
