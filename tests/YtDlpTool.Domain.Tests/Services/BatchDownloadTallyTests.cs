using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>
/// v1.5.0: a 50-video playlist used to mean 50 toasts. Batch jobs report once, together,
/// when the last of them ends and nothing more is being added.
/// </summary>
public class BatchDownloadTallyTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), X = Guid.NewGuid();

    [Fact]
    public void BatchJobs_AreAnnouncedOnceWhenTheLastEnds()
    {
        var t = new BatchDownloadTally();
        t.AddingStarted();
        t.Expect(A);
        t.Expect(B);
        Assert.Null(t.AddingFinished());

        Assert.True(t.IsBatchJob(A));
        Assert.Null(t.Finish(A, succeeded: true));
        Assert.Equal(new BatchTally(1, 1), t.Finish(B, succeeded: false));
    }

    [Fact]
    public void OtherJobs_AreNotPartOfIt()
    {
        var t = new BatchDownloadTally();
        t.AddingStarted();
        t.Expect(A);

        Assert.False(t.IsBatchJob(X));
        Assert.Null(t.Finish(X, succeeded: true));
    }

    [Fact]
    public void JobsThatEndBeforeAddingDoes_AreAnnouncedWhenAddingEnds()
    {
        var t = new BatchDownloadTally();
        t.AddingStarted();
        t.Expect(A);
        Assert.Null(t.Finish(A, succeeded: true));

        Assert.Equal(new BatchTally(1, 0), t.AddingFinished());
    }

    [Fact]
    public void ACancelledBatchJob_IsNotWaitedFor()
    {
        var t = new BatchDownloadTally();
        t.AddingStarted();
        t.Expect(A);
        t.Expect(B);
        t.AddingFinished();
        Assert.Null(t.Finish(A, succeeded: true));

        Assert.Equal(new BatchTally(1, 0), t.Forget(B));
    }

    [Fact]
    public void NothingAdded_NothingToAnnounce()
    {
        var t = new BatchDownloadTally();
        t.AddingStarted();
        Assert.Null(t.AddingFinished());
    }

    [Fact]
    public void AfterTheSummary_TheNextBatchStartsFresh()
    {
        var t = new BatchDownloadTally();
        t.AddingStarted();
        t.Expect(A);
        t.AddingFinished();
        t.Finish(A, succeeded: true);

        t.AddingStarted();
        t.Expect(B);
        t.AddingFinished();
        Assert.Equal(new BatchTally(0, 1), t.Finish(B, succeeded: false));
    }
}
