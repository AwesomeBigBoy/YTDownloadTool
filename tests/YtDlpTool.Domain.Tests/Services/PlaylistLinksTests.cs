using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>v1.5.0: which address reads the playlist a pasted video was opened from.</summary>
public class PlaylistLinksTests
{
    private const string Video = "dQw4w9WgXcQ";

    [Theory]
    [InlineData("PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    [InlineData("UU4QobU6STFB0P71PMvOGN5A")]
    // A Mix: /playlist?list=RD… is refused as "This playlist type is unviewable", the watch
    // page lists it (measured 2026-11-04: 404 videos for RDdQw4w9WgXcQ).
    [InlineData("RDdQw4w9WgXcQ")]
    public void ForVideo_ReadsTheListThroughTheWatchPage(string list)
    {
        Assert.Equal($"https://www.youtube.com/watch?v={Video}&list={list}", PlaylistLinks.ForVideo(Video, list));
    }

    [Theory]
    [InlineData("WL")]   // 稍後觀看
    [InlineData("LL")]   // 喜歡的影片
    [InlineData("LM")]   // YouTube Music 喜歡的音樂
    public void ForVideo_ListsThatNeedSignIn_AreNotOffered(string list)
    {
        Assert.Null(PlaylistLinks.ForVideo(Video, list));
    }

    [Theory]
    [InlineData(null, "PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    [InlineData(Video, null)]
    public void ForVideo_NeedsBoth(string? video, string? list)
    {
        Assert.Null(PlaylistLinks.ForVideo(video, list));
    }
}
