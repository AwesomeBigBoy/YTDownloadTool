namespace YtDlpTool.Domain.Services;

/// <summary>
/// Pure file-system probe used by the WPF executor (post v1.1.5-prep) to verify
/// that a "successful" yt-dlp invocation actually produced a playable media file
/// — not just sidecar artefacts. yt-dlp can exit 0 after producing only a .vtt
/// subtitle and a thumbnail when --download-sections + ffmpeg seek/cut fails
/// silently; this probe is the safety net.
///
/// Lives in the Domain assembly so its logic is unit-testable without dragging
/// in WPF references.
/// </summary>
public static class MediaOutputProbe
{
    /// <summary>
    /// File extensions that constitute a real "downloaded media" outcome.
    /// </summary>
    public static readonly string[] MediaExtensions =
        new[] { ".mp4", ".m4a", ".mp3", ".webm", ".mkv", ".aac", ".opus", ".ogg", ".flac", ".wav" };

    /// <summary>
    /// File extensions that count as sidecars — subtitles, thumbnails. If the only
    /// files present in the save directory matching the sanitized stem fall into
    /// this list, the download must be treated as a failure.
    /// </summary>
    public static readonly string[] SidecarExtensions =
        new[] { ".vtt", ".srt", ".ass", ".jpg", ".jpeg", ".png", ".webp", ".thumbnail" };

    /// <summary>
    /// Scans <paramref name="saveDirectory"/> for files matching
    /// <paramref name="sanitizedStem"/>.* and reports whether at least one of them
    /// has a media extension. The sidecar paths list is returned alongside so the
    /// caller can best-effort clean them up when no media file was produced.
    /// </summary>
    /// <returns>
    /// <c>found</c>=true when at least one matching file has a media extension.
    /// <c>sidecarPaths</c> contains the absolute paths of files matching the
    /// stem AND a sidecar extension (always populated regardless of <c>found</c>).
    /// </returns>
    public static (bool found, IReadOnlyList<string> sidecarPaths) VerifyMediaOutputExists(
        string saveDirectory,
        string sanitizedStem)
    {
        var empty = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(saveDirectory) || string.IsNullOrWhiteSpace(sanitizedStem))
            return (false, empty);
        if (!Directory.Exists(saveDirectory))
            return (false, empty);

        string[] matched;
        try
        {
            // Use the literal stem with ".*" suffix. Directory.GetFiles uses DOS-style
            // wildcards, so this matches any extension (including no extension).
            matched = Directory.GetFiles(saveDirectory, sanitizedStem + ".*");
        }
        catch
        {
            return (false, empty);
        }

        var hasMedia = false;
        var sidecars = new List<string>();
        foreach (var path in matched)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) continue;
            if (MediaExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                hasMedia = true;
            else if (SidecarExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                sidecars.Add(path);
        }

        return (hasMedia, sidecars);
    }
}
