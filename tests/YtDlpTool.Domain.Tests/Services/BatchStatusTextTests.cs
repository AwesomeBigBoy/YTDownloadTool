using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>v1.5.0: the status line under the URL box while and after a batch is added.</summary>
public class BatchStatusTextTests
{
    private static BatchProgress P(int total, int done, int added, int failed = 0, int skipped = 0,
        bool running = false, bool stopped = false, MappedError? common = null, MappedError? aborted = null) =>
        new(total, done, added, failed, skipped, running, stopped, common, aborted);

    private static readonly MappedError Unavailable =
        new(ErrorCategory.VideoUnavailable, "影片已被移除或設為私人", "E-VIDUNAV", false);

    private static readonly MappedError Ssl =
        new(ErrorCategory.NetworkError, "HTTPS 憑證驗證失敗", "E-SSL01", false);

    [Fact]
    public void Running_CountsTheLinkBeingAdded()
    {
        Assert.Equal("正在加入第 3 / 20 部影片…", BatchStatusText.Format(P(20, 2, 2, running: true)));
    }

    [Fact]
    public void Running_NeverCountsPastTheTotal()
    {
        Assert.Equal("正在加入第 2 / 2 部影片…", BatchStatusText.Format(P(2, 2, 2, running: true)));
    }

    [Fact]
    public void Done_AllAdded()
    {
        Assert.Equal("已加入 3 部影片", BatchStatusText.Format(P(3, 3, 3)));
    }

    [Fact]
    public void Done_WithFailures_SaysWhyInsteadOfGuessing()
    {
        Assert.Equal("已加入 1 部影片，2 部無法加入：影片已被移除或設為私人",
            BatchStatusText.Format(P(3, 3, 1, failed: 2, common: Unavailable)));
    }

    [Fact]
    public void Done_WithSkippedLinks()
    {
        Assert.Equal("已加入 2 部影片，略過 1 部重複或已在下載清單中的影片",
            BatchStatusText.Format(P(2, 2, 2, skipped: 1)));
    }

    [Fact]
    public void Aborted_SaysItStoppedAndWhy()
    {
        Assert.Equal("連續 3 部無法加入，已停止加入其餘 2 部：HTTPS 憑證驗證失敗",
            BatchStatusText.Format(P(5, 3, 0, failed: 3, common: Ssl, aborted: Ssl)));
    }

    [Fact]
    public void Stopped_ByTheUser()
    {
        Assert.Equal("已加入 1 部影片，其餘 3 部已停止加入", BatchStatusText.Format(P(4, 1, 1, stopped: true)));
    }
}
