using YtDlpTool.Domain.Models;
using YtDlpTool.Process;
using YtDlpTool.Process.Tests.Helpers;

namespace YtDlpTool.Process.Tests;

/// <summary>
/// v1.5.0: yt-dlp on its own reads %APPDATA%\yt-dlp\config.txt, a yt-dlp.conf next to the
/// exe, and plugins from the user's plugin folders — whatever someone who also uses the
/// yt-dlp CLI left on the PC (the development machine has such a config). The app passes
/// everything it needs on the command line, so nothing else may change its behaviour:
/// a stray --download-archive would silently skip downloads, --paths would move files.
/// </summary>
public class YtDlpRunnerConfigIsolationTests : IDisposable
{
    private readonly string _root;

    public YtDlpRunnerConfigIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ytdlp-iso-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DownloadRequest Request(string stem) => new(
        Url: "https://www.youtube.com/watch?v=FAKE0001234",
        Mode: DownloadMode.VideoOnly,
        ChosenFormat: new VideoFormat("299", 1080, "avc1", null, "mp4", 120_000_000, null),
        SubtitleLanguageCodes: Array.Empty<string>(),
        ClipRange: null,
        SaveDirectory: _root,
        SanitizedFileStem: stem);

    private static void AssertIsolated(string[] args)
    {
        var end = Array.IndexOf(args, "--");
        foreach (var flag in new[] { "--ignore-config", "--no-plugin-dirs" })
        {
            var i = Array.IndexOf(args, flag);
            Assert.True(i >= 0, $"{flag} missing: " + string.Join(' ', args));
            Assert.True(i < end, $"{flag} must precede the -- marker");
        }
    }

    [Fact]
    public async Task Download_IgnoresUserConfigAndPluginFolders()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());

        var result = await runner.DownloadAsync(Request("isolated"));

        Assert.True(result.IsSuccess, result.ErrorStderr);
        AssertIsolated(await File.ReadAllLinesAsync(result.OutputFilePath + ".args"));
    }

    [Fact]
    public async Task Subtitles_IgnoreUserConfigAndPluginFolders()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());

        var result = await runner.DownloadSubtitlesOnlyAsync(
            "https://www.youtube.com/watch?v=FAKE0001234", new[] { "en" }, _root, "subs");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        AssertIsolated(await File.ReadAllLinesAsync(result.SubtitleFilePaths[0] + ".args"));
    }

    [Fact]
    public async Task TlsPlugin_IsAddedAfterThePluginFoldersAreCleared()
    {
        // --no-plugin-dirs clears every plugin folder given BEFORE it, so ours must follow.
        var pluginDir = YtDlpTlsPlugin.Install(Path.Combine(_root, "plugins"), out var error);
        Assert.True(pluginDir is not null, error);
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path(),
            allowUntrustedCerts: true, tlsPluginDirectory: pluginDir);

        var result = await runner.DownloadAsync(Request("isolated_tls"));

        Assert.True(result.IsSuccess, result.ErrorStderr);
        var args = await File.ReadAllLinesAsync(result.OutputFilePath + ".args");
        Assert.True(Array.IndexOf(args, "--no-plugin-dirs") is >= 0 and var clear &&
                    Array.IndexOf(args, "--plugin-dirs") > clear,
            "--plugin-dirs must come after --no-plugin-dirs: " + string.Join(' ', args));
    }
}
