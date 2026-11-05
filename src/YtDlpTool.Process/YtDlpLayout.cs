using System.IO.Compression;

namespace YtDlpTool.Process;

/// <summary>
/// Resolves which yt-dlp binary to run, and expands the packaged one-directory
/// build when an update has delivered it.
///
/// v1.4.0 — why yt-dlp stopped being a single .exe:
///
/// A PyInstaller <c>--onefile</c> binary re-extracts its entire ~40 MB bundle into a
/// brand-new <c>%TEMP%\_MEIxxxxxx</c> on EVERY run, then deletes it on exit. Nothing
/// carries over between processes, so antivirus rescans the whole payload every single
/// time. Two separate field machines (2026-08) measured 18-24 seconds of that before
/// yt-dlp executed a single line of its own code — on a 30s budget, leaving 6-12s for
/// actual network work. Enough on a plain network; not enough behind an HTTPS-inspecting
/// proxy, which is exactly the split the reporter observed between network segments.
///
/// v1.3.7 and v1.3.8 worked around the symptom (shared warm-up, timeouts scaled by the
/// measured cold start). This removes the cause: a <c>--onedir</c> build starts in
/// milliseconds because there is nothing to extract.
///
/// Why a zip rather than teaching the updater about directories:
///
/// The in-app updater must keep working — it is how every existing user receives this
/// fix. <c>UpdateApplier</c> replaces individual files with backup-and-rollback, and
/// teaching it to swap directories would mean changing the one component whose failure
/// cannot be repaired remotely. Instead the release ships <c>bin\yt-dlp-pkg.zip</c> as
/// an ordinary manifest entry: the updater downloads it, verifies its SHA-256 and its
/// Sigstore signature, and drops it in place exactly like any other file, with zero
/// changes to that code path. Clients running older builds also handle it safely — to
/// them it is just a file they do not execute, and their existing yt-dlp.exe keeps
/// working until the app binary that understands it has been installed.
///
/// The app then expands it once, here, on the next launch.
/// </summary>
public static class YtDlpLayout
{
    /// <summary>Directory name of the expanded one-directory build, under bin\.</summary>
    public const string DirectoryName = "yt-dlp";

    /// <summary>Packaged one-directory build as delivered by the updater, under bin\.</summary>
    public const string PackageName = "yt-dlp-pkg.zip";

    /// <summary>Legacy single-file build shipped up to and including v1.3.8.</summary>
    public const string LegacyExeName = "yt-dlp.exe";

    public static string PackagePath(string binDirectory) =>
        Path.Combine(binDirectory, PackageName);

    public static string DirectoryExePath(string binDirectory) =>
        Path.Combine(binDirectory, DirectoryName, LegacyExeName);

    public static string LegacyExePath(string binDirectory) =>
        Path.Combine(binDirectory, LegacyExeName);

    /// <summary>
    /// Path the app should execute. Prefers the expanded one-directory build and falls
    /// back to the legacy single-file one.
    ///
    /// The fallback is not dead code: a user updating from v1.3.8 receives the new
    /// YtDlpTool.exe and the package in the same batch, and if expansion has not run yet
    /// — or fails — their existing yt-dlp.exe must still work. Downgrades stay safe for
    /// the same reason. Returns the one-directory path when neither exists so callers
    /// surface a sensible name in errors.
    /// </summary>
    public static string ResolveExecutable(string binDirectory)
    {
        var packaged = DirectoryExePath(binDirectory);
        if (File.Exists(packaged)) return packaged;

        var legacy = LegacyExePath(binDirectory);
        if (File.Exists(legacy)) return legacy;

        return packaged;
    }

