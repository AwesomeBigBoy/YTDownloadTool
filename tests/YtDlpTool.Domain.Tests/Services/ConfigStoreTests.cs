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
    public void SaveThenLoad_RemembersTheLastFormatAndQuality()
    {
        // v1.5.0: the 格式 and 畫質 used for the last download are preselected next time.
        var store = new ConfigStore(_path);
        var cfg = AppConfig.CreateDefault();
        cfg.LastMode = DownloadMode.AudioOnly;
        cfg.LastVideoQuality = "720p";
        cfg.LastAudioQuality = "MP3";
        store.Save(cfg);

        var loaded = store.Load();
        Assert.Equal(DownloadMode.AudioOnly, loaded.LastMode);
        Assert.Equal("720p", loaded.LastVideoQuality);
        Assert.Equal("MP3", loaded.LastAudioQuality);
    }

    [Fact]
    public void Default_RemembersNothingYet()
    {
        var cfg = new ConfigStore(_path).Load();
        Assert.Null(cfg.LastMode);
        Assert.Null(cfg.LastVideoQuality);
        Assert.Null(cfg.LastAudioQuality);
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
    public void Save_WhileAnotherSaveIsWritingItsTempFile_StillSaves()
    {
        // The UI (the remembered 格式/畫質) and the background update check both save. With
        // one shared temp file name the second save hit the first one's open file and was lost.
        var store = new ConfigStore(_path);
        var cfg = AppConfig.CreateDefault();
        cfg.LastMode = DownloadMode.AudioOnly;
        using (new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            store.Save(cfg);
        }
        Assert.Equal(DownloadMode.AudioOnly, store.Load().LastMode);
    }

    [Fact]
    public async Task Save_FromSeveralThreadsAtOnce_NeverThrowsAndLeavesNoTempFiles()
    {
        var store = new ConfigStore(_path);
        var audio = AppConfig.CreateDefault();
        audio.LastMode = DownloadMode.AudioOnly;
        var video = AppConfig.CreateDefault();
        video.LastMode = DownloadMode.VideoOnly;

        // Plain pool threads released together (Parallel.For runs on the test framework's
        // scheduler here); each saves five times.
        using var start = new Barrier(4);
        var savers = Enumerable.Range(0, 4).Select(n => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 5; i++) store.Save(n % 2 == 0 ? audio : video);
        })).ToArray();
        // Throws TimeoutException when the saves do not finish.
        await Task.WhenAll(savers).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(store.Load().LastMode, new DownloadMode?[] { DownloadMode.AudioOnly, DownloadMode.VideoOnly });
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
    }

    [Fact]
    public void Save_WritesTheFileInPlace_NeverRenamesANewOneOverIt()
    {
        // On PCs with endpoint security a rename over an existing file is held ~3 s
        // (File.Replace: ~6 s; measured 2026-11-04 in %TEMP% and next to the exe), and the
        // remembered 格式/畫質 is saved on the UI thread at every 下載. The same file id
        // before and after the save means it was written in place.
        var store = new ConfigStore(_path);
        store.Save(AppConfig.CreateDefault());
        var before = FileId(_path);

        var cfg = AppConfig.CreateDefault();
        cfg.LastMode = DownloadMode.AudioOnly;
        store.Save(cfg);

        Assert.Equal(before, FileId(_path));
        Assert.Equal(DownloadMode.AudioOnly, store.Load().LastMode);
    }

    [Fact]
    public void Load_ConfigCutShortByACrash_FallsBackToTheCopyWrittenFirst()
    {
        var store = new ConfigStore(_path);
        var cfg = AppConfig.CreateDefault();
        cfg.ConcurrentDownloads = 4;
        store.Save(cfg);

        File.WriteAllText(_path, "{ \"ConcurrentDown");   // the write a crash cut short

        Assert.Equal(4, store.Load().ConcurrentDownloads);
    }

    private static (uint Volume, ulong Index) FileId(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.True(GetFileInformationByHandle(fs.SafeFileHandle, out var info));
        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        out ByHandleFileInformation info);

    [Fact]
    public void Load_CorruptFile_ReturnsDefault()
    {
        File.WriteAllText(_path, "{ not valid json");
        var store = new ConfigStore(_path);
        var cfg = store.Load();
        Assert.Equal(2, cfg.ConcurrentDownloads);
    }
}
