namespace YtDlpTool.Process;

public sealed class FfmpegRunner
{
    private readonly string _executable;

    public FfmpegRunner(string executable) => _executable = executable;

    public async Task<(bool IsHealthy, string? Version)> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var args = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: new[] { "-version" },
            Timeout: TimeSpan.FromSeconds(5));

        var firstLine = null as string;
        var exit = await ProcessSandbox.RunAsync(args,
            onStdout: l => firstLine ??= l.Text,
            cancellationToken: cancellationToken);

        if (exit.ExitCode != 0 || firstLine is null) return (false, null);
        return (true, firstLine);
    }
}
