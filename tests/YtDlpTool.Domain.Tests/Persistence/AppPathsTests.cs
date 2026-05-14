using YtDlpTool.Domain.Persistence;

namespace YtDlpTool.Domain.Tests.Persistence;

public class AppPathsTests
{
    [Fact]
    public void DataRoot_WritableAppDir_UsesAppDir()
    {
        var tempApp = Path.Combine(Path.GetTempPath(), "ytdlp-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempApp);
        try
        {
            var paths = AppPaths.ResolveForAppDirectory(tempApp);
            Assert.Equal(tempApp, paths.AppDirectory);
            Assert.Equal(tempApp, paths.DataRoot);
            Assert.Equal(Path.Combine(tempApp, "config.json"), paths.ConfigFile);
            Assert.Equal(Path.Combine(tempApp, "logs"), paths.LogsDirectory);
            Assert.Equal(Path.Combine(tempApp, "state.log"), paths.StateLog);
            Assert.Equal(Path.Combine(tempApp, ".update"), paths.UpdateStaging);
            Assert.Equal(Path.Combine(tempApp, "bin"), paths.BinDirectory);
        }
        finally { Directory.Delete(tempApp, recursive: true); }
    }

    [Fact]
    public void DataRoot_ReadOnlyAppDir_UsesLocalAppData()
    {
        var paths = AppPaths.ResolveForAppDirectory(
            appDir: @"C:\Some\ReadOnly\Path",
            isWritable: _ => false);
        var expectedShadow = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YtDlpTool");
        Assert.Equal(expectedShadow, paths.DataRoot);
        Assert.Equal(Path.Combine(expectedShadow, "config.json"), paths.ConfigFile);
        Assert.Equal(@"C:\Some\ReadOnly\Path\bin", paths.BinDirectory);
    }
}
