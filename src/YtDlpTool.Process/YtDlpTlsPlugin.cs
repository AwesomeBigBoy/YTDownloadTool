using System.Text;

namespace YtDlpTool.Process;

/// <summary>
/// Installs the yt-dlp plugin that implements AllowUntrustedCertificates.
///
/// v1.5.0 — why this exists. Up to v1.4.0 the workaround was a PyInstaller runtime hook
/// compiled into our own build of yt-dlp.exe. A home-built, unsigned PyInstaller binary
/// with a hash no one else has is exactly what endpoint security distrusts: on a machine
/// with an endpoint-protection agent, its behaviour monitoring held every launch ~27 s and
/// then failed it with "Access denied" (Win32 error 5, surfaced as E-BLOCK01), while the
/// official yt-dlp build launched the same way ran in under a second. So the app now ships
/// upstream's binary byte-for-byte, and the one customisation moved into a plugin — plain
/// Python that the official build imports from the directory given with --plugin-dirs.
///
/// yt-dlp treats every child of a --plugin-dirs directory as a plugin package root and
/// imports the modules in &lt;child&gt;/yt_dlp_plugins/&lt;type&gt;/. The module defines no
/// classes; importing it is the whole effect, and it is a no-op unless the environment
/// variable YTDLP_RELAX_SECLEVEL=1 is set (YtDlpRunner sets it only when the user opted in).
///
/// The file is rewritten on every launch while the setting is on, like system-ca-bundle.pem,
/// so a stale or tampered copy never survives a restart; v1.5.0: anything else in the
/// folder is removed (yt-dlp would import it too), and the folder is deleted while the
/// setting is off (<see cref="Remove"/>).
/// </summary>
public static class YtDlpTlsPlugin
{
    /// <summary>Plugin package root created under the directory passed to --plugin-dirs.</summary>
    public const string PackageFolderName = "ytdlptool-tls";

    /// <summary>Module file name. Names starting with '_' are skipped by yt-dlp's loader.</summary>
    public const string ModuleFileName = "ytdlptool_tls_relax.py";

    private const string ResourceName = "YtDlpTool.Process.Plugins.ytdlptool_tls_relax.py";

    private static readonly Lazy<string> Source = new(() =>
    {
        using var stream = typeof(YtDlpTlsPlugin).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    /// <summary>The plugin's Python source, exactly as written to disk.</summary>
    public static string ModuleSource => Source.Value;

    /// <summary>
    /// Writes the plugin below <paramref name="pluginsRoot"/> and returns that directory —
    /// the value to pass to yt-dlp's --plugin-dirs. Returns null (with
    /// <paramref name="error"/> set) instead of throwing, because startup must carry on
    /// without the plugin rather than fail.
    /// </summary>
    public static string? Install(string pluginsRoot, out string? error)
    {
        error = null;
        try
        {
            var root = Path.GetFullPath(pluginsRoot);
            var moduleDir = Path.Combine(root, PackageFolderName, "yt_dlp_plugins", "postprocessor");
            var target = Path.Combine(moduleDir, ModuleFileName);
            // The folder sits next to the exe, which on a shared folder other users can
            // write to. A link anywhere here — the root itself, a folder on the way to the
            // module, an entry the clean-up meets — is removed as a link and never followed:
            // the clean-up below would otherwise delete whatever it points at.
            // Top down, so nothing is ever reached through a link that is about to go.
            var onTheWayDown = new[] { root, Path.Combine(root, PackageFolderName),
                Path.Combine(root, PackageFolderName, "yt_dlp_plugins"), moduleDir };
            foreach (var dir in onTheWayDown)
            {
                if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(dir);
            }
            Directory.CreateDirectory(moduleDir);
            // Only our module may be imported: yt-dlp loads every package below
            // --plugin-dirs and every module of every plugin type in it.
            Prune(new DirectoryInfo(root), target, moduleDir);
            // Written in place (v1.5.0): a rename over the old copy is held ~3 s by endpoint
            // security on managed PCs, at every launch. UTF-8 without BOM: what Python
            // expects of a source file. yt-dlp is not running yet when this is called.
            File.WriteAllText(target, ModuleSource, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return pluginsRoot;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // Everything below dir except keepFile and the folders on the way to keepDir. Links are
    // deleted as links (FileInfo / DirectoryInfo.Delete on a reparse point removes the link).
    private static void Prune(DirectoryInfo dir, string keepFile, string keepDir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos().ToList())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Delete();
                continue;
            }
            if (entry is DirectoryInfo sub)
            {
                Prune(sub, keepFile, keepDir);
                var onTheWay = string.Equals(sub.FullName, keepDir, StringComparison.OrdinalIgnoreCase) ||
                               keepDir.StartsWith(sub.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (!onTheWay) sub.Delete();
            }
            else if (!string.Equals(entry.FullName, keepFile, StringComparison.OrdinalIgnoreCase))
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
    }

    /// <summary>
    /// v1.5.0: the setting is off — deletes the folder <see cref="Install"/> writes.
    /// Returns whether there was one to delete; best effort, like Install.
    /// </summary>
    public static bool Remove(string pluginsRoot, out string? error)
    {
        error = null;
        if (!Directory.Exists(pluginsRoot)) return false;
        try
        {
            Directory.Delete(pluginsRoot, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