    /// <summary>
    /// Expands <c>bin\yt-dlp-pkg.zip</c> into <c>bin\yt-dlp\</c> if it is present, then
    /// deletes the package. Idempotent and safe to call on every launch: it returns
    /// <see cref="ExpandOutcome.NothingToDo"/> when no package is waiting, which is the
    /// normal case.
    ///
    /// Expansion goes to a temporary sibling directory and is swapped in only once it
    /// has completed, so an interrupted run (power loss, the user closing the window,
    /// AV killing the process mid-write) can never leave a half-written yt-dlp behind
    /// that would then fail in confusing ways. The previous directory is kept until the
    /// swap succeeds and removed afterwards.
    /// </summary>
    /// <param name="force">
    /// v1.5.0: expand even when the stamp says this package is already in place — 修復,
    /// 重新下載元件 and the updater fetch the current package precisely because the build in
    /// place may be damaged. Startup leaves it false.
    /// </param>
    public static ExpandOutcome ExpandPackageIfPresent(string binDirectory, out string? error, TimeSpan? swapWait = null,
        bool force = false)
    {
        error = null;
        var package = PackagePath(binDirectory);
        if (!File.Exists(package)) return ExpandOutcome.NothingToDo;

        var target = Path.Combine(binDirectory, DirectoryName);
        var staging = target + ".new";
        var previous = target + ".old";
        var wait = swapWait ?? DefaultSwapWait;

        try
        {
            // v1.5.0: a package that is already in place — its swap succeeded but the
            // package could not be deleted (read-only, held open). Only the clean-up is
            // tried again: before, every launch expanded and swapped the whole build anew.
            var fingerprint = Fingerprint(package);
            // Only while its yt-dlp.exe is still there: antivirus may have quarantined it.
            if (!force && ReadStamp(target) == fingerprint && File.Exists(Path.Combine(target, LegacyExeName)))
            {
                TryRemovePackage(package, out error);
                return ExpandOutcome.NothingToDo;
            }

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ZipFile.ExtractToDirectory(package, staging);

            // A package whose payload does not contain the executable is worse than no
            // package at all — swapping it in would break a working install.
            if (!File.Exists(Path.Combine(staging, LegacyExeName)))
            {
                Directory.Delete(staging, recursive: true);
                error = $"{PackageName} 內沒有 {LegacyExeName}";
                return ExpandOutcome.Failed;
            }
            File.WriteAllText(Path.Combine(staging, StampName), fingerprint);

            if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
            if (Directory.Exists(target)) MoveDirectoryPatiently(target, previous, wait);

            try
            {
                MoveDirectoryPatiently(staging, target, wait);
            }
            catch
            {
                // Put the working copy back before giving up.
                if (!Directory.Exists(target) && Directory.Exists(previous))
                    MoveDirectoryPatiently(previous, target, wait);
                throw;
            }

            // v1.5.0: the swap above is the update. Tidying up after it is best-effort —
            // antivirus routinely holds freshly written files open for a moment, and a
            // failure here used to be reported as a failed expansion even though the new
            // build was already in place. A leftover package is removed on a later launch
            // (the stamp says it is in place already); a leftover .old directory is
            // cleared before the next swap. A package that cannot be removed is reported
            // through error, with the Expanded outcome.
            try { if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true); } catch { }
            TryRemovePackage(package, out error);
            return ExpandOutcome.Expanded;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            // Leave the package in place. The legacy exe (or the previous directory) is
            // still what ResolveExecutable finds, so the app keeps working and the next
            // launch retries the expansion.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            return ExpandOutcome.Failed;
        }
    }

    /// <summary>Inside the expanded directory: the fingerprint of the package it came from.</summary>
    public const string StampName = ".package-sha256";

    private static string Fingerprint(string package)
    {
        using var stream = File.OpenRead(package);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string? ReadStamp(string directory)
    {
        try
        {
            var stamp = Path.Combine(directory, StampName);
            return File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A read-only package (copied from a read-only share, say) could never be deleted;
    // a scanner that has just read it lets go within a moment.
    private static void TryRemovePackage(string package, out string? error)
    {
        error = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.SetAttributes(package, FileAttributes.Normal);
                File.Delete(package);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (clock.ElapsedMilliseconds >= 1000)
                {
                    error = $"無法刪除 {PackageName}：{ex.Message}";
                    return;
                }
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// v1.5.0: how long each directory rename in the swap keeps retrying while something
    /// holds a file inside it open.
    ///
    /// Windows refuses to rename a directory while ANY file in it is open — even with full
    /// sharing, which is how antivirus opens a file to scan it — and .NET reports
    /// "Access to the path '…' is denied." Log 2026-11-04: the 修復 swap failed exactly
    /// that way right after an extraction that real-time scanning had slowed to 16 s. npm's
    /// graceful-fs retries renames on Windows for the same reason. Each attempt costs nothing
    /// when nothing holds the files, which is the normal case.
    /// </summary>
    public static readonly TimeSpan DefaultSwapWait = TimeSpan.FromSeconds(10);

    private static void MoveDirectoryPatiently(string source, string destination, TimeSpan wait)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException ||
                                       (ex is IOException && ex is not DirectoryNotFoundException))
            {
                if (clock.Elapsed + delay >= wait) throw;
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }
    }

    /// <summary>
    /// v1.5.0: puts a package the updater has just written into service now instead of on
    /// the next launch. Every path that downloads it (the update banner, Settings' 檢查更新
    /// and 重新下載元件, the health banner's 修復) used to report success while the app kept
    /// running the old build until a restart nobody was told about.
    ///
    /// <see cref="ActivationOutcome.InPlace"/>: <paramref name="runningExecutable"/> — the
    /// path the app's YtDlpRunner was built with — now runs the new build, so the very next
    /// call uses it. <see cref="ActivationOutcome.NeedsRestart"/>: the new build takes effect
    /// on the next launch, either because the swap failed (the package stays, and startup
    /// expands it before anything runs) or because the runner still points at the legacy
    /// single file.
    /// </summary>
    public static ActivationOutcome ActivatePackage(string binDirectory, string runningExecutable, out string? error,
        TimeSpan? swapWait = null, bool force = false)
    {
        if (ExpandPackageIfPresent(binDirectory, out error, swapWait, force) == ExpandOutcome.Failed)
            return ActivationOutcome.NeedsRestart;
        return string.Equals(ResolveExecutable(binDirectory), runningExecutable, StringComparison.OrdinalIgnoreCase)
            ? ActivationOutcome.InPlace
            : ActivationOutcome.NeedsRestart;
    }
}

public enum ExpandOutcome
{
    NothingToDo,
    Expanded,
    Failed,
}

public enum ActivationOutcome
{
    InPlace,
    NeedsRestart,
}
