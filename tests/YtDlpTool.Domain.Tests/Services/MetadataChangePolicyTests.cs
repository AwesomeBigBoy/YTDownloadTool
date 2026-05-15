using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class MetadataChangePolicyTests
{
    private static VideoMetadata Meta(string id) => new(
        VideoId: id,
        Title: "t",
        Channel: "c",
        Duration: TimeSpan.FromSeconds(60),
        ThumbnailUrl: "",
        Formats: Array.Empty<VideoFormat>(),
        Subtitles: Array.Empty<SubtitleTrack>());

    [Fact]
    public void NullToMeta_DoesNotReset()
    {
        // First successful parse — the user has just started, never had options.
        Assert.False(MetadataChangePolicy.ShouldResetOptions(null, Meta("abc")));
    }

    [Fact]
    public void MetaToNull_DoesNotReset()
    {
        // Clear / parse error — keep what the user picked, they'll likely re-paste similar.
        Assert.False(MetadataChangePolicy.ShouldResetOptions(Meta("abc"), null));
    }

    [Fact]
    public void SameVideoId_DoesNotReset()
    {
        // Different URL forms (youtu.be vs youtube.com) that resolve to the same video.
        Assert.False(MetadataChangePolicy.ShouldResetOptions(Meta("abc"), Meta("abc")));
    }

    [Fact]
    public void DifferentVideoId_Resets()
    {
        // The actual reset trigger: user switched to a different video.
        Assert.True(MetadataChangePolicy.ShouldResetOptions(Meta("abc"), Meta("xyz")));
    }

    [Fact]
    public void NullToNull_DoesNotReset()
    {
        Assert.False(MetadataChangePolicy.ShouldResetOptions(null, null));
    }
}
