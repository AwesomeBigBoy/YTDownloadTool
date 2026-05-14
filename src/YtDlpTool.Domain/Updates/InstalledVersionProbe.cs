namespace YtDlpTool.Domain.Updates;

public static class InstalledVersionProbe
{
    public static bool IsRemoteNewer(string localVersion, string remoteVersion)
    {
        if (string.IsNullOrWhiteSpace(localVersion)) return !string.IsNullOrWhiteSpace(remoteVersion);
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
