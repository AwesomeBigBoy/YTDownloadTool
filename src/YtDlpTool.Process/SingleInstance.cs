using System.Security.Cryptography;
using System.Text;

namespace YtDlpTool.Process;

/// <summary>
/// v1.5.2: one running copy per portable folder. A second launch from the same folder used to
/// die in AppHost's constructor on state.log, which the first copy holds, with the native
/// 「程式啟動失敗」 box. App.OnStartup takes this first and, when another copy has it, brings
/// that copy's window to the front instead. Copies in different folders are different
/// installs and run side by side.
///
/// A restart (App.RestartApplication) starts the new copy while the old one is still closing,
/// so it passes <see cref="RestartedArgument"/> and the new copy waits for the old one to let
/// go, which it does only after AppHost has closed its files. Later versions must keep
/// honouring the argument: it is how this version hands over to them after an update.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string RestartedArgument = "--restarted";

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// The same name for any spelling of the folder (case, a trailing separator, "."), in the
    /// session's namespace. Hashed, because a name may hold no backslash after the namespace.
    /// </summary>
    public static string MutexName(string appDirectory)
    {
        var folder = Path.GetFullPath(appDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(folder));
        return @"Local\YtDlpTool-" + Convert.ToHexString(hash, 0, 16);
    }

    /// <summary>
    /// Owns the folder for this process, waiting up to <paramref name="wait"/> for a copy that
    /// is closing. Null when another copy keeps it. Dispose on the thread that acquired it.
    /// </summary>
    public static SingleInstance? TryAcquire(string appDirectory, TimeSpan wait)
    {
        var mutex = new Mutex(false, MutexName(appDirectory));
        try
        {
            if (mutex.WaitOne(wait)) return new SingleInstance(mutex);
        }
        catch (AbandonedMutexException)
        {
            // The copy that held it ended without letting go (killed, crashed): ours now.
            return new SingleInstance(mutex);
        }
        mutex.Dispose();
        return null;
    }

    public static bool IsRestart(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, RestartedArgument, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* owned by another thread: the OS releases it at exit */ }
        _mutex.Dispose();
    }
}
