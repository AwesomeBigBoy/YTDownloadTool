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

    /// <summary>
    /// Mimics the filename-conflict dialog: the executor tells the queue it is waiting for
    /// the user, stays silent far longer than the no-progress timeout, then carries on.
    /// </summary>
    private sealed class WaitsForUserFakeExecutor : IDownloadExecutor
    {
        public async Task<DownloadExecutionResult> ExecuteAsync(
            DownloadJob job, IProgress<DownloadProgressSnapshot> progress,
            CancellationToken cancellationToken)
        {
            progress.Report(new DownloadProgressSnapshot(0, null, null, WaitingForUser: true));
            await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return new DownloadExecutionResult(false, null, null, WasCancelled: true);

            progress.Report(new DownloadProgressSnapshot(50, 1024, null));
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return new DownloadExecutionResult(true, "C:\\out\\video.mp4", null, WasCancelled: false);
        }
    }

    [Fact]
    public async Task DownloadQueue_WaitingForUser_DoesNotTripTheWatchdog()
    {
        // v1.5.0 field-repro: a filename-conflict dialog left open for more than 90 s made
        // the no-progress watchdog cancel the job, and clicking 自動改名 afterwards then
        // failed it as E-STUCK01 「下載卡住」. Time spent waiting for the user is not a stall.
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(
            new WaitsForUserFakeExecutor(),
            maxConcurrency: 1,
            onEvent: e => { lock (events) events.Add(e); },
            logger: null,
            max429Retries: 0,
            rateLimitRetryDelay: TimeSpan.FromMilliseconds(50),
            noProgressTimeout: TimeSpan.FromMilliseconds(100),
            watchdogInterval: TimeSpan.FromMilliseconds(10));

        queue.Enqueue(MakeJob());
        await WaitFor(() => { lock (events) return events.Any(e => e is JobCompletedEvent or JobFailedEvent); }, timeoutMs: 4000);

        lock (events)
        {
            Assert.Contains(events, e => e is JobCompletedEvent);
            Assert.DoesNotContain(events, e => e is JobFailedEvent);
        }
    }

    /// <summary>The conflict dialog, answered after longer than the timeout; then the download hangs.</summary>
    private sealed class AnswersThenStallsFakeExecutor : IDownloadExecutor
    {
        public async Task<DownloadExecutionResult> ExecuteAsync(
            DownloadJob job, IProgress<DownloadProgressSnapshot> progress,
            CancellationToken cancellationToken)
        {
            progress.Report(new DownloadProgressSnapshot(0, null, null, WaitingForUser: true));
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            progress.Report(new DownloadProgressSnapshot(0, null, null));
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            return new DownloadExecutionResult(false, null, null, WasCancelled: true);
        }
    }

    [Fact]
    public async Task DownloadQueue_AfterTheUserAnswers_TheWatchdogIsArmedAgain()
    {
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(
            new AnswersThenStallsFakeExecutor(),
            maxConcurrency: 1,
            onEvent: e => { lock (events) events.Add(e); },
            logger: null,
            max429Retries: 0,
            rateLimitRetryDelay: TimeSpan.FromMilliseconds(50),
            noProgressTimeout: TimeSpan.FromMilliseconds(100),
            watchdogInterval: TimeSpan.FromMilliseconds(10));

        queue.Enqueue(MakeJob());
        await WaitFor(() => { lock (events) return events.Any(e => e is JobFailedEvent); }, timeoutMs: 4000);

        lock (events) Assert.Equal("E-STUCK01", events.OfType<JobFailedEvent>().Single().Error.ErrorCode);
    }

    private sealed class ReportsAndRecordsFakeExecutor : IDownloadExecutor
    {
        public Exception? ReportThrew;

        public async Task<DownloadExecutionResult> ExecuteAsync(
            DownloadJob job, IProgress<DownloadProgressSnapshot> progress,
            CancellationToken cancellationToken)
        {
            try { progress.Report(new DownloadProgressSnapshot(50, 1024, null)); }
            catch (Exception ex) { ReportThrew = ex; }
            await Task.Yield();
            return new DownloadExecutionResult(true, @"C:\out\video.mp4", null, WasCancelled: false);
        }
    }

    [Fact]
    public async Task AProgressListenerThatThrows_NeverReachesTheReportingThread()
    {
        // Reports run on yt-dlp's stdout reader thread now, where an exception ends the
        // process — a full disk under state.log, a listener bug.
        var fake = new ReportsAndRecordsFakeExecutor();
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(fake, maxConcurrency: 1, onEvent: e =>
        {
            if (e is JobProgressEvent) throw new IOException("state.log: disk full");
            lock (events) events.Add(e);
        });

        queue.Enqueue(MakeJob());
        await WaitFor(() => { lock (events) return events.Any(e => e is JobCompletedEvent); });

        Assert.Null(fake.ReportThrew);
    }

    /// <summary>A UI thread that is busy: whatever is posted to it waits (here, forever).</summary>
    private sealed class BusyUiContext : SynchronizationContext
    {
        public int Posted;
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posted);
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }

    private sealed class ReportsFromAWorkerFakeExecutor : IDownloadExecutor
    {
        public async Task<DownloadExecutionResult> ExecuteAsync(
            DownloadJob job, IProgress<DownloadProgressSnapshot> progress,
            CancellationToken cancellationToken)
        {
            // yt-dlp's output arrives on a reader thread.
            await Task.Run(() =>
            {
                for (var p = 10; p <= 90; p += 20) progress.Report(new DownloadProgressSnapshot(p, 1024, null));
            }, cancellationToken).ConfigureAwait(false);
            return new DownloadExecutionResult(true, "C:\\out\\video.mp4", null, WasCancelled: false);
        }
    }

    [Fact]
    public async Task Progress_IsHandledOnTheReportingThread_InOrder_EvenForAJobQueuedFromTheUiThread()
    {
        // A job queued from the UI thread wrapped its progress in Progress<T>, which posts
        // every line to that thread — before the UI's throttle could drop it — and a job
        // started from the pool got its reports on pool threads in any order: the order the
        // watchdog's waiting-for-the-user flag depends on.
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(new ReportsFromAWorkerFakeExecutor(), maxConcurrency: 1,
            onEvent: e => { lock (events) events.Add(e); });
        var ui = new BusyUiContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            queue.Enqueue(MakeJob());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await WaitFor(() => { lock (events) return events.Any(e => e is JobCompletedEvent); });

        lock (events)
            Assert.Equal(new[] { 10.0, 30, 50, 70, 90 }, events.OfType<JobProgressEvent>().Select(e => e.Progress.Percent));
        Assert.Equal(0, ui.Posted);
    }

    private sealed class StuckDuringRateLimitFakeExecutor : IDownloadExecutor
    {
        // First call returns RateLimited. Second call (the retry) hangs forever
        // until cancelled — simulating the watchdog firing during the retry.
        public int Calls;
        public async Task<DownloadExecutionResult> ExecuteAsync(
            DownloadJob job, IProgress<DownloadProgressSnapshot> progress,
            CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref Calls);
            if (n == 1)
            {
                await Task.Yield();
                return new DownloadExecutionResult(false, null,
                    new MappedError(ErrorCategory.RateLimited, "rate", "E-RATE001", true),
                    WasCancelled: false);
            }
            // Second call: report once then go silent so watchdog trips.
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
    public async Task DownloadQueue_StuckDuringRateLimit_OnlyOneTerminalEvent()
    {
        // Fix E: a stuck watchdog firing during the rate-limit retry path used to
        // double-emit (one cancel from the watchdog, one failure from the executor
        // result). The terminalEmitted guard collapses those into a single event.
        var fake = new StuckDuringRateLimitFakeExecutor();
        var events = new List<QueueEvent>();
        using var queue = new DownloadQueue(
            fake,
            maxConcurrency: 1,
            onEvent: e => { lock (events) events.Add(e); },
            logger: null,
            max429Retries: 1,
            rateLimitRetryDelay: TimeSpan.FromMilliseconds(10),
            noProgressTimeout: TimeSpan.FromMilliseconds(50),
            watchdogInterval: TimeSpan.FromMilliseconds(10));

        queue.Enqueue(MakeJob());
        await WaitFor(() => { lock (events) return events.Any(e => e is JobFailedEvent); }, timeoutMs: 4000);

        // Give the queue a beat to see if any second terminal event escapes.
        await Task.Delay(200);

        lock (events)
        {
            var terminals = events.Count(e => e is JobFailedEvent || e is JobCancelledEvent || e is JobCompletedEvent);
            Assert.Equal(1, terminals);
            Assert.Single(events.OfType<JobFailedEvent>());
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
