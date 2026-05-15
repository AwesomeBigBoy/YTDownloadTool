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
