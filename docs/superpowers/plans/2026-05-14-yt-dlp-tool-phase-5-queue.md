# Phase 5 · Download Queue + Persistence

**Goal:** Implement `DownloadQueue` (concurrency-controlled job scheduler with events) and `StateJournal` (JSONL persistence for crash recovery).

**Prerequisites:** Phase 4 complete (tag `phase-4-process-complete`).

---

### Task 5.1: `StateJournal` types and JSON context

**Files:**
- Create: `src/YtDlpTool.Domain/Persistence/StateJournalEvent.cs`
- Create: `src/YtDlpTool.Domain/Persistence/StateJournalEventType.cs`
- Create: `src/YtDlpTool.Domain/Persistence/JobSnapshot.cs`
- Modify: `src/YtDlpTool.Domain/Persistence/AppJsonContext.cs` (add types)

- [ ] **Step 1: Create types**

```csharp
// src/YtDlpTool.Domain/Persistence/StateJournalEventType.cs
namespace YtDlpTool.Domain.Persistence;

public enum StateJournalEventType { JobEnqueued, JobStarted, JobProgress, JobCompleted, JobFailed, JobCancelled }
```

```csharp
// src/YtDlpTool.Domain/Persistence/JobSnapshot.cs
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Persistence;

public sealed class JobSnapshot
{
    public Guid Id { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public DownloadMode Mode { get; set; }
    public string FormatId { get; set; } = "";
    public int? FormatHeight { get; set; }
    public string FormatExt { get; set; } = "";
    public List<string> SubtitleLanguageCodes { get; set; } = new();
    public string? ClipStart { get; set; }
    public string? ClipEnd { get; set; }
    public string SaveDirectory { get; set; } = "";
}
```

```csharp
// src/YtDlpTool.Domain/Persistence/StateJournalEvent.cs
namespace YtDlpTool.Domain.Persistence;

public sealed class StateJournalEvent
{
    public DateTimeOffset At { get; set; }
    public StateJournalEventType Type { get; set; }
    public Guid JobId { get; set; }
    public JobSnapshot? Snapshot { get; set; }
    public double? ProgressPercent { get; set; }
    public string? FailureCode { get; set; }
}
```

- [ ] **Step 2: Extend `AppJsonContext`**

Modify `src/YtDlpTool.Domain/Persistence/AppJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    Converters = new[] { typeof(JsonStringEnumConverter) },
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(StateJournalEvent))]
[JsonSerializable(typeof(JobSnapshot))]
public partial class AppJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 3: Build**

```powershell
dotnet build src/YtDlpTool.Domain/
```
Expected: succeeds.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool.Domain/Persistence/
git commit -m "feat(domain): StateJournal types + JsonContext extension"
```

---

### Task 5.2: `StateJournal` — append-only JSONL

**Files:**
- Create: `src/YtDlpTool.Domain/Persistence/StateJournal.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Persistence/StateJournalTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/YtDlpTool.Domain.Tests/Persistence/StateJournalTests.cs
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
```

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Implement**

