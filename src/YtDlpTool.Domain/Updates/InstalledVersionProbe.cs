namespace YtDlpTool.Domain.Updates;

public static class InstalledVersionProbe
{
    public static bool IsRemoteNewer(string localVersion, string remoteVersion)
    {
        // v1.3.4: empty local version means the --version probe failed, NOT that
        // the component is missing from disk (yt-dlp.exe / ffmpeg.exe are always
        // bundled in the install). The original "empty → remote is newer" rule
        // caused a nag-loop on machines where yt-dlp.exe is on disk but refuses
        // to launch (AV blocking PyInstaller cold-start in some environments):
        // each launch failed the probe → update logic offered an update → user
        // accepted → same yt-dlp.exe got reinstalled → next launch still failed.
        // Treat "probe failed" as "unknown, leave it alone" so the user isn't
        // pushed toward a fix that can't help.
        if (string.IsNullOrWhiteSpace(localVersion)) return false;
        if (string.IsNullOrWhiteSpace(remoteVersion)) return false;
        var localParts = ParseParts(localVersion);
        var remoteParts = ParseParts(remoteVersion);
        var len = Math.Max(localParts.Length, remoteParts.Length);
        for (int i = 0; i < len; i++)
        {
            var l = i < localParts.Length ? localParts[i] : 0;
            var r = i < remoteParts.Length ? remoteParts[i] : 0;
            if (r > l) return true;
            if (r < l) return false;
        }
        return false;
    }

    private static int[] ParseParts(string v)
    {
        var stripped = v.TrimStart('v', 'V');
        return stripped.Split('.', '-')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();
    }
}
