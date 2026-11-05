using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class CoverArtTests
{
    [Theory]
    // 音訊: music players and Explorer show the cover — kept.
    [InlineData(DownloadMode.AudioOnly, false, true)]
    // v1.5.0, the user's call: not for 影片. On PCs with endpoint security embedding cost
    // ~15–19 s of a ~37 s download of a 19-s clip (37.4 → 18.0 s without, measured
    // 2026-11-04), and a player shows the video itself.
    [InlineData(DownloadMode.AudioAndVideo, false, false)]
    [InlineData(DownloadMode.VideoOnly, false, false)]
    // 擷取片段 goes through ffmpeg's cut, which never carried the cover.
    [InlineData(DownloadMode.AudioOnly, true, false)]
    [InlineData(DownloadMode.AudioAndVideo, true, false)]
    public void Embeds(DownloadMode mode, bool hasClip, bool expected)
    {
        Assert.Equal(expected, CoverArt.Embeds(mode, hasClip));
    }
}
