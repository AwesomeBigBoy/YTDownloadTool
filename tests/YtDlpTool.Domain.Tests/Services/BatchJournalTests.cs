using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>
/// v1.5.0: links of a playlist or multi-link paste that were not queued yet when the app
/// closed come back at the next start, with the interrupted downloads.
/// </summary>
public class BatchJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ytdlp-batchjournal-" + Guid.NewGuid() + ".log");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string Url(string id) => "https://www.youtube.com/watch?v=" + id;
    private static BatchWork W(string id) => new(Url(id), DownloadMode.AudioOnly, "M4A");

    private IReadOnlyList<JobSnapshot> OpenAfterClose(StateJournal journal)
    {
        journal.Dispose();
        return StateJournal.ReconstructOpenJobs(StateJournal.ReadAll(_path)).ToList();
    }

    [Fact]
    public async Task LinksStillWaitingWhenTheAppCloses_AreOpenInTheJournal()
    {
        var journal = new StateJournal(_path);
        var never = new TaskCompletionSource<BatchItemResult>();
        var q = new BatchQueue(
            (w, _) => w.Url == Url("AAAAAAAAAAA") ? Task.FromResult(BatchItemResult.Added) : never.Task,
            _ => false);
        BatchJournal.Attach(q, journal);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC") });
        await WaitFor(() => q.Progress.Done == 1);   // A queued, B being parsed, C waiting

        var open = OpenAfterClose(journal);
        Assert.Equal(new[] { Url("BBBBBBBBBBB"), Url("CCCCCCCCCCC") }, open.Select(o => o.Url));
        Assert.All(open, o => Assert.Equal(DownloadMode.AudioOnly, o.Mode));
    }

    [Fact]
    public async Task QueuedFailedAndStoppedLinks_AreNotOfferedAgain()
    {
        // Queued: the download itself is journaled. Failed: retrying cannot help. Stopped:
        // the user said no more.
        var journal = new StateJournal(_path);
        var never = new TaskCompletionSource<BatchItemResult>();
        var q = new BatchQueue((w, ct) =>
        {
            if (w.Url == Url("AAAAAAAAAAA"))
                return Task.FromResult(BatchItemResult.Failed(new MappedError(ErrorCategory.VideoUnavailable, "私人影片", "E-PRIV", false)));
            if (w.Url == Url("BBBBBBBBBBB")) return Task.FromResult(BatchItemResult.Added);
            return never.Task.WaitAsync(ct);
        }, _ => false);
        BatchJournal.Attach(q, journal);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC"), W("DDDDDDDDDDD") });
        await WaitFor(() => q.Progress.Done == 2);   // C being parsed, D waiting
        q.Stop();
        await q.Completion;

        Assert.Empty(OpenAfterClose(journal));
    }

    [Fact]
    public async Task LinksDroppedByTheRepeatedFailureRule_AreNotOfferedAgain()
    {
        // The status line says the rest was not added; the next start must not bring it back.
        var journal = new StateJournal(_path);
        var offline = new MappedError(ErrorCategory.NetworkError, "無法連線", "E-NET001", true);
        var q = new BatchQueue((_, _) => Task.FromResult(BatchItemResult.Failed(offline)), _ => false);
        BatchJournal.Attach(q, journal);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("CCCCCCCCCCC"), W("DDDDDDDDDDD"), W("EEEEEEEEEEE") });
        await q.Completion;

        Assert.NotNull(q.Progress.AbortedOn);
        Assert.Empty(OpenAfterClose(journal));
    }

    [Fact]
    public async Task DuplicatesAndLinksAlreadyDownloading_AreNotJournaled()
    {
        var journal = new StateJournal(_path);
        var never = new TaskCompletionSource<BatchItemResult>();
        var q = new BatchQueue((_, _) => never.Task, url => url == Url("BBBBBBBBBBB"));
        BatchJournal.Attach(q, journal);

        q.Add(new[] { W("AAAAAAAAAAA"), W("BBBBBBBBBBB"), W("AAAAAAAAAAA") });
        await WaitFor(() => q.Progress.IsRunning);

        Assert.Equal(new[] { Url("AAAAAAAAAAA") }, OpenAfterClose(journal).Select(o => o.Url));
    }

    private static async Task WaitFor(Func<bool> cond, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("condition not satisfied");
    }
}