```csharp
// src/YtDlpTool.Domain/Persistence/StateJournal.cs
using System.Text.Json;

namespace YtDlpTool.Domain.Persistence;

public sealed class StateJournal : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public StateJournal(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
    }

    public void Append(StateJournalEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, AppJsonContext.Default.StateJournalEvent);
        lock (_gate)
        {
            _writer!.WriteLine(json);
        }
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }

    public static IEnumerable<StateJournalEvent> ReadAll(string path)
    {
        if (!File.Exists(path)) yield break;
        using var reader = new StreamReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            StateJournalEvent? evt = null;
            try { evt = JsonSerializer.Deserialize(line, AppJsonContext.Default.StateJournalEvent); }
            catch (JsonException) { continue; }
            if (evt is not null) yield return evt;
        }
    }

    public static IEnumerable<JobSnapshot> ReconstructOpenJobs(IEnumerable<StateJournalEvent> events)
    {
        var open = new Dictionary<Guid, JobSnapshot>();
        var closed = new HashSet<Guid>();
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case StateJournalEventType.JobEnqueued when e.Snapshot is not null:
                    if (!closed.Contains(e.JobId)) open[e.JobId] = e.Snapshot;
                    break;
                case StateJournalEventType.JobCompleted:
                case StateJournalEventType.JobFailed:
                case StateJournalEventType.JobCancelled:
                    closed.Add(e.JobId);
                    open.Remove(e.JobId);
                    break;
            }
        }
        return open.Values;
    }

    public static void Truncate(string path)
    {
        if (File.Exists(path)) File.WriteAllText(path, "");
    }
}
```

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~StateJournalTests"
```
Expected: 3 pass.

- [ ] **Step 5: Commit**

```powershell
git add src/YtDlpTool.Domain/Persistence/StateJournal.cs tests/YtDlpTool.Domain.Tests/Persistence/StateJournalTests.cs
git commit -m "feat(domain): StateJournal append/read/reconstruct with malformed-line skip"
```

---

### Task 5.3: `DownloadQueue` — concurrency-controlled scheduler

**Files:**
- Create: `src/YtDlpTool.Domain/Services/IDownloadExecutor.cs`
- Create: `src/YtDlpTool.Domain/Services/DownloadExecutionResult.cs`
- Create: `src/YtDlpTool.Domain/Services/DownloadQueue.cs`
- Create: `src/YtDlpTool.Domain/Services/QueueEvent.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Services/DownloadQueueTests.cs`

The Queue owns concurrency, fires events when state changes, and delegates the actual download to an `IDownloadExecutor` (the WPF app supplies a real one backed by `YtDlpRunner`; tests use a fake).

- [ ] **Step 1: Create `IDownloadExecutor`**

```csharp
// src/YtDlpTool.Domain/Services/IDownloadExecutor.cs
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public interface IDownloadExecutor
{
    Task<DownloadExecutionResult> ExecuteAsync(
        DownloadJob job,
        IProgress<DownloadProgressSnapshot> progress,
        CancellationToken cancellationToken);
}

public sealed record DownloadProgressSnapshot(double Percent, long? BytesPerSecond, TimeSpan? Eta);
```

```csharp
// src/YtDlpTool.Domain/Services/DownloadExecutionResult.cs
namespace YtDlpTool.Domain.Services;

public sealed record DownloadExecutionResult(
    bool IsSuccess,
    string? OutputFilePath,
    MappedError? Error,
    bool WasCancelled);
```

- [ ] **Step 2: Create `QueueEvent`**

```csharp
// src/YtDlpTool.Domain/Services/QueueEvent.cs
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public abstract record QueueEvent(DownloadJob Job);
public sealed record JobEnqueuedEvent(DownloadJob Job) : QueueEvent(Job);
public sealed record JobStartedEvent(DownloadJob Job) : QueueEvent(Job);
public sealed record JobProgressEvent(DownloadJob Job, DownloadProgressSnapshot Progress) : QueueEvent(Job);
public sealed record JobCompletedEvent(DownloadJob Job, string OutputFilePath) : QueueEvent(Job);
public sealed record JobFailedEvent(DownloadJob Job, MappedError Error) : QueueEvent(Job);
public sealed record JobCancelledEvent(DownloadJob Job) : QueueEvent(Job);
```

- [ ] **Step 3: Implement `DownloadQueue`**

```csharp
// src/YtDlpTool.Domain/Services/DownloadQueue.cs
using System.Collections.Concurrent;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

public sealed class DownloadQueue : IDisposable
{
    private readonly IDownloadExecutor _executor;
    private readonly Action<QueueEvent> _onEvent;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentQueue<DownloadJob> _pending = new();
    private readonly SemaphoreSlim _slot;
    private readonly CancellationTokenSource _shutdown = new();
    private int _maxConcurrency;

