using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class ConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public ConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ytdlp-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var store = new ConfigStore(_path);
        var cfg = store.Load();
        Assert.Equal(2, cfg.ConcurrentDownloads);
        Assert.Equal("zh-TW", cfg.LanguageCode);
    }

    [Fact]
    public void SaveThenLoad_Roundtrips()
    {
        var store = new ConfigStore(_path);
        var cfg = AppConfig.CreateDefault();
        cfg.ConcurrentDownloads = 4;
        cfg.Theme = ThemePreference.Dark;
        store.Save(cfg);

        var loaded = store.Load();
        Assert.Equal(4, loaded.ConcurrentDownloads);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
    }

    [Fact]
    public void Save_AtomicViaTempFile()
    {
        var store = new ConfigStore(_path);
        var cfg = AppConfig.CreateDefault();
        store.Save(cfg);
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefault()
    {
        File.WriteAllText(_path, "{ not valid json");
        var store = new ConfigStore(_path);
        var cfg = store.Load();
        Assert.Equal(2, cfg.ConcurrentDownloads);
    }
}
