namespace YtDlpTool.Domain.Services;

/// <summary>
/// v1.5.0: an <see cref="IProgress{T}"/> that runs its handler on the reporting thread, at
/// once and in order. <see cref="Progress{T}"/> posts every report to the
/// SynchronizationContext it was created on — for a download queued from the UI, the UI
/// thread, once per yt-dlp output line and before the UI's throttle could drop it — and,
/// without one, to the thread pool in no particular order (the order the watchdog's
/// waiting-for-the-user flag depends on). Handlers must be thread-safe.
/// </summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
