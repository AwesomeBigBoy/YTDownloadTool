using System.Diagnostics;
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

    [Fact]
    public void Expand_WhenThePackageCannotBeDeletedAfterTheSwap_StillReportsExpanded()
    {
        // v1.5.0 replaces every install's yt-dlp in one go, on machines where antivirus
        // routinely holds freshly written files open. Once the new directory is in place
        // the update has succeeded; failing to tidy up the package afterwards must not be
        // reported (and logged) as a failed expansion. The package is simply removed on a
        // later launch.
        Directory.CreateDirectory(Path.Combine(_bin, YtDlpLayout.DirectoryName));
        File.WriteAllText(YtDlpLayout.DirectoryExePath(_bin), "old-version");
        var package = MakePackage("yt-dlp.exe");

        ExpandOutcome outcome;
        // Readers may share it (so extraction works) but nobody may delete it.
        using (new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            outcome = YtDlpLayout.ExpandPackageIfPresent(_bin, out _);
        }

        Assert.Equal(ExpandOutcome.Expanded, outcome);
        Assert.Equal("payload:yt-dlp.exe", File.ReadAllText(YtDlpLayout.DirectoryExePath(_bin)));

        // Next launch: the leftover package is removed — without expanding and swapping the
        // whole build again (v1.5.0; it used to, on every launch while it stayed).
        var marker = Path.Combine(_bin, YtDlpLayout.DirectoryName, "still-the-same-directory.txt");
        File.WriteAllText(marker, "x");
        Assert.Equal(ExpandOutcome.NothingToDo, YtDlpLayout.ExpandPackageIfPresent(_bin, out _));
        Assert.False(File.Exists(package));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Expand_AReadOnlyPackage_IsRemovedAfterTheSwap()
    {
        // A package copied from a read-only share or unpacked by a tool that keeps the
        // attribute could never be deleted, so every launch expanded it again.
        var package = MakePackage("yt-dlp.exe");
        File.SetAttributes(package, FileAttributes.ReadOnly);

        Assert.Equal(ExpandOutcome.Expanded, YtDlpLayout.ExpandPackageIfPresent(_bin, out var error));

        Assert.False(File.Exists(package), error);
    }

    [Fact]
    public void Expand_APackageThatStaysLocked_IsExpandedOnceAndTheFailureToRemoveItIsReported()
    {
        var package = MakePackage("yt-dlp.exe");
        using var held = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Equal(ExpandOutcome.Expanded, YtDlpLayout.ExpandPackageIfPresent(_bin, out var first));
        Assert.Contains(YtDlpLayout.PackageName, first);   // logged with result=ok, no longer silently

        var marker = Path.Combine(_bin, YtDlpLayout.DirectoryName, "still-the-same-directory.txt");
        File.WriteAllText(marker, "x");
        Assert.Equal(ExpandOutcome.NothingToDo, YtDlpLayout.ExpandPackageIfPresent(_bin, out _));
        Assert.Equal(ExpandOutcome.NothingToDo, YtDlpLayout.ExpandPackageIfPresent(_bin, out _));
        Assert.True(File.Exists(marker));
    }

    // A package that was expanded but could not be deleted, so its stamp is in place.
    private string ExpandedButUndeletable()
    {
        var package = MakePackage("yt-dlp.exe");
        using (new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(ExpandOutcome.Expanded, YtDlpLayout.ExpandPackageIfPresent(_bin, out _));
        }
        Assert.True(File.Exists(package));
        return package;
    }

    [Fact]
    public void Activate_ForcedByARepair_ExpandsEvenWhenTheStampMatches()
    {
        // 修復 / 重新下載元件 fetch the current package — usually the build already in place.
        // With the stamp trusted, a damaged yt-dlp.exe (E-COMP002 sends people to exactly
        // these buttons) was "repaired" by deleting the zip and changing nothing.
        ExpandedButUndeletable();
        File.WriteAllText(YtDlpLayout.DirectoryExePath(_bin), "damaged");

        var outcome = YtDlpLayout.ActivatePackage(_bin, YtDlpLayout.DirectoryExePath(_bin), out var error, force: true);

        Assert.Equal(ActivationOutcome.InPlace, outcome);
        Assert.Equal("payload:yt-dlp.exe", File.ReadAllText(YtDlpLayout.DirectoryExePath(_bin)));
        Assert.Null(error);
    }

    [Fact]
    public void Expand_AtStartup_ExpandsAgainWhenTheExeIsGone()
    {
        // Quarantined by antivirus after the first expansion: the stamp alone is no proof.
        ExpandedButUndeletable();
        File.Delete(YtDlpLayout.DirectoryExePath(_bin));

        Assert.Equal(ExpandOutcome.Expanded, YtDlpLayout.ExpandPackageIfPresent(_bin, out _));

        Assert.Equal("payload:yt-dlp.exe", File.ReadAllText(YtDlpLayout.DirectoryExePath(_bin)));
    }

    [Fact]
    public void Expand_ANewPackageAfterAnUndeletableOne_IsExpanded()
    {
        var package = MakePackage("yt-dlp.exe");
        using (new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            YtDlpLayout.ExpandPackageIfPresent(_bin, out _);
        }
        File.Delete(package);
        MakePackage("yt-dlp.exe", "_internal/new-in-this-version.txt");

        Assert.Equal(ExpandOutcome.Expanded, YtDlpLayout.ExpandPackageIfPresent(_bin, out _));
        Assert.True(File.Exists(Path.Combine(_bin, YtDlpLayout.DirectoryName, "_internal", "new-in-this-version.txt")));
    }

    // ── v1.5.0: the swap waits out a briefly held file ──────────────────────────

    // How antivirus opens a file to scan it: full sharing, so deletes still work — but
    // Windows refuses to rename a directory while ANY file inside it is open.
    private static FileStream HoldLikeAScanner(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    [Fact]
    public void Expand_WhenAFileInTheOldBuildIsBrieflyHeldOpen_WaitsAndSwaps()
    {
        // Log 2026-11-04: 修復 failed with "Access to the path '…\yt-dlp.new' is
        // denied." right after an extraction that real-time scanning had slowed to 16 s.
        // A hold that ends within a few seconds must delay the swap, not fail it.
        Directory.CreateDirectory(Path.Combine(_bin, YtDlpLayout.DirectoryName));
        File.WriteAllText(YtDlpLayout.DirectoryExePath(_bin), "old-version");
        MakePackage("yt-dlp.exe");

        var scan = HoldLikeAScanner(YtDlpLayout.DirectoryExePath(_bin));
        ExpandOutcome outcome;
        string? error;
        using (new Timer(_ => scan.Dispose(), null, 1500, Timeout.Infinite))
        {
            outcome = YtDlpLayout.ExpandPackageIfPresent(_bin, out error);
        }
        scan.Dispose();

        Assert.True(outcome == ExpandOutcome.Expanded, $"{outcome}: {error}");
        Assert.Equal("payload:yt-dlp.exe", File.ReadAllText(YtDlpLayout.DirectoryExePath(_bin)));
    }

    [Fact]
    public void Expand_WhenTheOldBuildStaysInUse_GivesUpAndKeepsItWorking()
    {
        // A build that is still running (a download in progress) cannot be swapped out.
        // The wait is bounded; the old build stays in place and the package stays for the
        // next launch, which expands it before anything runs.
        Directory.CreateDirectory(Path.Combine(_bin, YtDlpLayout.DirectoryName));
        File.WriteAllText(YtDlpLayout.DirectoryExePath(_bin), "old-version");
        var package = MakePackage("yt-dlp.exe");

        ExpandOutcome outcome;
        var sw = Stopwatch.StartNew();
        using (HoldLikeAScanner(YtDlpLayout.DirectoryExePath(_bin)))
        {
            outcome = YtDlpLayout.ExpandPackageIfPresent(_bin, out _, swapWait: TimeSpan.FromMilliseconds(300));
        }

        Assert.Equal(ExpandOutcome.Failed, outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.Equal("old-version", File.ReadAllText(YtDlpLayout.DirectoryExePath(_bin)));
        Assert.True(File.Exists(package));
        Assert.False(Directory.Exists(Path.Combine(_bin, YtDlpLayout.DirectoryName + ".new")));
    }

    // ── v1.5.0: ActivatePackage — use a just-downloaded package without a restart ──

    [Fact]
    public void Activate_ReplacesTheRunningDirectoryBuildInPlace()
    {
        Directory.CreateDirectory(Path.Combine(_bin, YtDlpLayout.DirectoryName));
        File.WriteAllText(YtDlpLayout.DirectoryExePath(_bin), "old-version");
        MakePackage("yt-dlp.exe");
        var running = YtDlpLayout.ResolveExecutable(_bin);

        var outcome = YtDlpLayout.ActivatePackage(_bin, running, out var error);

        Assert.True(outcome == ActivationOutcome.InPlace, $"{outcome}: {error}");
        Assert.Equal("payload:yt-dlp.exe", File.ReadAllText(running));
        Assert.False(File.Exists(YtDlpLayout.PackagePath(_bin)));
    }

    [Fact]
    public void Activate_RepairsADirectoryBuildWhoseExeIsMissing()
    {
        // The case 修復 exists for: the directory is there but yt-dlp.exe is gone
        // (refused or quarantined by endpoint security). The runner was built with the
        // directory path, so the repaired build is used without a restart.
        Directory.CreateDirectory(Path.Combine(_bin, YtDlpLayout.DirectoryName, "_internal"));
        var running = YtDlpLayout.ResolveExecutable(_bin);
        MakePackage("yt-dlp.exe");

        Assert.Equal(ActivationOutcome.InPlace, YtDlpLayout.ActivatePackage(_bin, running, out _));
        Assert.True(File.Exists(running));
    }

    [Fact]
    public void Activate_UnderALegacySingleFileRunner_NeedsRestart()
    {
        // The runner keeps executing bin\yt-dlp.exe until the app restarts; the new
        // directory build is what the NEXT launch resolves.
        File.WriteAllText(YtDlpLayout.LegacyExePath(_bin), "legacy");
        var running = YtDlpLayout.ResolveExecutable(_bin);
        MakePackage("yt-dlp.exe");

        Assert.Equal(ActivationOutcome.NeedsRestart, YtDlpLayout.ActivatePackage(_bin, running, out _));
        Assert.True(File.Exists(YtDlpLayout.DirectoryExePath(_bin)));
    }

    [Fact]
    public void Activate_WhenTheSwapFails_NeedsRestartAndKeepsThePackage()
    {
        Directory.CreateDirectory(Path.Combine(_bin, YtDlpLayout.DirectoryName));
        File.WriteAllText(YtDlpLayout.DirectoryExePath(_bin), "old-version");
        var package = MakePackage("yt-dlp.exe");
        var running = YtDlpLayout.ResolveExecutable(_bin);

        ActivationOutcome outcome;
        using (HoldLikeAScanner(running))
        {
            outcome = YtDlpLayout.ActivatePackage(_bin, running, out _, swapWait: TimeSpan.FromMilliseconds(300));
        }

        Assert.Equal(ActivationOutcome.NeedsRestart, outcome);
        Assert.True(File.Exists(package));
        Assert.Equal("old-version", File.ReadAllText(running));
    }
}
