using YtDlpTool.Domain.Models;
using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

/// <summary>
/// v1.1.13 — pure-argument-builder tests for the new subtitle-handling helpers
/// on <see cref="FfmpegRunner"/>. Mirrors the existing <c>FfmpegRunnerCutTests</c>
/// pattern: no real ffmpeg invocation, only assertions on what argv we emit.
/// </summary>
public class FfmpegRunnerSubtitleTests
{
    private static TimeRange Range(string start, string end) =>
        new(TimeSpan.Parse(start), TimeSpan.Parse(end));

    // -------- BuildMuxArgs --------

    [Fact]
    public void BuildMuxArgs_NoSubs_StreamCopiesMediaAndMapsOnlyInput0()
    {
        var args = FfmpegRunner.BuildMuxArgs(
            "media.mp4", Array.Empty<string>(), "out.mp4").ToList();

        Assert.Contains("-y", args);
        var iIdx = args.IndexOf("-i");
        Assert.True(iIdx >= 0 && args[iIdx + 1] == "media.mp4");
        var cIdx = args.IndexOf("-c");
        Assert.True(cIdx >= 0 && args[cIdx + 1] == "copy");
        Assert.Contains("-map", args);
        // With no subs the final arg must be the output path.
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void BuildMuxArgs_SingleSub_AddsInputMapAndLangMetadata()
    {
        var subs = new[] { @"C:\tmp\video.zh-Hant.vtt" };
        var args = FfmpegRunner.BuildMuxArgs("media.mp4", subs, "out.mp4").ToList();

        // Two -i flags: media + subtitle, in order.
        var iIndices = args.Select((a, i) => (a, i)).Where(t => t.a == "-i").Select(t => t.i).ToList();
        Assert.Equal(2, iIndices.Count);
        Assert.Equal("media.mp4", args[iIndices[0] + 1]);
        Assert.Equal(@"C:\tmp\video.zh-Hant.vtt", args[iIndices[1] + 1]);

        // -c:s mov_text appears (so subtitles survive in mp4).
        var csIdx = args.IndexOf("-c:s");
        Assert.True(csIdx >= 0 && args[csIdx + 1] == "mov_text");

        // -map 0 + -map 1:0 (track 0 of subtitle input).
        var mapIndices = args.Select((a, i) => (a, i)).Where(t => t.a == "-map").Select(t => t.i).ToList();
        Assert.Equal(2, mapIndices.Count);
        Assert.Equal("0", args[mapIndices[0] + 1]);
        Assert.Equal("1:0", args[mapIndices[1] + 1]);

        // language metadata for subtitle stream 0.
        Assert.Contains("-metadata:s:s:0", args);
        var metaIdx = args.IndexOf("-metadata:s:s:0");
        Assert.Equal("language=zh-Hant", args[metaIdx + 1]);

        // Output remains the final arg.
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void BuildMuxArgs_MultipleSubs_MapsEachAndEmitsPerStreamLang()
    {
        var subs = new[]
        {
            @"D:\out\clip.en.vtt",
            @"D:\out\clip.zh-Hant.vtt",
            @"D:\out\clip.ja.srt",
        };
        var args = FfmpegRunner.BuildMuxArgs("clip.mp4", subs, "final.mp4").ToList();

        // Four -i flags: media + 3 subs.
        Assert.Equal(4, args.Count(a => a == "-i"));

        // Map sequence: 0, 1:0, 2:0, 3:0
        var mapIndices = args.Select((a, i) => (a, i)).Where(t => t.a == "-map").Select(t => t.i).ToList();
        Assert.Equal(4, mapIndices.Count);
        Assert.Equal("0", args[mapIndices[0] + 1]);
        Assert.Equal("1:0", args[mapIndices[1] + 1]);
        Assert.Equal("2:0", args[mapIndices[2] + 1]);
        Assert.Equal("3:0", args[mapIndices[3] + 1]);

        // Per-stream language metadata.
        Assert.Equal("language=en", args[args.IndexOf("-metadata:s:s:0") + 1]);
        Assert.Equal("language=zh-Hant", args[args.IndexOf("-metadata:s:s:1") + 1]);
        Assert.Equal("language=ja", args[args.IndexOf("-metadata:s:s:2") + 1]);
    }

    [Fact]
    public void BuildMuxArgs_SubWithoutLangSuffix_OmitsMetadata()
    {
        // ffmpeg should not be told "language=" with an empty value — that's
        // both useless and may trip strict parsing on some builds.
        var subs = new[] { @"C:\tmp\subtitle.vtt" }; // no <lang> segment
        var args = FfmpegRunner.BuildMuxArgs("media.mp4", subs, "out.mp4").ToList();
        Assert.DoesNotContain("-metadata:s:s:0", args);
    }

    // -------- BuildSubtitleCutArgs --------

    [Fact]
    public void BuildSubtitleCutArgs_SeekBeforeInputAndStreamCopy()
    {
        var args = FfmpegRunner.BuildSubtitleCutArgs(
            "in.vtt", "out.vtt", Range("00:01:10", "00:01:30")).ToList();

        Assert.Contains("-y", args);
        var ssIdx = args.IndexOf("-ss");
        var iIdx = args.IndexOf("-i");
        Assert.True(ssIdx >= 0 && iIdx >= 0);
        Assert.True(ssIdx < iIdx, "-ss must precede -i");
        Assert.Equal("00:01:10", args[ssIdx + 1]);

        // -to after -i is duration when -ss is before -i.
        var toIdx = args.IndexOf("-to");
        Assert.True(toIdx >= 0);
        Assert.Equal("00:00:20", args[toIdx + 1]);

        var cIdx = args.IndexOf("-c");
        Assert.True(cIdx >= 0 && args[cIdx + 1] == "copy");
        Assert.Equal("out.vtt", args[^1]);
    }

    // -------- ExtractLangFromFilename --------

    [Theory]
    [InlineData("video.zh-Hant.vtt", "zh-Hant")]
    [InlineData("video.en.vtt", "en")]
    [InlineData("video.ja.srt", "ja")]
    [InlineData("a.b.c.fr.vtt", "fr")]
    [InlineData("VIDEO.EN.VTT", "EN")]   // case-insensitive ext, but lang preserved
    public void ExtractLangFromFilename_KnownPatterns_ReturnsLangSegment(string fileName, string expected)
    {
        Assert.Equal(expected, FfmpegRunner.ExtractLangFromFilename(fileName));
    }

    [Theory]
    [InlineData("subtitle.vtt")]    // no lang segment
    [InlineData("track.srt")]
    [InlineData("video.mp4")]       // wrong extension
    [InlineData("")]
    public void ExtractLangFromFilename_NoLangOrWrongExt_ReturnsEmpty(string fileName)
    {
        Assert.Equal("", FfmpegRunner.ExtractLangFromFilename(fileName));
    }
}
