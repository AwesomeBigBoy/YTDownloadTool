using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

public sealed record FfmpegCutResult(bool IsSuccess, string? ErrorMessage, bool WasCancelled);

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

    /// <summary>
    /// v1.1.12: cuts a previously-downloaded media file to a sub-range using
    /// stream copy (no re-encode). Used by the two-pass clip download path —
    /// yt-dlp produces the full media, ffmpeg trims to the requested time
    /// range. Cuts land on the nearest keyframe at or before the requested
    /// start, which is normally within 1-2 seconds for YouTube fragments;
    /// the trade-off vs. frame-accurate re-encode is huge: lossless, fast,
    /// and never silently produces an empty file when codecs disagree.
    /// </summary>
    public async Task<FfmpegCutResult> CutAsync(
        string inputPath,
        string outputPath,
        TimeRange range,
        DownloadMode mode,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var args = BuildCutArgs(inputPath, outputPath, range, mode).ToList();
        progress?.Report(0.0);
        var psa = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: args,
            Timeout: TimeSpan.FromMinutes(5));
        var exit = await ProcessSandbox.RunAsync(psa, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (exit.Cancelled) return new FfmpegCutResult(false, exit.Stderr, WasCancelled: true);
        if (exit.ExitCode != 0) return new FfmpegCutResult(false, exit.Stderr, WasCancelled: false);
        progress?.Report(100.0);
        return new FfmpegCutResult(true, null, WasCancelled: false);
    }

    /// <summary>
    /// Builds the ffmpeg argument list for a stream-copy cut. Extracted so it can
    /// be unit-tested without invoking a real ffmpeg binary.
    /// </summary>
    /// <remarks>
    /// Argument order rationale:
    ///   <c>-ss</c> BEFORE <c>-i</c> performs a fast keyframe-aligned seek that
    ///   does not require demuxing the entire file. <c>-to</c> after <c>-i</c>
    ///   then means "duration relative to the seek point" — so we pass the
    ///   duration (End - Start), not the absolute end timestamp.
    ///   <c>-c copy</c> stream-copies all tracks (works for mp4 video+audio and
    ///   audio-only m4a/mp3 alike). <c>-map_metadata 0</c> preserves the embedded
    ///   thumbnail and other container metadata; <c>+faststart</c> relocates the
    ///   moov atom to the front so the output starts playing without buffering.
    /// </remarks>
    internal static IEnumerable<string> BuildCutArgs(
        string inputPath,
        string outputPath,
        TimeRange range,
        DownloadMode mode)
    {
        _ = mode; // reserved for future per-mode tuning; ffmpeg auto-detects codecs
        var duration = range.End - range.Start;
        return new[]
        {
            "-y",                                          // overwrite output
            "-ss", FormatTime(range.Start),                // fast keyframe seek (before -i)
            "-i", inputPath,
            "-to", FormatTime(duration),                   // -to after -i = duration when -ss precedes -i
            "-c", "copy",                                  // no re-encode
            "-map_metadata", "0",                          // preserve metadata (embedded thumbnail, etc.)
            "-movflags", "+faststart",                     // moov atom at front for streaming-friendly mp4
            outputPath,
        };
    }

    private static string FormatTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
}
