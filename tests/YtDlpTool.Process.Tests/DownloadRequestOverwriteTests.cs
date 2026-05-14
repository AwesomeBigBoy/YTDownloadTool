using YtDlpTool.Domain.Models;
using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

public class DownloadRequestOverwriteTests
{
    private static DownloadRequest Make(bool forceOverwrite) =>
        new(
            Url: "https://example.com/v",
            Mode: DownloadMode.VideoOnly,
            ChosenFormat: new VideoFormat("299", 1080, "avc1", null, "mp4", null, null),
            SubtitleLanguageCodes: Array.Empty<string>(),
            ClipRange: null,
            SaveDirectory: "C:\\temp",
            SanitizedFileStem: "v",
            ForceOverwrite: forceOverwrite);

    [Fact]
    public void DefaultRequest_DoesNotForceOverwrite()
    {
        var r = Make(forceOverwrite: false);
        Assert.False(r.ForceOverwrite);
    }

    [Fact]
    public void ExplicitOverwrite_PropagatesThroughRecord()
    {
        var r = Make(forceOverwrite: true);
        Assert.True(r.ForceOverwrite);
    }

    [Fact]
    public void WithExpression_PreservesOtherFields()
    {
        var r = Make(forceOverwrite: false) with { ForceOverwrite = true };
        Assert.True(r.ForceOverwrite);
        Assert.Equal("v", r.SanitizedFileStem);
        Assert.Equal(DownloadMode.VideoOnly, r.Mode);
    }
}
