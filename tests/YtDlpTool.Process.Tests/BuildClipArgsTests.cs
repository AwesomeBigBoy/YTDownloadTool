using YtDlpTool.Domain.Models;
using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

public class BuildClipArgsTests
{
    private static VideoFormat AnyFormat() => new(
        FormatId: "best",
        Height: 720,
        VideoCodec: "h264",
        AudioCodec: "aac",
        Extension: "mp4",
        FileSizeBytes: 1024 * 1024,
        AudioBitrateKbps: 128);

    private static DownloadRequest Make(DownloadMode mode, TimeRange? clip) => new(
        Url: "https://example",
        Mode: mode,
        ChosenFormat: AnyFormat(),
        SubtitleLanguageCodes: Array.Empty<string>(),
        ClipRange: clip,
        SaveDirectory: "C:/tmp",
        SanitizedFileStem: "video");

    [Fact]
    public void NoClipRange_ProducesNoArgs()
    {
        var args = YtDlpRunner.BuildClipArgs(Make(DownloadMode.AudioAndVideo, clip: null)).ToList();
        Assert.Empty(args);
    }

    [Fact]
    public void VideoClip_IncludesForceKeyframes()
    {
        var range = new TimeRange(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
        var args = YtDlpRunner.BuildClipArgs(Make(DownloadMode.AudioAndVideo, range)).ToList();
        Assert.Contains("--download-sections", args);
        Assert.Contains(range.ToYtDlpFormat(), args);
        Assert.Contains("--force-keyframes-at-cuts", args);
    }

    [Fact]
    public void VideoOnlyClip_IncludesForceKeyframes()
    {
        var range = new TimeRange(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
        var args = YtDlpRunner.BuildClipArgs(Make(DownloadMode.VideoOnly, range)).ToList();
        Assert.Contains("--force-keyframes-at-cuts", args);
    }

    [Fact]
    public void AudioOnlyClip_OmitsForceKeyframes()
    {
        // Fix 9: --force-keyframes-at-cuts has no effect for audio-only and yt-dlp prints
        // a misleading warning. The arg list should NOT contain it.
        var range = new TimeRange(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
        var args = YtDlpRunner.BuildClipArgs(Make(DownloadMode.AudioOnly, range)).ToList();
        Assert.Contains("--download-sections", args);
        Assert.Contains(range.ToYtDlpFormat(), args);
        Assert.DoesNotContain("--force-keyframes-at-cuts", args);
    }

    [Fact]
    public void ClipRangeFormat_MatchesYtDlpAsterisk()
    {
        // yt-dlp's --download-sections expects "*HH:MM:SS-HH:MM:SS" for an absolute
        // time-range section. Verify our formatter still produces it.
        var range = new TimeRange(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(30),
                                  TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(45));
        Assert.Equal("*00:01:30-00:03:45", range.ToYtDlpFormat());
    }
}
