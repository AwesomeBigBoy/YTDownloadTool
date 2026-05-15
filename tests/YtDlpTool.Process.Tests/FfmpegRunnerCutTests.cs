using YtDlpTool.Domain.Models;
using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

/// <summary>
/// Unit tests for <see cref="FfmpegRunner"/>'s argument-building. The actual ffmpeg
/// process is not invoked here — we deliberately keep these tests free of any
/// dependency on a real ffmpeg.exe binary so the suite runs cleanly in CI and on
/// developer machines that may not have ffmpeg on PATH. A real-binary smoke test
/// would belong in a separate integration suite.
/// </summary>
public class FfmpegRunnerCutTests
{
    private static TimeRange Range(string start, string end) =>
        new(TimeSpan.Parse(start), TimeSpan.Parse(end));

    [Fact]
    public void BuildCutArgs_SeekBeforeInput_FastKeyframeSeek()
    {
        // -ss MUST come before -i so ffmpeg can do a fast keyframe-aligned seek
        // instead of demuxing the whole file. Verifying ordering directly.
        var args = FfmpegRunner.BuildCutArgs(
            "in.mp4", "out.mp4",
            Range("00:01:10", "00:01:20"),
            DownloadMode.AudioAndVideo).ToList();

        var ssIndex = args.IndexOf("-ss");
        var iIndex = args.IndexOf("-i");
        Assert.True(ssIndex >= 0, "-ss missing");
        Assert.True(iIndex >= 0, "-i missing");
        Assert.True(ssIndex < iIndex, "-ss must precede -i for fast seek");
    }

    [Fact]
    public void BuildCutArgs_SeekValue_MatchesRangeStart()
    {
        var args = FfmpegRunner.BuildCutArgs(
            "in.mp4", "out.mp4",
            Range("00:01:10", "00:01:20"),
            DownloadMode.AudioAndVideo).ToList();

        var ssIndex = args.IndexOf("-ss");
        Assert.Equal("00:01:10", args[ssIndex + 1]);
    }

    [Fact]
    public void BuildCutArgs_DurationFlag_EmitsRelativeDurationNotEndTimestamp()
    {
        // Because -ss precedes -i, the -to argument is interpreted as duration
        // from the seek point, not as an absolute end timestamp. Pass duration.
        var args = FfmpegRunner.BuildCutArgs(
            "in.mp4", "out.mp4",
            Range("00:01:10", "00:01:30"),
            DownloadMode.AudioAndVideo).ToList();

        var toIndex = args.IndexOf("-to");
        Assert.True(toIndex >= 0, "-to missing");
        Assert.Equal("00:00:20", args[toIndex + 1]);
    }

    [Fact]
    public void BuildCutArgs_StreamCopy_NoReencode()
    {
        var args = FfmpegRunner.BuildCutArgs(
            "in.mp4", "out.mp4",
            Range("00:00:00", "00:00:05"),
            DownloadMode.AudioAndVideo).ToList();

        var cIndex = args.IndexOf("-c");
        Assert.True(cIndex >= 0, "-c missing");
        Assert.Equal("copy", args[cIndex + 1]);
    }

    [Fact]
    public void BuildCutArgs_OverwritesOutput_PassesY()
    {
        var args = FfmpegRunner.BuildCutArgs(
            "in.mp4", "out.mp4",
            Range("00:00:00", "00:00:05"),
            DownloadMode.AudioAndVideo).ToList();
        Assert.Contains("-y", args);
    }

    [Fact]
    public void BuildCutArgs_PreservesMetadataAndFaststart()
    {
        var args = FfmpegRunner.BuildCutArgs(
            "in.mp4", "out.mp4",
            Range("00:00:00", "00:00:05"),
            DownloadMode.AudioAndVideo).ToList();

        var mapIndex = args.IndexOf("-map_metadata");
        Assert.True(mapIndex >= 0, "-map_metadata missing");
        Assert.Equal("0", args[mapIndex + 1]);

        var moovIndex = args.IndexOf("-movflags");
        Assert.True(moovIndex >= 0, "-movflags missing");
        Assert.Equal("+faststart", args[moovIndex + 1]);
    }

    [Fact]
    public void BuildCutArgs_InputAndOutput_BothPresent()
    {
        var args = FfmpegRunner.BuildCutArgs(
            "C:/temp/input.mp4", "C:/temp/output.mp4",
            Range("00:00:00", "00:00:05"),
            DownloadMode.AudioAndVideo).ToList();
        Assert.Contains("C:/temp/input.mp4", args);
        Assert.Contains("C:/temp/output.mp4", args);
        // Output must be the last arg so ffmpeg knows it is the output target.
        Assert.Equal("C:/temp/output.mp4", args[^1]);
    }

    [Fact]
    public void BuildCutArgs_AudioOnlyMode_SameArgsAsVideo()
    {
        // ffmpeg auto-detects format from the input container; we don't switch
        // codec flags based on DownloadMode (the mode parameter is reserved for
        // future per-mode tuning). Stream-copy works equally for m4a/mp3.
        var range = Range("00:00:10", "00:00:20");
        var video = FfmpegRunner.BuildCutArgs("in.mp4", "out.mp4", range, DownloadMode.AudioAndVideo).ToList();
        var audio = FfmpegRunner.BuildCutArgs("in.m4a", "out.m4a", range, DownloadMode.AudioOnly).ToList();

        // Stream-copy and overwrite flags should be identical between modes.
        Assert.Equal(video.IndexOf("-c"), audio.IndexOf("-c"));
        Assert.Contains("-y", audio);
        Assert.Contains("copy", audio);
    }
}
