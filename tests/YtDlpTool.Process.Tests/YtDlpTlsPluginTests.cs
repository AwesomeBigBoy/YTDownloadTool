using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

/// <summary>
/// The plugin is plain Python that the official yt-dlp build imports from the directory
/// passed with --plugin-dirs. yt-dlp treats every child of that directory as a plugin
/// package root and imports modules from &lt;child&gt;/yt_dlp_plugins/&lt;type&gt;/, so the
/// on-disk shape is part of the contract, not an implementation detail.
/// </summary>
public class YtDlpTlsPluginTests : IDisposable
{
    private readonly string _root;

    public YtDlpTlsPluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ytdlp-plugin-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ModulePath() => Path.Combine(_root,
        YtDlpTlsPlugin.PackageFolderName, "yt_dlp_plugins", "postprocessor", YtDlpTlsPlugin.ModuleFileName);

    [Fact]
    public void Install_WritesTheModuleWhereYtDlpLooksForPlugins()
    {
        var dir = YtDlpTlsPlugin.Install(_root, out var error);

        Assert.Null(error);
        Assert.Equal(_root, dir);
        Assert.True(File.Exists(ModulePath()), "expected " + ModulePath());
    }

    [Fact]
    public void Install_WritesTheEmbeddedSource()
    {
        YtDlpTlsPlugin.Install(_root, out _);

        var written = File.ReadAllText(ModulePath());
        Assert.Equal(YtDlpTlsPlugin.ModuleSource, written);
        // The plugin must stay inert unless the app also sets the opt-in env var.
        Assert.Contains("os.environ.get('YTDLP_RELAX_SECLEVEL') == '1'", written);
        Assert.Contains("DEFAULT@SECLEVEL=0", written);
    }

    [Fact]
    public void Install_RestoresATamperedCopy()
    {
        YtDlpTlsPlugin.Install(_root, out _);
        File.WriteAllText(ModulePath(), "import os; os.system('calc')");

        YtDlpTlsPlugin.Install(_root, out _);

        Assert.Equal(YtDlpTlsPlugin.ModuleSource, File.ReadAllText(ModulePath()));
    }

    [Fact]
    public void Install_LeavesOnlyItsOwnModuleInTheFolder()
    {
        // yt-dlp imports every package below --plugin-dirs. Anything else dropped there —
        // by hand, by an older build, by someone who can write to the app folder — was
        // imported too while the setting was on.
        var stranger = Path.Combine(_root, "someone-else", "yt_dlp_plugins", "postprocessor", "other.py");
        var extra = Path.Combine(_root, YtDlpTlsPlugin.PackageFolderName, "yt_dlp_plugins", "extractor", "extra.py");
        foreach (var path in new[] { stranger, extra })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "print('hello')");
        }

        Assert.NotNull(YtDlpTlsPlugin.Install(_root, out var error));

        Assert.Equal(new[] { ModulePath() }, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Null(error);
    }

    // A directory junction: no elevation needed, and what "mklink /J" makes is what a
    // co-user of a shared folder could plant.
    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using (var p = System.Diagnostics.Process.Start(psi)!) p.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create a junction");
    }

    private static string Outside()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ytdlp-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outside, "sub"));
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "x");
        File.WriteAllText(Path.Combine(outside, "sub", "also-precious.txt"), "x");
        return outside;
    }

    [Fact]
    public void Install_NeverDeletesThroughALinkInThePluginFolder()
    {
        // The folder sits next to the exe. On a shared folder another user could put a link
        // there (ytdlp-plugins\x pointing at someone's Documents); the clean-up walked into it.
        var outside = Outside();
        Directory.CreateDirectory(_root);
        Junction(Path.Combine(_root, "x"), outside);
        try
        {
            Assert.NotNull(YtDlpTlsPlugin.Install(_root, out var error));

            Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
            Assert.True(File.Exists(Path.Combine(outside, "sub", "also-precious.txt")));
            Assert.False(Directory.Exists(Path.Combine(_root, "x")));
            Assert.Equal(new[] { ModulePath() }, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Install_WhenThePluginFolderItselfIsALink_LeavesWhatItPointsAt()
    {
        var outside = Outside();
        Junction(_root, outside);
        try
        {
            Assert.NotNull(YtDlpTlsPlugin.Install(_root, out _));

            Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
            Assert.True(File.Exists(Path.Combine(outside, "sub", "also-precious.txt")));
            Assert.False(new DirectoryInfo(_root).Attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert.True(File.Exists(ModulePath()));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Remove_WhenThePluginFolderIsALink_RemovesOnlyTheLink()
    {
        var outside = Outside();
        Junction(_root, outside);
        try
        {
            YtDlpTlsPlugin.Remove(_root, out _);

            Assert.False(Directory.Exists(_root));
            Assert.True(File.Exists(Path.Combine(outside, "sub", "also-precious.txt")));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Remove_DeletesTheWholeFolder()
    {
        // The setting is off: nothing of the plugin stays on disk.
        YtDlpTlsPlugin.Install(_root, out _);

        Assert.True(YtDlpTlsPlugin.Remove(_root, out var error), error);

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Remove_WhenThereIsNothing_IsQuiet()
    {
        Assert.False(YtDlpTlsPlugin.Remove(_root, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Install_IntoAnUnusablePath_ReturnsNullInsteadOfThrowing()
    {
        // A file where the directory should be: CreateDirectory fails. Startup must
        // carry on — the caller then simply runs without the plugin.
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "not a directory");
        try
        {
            var dir = YtDlpTlsPlugin.Install(_root, out var error);

            Assert.Null(dir);
            Assert.False(string.IsNullOrEmpty(error));
        }
        finally { File.Delete(_root); }
    }
}
