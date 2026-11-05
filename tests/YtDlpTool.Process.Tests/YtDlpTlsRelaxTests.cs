using System.Reflection;
using YtDlpTool.Domain.Models;
using YtDlpTool.Process;
using YtDlpTool.Process.Tests.Helpers;

namespace YtDlpTool.Process.Tests;

/// <summary>
/// v1.5.0: yt-dlp ships as upstream's official build, byte-for-byte, so the
/// AllowUntrustedCertificates workaround can no longer live inside the exe. It is now
/// --no-check-certificates plus a yt-dlp plugin the app writes to disk (YtDlpTlsPlugin),
/// and both have to reach EVERY yt-dlp invocation.
///
/// Before v1.5.0 the download path never passed --no-check-certificates at all; it only
/// worked behind HTTPS-inspecting proxies because the patched exe disabled verification
/// by itself. With the official build that safety net is gone, so these tests pin the
/// flag on the download path too.
/// </summary>
public class YtDlpTlsRelaxTests : IDisposable
{
    private readonly string _root;

    public YtDlpTlsRelaxTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ytdlp-tls-" + Guid.NewGuid().ToString("N"));
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

    private string InstallPlugin()
    {
        var dir = YtDlpTlsPlugin.Install(Path.Combine(_root, "plugins"), out var error);
        Assert.True(dir is not null, error);
        return dir!;
    }

    private static void AssertPluginDirBeforeUrl(string[] args, string pluginDir)
    {
        var i = Array.IndexOf(args, "--plugin-dirs");
        Assert.True(i >= 0 && i + 1 < args.Length, "--plugin-dirs missing: " + string.Join(' ', args));
        Assert.Equal(pluginDir, args[i + 1]);
        // Anything after the "--" end-of-options marker is a URL to yt-dlp.
        Assert.True(i < Array.IndexOf(args, "--"), "--plugin-dirs must precede the -- marker");
    }

    private static string[] RelaxArgs(YtDlpRunner runner)
    {
        var m = typeof(YtDlpRunner).GetMethod("BuildTlsRelaxArgs",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((IEnumerable<string>)m.Invoke(runner, null)!).ToArray();
    }

    [Fact]
    public async Task Download_WithRelaxOn_PassesNoCheckCertificatesAndPluginDir()
    {
        var pluginDir = InstallPlugin();
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path(),
            allowUntrustedCerts: true, tlsPluginDirectory: pluginDir);

        var result = await runner.DownloadAsync(Request("relax_on"));

        Assert.True(result.IsSuccess, result.ErrorStderr);
        var args = await File.ReadAllLinesAsync(result.OutputFilePath + ".args");
        Assert.Contains("--no-check-certificates", args);
        AssertPluginDirBeforeUrl(args, pluginDir);
    }

    [Fact]
    public async Task Download_WithRelaxOff_RunsYtDlpUnmodified()
    {
        // The default for almost every user: the official build must run exactly as
        // upstream ships it — no plugin, no relaxed verification.
        var pluginDir = InstallPlugin();
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path(),
            allowUntrustedCerts: false, tlsPluginDirectory: pluginDir);

        var result = await runner.DownloadAsync(Request("relax_off"));

        Assert.True(result.IsSuccess, result.ErrorStderr);
        var args = await File.ReadAllLinesAsync(result.OutputFilePath + ".args");
        Assert.DoesNotContain("--no-check-certificates", args);
        Assert.DoesNotContain("--plugin-dirs", args);
    }

    [Fact]
    public async Task Subtitles_WithRelaxOn_PassesPluginDir()
    {
        var pluginDir = InstallPlugin();
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path(),
            allowUntrustedCerts: true, tlsPluginDirectory: pluginDir);

        var result = await runner.DownloadSubtitlesOnlyAsync(
            "https://www.youtube.com/watch?v=FAKE0001234", new[] { "en" }, _root, "subs");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var args = await File.ReadAllLinesAsync(result.SubtitleFilePaths[0] + ".args");
        Assert.Contains("--no-check-certificates", args);
        AssertPluginDirBeforeUrl(args, pluginDir);
    }

    [Fact]
    public void RelaxArgs_Off_AreEmpty()
    {
        // FetchMetadataAsync builds its argv from the same helper; the fake exe has no
        // argv side channel on the metadata path, so the helper is pinned directly.
        var runner = new YtDlpRunner("yt-dlp.exe", allowUntrustedCerts: false, tlsPluginDirectory: InstallPlugin());
        Assert.Empty(RelaxArgs(runner));
    }

    [Fact]
    public void RelaxArgs_On_ContainFlagAndPluginDir()
    {
        var pluginDir = InstallPlugin();
        var runner = new YtDlpRunner("yt-dlp.exe", allowUntrustedCerts: true, tlsPluginDirectory: pluginDir);
        Assert.Equal(new[] { "--no-check-certificates", "--plugin-dirs", pluginDir }, RelaxArgs(runner));
    }

    [Fact]
    public void RelaxArgs_On_WithMissingPluginDir_KeepsTheFlagButNotTheDir()
    {
        // yt-dlp raises "Invalid plugin directory" and exits for a --plugin-dirs path that
        // does not exist, which would turn a failed plugin write into a dead app. Without
        // the plugin the user still gets the CLI-level relaxation.
        var runner = new YtDlpRunner("yt-dlp.exe", allowUntrustedCerts: true,
            tlsPluginDirectory: Path.Combine(_root, "does-not-exist"));
        Assert.Equal(new[] { "--no-check-certificates" }, RelaxArgs(runner));
    }
}
