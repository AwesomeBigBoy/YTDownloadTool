using System.IO.Compression;
using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

public class YtDlpLayoutTests : IDisposable
{
    private readonly string _bin;

    public YtDlpLayoutTests()
    {
        _bin = Path.Combine(Path.GetTempPath(), "ytdlplayout-" + Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(_bin);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_bin)!, recursive: true); } catch { }
    }

    private string MakePackage(params string[] entryNames)
    {
        var src = Path.Combine(Path.GetTempPath(), "ytdlppkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(src, "_internal"));
        foreach (var n in entryNames)
        {
            var p = Path.Combine(src, n);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "payload:" + n);
        }
        var zip = YtDlpLayout.PackagePath(_bin);
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(src, zip);
        Directory.Delete(src, recursive: true);
        return zip;
    }

    // ── ResolveExecutable ───────────────────────────────────────────────────────

    [Fact]
    public void Resolve_PrefersDirectoryBuildOverLegacyExe()
    {
        Directory.CreateDirectory(Path.Combine(_bin, YtDlpLayout.DirectoryName));
        File.WriteAllText(YtDlpLayout.DirectoryExePath(_bin), "dir");
        File.WriteAllText(YtDlpLayout.LegacyExePath(_bin), "legacy");

        Assert.Equal(YtDlpLayout.DirectoryExePath(_bin), YtDlpLayout.ResolveExecutable(_bin));
    }

    [Fact]
    public void Resolve_FallsBackToLegacyExe()
    {
        // The v1.3.8 -> v1.4.0 transition: the new app binary is installed but the
        // package has not been expanded yet. The old exe must still be used, or the
        // update leaves the user with no working yt-dlp at all.
        File.WriteAllText(YtDlpLayout.LegacyExePath(_bin), "legacy");

        Assert.Equal(YtDlpLayout.LegacyExePath(_bin), YtDlpLayout.ResolveExecutable(_bin));
    }

    [Fact]
    public void Resolve_WithNeitherPresent_NamesTheDirectoryBuild()
    {
        Assert.Equal(YtDlpLayout.DirectoryExePath(_bin), YtDlpLayout.ResolveExecutable(_bin));
    }

    // ── ExpandPackageIfPresent ──────────────────────────────────────────────────

    [Fact]
    public void Expand_NoPackage_IsANoOp()
    {
        Assert.Equal(ExpandOutcome.NothingToDo, YtDlpLayout.ExpandPackageIfPresent(_bin, out var err));
        Assert.Null(err);
    }

    [Fact]
    public void Expand_UnpacksDeletesPackageAndBecomesResolvable()
    {
        MakePackage("yt-dlp.exe", "_internal/python313.dll");

        Assert.Equal(ExpandOutcome.Expanded, YtDlpLayout.ExpandPackageIfPresent(_bin, out var err));
        Assert.Null(err);

        Assert.True(File.Exists(YtDlpLayout.DirectoryExePath(_bin)));
        Assert.True(File.Exists(Path.Combine(_bin, YtDlpLayout.DirectoryName, "_internal", "python313.dll")));
        Assert.False(File.Exists(YtDlpLayout.PackagePath(_bin)));
        Assert.Equal(YtDlpLayout.DirectoryExePath(_bin), YtDlpLayout.ResolveExecutable(_bin));
    }

    [Fact]
    public void Expand_IsIdempotentAcrossLaunches()
    {
        MakePackage("yt-dlp.exe");
        Assert.Equal(ExpandOutcome.Expanded, YtDlpLayout.ExpandPackageIfPresent(_bin, out _));
        // Every subsequent launch calls this again; it must stay cheap and silent.
        Assert.Equal(ExpandOutcome.NothingToDo, YtDlpLayout.ExpandPackageIfPresent(_bin, out _));
        Assert.True(File.Exists(YtDlpLayout.DirectoryExePath(_bin)));
    }

    [Fact]
    public void Expand_ReplacesAnExistingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_bin, YtDlpLayout.DirectoryName));
        File.WriteAllText(YtDlpLayout.DirectoryExePath(_bin), "old-version");
        File.WriteAllText(Path.Combine(_bin, YtDlpLayout.DirectoryName, "stale.dll"), "stale");

        MakePackage("yt-dlp.exe");
        Assert.Equal(ExpandOutcome.Expanded, YtDlpLayout.ExpandPackageIfPresent(_bin, out _));

        Assert.Equal("payload:yt-dlp.exe", File.ReadAllText(YtDlpLayout.DirectoryExePath(_bin)));
        // A stale file from the previous version must not survive into the new one.
        Assert.False(File.Exists(Path.Combine(_bin, YtDlpLayout.DirectoryName, "stale.dll")));
    }

    [Fact]
    public void Expand_PackageWithoutTheExe_IsRejectedAndLeavesInstallWorking()
    {
        Directory.CreateDirectory(Path.Combine(_bin, YtDlpLayout.DirectoryName));
        File.WriteAllText(YtDlpLayout.DirectoryExePath(_bin), "working-version");
        MakePackage("_internal/only-junk.dll");

        Assert.Equal(ExpandOutcome.Failed, YtDlpLayout.ExpandPackageIfPresent(_bin, out var err));
        Assert.NotNull(err);

        // The working install must be untouched — swapping in a payload with no
        // executable would break a user who currently has one.
        Assert.Equal("working-version", File.ReadAllText(YtDlpLayout.DirectoryExePath(_bin)));
    }

    [Fact]
    public void Expand_CorruptPackage_KeepsLegacyExeUsable()
    {
        File.WriteAllText(YtDlpLayout.LegacyExePath(_bin), "legacy");
        File.WriteAllText(YtDlpLayout.PackagePath(_bin), "this is not a zip");

        Assert.Equal(ExpandOutcome.Failed, YtDlpLayout.ExpandPackageIfPresent(_bin, out var err));
        Assert.NotNull(err);
        Assert.Equal(YtDlpLayout.LegacyExePath(_bin), YtDlpLayout.ResolveExecutable(_bin));
    }

    [Fact]
    public void Expand_LeavesNoStagingDirectoryBehind()
    {
        MakePackage("yt-dlp.exe");
        YtDlpLayout.ExpandPackageIfPresent(_bin, out _);

        Assert.False(Directory.Exists(Path.Combine(_bin, YtDlpLayout.DirectoryName + ".new")));
        Assert.False(Directory.Exists(Path.Combine(_bin, YtDlpLayout.DirectoryName + ".old")));
    }
}
