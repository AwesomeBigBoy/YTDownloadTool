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
            Pid: SafePid(process));
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

    // Env vars we DELIBERATELY pass through from the parent process. The strip was
    // initially "out of paranoia" (defend PATH hijack per spec §5.2) but was too
    // aggressive: managed environments set HTTP_PROXY/HTTPS_PROXY via GPO and
    // yt-dlp/Python only sees the proxy via these env vars. The whitelist below
    // covers the env vars yt-dlp / PyInstaller actually need. PATH itself stays
    // sandboxed (rewritten to <bin>;<system32>) — we still don't inherit the
    // user's PATH because that's the actual hijack vector.
    private static readonly string[] PassThroughEnvVars =
    {
        // Proxy configuration — primary reason this list exists.
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
        "http_proxy", "https_proxy", "no_proxy",
        "ALL_PROXY", "all_proxy",

        // SSL / CA bundle — managed networks with SSL inspection point Python urllib
        // at an site-installed CA bundle via these vars.
        "SSL_CERT_FILE", "SSL_CERT_DIR",
        "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE",

        // User-profile paths — yt-dlp's cookie handling and some config paths read these.
        "USERPROFILE", "USERNAME", "USERDOMAIN",
        "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH",
    };

    private static void ConfigureSandboxedEnvironment(
        ProcessStartInfo info,
        string exePath,
        IReadOnlyDictionary<string, string>? extraEnv)
    {
        info.EnvironmentVariables.Clear();
        var binDir = Path.GetDirectoryName(exePath) ?? "";
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var tempDir = Path.GetTempPath();
        info.EnvironmentVariables["SystemRoot"] = systemRoot;
        info.EnvironmentVariables["Temp"] = tempDir;
        info.EnvironmentVariables["TMP"] = tempDir;
        info.EnvironmentVariables["Path"] = $"{binDir};{systemDir};{Path.Combine(systemRoot, "System32")}";
        info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        info.EnvironmentVariables["PYTHONUTF8"] = "1";

        foreach (var name in PassThroughEnvVars)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                info.EnvironmentVariables[name] = value;
        }

        // Explicit injection from the caller wins over the whitelist — used to force
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
}
