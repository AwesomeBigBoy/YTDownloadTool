using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>v1.5.0: the links of a playlist or multi-link paste, parsed and queued one by one.</summary>
public class BatchQueueTests
{
    private static string Url(string id) => "https://www.youtube.com/watch?v=" + id;
    private static BatchWork W(string id) => new(Url(id), DownloadMode.AudioOnly, "M4A");
    private static MappedError Err(ErrorCategory category, string code) => new(category, "原因 " + code, code, false);

    private sealed class Fake
    {
        public readonly List<string> Calls = new();
        public Func<BatchWork, CancellationToken, Task<BatchItemResult>> Next =
            (_, _) => Task.FromResult(BatchItemResult.Added);

        public Task<BatchItemResult> QueueOne(BatchWork w, CancellationToken ct)
        {
            lock (Calls) Calls.Add(w.Url);
            return Next(w, ct);
        }
    }

    [Fact]
    public async Task Add_QueuesEachLinkOnce_InOrder()
    {
        // A playlist can list the same video twice; two jobs would write the same files.
        var fake = new Fake();
        var q = new BatchQueue(fake.QueueOne, _ => false);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("AAAAAAAAAAA"), W("CCCCCCCCCCC") });
        await q.Completion;

        Assert.Equal(new[] { Url("AAAAAAAAAAA"), Url("BBBBBBBBBBB"), Url("CCCCCCCCCCC") }, fake.Calls);
        var p = q.Progress;
        Assert.Equal((3, 3, 3, 0, 1), (p.Total, p.Done, p.Added, p.Failed, p.Skipped));
        Assert.False(p.IsRunning);
    }

    [Fact]
    public async Task Add_SkipsLinksAlreadyInTheDownloadList()
    {
        var fake = new Fake();
        var q = new BatchQueue(fake.QueueOne, url => url == Url("BBBBBBBBBBB"));

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC") });
        await q.Completion;

        Assert.Equal(new[] { Url("AAAAAAAAAAA"), Url("CCCCCCCCCCC") }, fake.Calls);
        Assert.Equal(1, q.Progress.Skipped);
    }

    [Fact]
    public async Task Add_WhileRunning_JoinsTheSameBatch()
    {
        var gate = new TaskCompletionSource<BatchItemResult>();
        var fake = new Fake();
        var first = true;
        fake.Next = (_, _) =>
        {
            if (!first) return Task.FromResult(BatchItemResult.Added);
            first = false;
            return gate.Task;
        };
        var q = new BatchQueue(fake.QueueOne, _ => false);

        q.Add(new[] { W("AAAAAAAAAAA") });
        q.Add(new[] { W("BBBBBBBBBBB"), W("AAAAAAAAAAA") });
        gate.SetResult(BatchItemResult.Added);
        await q.Completion;

        Assert.Equal(new[] { Url("AAAAAAAAAAA"), Url("BBBBBBBBBBB") }, fake.Calls);
        Assert.Equal((2, 2, 1), (q.Progress.Total, q.Progress.Added, q.Progress.Skipped));
    }

    [Fact]
    public async Task Failures_AreCountedWithTheMostCommonReason()
    {
        var results = new Queue<BatchItemResult>(new[]
        {
            BatchItemResult.Added,
            BatchItemResult.Failed(Err(ErrorCategory.VideoUnavailable, "E-VIDUNAV")),
            BatchItemResult.Failed(Err(ErrorCategory.YouTubeRefused, "E-AUTH001")),
            BatchItemResult.Failed(Err(ErrorCategory.VideoUnavailable, "E-VIDUNAV")),
        });
        var fake = new Fake { Next = (_, _) => Task.FromResult(results.Dequeue()) };
        var q = new BatchQueue(fake.QueueOne, _ => false);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC"), W("DDDDDDDDDDD") });
        await q.Completion;

        Assert.Equal((1, 3), (q.Progress.Added, q.Progress.Failed));
        Assert.Equal("E-VIDUNAV", q.Progress.CommonError?.ErrorCode);
        Assert.Null(q.Progress.AbortedOn);
    }

    [Fact]
    public async Task ThreeFailuresInARowThatHitEveryVideo_StopTheRest()
    {
        // Behind an HTTPS-inspecting proxy every parse fails the same way, and each one
        // can take 30 s or more: a 50-video playlist would grind for half an hour.
        var fake = new Fake
        {
            Next = (_, _) => Task.FromResult(BatchItemResult.Failed(Err(ErrorCategory.NetworkError, "E-SSL01"))),
        };
        var q = new BatchQueue(fake.QueueOne, _ => false);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC"), W("DDDDDDDDDDD"), W("EEEEEEEEEEE") });
        await q.Completion;

        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal("E-SSL01", q.Progress.AbortedOn?.ErrorCode);
        Assert.Equal((5, 3, 3), (q.Progress.Total, q.Progress.Done, q.Progress.Failed));
    }

    [Fact]
    public async Task FailuresOfSingleVideos_DoNotStopTheBatch()
    {
        var fake = new Fake
        {
            Next = (_, _) => Task.FromResult(BatchItemResult.Failed(Err(ErrorCategory.VideoUnavailable, "E-VIDUNAV"))),
        };
        var q = new BatchQueue(fake.QueueOne, _ => false);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC"), W("DDDDDDDDDDD") });
        await q.Completion;

        Assert.Equal(4, fake.Calls.Count);
        Assert.Null(q.Progress.AbortedOn);
    }

    [Fact]
    public async Task ASuccessInBetween_ResetsTheRun()
    {
        var ssl = BatchItemResult.Failed(Err(ErrorCategory.NetworkError, "E-SSL01"));
        var results = new Queue<BatchItemResult>(new[] { ssl, ssl, BatchItemResult.Added, ssl, ssl });
        var fake = new Fake { Next = (_, _) => Task.FromResult(results.Dequeue()) };
        var q = new BatchQueue(fake.QueueOne, _ => false);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC"), W("DDDDDDDDDDD"), W("EEEEEEEEEEE") });
        await q.Completion;

        Assert.Equal(5, fake.Calls.Count);
        Assert.Null(q.Progress.AbortedOn);
    }

    [Fact]
    public async Task Stop_DropsTheRest_AndTheLinkBeingParsedIsNotAFailure()
    {
        var parsing = new TaskCompletionSource();
        var fake = new Fake();
        fake.Next = async (w, ct) =>
        {
            if (w.Url != Url("BBBBBBBBBBB")) return BatchItemResult.Added;
            parsing.SetResult();
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            return BatchItemResult.Failed(Err(ErrorCategory.UnknownError, "E-CANCEL"));
        };
        var q = new BatchQueue(fake.QueueOne, _ => false);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC"), W("DDDDDDDDDDD") });
        await parsing.Task;
        q.Stop();
        await q.Completion;

        Assert.Equal(new[] { Url("AAAAAAAAAAA"), Url("BBBBBBBBBBB") }, fake.Calls);
        var p = q.Progress;
        Assert.Equal((1, 0, 1, 4), (p.Added, p.Failed, p.Done, p.Total));
        Assert.True(p.Stopped);
    }

    [Fact]
    public async Task AnException_CountsAsAFailure_AndTheBatchGoesOn()
    {
        var fake = new Fake();
        fake.Next = (w, _) => w.Url == Url("BBBBBBBBBBB")
            ? throw new IOException("disk full")
            : Task.FromResult(BatchItemResult.Added);
        var q = new BatchQueue(fake.QueueOne, _ => false);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC") });
        await q.Completion;

        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal((2, 1), (q.Progress.Added, q.Progress.Failed));
        Assert.Contains("disk full", q.Progress.CommonError?.UserMessage);
    }

    [Fact]
    public async Task ABatchAfterTheLastOneFinished_StartsFromZero()
    {
        var fake = new Fake();
        var q = new BatchQueue(fake.QueueOne, _ => false);
        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB") });
        await q.Completion;

        q.Add(new[] { W("CCCCCCCCCCC"), W("AAAAAAAAAAA") });
        await q.Completion;

        // A video queued by the previous batch is no duplicate of this one (it may have been
        // removed from the list since); whether it is still downloading is isAlreadyQueued's call.
        Assert.Equal((2, 2, 0), (q.Progress.Total, q.Progress.Added, q.Progress.Skipped));
    }

    [Fact]
    public async Task Changed_IsRaisedAsTheBatchMovesOn()
    {
        var fake = new Fake();
        var q = new BatchQueue(fake.QueueOne, _ => false);
        var seen = new List<(int Done, bool Running)>();
        q.Changed += () => seen.Add((q.Progress.Done, q.Progress.IsRunning));

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB") });
        await q.Completion;

        Assert.Equal((0, true), seen.First());
        Assert.Equal((2, false), seen.Last());
    }
}