    public DownloadQueue(IDownloadExecutor executor, int maxConcurrency, Action<QueueEvent> onEvent)
    {
        if (maxConcurrency < 1 || maxConcurrency > 5)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "must be 1..5");
        _executor = executor;
        _onEvent = onEvent;
        _maxConcurrency = maxConcurrency;
        _slot = new SemaphoreSlim(maxConcurrency, 5);
    }

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            if (value < 1 || value > 5) throw new ArgumentOutOfRangeException(nameof(value));
            var delta = value - _maxConcurrency;
            _maxConcurrency = value;
            if (delta > 0) _slot.Release(delta);
            // Shrinking concurrency takes effect as in-flight jobs complete.
        }
    }

    public void Enqueue(DownloadJob job)
    {
        _onEvent(new JobEnqueuedEvent(job));
        _pending.Enqueue(job);
        _ = TryStartNextAsync();
    }

    public bool Cancel(Guid jobId)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        // Pending cancellation — rebuild queue without that job.
        var keep = new List<DownloadJob>();
        DownloadJob? removed = null;
        while (_pending.TryDequeue(out var j))
            if (j.Id == jobId) removed = j;
            else keep.Add(j);
        foreach (var j in keep) _pending.Enqueue(j);
        if (removed is not null)
        {
            removed.MarkCancelled();
            _onEvent(new JobCancelledEvent(removed));
            return true;
        }
        return false;
    }

    private async Task TryStartNextAsync()
    {
        if (_shutdown.IsCancellationRequested) return;
        if (!await _slot.WaitAsync(0).ConfigureAwait(false)) return;
        if (!_pending.TryDequeue(out var job)) { _slot.Release(); return; }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _running[job.Id] = cts;

        _ = RunJobAsync(job, cts);
        _ = TryStartNextAsync(); // try to fill remaining slots
    }

    private async Task RunJobAsync(DownloadJob job, CancellationTokenSource cts)
    {
        try
        {
            job.MarkDownloading();
            _onEvent(new JobStartedEvent(job));

            var progress = new Progress<DownloadProgressSnapshot>(snap =>
            {
                job.ReportProgress(snap.Percent, snap.BytesPerSecond, snap.Eta);
                _onEvent(new JobProgressEvent(job, snap));
            });

            DownloadExecutionResult result;
            try
            {
                result = await _executor.ExecuteAsync(job, progress, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new DownloadExecutionResult(false, null, null, WasCancelled: true);
            }

            if (result.WasCancelled)
            {
                job.MarkCancelled();
                _onEvent(new JobCancelledEvent(job));
            }
            else if (result.IsSuccess && result.OutputFilePath is not null)
            {
                job.MarkCompleted(result.OutputFilePath);
                _onEvent(new JobCompletedEvent(job, result.OutputFilePath));
            }
            else
            {
                var err = result.Error ?? new MappedError(ErrorCategory.UnknownError, "下載失敗", "E-UNKNOWN", false);
                job.MarkFailed(err.UserMessage, err.ErrorCode);
                _onEvent(new JobFailedEvent(job, err));
            }
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
            cts.Dispose();
            _slot.Release();
            _ = TryStartNextAsync();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var cts in _running.Values) try { cts.Cancel(); } catch { }
        _shutdown.Dispose();
        _slot.Dispose();
    }
}
```

- [ ] **Step 4: Test with a fake executor**

```csharp
// tests/YtDlpTool.Domain.Tests/Services/DownloadQueueTests.cs
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
        await WaitFor(() => fake.Completed == 1);

        Assert.Contains(events, e => e is JobEnqueuedEvent);
        Assert.Contains(events, e => e is JobStartedEvent);
        Assert.Contains(events, e => e is JobCompletedEvent);
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
        await WaitFor(() => events.Any(e => e is JobCancelledEvent));
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
        Assert.Contains(events, e => e is JobCancelledEvent ev && ev.Job.Id == pending.Id);
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
```

- [ ] **Step 5: Run tests**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~DownloadQueueTests"
```
Expected: 4 pass.

- [ ] **Step 6: Commit**

```powershell
git add src/YtDlpTool.Domain/Services/IDownloadExecutor.cs src/YtDlpTool.Domain/Services/DownloadExecutionResult.cs src/YtDlpTool.Domain/Services/QueueEvent.cs src/YtDlpTool.Domain/Services/DownloadQueue.cs tests/YtDlpTool.Domain.Tests/Services/DownloadQueueTests.cs
git commit -m "feat(domain): DownloadQueue with concurrency control & cancellation events"
```

