using System.Runtime.Versioning;

namespace YtDlpTool.Process;

/// <summary>
/// Reads the per-user HTTP proxy configuration that IE / Edge / WinHTTP-aware apps
/// use, so we can pass it through to yt-dlp's --proxy flag. yt-dlp's underlying
/// urllib does NOT honour Windows WinHTTP proxy automatically — only HTTP_PROXY /
/// HTTPS_PROXY env vars or an explicit --proxy argument. In managed environments the
/// system proxy is almost always present in the Internet Settings hive, so
/// detecting it here gets us a working out-of-the-box experience.
/// </summary>
public static class SystemProxy
{
    /// <summary>
    /// Returns the system HTTP proxy URL (http://host:port) or null if no proxy is
    /// configured / proxy is disabled / detection fails. Best-effort; never throws.
    /// </summary>
    public static string? DetectHttpProxy()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return DetectHttpProxyWindows();
        }
        catch
        {
            // Registry read can throw on locked-down profiles; treat as "no proxy".
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? DetectHttpProxyWindows()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null) return null;

        var enabled = key.GetValue("ProxyEnable");
        if (enabled is not int e || e == 0) return null;

        var server = key.GetValue("ProxyServer") as string;
        if (string.IsNullOrWhiteSpace(server)) return null;

        // ProxyServer can be "host:port" (single) or
        // "http=host:port;https=host:port;ftp=host:port" (per-protocol).
        if (server.Contains('='))
        {
            var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var kv = p.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("http", StringComparison.OrdinalIgnoreCase))
                    return $"http://{kv[1].Trim()}";
            }
            return null;
        }
        return $"http://{server.Trim()}";
    }
}
