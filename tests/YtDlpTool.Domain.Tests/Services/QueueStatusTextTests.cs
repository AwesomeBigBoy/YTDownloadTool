using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class QueueStatusTextTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void Pending() =>
        Assert.Equal("等待中", QueueStatusText.Format(JobStatus.Pending, 0, null, null, null));

    [Fact]
    public void Downloading_FullLine() =>
        Assert.Equal("62% · 3.2 MB/s · 剩餘 0:12",
            QueueStatusText.Format(JobStatus.Downloading, 62.4, (long)(3.2 * MiB), TimeSpan.FromSeconds(12), null));

    [Fact]
    public void Downloading_WhileYtDlpMergesAndEmbeds_SaysItIsProcessing()
    {
        // The bytes are in; the merge, the cover and the clean-up follow, and on a managed
        // PC that is 20 s or more. 「80% · 115.2 KB/s」 frozen that long looked like a hang.
        Assert.Equal("80% · 處理中…",
            QueueStatusText.Format(JobStatus.Downloading, 80, 115_000, null, null, processing: true));
    }

    [Fact]
    public void ADownloadBeingCancelled_SaysSo()
    {
        // Cancelling stops yt-dlp at once, but clearing its files can take ~3 s per file on a
        // managed PC; the row used to keep saying 處理中 all that time.
        Assert.Equal("正在取消…",
            QueueStatusText.Format(JobStatus.Downloading, 80, null, null, null, processing: true, cancelling: true));
        Assert.Equal("正在取消…", QueueStatusText.Format(JobStatus.Pending, 0, null, null, null, cancelling: true));
    }

    [Fact]
    public void Downloading_NoSpeedNoEta() =>
        Assert.Equal("5%", QueueStatusText.Format(JobStatus.Downloading, 5, null, null, null));

    [Fact]
    public void Downloading_LongEta() =>
        Assert.Equal("10% · 剩餘 1:02:03",
            QueueStatusText.Format(JobStatus.Downloading, 10, null, new TimeSpan(1, 2, 3), null));

    [Fact]
    public void Downloading_ZeroEtaIsOmitted()
    {
        // yt-dlp reports ETA 00:00 mid-download for fragmented (DASH) streams; seen as
        // 「78% · 702.3 KB/s · 剩餘 0:00」 in the v1.5.0 UI. A zero is never informative.
        Assert.Equal("78% · 702.3 KB/s",
            QueueStatusText.Format(JobStatus.Downloading, 78, 719155, TimeSpan.Zero, null));
    }

    [Fact]
    public void Downloading_PercentIsFloored()
    {
        // 99.6 must not read "100%" while the last bytes (or the merge) are still running.
        Assert.Equal("99%", QueueStatusText.Format(JobStatus.Downloading, 99.6, null, null, null));
    }

    [Fact]
    public void Completed() =>
        Assert.Equal("已完成", QueueStatusText.Format(JobStatus.Completed, 100, null, null, null));

    [Fact]
    public void Failed_UsesReason() =>
        Assert.Equal("HTTP 403：已被拒絕", QueueStatusText.Format(JobStatus.Failed, 40, null, null, "HTTP 403：已被拒絕"));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Failed_DefaultText(string? reason) =>
        Assert.Equal("下載失敗", QueueStatusText.Format(JobStatus.Failed, 0, null, null, reason));

    [Fact]
    public void Cancelled() =>
        Assert.Equal("已取消", QueueStatusText.Format(JobStatus.Cancelled, 30, null, null, null));

    [Theory]
    [InlineData(900L, "900 B/s")]
    [InlineData(512L * 1024, "512 KB/s")]
    [InlineData(3355443L, "3.2 MB/s")]
    [InlineData(1610612736L, "1.5 GB/s")]
    public void Speed_Units(long bps, string expected) =>
        Assert.Equal(expected, QueueStatusText.FormatSpeed(bps));
}
