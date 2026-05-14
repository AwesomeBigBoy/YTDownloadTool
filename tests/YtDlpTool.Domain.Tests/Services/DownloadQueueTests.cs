using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class DownloadQueueTests
{
    private static DownloadJob MakeJob() =>
        new(
            url: "https://www.youtube.com/watch?v=abc",
            title: "T",
            thumbnailUrl: "",
            mode: DownloadMode.AudioOnly,
            chosenFormat: new VideoFormat("140", null, null, "mp4a", "m4a", null, 128),
            subtitleLanguageCodes: Array.Empty<string>(),
            clipRange: null,
            saveDirectory: "C:\\Downloads");

    private sealed class FakeExecutor : IDownloadExecutor
    {
        public int Started, Completed;
        public TaskCompletionSource? GateForFirstJob;
        public bool ReturnFailure;
        public bool RespectCancellation = true;

        public async Task<DownloadExecutionResult> ExecuteAsync(
            DownloadJob job, IProgress<DownloadProgressSnapshot> progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Started);
            progress.Report(new DownloadProgressSnapshot(0, null, null));
            if (GateForFirstJob is not null && Started == 1)
                await GateForFirstJob.Task.ConfigureAwait(false);
            try
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (RespectCancellation)
            {
                return new DownloadExecutionResult(false, null, null, WasCancelled: true);
            }
            progress.Report(new DownloadProgressSnapshot(100, null, null));
            Interlocked.Increment(ref Completed);
            return ReturnFailure
                ? new DownloadExecutionResult(false, null,
                    new MappedError(ErrorCategory.NetworkError, "網路斷", "E-NET001", true), false)
                : new DownloadExecutionResult(true, "C:\\Downloads\\T.m4a", null, false);
        }
    }

    [Fact]
    public async Task Enqueue_SingleJob_Completes()
    {
        var fake = new FakeExecutor();
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(fake, maxConcurrency: 2, onEvent: e => { lock (events) events.Add(e); });

        queue.Enqueue(MakeJob());
        await WaitFor(() => { lock (events) return events.Any(e => e is JobCompletedEvent); });

        lock (events)
        {
            Assert.Contains(events, e => e is JobEnqueuedEvent);
            Assert.Contains(events, e => e is JobStartedEvent);
            Assert.Contains(events, e => e is JobCompletedEvent);
        }
    }

    [Fact]
    public async Task Enqueue_FiveJobs_RespectsConcurrencyTwo()
    {
        var fake = new FakeExecutor { GateForFirstJob = new TaskCompletionSource() };
        using var queue = new DownloadQueue(fake, maxConcurrency: 2, _ => { });

        for (int i = 0; i < 5; i++) queue.Enqueue(MakeJob());

        await WaitFor(() => fake.Started >= 2);
        var startedBeforeGate = fake.Started;
        Assert.True(startedBeforeGate <= 2, $"started={startedBeforeGate}");

        fake.GateForFirstJob.SetResult();
        await WaitFor(() => fake.Completed == 5);
    }

    [Fact]
    public async Task Cancel_RunningJob_ReportsCancelled()
    {
        var fake = new FakeExecutor { GateForFirstJob = new TaskCompletionSource() };
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(fake, maxConcurrency: 1, e => { lock (events) events.Add(e); });

        var job = MakeJob();
        queue.Enqueue(job);
        await WaitFor(() => fake.Started == 1);
        Assert.True(queue.Cancel(job.Id));
        fake.GateForFirstJob.SetResult();
        await WaitFor(() => { lock (events) return events.Any(e => e is JobCancelledEvent); });
    }

    [Fact]
    public async Task Cancel_PendingJob_ReportsCancelled()
    {
        var fake = new FakeExecutor { GateForFirstJob = new TaskCompletionSource() };
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(fake, maxConcurrency: 1, e => { lock (events) events.Add(e); });

        var first = MakeJob();
        var pending = MakeJob();
        queue.Enqueue(first);
        queue.Enqueue(pending);
        await WaitFor(() => fake.Started == 1);
        Assert.True(queue.Cancel(pending.Id));
        lock (events)
        {
            Assert.Contains(events, e => e is JobCancelledEvent ev && ev.Job.Id == pending.Id);
        }
        fake.GateForFirstJob.SetResult();
    }

    private static async Task WaitFor(Func<bool> cond, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("condition not satisfied");
    }
}
