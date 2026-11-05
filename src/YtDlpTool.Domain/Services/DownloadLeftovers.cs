using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Services;

/// <summary>
/// v1.5.0: the files yt-dlp leaves for an output name while it works, and only those. Used
/// when a download writes straight to its final name: before it starts (so a stream of an
/// earlier, different video with the same title is not resumed into this one) and when it
/// fails. A finished file — "X.mp4", "X.m4a" — or a file of the user's is never one.
/// </summary>
public static class DownloadLeftovers
{
    // What follows "<stem>.": one stream of a merge ("f137.mp4", "f140-drc.m4a" — YouTube
    // format ids start with a digit), possibly still downloading (".part", or a fragmented
    // stream's ".part-Frag3" / ".part-Frag3.part") or with fragment bookkeeping (".ytdl");
    // a post-processor's temporary output ("temp.mp4"); or "mp4.part" / "part" / "ytdl".
    // Case-sensitive: yt-dlp writes these in lower case, and "<stem>.F1.mp4" or
    // "<stem>.Temp.mp4" would be someone's own file.
    private static readonly Regex Tail = new(
        @"^(?:f[0-9][0-9A-Za-z_-]*\.[0-9A-Za-z]+(?:\.part(?:-Frag[0-9]+(?:\.part)?)?|\.ytdl)?" +
        @"|temp\.[0-9A-Za-z]+|(?:[0-9A-Za-z]+\.)?(?:part(?:-Frag[0-9]+(?:\.part)?)?|ytdl))$",
        RegexOptions.CultureInvariant);

    public static bool IsLeftover(string fileName, string stem) =>
        fileName.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) &&
        Tail.IsMatch(fileName[(stem.Length + 1)..]);

    /// <summary>Best effort: a file that cannot be deleted now is left where it is.</summary>
    public static void Delete(string directory, string stem)
    {
        if (string.IsNullOrEmpty(stem) || !Directory.Exists(directory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, stem + ".*").ToList())
            {
                if (!IsLeftover(Path.GetFileName(path), stem)) continue;
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
