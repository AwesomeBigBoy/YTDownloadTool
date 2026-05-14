using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;

namespace YtDlpTool.Domain.Tests.Persistence;

public class StateJournalTests : IDisposable
{
    private readonly string _path;

    public StateJournalTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "ytdlp-state-" + Guid.NewGuid() + ".log");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void AppendThenRead_RoundtripsEvents()
    {
        using (var journal = new StateJournal(_path))
        {
            journal.Append(new StateJournalEvent
            {
                At = DateTimeOffset.UtcNow,
                Type = StateJournalEventType.JobEnqueued,
                JobId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Snapshot = new JobSnapshot
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Url = "https://www.youtube.com/watch?v=abc",
                    Title = "T", Mode = DownloadMode.AudioOnly,
                    FormatId = "140", FormatExt = "m4a",
                    SaveDirectory = "C:\\Downloads"
                }
            });
            journal.Append(new StateJournalEvent
            {
                At = DateTimeOffset.UtcNow,
                Type = StateJournalEventType.JobProgress,
                JobId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ProgressPercent = 42.5
            });
        }

        var events = StateJournal.ReadAll(_path).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(StateJournalEventType.JobEnqueued, events[0].Type);
        Assert.Equal("https://www.youtube.com/watch?v=abc", events[0].Snapshot!.Url);
        Assert.Equal(42.5, events[1].ProgressPercent);
    }

    [Fact]
    public void Read_MalformedLine_IsSkipped()
    {
        File.WriteAllText(_path, "{\"At\":\"2026-05-14T00:00:00Z\",\"Type\":\"JobEnqueued\",\"JobId\":\"11111111-1111-1111-1111-111111111111\"}\nthis is not json\n");
        var events = StateJournal.ReadAll(_path).ToList();
        Assert.Single(events);
    }

    [Fact]
    public void Reconstruct_LatestStatusPerJob_ReturnsLatestOpenJobs()
    {
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();
        var events = new[]
        {
            new StateJournalEvent { At = DateTimeOffset.UtcNow, Type = StateJournalEventType.JobEnqueued, JobId = jobA,
                Snapshot = new JobSnapshot { Id = jobA, Url = "a", FormatId = "0", SaveDirectory = "" } },
            new StateJournalEvent { At = DateTimeOffset.UtcNow, Type = StateJournalEventType.JobStarted, JobId = jobA },
            new StateJournalEvent { At = DateTimeOffset.UtcNow, Type = StateJournalEventType.JobCompleted, JobId = jobA },
            new StateJournalEvent { At = DateTimeOffset.UtcNow, Type = StateJournalEventType.JobEnqueued, JobId = jobB,
                Snapshot = new JobSnapshot { Id = jobB, Url = "b", FormatId = "0", SaveDirectory = "" } },
            new StateJournalEvent { At = DateTimeOffset.UtcNow, Type = StateJournalEventType.JobProgress, JobId = jobB, ProgressPercent = 50 },
        };
        var open = StateJournal.ReconstructOpenJobs(events).ToList();
        Assert.Single(open);
        Assert.Equal(jobB, open[0].Id);
    }
}