---

### Task 5.4: Wire `StateJournal` into queue events via `JournaledQueue` adapter

**Files:**
- Create: `src/YtDlpTool.Domain/Services/JournaledQueue.cs`
- Create: `tests/YtDlpTool.Domain.Tests/Services/JournaledQueueTests.cs`

Rather than putting journal writes inside `DownloadQueue` (which would couple it to persistence), we wrap with an adapter.

- [ ] **Step 1: Write test**

```csharp
// tests/YtDlpTool.Domain.Tests/Services/JournaledQueueTests.cs
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
```

- [ ] **Step 2: Implement adapter**

```csharp
// src/YtDlpTool.Domain/Services/JournaledQueue.cs
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;

namespace YtDlpTool.Domain.Services;

public static class JournaledQueue
{
    public static Action<QueueEvent> Wrap(StateJournal journal, Action<QueueEvent> downstream)
    {
        return evt =>
        {
            journal.Append(ToJournalEvent(evt));
            downstream(evt);
        };
    }

    private static StateJournalEvent ToJournalEvent(QueueEvent evt) => evt switch
    {
        JobEnqueuedEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobEnqueued,
            JobId = e.Job.Id,
            Snapshot = ToSnapshot(e.Job)
        },
        JobStartedEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobStarted,
            JobId = e.Job.Id
        },
        JobProgressEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobProgress,
            JobId = e.Job.Id,
            ProgressPercent = e.Progress.Percent
        },
        JobCompletedEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobCompleted,
            JobId = e.Job.Id
        },
        JobFailedEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobFailed,
            JobId = e.Job.Id,
            FailureCode = e.Error.ErrorCode
        },
        JobCancelledEvent e => new StateJournalEvent
        {
            At = DateTimeOffset.UtcNow,
            Type = StateJournalEventType.JobCancelled,
            JobId = e.Job.Id
        },
        _ => throw new InvalidOperationException($"unknown event type {evt.GetType().Name}")
    };

    private static JobSnapshot ToSnapshot(DownloadJob job) => new()
    {
        Id = job.Id,
        Url = job.Url,
        Title = job.Title,
        ThumbnailUrl = job.ThumbnailUrl,
        Mode = job.Mode,
        FormatId = job.ChosenFormat.FormatId,
        FormatHeight = job.ChosenFormat.Height,
        FormatExt = job.ChosenFormat.Extension,
        SubtitleLanguageCodes = job.SubtitleLanguageCodes.ToList(),
        ClipStart = job.ClipRange is null ? null : job.ClipRange.Start.ToString(@"hh\:mm\:ss"),
        ClipEnd = job.ClipRange is null ? null : job.ClipRange.End.ToString(@"hh\:mm\:ss"),
        SaveDirectory = job.SaveDirectory
    };
}
```

- [ ] **Step 3: Run test**

```powershell
dotnet test tests/YtDlpTool.Domain.Tests/ --filter "FullyQualifiedName~JournaledQueueTests"
```
Expected: passes.

- [ ] **Step 4: Commit**

```powershell
git add src/YtDlpTool.Domain/Services/JournaledQueue.cs tests/YtDlpTool.Domain.Tests/Services/JournaledQueueTests.cs
git commit -m "feat(domain): JournaledQueue adapter persists queue events to StateJournal"
```

---

### Task 5.5: Full suite + AOT

- [ ] **Step 1: Test all**

```powershell
dotnet test
```
Expected: green.

- [ ] **Step 2: AOT publish**

```powershell
dotnet publish src/YtDlpTool/YtDlpTool.csproj -c Release -r win-x64
```
Expected: succeeds.

- [ ] **Step 3: Tag**

```powershell
git tag phase-5-queue-complete
```

---

## Phase 5 complete gate

- [ ] `StateJournal` (append, read, reconstruct, malformed skip)
- [ ] `DownloadQueue` with concurrency control, cancellation (running + pending)
- [ ] `JournaledQueue` adapter
- [ ] AOT publish green
- [ ] Tag `phase-5-queue-complete`

Proceed to Phase 6.
