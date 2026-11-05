using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>v1.5.0: the progress the taskbar button shows for the download list.</summary>
public class TaskbarProgressTrackerTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    private static QueueRow Row(Guid id, JobStatus s, double p = 0) => new(id, s, p);

    [Fact]
    public void NothingToDownload_ShowsNothing()
    {
        var t = new TaskbarProgressTracker();
        Assert.Equal((TaskbarState.None, 0d), t.Update(Array.Empty<QueueRow>()));
    }

    [Fact]
    public void OneDownload_ShowsItsProgress()
    {
        var t = new TaskbarProgressTracker();
        Assert.Equal((TaskbarState.Normal, 0.4), t.Update(new[] { Row(A, JobStatus.Downloading, 40) }));
    }

    [Fact]
    public void OnlyWaiting_IsIndeterminate()
    {
        var t = new TaskbarProgressTracker();
        Assert.Equal(TaskbarState.Indeterminate, t.Update(new[] { Row(A, JobStatus.Pending) }).State);
    }

    [Fact]
    public void RetriedRow_ItsFailedRunNoLongerCountsAsDone()
    {
        // 重試 puts a new job (a new id) into the failed row. The failed run has to leave the
        // batch, or a phantom 100 % holds the bar up while the retry and the other one run.
        var t = new TaskbarProgressTracker();
        t.Update(new[] { Row(A, JobStatus.Downloading, 50), Row(B, JobStatus.Downloading, 10) });
        Assert.Equal(0.75, t.Update(new[] { Row(A, JobStatus.Downloading, 50), Row(B, JobStatus.Failed) }).Value);

        var retry = Guid.NewGuid();
        t.Forget(B);

        Assert.Equal(0.25, t.Update(new[] { Row(A, JobStatus.Downloading, 50), Row(retry, JobStatus.Pending) }).Value);
    }

    [Fact]
    public void SeveralDownloads_ShowTheAverage()
    {
        var t = new TaskbarProgressTracker();
        Assert.Equal(0.25, t.Update(new[] { Row(A, JobStatus.Downloading, 50), Row(B, JobStatus.Pending) }).Value);
    }

    [Fact]
    public void AFinishedDownload_StillCountsAsDone()
    {
        // Averaging only what is still running would send the bar backwards (90%+10% →
        // 50%, then 20% once the first finishes).
        var t = new TaskbarProgressTracker();
        t.Update(new[] { Row(A, JobStatus.Downloading, 90), Row(B, JobStatus.Downloading, 10) });

        var now = t.Update(new[] { Row(A, JobStatus.Completed, 100), Row(B, JobStatus.Downloading, 20) });

        Assert.Equal((TaskbarState.Normal, 0.6), now);
    }

    [Fact]
    public void AFailedDownload_CountsAsDone()
    {
        var t = new TaskbarProgressTracker();
        t.Update(new[] { Row(A, JobStatus.Downloading, 7.8), Row(B, JobStatus.Downloading, 50) });

        Assert.Equal(0.75, t.Update(new[] { Row(A, JobStatus.Failed, 7.8), Row(B, JobStatus.Downloading, 50) }).Value);
    }

    [Fact]
    public void WhenEverythingIsDone_ShowsNothing_AndTheNextBatchStartsFresh()
    {
        var t = new TaskbarProgressTracker();
        t.Update(new[] { Row(A, JobStatus.Downloading, 50) });
        Assert.Equal(TaskbarState.None, t.Update(new[] { Row(A, JobStatus.Completed, 100) }).State);

        // Rows from the finished batch stay in the list but are not part of the new one.
        var next = t.Update(new[] { Row(A, JobStatus.Completed, 100), Row(B, JobStatus.Downloading, 10) });

        Assert.Equal((TaskbarState.Normal, 0.1), next);
    }

    [Fact]
    public void ACancelledDownload_LeavesTheBatch()
    {
        // A cancelled row is removed from the list; it is no longer work to wait for.
        var t = new TaskbarProgressTracker();
        t.Update(new[] { Row(A, JobStatus.Downloading, 50), Row(B, JobStatus.Downloading, 50) });

        Assert.Equal(0.6, t.Update(new[] { Row(B, JobStatus.Downloading, 60) }).Value);
    }

    [Fact]
    public void AFinishedRowRemovedFromTheList_StillCounts()
    {
        var t = new TaskbarProgressTracker();
        t.Update(new[] { Row(A, JobStatus.Downloading, 90), Row(B, JobStatus.Pending) });
        t.Update(new[] { Row(A, JobStatus.Completed, 100), Row(B, JobStatus.Downloading, 50) });

        // The user clears 已完成 while B is still running.
        Assert.Equal(0.75, t.Update(new[] { Row(B, JobStatus.Downloading, 50) }).Value);
    }

    [Fact]
    public void ARowAddedMidBatch_JoinsIt()
    {
        var t = new TaskbarProgressTracker();
        t.Update(new[] { Row(A, JobStatus.Downloading, 100 - 0.0001) });
        var now = t.Update(new[] { Row(A, JobStatus.Completed, 100), Row(C, JobStatus.Pending) });
        Assert.Equal((TaskbarState.Normal, 0.5), now);
    }
}
