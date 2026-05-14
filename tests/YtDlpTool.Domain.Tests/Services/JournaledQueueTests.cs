using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class JournaledQueueTests : IDisposable
{
    private readonly string _journalPath;

    public JournaledQueueTests()
    {
        _journalPath = Path.Combine(Path.GetTempPath(), "ytdlp-jq-" + Guid.NewGuid() + ".log");
    }

    public void Dispose()
    {
        if (File.Exists(_journalPath)) File.Delete(_journalPath);
    }

    [Fact]
    public void Wrap_PassesEventsThroughAndPersists()
    {
        using var journal = new StateJournal(_journalPath);
        var seen = new List<QueueEvent>();
        var wrapped = JournaledQueue.Wrap(journal, e => seen.Add(e));

        var job = new DownloadJob(
            url: "https://www.youtube.com/watch?v=abc",
            title: "T", thumbnailUrl: "", mode: DownloadMode.AudioOnly,
            chosenFormat: new VideoFormat("140", null, null, "mp4a", "m4a", null, 128),
            subtitleLanguageCodes: Array.Empty<string>(), clipRange: null,
            saveDirectory: "C:\\D");

        wrapped(new JobEnqueuedEvent(job));
        wrapped(new JobProgressEvent(job, new DownloadProgressSnapshot(50, null, null)));
        wrapped(new JobCompletedEvent(job, "C:\\D\\T.m4a"));

        Assert.Equal(3, seen.Count);
        journal.Dispose();

        var events = StateJournal.ReadAll(_journalPath).ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal(StateJournalEventType.JobEnqueued, events[0].Type);
        Assert.NotNull(events[0].Snapshot);
        Assert.Equal(50, events[1].ProgressPercent);
        Assert.Equal(StateJournalEventType.JobCompleted, events[2].Type);
    }
}
