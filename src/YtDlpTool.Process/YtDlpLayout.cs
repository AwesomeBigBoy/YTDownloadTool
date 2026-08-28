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
    public static ExpandOutcome ExpandPackageIfPresent(string binDirectory, out string? error)
    {
        error = null;
        var package = PackagePath(binDirectory);
        if (!File.Exists(package)) return ExpandOutcome.NothingToDo;

        var target = Path.Combine(binDirectory, DirectoryName);
        var staging = target + ".new";
        var previous = target + ".old";

        try
        {
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

            if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
            if (Directory.Exists(target)) Directory.Move(target, previous);

            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                // Put the working copy back before giving up.
                if (!Directory.Exists(target) && Directory.Exists(previous))
                    Directory.Move(previous, target);
                throw;
            }

            if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
            File.Delete(package);
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
}

public enum ExpandOutcome
{
    NothingToDo,
    Expanded,
    Failed,
}
