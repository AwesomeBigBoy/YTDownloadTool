using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

public class ProcessSandboxTests
{
    private static readonly string CmdPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    [Fact]
    public async Task Run_SimpleEcho_ReceivesStdoutLine()
    {
        var lines = new List<string>();
        var args = new ProcessStartArguments(
            ExecutablePath: CmdPath,
            Arguments: new[] { "/c", "echo hello-world" });
        var result = await ProcessSandbox.RunAsync(args, l => lines.Add(l.Text));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines, l => l.Contains("hello-world"));
    }

    [Fact]
    public async Task Run_Cancellation_KillsProcess()
    {
        var args = new ProcessStartArguments(
            ExecutablePath: CmdPath,
            Arguments: new[] { "/c", "ping -n 30 127.0.0.1 > NUL" });
        using var cts = new CancellationTokenSource();
        var task = ProcessSandbox.RunAsync(args, cancellationToken: cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        var result = await task;
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task Run_Timeout_KillsProcess()
    {
        var args = new ProcessStartArguments(
            ExecutablePath: CmdPath,
            Arguments: new[] { "/c", "ping -n 10 127.0.0.1 > NUL" },
            Timeout: TimeSpan.FromMilliseconds(500));
        var result = await ProcessSandbox.RunAsync(args);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task Run_NonExistentExe_ReturnsFriendlyError()
    {
        // Fix B: AppLocker / WDAC / AV quarantine can make Process.Start throw a
        // Win32Exception. The sandbox should swallow that and surface the diagnostic
        // through ProcessExitInfo.Stderr so ErrorMapper can map it cleanly.
        var args = new ProcessStartArguments(
            ExecutablePath: @"C:\path\that\does\not\exist\nope.exe",
            Arguments: Array.Empty<string>());
        var result = await ProcessSandbox.RunAsync(args);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Contains("找不到可執行檔", result.Stderr);
    }

    [Fact]
    public async Task ProcessSandbox_LongStdoutHistory_RetainsLast30Lines()
    {
        // Fix B (v1.1.8): the sandbox now keeps a bounded ring buffer of the last
        // 30 stdout lines and surfaces it on ProcessExitInfo.RecentStdout, so that
        // download.failed logs still have a payload when stderr is empty (e.g. a
        // silent yt-dlp retry loop killed by the watchdog).
        var args = new ProcessStartArguments(
            ExecutablePath: CmdPath,
            // /v:on enables delayed expansion; %i becomes the loop variable inside
            // a single-line `for /L`. We emit 50 lines so the ring buffer must drop
            // lines 1..20 and keep only 21..50.
            Arguments: new[] { "/c", "for /L %i in (1,1,50) do @echo line%i" });
        var result = await ProcessSandbox.RunAsync(args);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.RecentStdout));

        var retained = result.RecentStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(30, retained.Length);
        // Line 20 (and anything before) must have been evicted.
        Assert.DoesNotContain("line20", retained);
        Assert.DoesNotContain("line1", retained);
        // Line 21 should be the new head, line 50 the tail.
        Assert.Equal("line21", retained[0]);
        Assert.Equal("line50", retained[^1]);
    }

    [Fact]
    public async Task Run_EnvironmentIsWhitelisted()
    {
        // Set a variable in our process that should NOT propagate to the child.
        Environment.SetEnvironmentVariable("YTDLP_TEST_SHOULD_NOT_LEAK", "1");
        try
        {
            var lines = new List<string>();
            var args = new ProcessStartArguments(
                ExecutablePath: CmdPath,
                Arguments: new[] { "/c", "set" });
            var result = await ProcessSandbox.RunAsync(args, l => lines.Add(l.Text));
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(lines, l => l.Contains("YTDLP_TEST_SHOULD_NOT_LEAK"));
            Assert.Contains(lines, l => l.StartsWith("SystemRoot=", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(lines, l => l.StartsWith("PYTHONUTF8=", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("YTDLP_TEST_SHOULD_NOT_LEAK", null);
        }
    }
}
