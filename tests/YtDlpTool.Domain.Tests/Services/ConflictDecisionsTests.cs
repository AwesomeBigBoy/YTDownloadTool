using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>
/// v1.5.0: 「檔案已存在」 when a playlist is added again — one question, and its answer for
/// the rest of that batch if the user ticks it.
/// </summary>
public class ConflictDecisionsTests
{
    private static readonly Guid Batch = Guid.NewGuid(), OtherBatch = Guid.NewGuid();

    private sealed class Asker
    {
        public int Asked;
        public readonly List<bool> Offers = new();
        public (FilenameConflictResolution, bool) Answer = (FilenameConflictResolution.AutoRename, false);

        public Task<(FilenameConflictResolution, bool)> Ask(bool offerForTheRest)
        {
            Interlocked.Increment(ref Asked);
            lock (Offers) Offers.Add(offerForTheRest);
            return Task.FromResult(Answer);
        }
    }

    [Fact]
    public async Task ForTheRest_AnswersTheSameBatchWithoutAsking()
    {
        var d = new ConflictDecisions();
        var asker = new Asker { Answer = (FilenameConflictResolution.Cancel, true) };

        Assert.Equal(FilenameConflictResolution.Cancel, await d.DecideAsync(Batch, asker.Ask));
        Assert.Equal(FilenameConflictResolution.Cancel, await d.DecideAsync(Batch, asker.Ask));
        Assert.Equal(FilenameConflictResolution.Cancel, await d.DecideAsync(Batch, asker.Ask));

        Assert.Equal(1, asker.Asked);
    }

    [Fact]
    public async Task OtherBatchesAndSingleVideos_AreStillAsked()
    {
        var d = new ConflictDecisions();
        var asker = new Asker { Answer = (FilenameConflictResolution.Overwrite, true) };
        await d.DecideAsync(Batch, asker.Ask);

        await d.DecideAsync(OtherBatch, asker.Ask);
        await d.DecideAsync(null, asker.Ask);

        Assert.Equal(3, asker.Asked);
        // A single video has no "rest of the batch" to offer.
        Assert.Equal(new[] { true, true, false }, asker.Offers);
    }

    [Fact]
    public async Task WithoutForTheRest_TheNextOneIsAskedAgain()
    {
        var d = new ConflictDecisions();
        var asker = new Asker { Answer = (FilenameConflictResolution.AutoRename, false) };

        await d.DecideAsync(Batch, asker.Ask);
        await d.DecideAsync(Batch, asker.Ask);

        Assert.Equal(2, asker.Asked);
    }

    [Fact]
    public async Task OneQuestionAtATime_AndOneWaitingItsTurnTakesTheAnswerForTheRest()
    {
        // Downloads run side by side, so two can reach an existing file together; each used
        // to open its own dialog, stacked on the first.
        var d = new ConflictDecisions();
        var answer = new TaskCompletionSource<(FilenameConflictResolution, bool)>();
        var asked = 0;
        Task<(FilenameConflictResolution, bool)> Ask(bool _)
        {
            Interlocked.Increment(ref asked);
            return answer.Task;
        }

        var first = d.DecideAsync(Batch, Ask);
        var second = d.DecideAsync(Batch, Ask);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref asked));

        answer.SetResult((FilenameConflictResolution.Overwrite, true));

        Assert.Equal(FilenameConflictResolution.Overwrite, await first);
        Assert.Equal(FilenameConflictResolution.Overwrite, await second);
        Assert.Equal(1, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task OneQuestionAtATime_AcrossBatches()
    {
        var d = new ConflictDecisions();
        var answer = new TaskCompletionSource<(FilenameConflictResolution, bool)>();
        var asked = 0;
        Task<(FilenameConflictResolution, bool)> Ask(bool _) =>
            Interlocked.Increment(ref asked) == 1 ? answer.Task : Task.FromResult((FilenameConflictResolution.AutoRename, false));

        var first = d.DecideAsync(Batch, Ask);
        var second = d.DecideAsync(null, Ask);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref asked));

        answer.SetResult((FilenameConflictResolution.Cancel, false));
        await Task.WhenAll(first, second);
        Assert.Equal(2, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task ADownloadCancelledWhileWaitingItsTurn_StopsWaiting()
    {
        var d = new ConflictDecisions();
        var never = new TaskCompletionSource<(FilenameConflictResolution, bool)>();
        _ = d.DecideAsync(Batch, _ => never.Task);
        using var cts = new CancellationTokenSource();

        var waiting = d.DecideAsync(Batch, _ => never.Task, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
