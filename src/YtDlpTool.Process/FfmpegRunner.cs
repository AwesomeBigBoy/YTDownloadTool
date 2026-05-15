using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

public sealed record FfmpegCutResult(bool IsSuccess, string? ErrorMessage, bool WasCancelled);

public sealed record FfmpegMuxResult(bool IsSuccess, string? ErrorMessage);

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
    /// v1.1.13: cuts a previously-downloaded subtitle file (.vtt/.srt) to the
    /// same time range used for the media cut. Stream-copy is fine for text
    /// subtitles too — ffmpeg just rewrites timestamps relative to the new
    /// origin. Keeps the executor's clip+subtitle path symmetric with the
    /// media cut.
    /// </summary>
    public async Task<FfmpegCutResult> CutSubtitleAsync(
        string inputVttPath,
        string outputVttPath,
        TimeRange range,
        CancellationToken cancellationToken = default)
    {
        var args = BuildSubtitleCutArgs(inputVttPath, outputVttPath, range).ToList();
        var psa = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: args,
            Timeout: TimeSpan.FromMinutes(2));
        var exit = await ProcessSandbox.RunAsync(psa, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (exit.Cancelled) return new FfmpegCutResult(false, exit.Stderr, WasCancelled: true);
        if (exit.ExitCode != 0) return new FfmpegCutResult(false, exit.Stderr, WasCancelled: false);
        return new FfmpegCutResult(true, null, WasCancelled: false);
    }

    /// <summary>
    /// v1.1.13: muxes one or more subtitle sidecar files into a media container
    /// as soft subtitle tracks (mov_text inside mp4). Each subtitle's language
    /// tag is derived from the filename suffix (e.g. "stem.zh-Hant.vtt" →
    /// language=zh-Hant). Stream-copy for audio+video means no re-encode of
    /// the media; only the subtitle tracks are transcoded to mov_text so they
    /// survive inside an mp4 container.
    /// </summary>
    public async Task<FfmpegMuxResult> MuxSubtitlesAsync(
        string mediaPath,
        IReadOnlyList<string> subtitlePaths,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var args = BuildMuxArgs(mediaPath, subtitlePaths, outputPath).ToList();
        var psa = new ProcessStartArguments(
            ExecutablePath: _executable,
            Arguments: args,
            Timeout: TimeSpan.FromMinutes(5));
        var exit = await ProcessSandbox.RunAsync(psa, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (exit.ExitCode != 0) return new FfmpegMuxResult(false, exit.Stderr);
        return new FfmpegMuxResult(true, null);
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

    /// <summary>
    /// Builds the ffmpeg argument list for cutting a subtitle file (.vtt/.srt)
    /// to a sub-range. Extracted for unit-testing.
    /// </summary>
    internal static IEnumerable<string> BuildSubtitleCutArgs(
        string inputVttPath,
        string outputVttPath,
        TimeRange range)
    {
        var duration = range.End - range.Start;
        return new[]
        {
            "-y",
            "-ss", FormatTime(range.Start),
            "-i", inputVttPath,
            "-to", FormatTime(duration),
            "-c", "copy",
            outputVttPath,
        };
    }

    /// <summary>
    /// Builds the ffmpeg argument list for muxing N subtitle tracks into a media
    /// container. Extracted for unit-testing without invoking ffmpeg.
    /// </summary>
    /// <remarks>
    /// Layout:
    ///   -i media -i sub0 -i sub1 ... -c copy -c:s mov_text -map 0 -map 1:0 -map 2:0 ...
    ///   plus per-track <c>-metadata:s:s:N language=&lt;lang&gt;</c> tags derived
    ///   from each subtitle's filename suffix.
    /// Subtitles are transcoded to <c>mov_text</c> so they survive inside an mp4
    /// container (WebVTT itself is not valid as an mp4 stream). Audio + video
    /// remain stream-copied — no media re-encode.
    /// </remarks>
    internal static IEnumerable<string> BuildMuxArgs(
        string mediaPath,
        IReadOnlyList<string> subtitlePaths,
        string outputPath)
    {
        var args = new List<string> { "-y", "-i", mediaPath };
        foreach (var sub in subtitlePaths)
        {
            args.Add("-i");
            args.Add(sub);
        }

        args.Add("-c");
        args.Add("copy");
        args.Add("-c:s");
        args.Add("mov_text");
        args.Add("-map");
        args.Add("0");
        for (var i = 0; i < subtitlePaths.Count; i++)
        {
            args.Add("-map");
            args.Add($"{i + 1}:0");
        }

        for (var i = 0; i < subtitlePaths.Count; i++)
        {
            var lang = ExtractLangFromFilename(Path.GetFileName(subtitlePaths[i]));
            if (!string.IsNullOrEmpty(lang))
            {
                args.Add($"-metadata:s:s:{i}");
                args.Add($"language={lang}");
            }
        }

        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// Extracts a language code from a subtitle sidecar filename. yt-dlp writes
    /// files as <c>&lt;stem&gt;.&lt;lang&gt;.vtt</c> (e.g. <c>video.zh-Hant.vtt</c>),
    /// so the language is the segment between the final two dots. Returns an
    /// empty string when the filename does not match the expected pattern —
    /// callers omit the language metadata tag in that case rather than emitting
    /// a bogus value.
    /// </summary>
    public static string ExtractLangFromFilename(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        if (!fileName.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            return "";
        var withoutExt = Path.GetFileNameWithoutExtension(fileName); // "<stem>.<lang>"
        var dotIdx = withoutExt.LastIndexOf('.');
        if (dotIdx < 0) return "";
        return withoutExt[(dotIdx + 1)..];
    }

    private static string FormatTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
}
