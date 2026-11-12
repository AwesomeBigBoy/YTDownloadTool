using System.Diagnostics;
using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

/// <summary>
/// v1.5.2: one running copy per portable folder (App.OnStartup). A second launch from the
/// same folder died in AppHost's constructor on state.log, which the first copy holds, with
/// the native 「程式啟動失敗」 box. A mutex is owned per thread, so a thread of its own stands
/// in for the other copy's process.
/// </summary>
public class SingleInstanceTests
{
    private static string NewFolder() =>
        Path.Combine(Path.GetTempPath(), "ytdlp-one-" + Guid.NewGuid().ToString("N"));

    private static bool CanAcquireElsewhere(string folder, TimeSpan wait)
    {
        var acquired = false;
        var other = new Thread(() =>
        {
            using var copy = SingleInstance.TryAcquire(folder, wait);
            acquired = copy is not null;
        });
        other.Start();
        other.Join();
        return acquired;
    }

    [Fact]
    public void MutexName_IsTheSameForAnySpellingOfTheFolder()
    {
        var name = SingleInstance.MutexName(@"C:\Users\u\Desktop\YtDlpTool-v1.4.0-win-x64");

        Assert.Equal(name, SingleInstance.MutexName(@"c:\users\U\desktop\ytdlptool-v1.4.0-win-x64\"));
        Assert.Equal(name, SingleInstance.MutexName(@"C:\Users\u\Desktop\.\YtDlpTool-v1.4.0-win-x64"));
        Assert.StartsWith(@"Local\", name);
        // A mutex name may hold no backslash after its namespace.
        Assert.DoesNotContain('\\', name[@"Local\".Length..]);
    }

    [Fact]
    public void MutexName_DiffersForAnotherFolder()
    {
        Assert.NotEqual(SingleInstance.MutexName(@"C:\Apps\YtDlpTool"),
                        SingleInstance.MutexName(@"C:\Apps\YtDlpTool2"));
    }

    [Fact]
    public void TryAcquire_WhileAnotherCopyRunsFromTheFolder_Fails()
    {
        var folder = NewFolder();
        using var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero);
        Assert.NotNull(first);

        Assert.False(CanAcquireElsewhere(folder, TimeSpan.Zero));
    }

    [Fact]
    public void TryAcquire_AfterTheFirstCopyClosed_Succeeds()
    {
        var folder = NewFolder();
        var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero);
        Assert.NotNull(first);
        first!.Dispose();

        Assert.True(CanAcquireElsewhere(folder, TimeSpan.Zero));
    }

    [Fact]
    public void TryAcquire_WithAWait_TakesOverFromACopyThatIsClosing()
    {
        // RestartApplication starts the new copy before the old one has closed. The new one
        // waits (--restarted) instead of taking the old one for a second launch.
        var folder = NewFolder();
        using var held = new ManualResetEventSlim();
        using var close = new ManualResetEventSlim();
        var oldHeldIt = false;
        var closing = new Thread(() =>
        {
            using var old = SingleInstance.TryAcquire(folder, TimeSpan.Zero);
            oldHeldIt = old is not null;
            held.Set();
            close.Wait();
        });
        closing.Start();
        held.Wait();

        _ = Task.Delay(300).ContinueWith(_ => close.Set());
        var waited = Stopwatch.StartNew();
        var tookOver = CanAcquireElsewhere(folder, TimeSpan.FromSeconds(10));
        waited.Stop();
        closing.Join();

        Assert.True(oldHeldIt, "the closing copy never held the folder");
        Assert.True(tookOver);
        Assert.True(waited.ElapsedMilliseconds >= 200, $"took over after {waited.ElapsedMilliseconds} ms, before the old copy let go");
    }

    [Fact]
    public void TryAcquire_WhenTheCopyHoldingItDied_Succeeds()
    {
        var folder = NewFolder();
        // Never disposed: the thread ends while owning it, as a killed process would.
        var died = new Thread(() => SingleInstance.TryAcquire(folder, TimeSpan.Zero));
        died.Start();
        died.Join();

        Assert.True(CanAcquireElsewhere(folder, TimeSpan.Zero));
    }

    [Fact]
    public void TryAcquire_CopiesInDifferentFolders_RunSideBySide()
    {
        using var first = SingleInstance.TryAcquire(NewFolder(), TimeSpan.Zero);
        Assert.NotNull(first);

        Assert.True(CanAcquireElsewhere(NewFolder(), TimeSpan.Zero));
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--restarted" }, true)]
    [InlineData(new[] { "https://youtu.be/jNQXAC9IVRw" }, false)]
    public void IsRestart_ReadsTheHandoverArgument(string[] args, bool expected)
    {
        Assert.Equal(expected, SingleInstance.IsRestart(args));
    }
}
