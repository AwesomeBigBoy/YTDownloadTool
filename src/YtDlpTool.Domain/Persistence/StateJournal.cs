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

    /// <summary>
    /// Best effort, never throws: since v1.5.0 progress events arrive on yt-dlp's stdout
    /// reader thread, where an exception ends the process — after Dispose (yt-dlp prints
    /// through its kill grace while the app closes) or on a disk error.
    /// </summary>
    public void Append(StateJournalEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, JournalJsonContext.Default.StateJournalEvent);
        lock (_gate)
        {
            if (_writer is null) return;
            try { _writer.WriteLine(json); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
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
            try { evt = JsonSerializer.Deserialize(line, JournalJsonContext.Default.StateJournalEvent); }
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
                case StateJournalEventType.LinkPending when e.Snapshot is not null:
                    if (!closed.Contains(e.JobId)) open[e.JobId] = e.Snapshot;
                    break;
                case StateJournalEventType.JobCompleted:
                case StateJournalEventType.JobFailed:
                case StateJournalEventType.JobCancelled:
                case StateJournalEventType.LinkSettled:
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

    public IReadOnlyList<StateJournalEvent> ReadSnapshotAndClear(string path)
    {
        lock (_gate)
        {
            _writer?.Flush();
            var events = ReadAll(path).ToList();
            _writer?.Dispose();
            File.WriteAllText(path, "");
            _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            return events;
        }
    }
}
