using YtDlpTool.Domain.Logging;

namespace YtDlpTool.Domain.Tests.Logging;

public class AppLoggerTests : IDisposable
{
    private readonly string _dir;

    public AppLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ytdlp-log-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Log_WritesEntryToTodaysFile()
    {
        using var log = new AppLogger(_dir, LogLevel.Info, () => DateTime.Parse("2026-05-14T12:00:00"));
        log.Info("download_started", new Dictionary<string, string> { ["mode"] = "AudioOnly" });
        log.Flush();
        var file = Path.Combine(_dir, "2026-05-14.log");
        Assert.True(File.Exists(file));
        var content = File.ReadAllText(file);
        Assert.Contains("INFO", content);
        Assert.Contains("download_started", content);
        Assert.Contains("AudioOnly", content);
    }

    [Fact]
    public void Log_RespectsLevel()
    {
        using var log = new AppLogger(_dir, LogLevel.Warn, () => DateTime.UtcNow);
        log.Debug("debug_event", null);
        log.Info("info_event", null);
        log.Warn("warn_event", null);
        log.Flush();
        var content = File.ReadAllText(Directory.GetFiles(_dir, "*.log").Single());
        Assert.DoesNotContain("debug_event", content);
        Assert.DoesNotContain("info_event", content);
        Assert.Contains("warn_event", content);
    }

    [Fact]
    public void HashSuffix_SameInputSameOutput()
    {
        var a = AppLogger.HashSuffix("https://youtu.be/dQw4w9WgXcQ");
        var b = AppLogger.HashSuffix("https://youtu.be/dQw4w9WgXcQ");
        Assert.Equal(a, b);
        Assert.Equal(8, a.Length);
    }

    [Fact]
    public void PurgeOlderThan_RemovesOldFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "2020-01-01.log"), "old");
        File.WriteAllText(Path.Combine(_dir, "2099-01-01.log"), "future");
        AppLogger.PurgeOlderThan(_dir, TimeSpan.FromDays(7), DateTime.Parse("2026-05-14T00:00:00"));
        Assert.False(File.Exists(Path.Combine(_dir, "2020-01-01.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "2099-01-01.log")));
    }
}
