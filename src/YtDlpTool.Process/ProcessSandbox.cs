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
        var info = new ProcessStartInfo
        {
            FileName = args.ExecutablePath,
            WorkingDirectory = args.WorkingDirectory ?? Path.GetDirectoryName(args.ExecutablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args.Arguments) info.ArgumentList.Add(a);

        ConfigureSandboxedEnvironment(info, args.ExecutablePath);

        using var process = new System.Diagnostics.Process { StartInfo = info, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        long stdoutBytes = 0, stderrBytes = 0;
        var stdoutLimitExceeded = false;
        var stderrLimitExceeded = false;
        // Bounded ring of recent stdout lines for ProcessExitInfo.RecentStdout.
        // ConcurrentQueue + manual TryDequeue keeps Count <= RecentStdoutCapacity
        // under concurrent OutputDataReceived callbacks without locking.
        var recentStdout = new ConcurrentQueue<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
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
            RecentStdout: string.Join('\n', recentStdout));
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

    // Env vars we DELIBERATELY pass through from the parent process. Stripped initially
    // out of paranoia (防 PATH 劫持 per spec §5.2) but the strip was too aggressive:
    // managed environments set HTTP_PROXY/HTTPS_PROXY via GPO and yt-dlp/Python only
    // sees the proxy via these env vars. Bare yt-dlp CLI on those machines works because
    // it inherits the full environment; we were blocking ourselves.
    //
    // PATH itself stays sandboxed (rewritten to <bin>;<system32>) — we still don't
    // inherit the user's PATH because that's the actual hijack vector.
    private static readonly string[] PassThroughEnvVars =
    {
        // Proxy configuration — the main reason this list exists.
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

    private static void ConfigureSandboxedEnvironment(ProcessStartInfo info, string exePath)
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
        info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8"; // yt-dlp respects this
        info.EnvironmentVariables["PYTHONUTF8"] = "1";

        foreach (var name in PassThroughEnvVars)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                info.EnvironmentVariables[name] = value;
        }
    }
}
