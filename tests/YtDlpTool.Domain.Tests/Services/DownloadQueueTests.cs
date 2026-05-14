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
        public bool ReturnFailure = false; // explicit init silences CS0649 across compilers
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

    private sealed class RateLimitedFakeExecutor : IDownloadExecutor
    {
        public int Calls;
        public async Task<DownloadExecutionResult> ExecuteAsync(
            DownloadJob job, IProgress<DownloadProgressSnapshot> progress,
            CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref Calls);
            await Task.Yield();
            if (n == 1)
            {
                return new DownloadExecutionResult(false, null,
                    new MappedError(ErrorCategory.RateLimited, "rate", "E-RATE001", true),
                    WasCancelled: false);
            }
            return new DownloadExecutionResult(true, "C:\\Downloads\\T.m4a", null, false);
        }
    }

    [Fact]
    public async Task DownloadQueue_RateLimited_RetriesOnce()
    {
        var fake = new RateLimitedFakeExecutor();
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(
            fake,
            maxConcurrency: 1,
            onEvent: e => { lock (events) events.Add(e); },
            logger: null,
            max429Retries: 1,
            rateLimitRetryDelay: TimeSpan.FromMilliseconds(50));

        queue.Enqueue(MakeJob());
        await WaitFor(() => { lock (events) return events.Any(e => e is JobCompletedEvent); });

        Assert.Equal(2, fake.Calls);
        lock (events)
        {
            Assert.Contains(events, e => e is JobCompletedEvent);
            Assert.DoesNotContain(events, e => e is JobFailedEvent);
        }
    }

    private sealed class StuckFakeExecutor : IDownloadExecutor
    {
        public async Task<DownloadExecutionResult> ExecuteAsync(
            DownloadJob job, IProgress<DownloadProgressSnapshot> progress,
            CancellationToken cancellationToken)
        {
            // Report progress once then go silent until cancelled.
            progress.Report(new DownloadProgressSnapshot(5, 1024, TimeSpan.FromSeconds(60)));
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new DownloadExecutionResult(false, null, null, WasCancelled: true);
            }
            return new DownloadExecutionResult(false, null, null, WasCancelled: true);
        }
    }

    [Fact]
    public async Task DownloadQueue_StuckDownload_ReportsFailure()
    {
        var fake = new StuckFakeExecutor();
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(
            fake,
            maxConcurrency: 1,
            onEvent: e => { lock (events) events.Add(e); },
            logger: null,
            max429Retries: 0,
            rateLimitRetryDelay: TimeSpan.FromMilliseconds(50),
            noProgressTimeout: TimeSpan.FromMilliseconds(50),
            watchdogInterval: TimeSpan.FromMilliseconds(10));

        queue.Enqueue(MakeJob());
        await WaitFor(() => { lock (events) return events.Any(e => e is JobFailedEvent); }, timeoutMs: 4000);

        lock (events)
        {
            var failed = events.OfType<JobFailedEvent>().FirstOrDefault();
            Assert.NotNull(failed);
            Assert.Equal("E-STUCK01", failed!.Error.ErrorCode);
            Assert.DoesNotContain(events, e => e is JobCancelledEvent);
        }
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
