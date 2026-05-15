using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace YtDlpTool.Process;

/// <summary>
/// Reads the per-user HTTP proxy configuration that IE / Edge / WinHTTP-aware apps
/// use, so we can pass it through to yt-dlp's --proxy flag. yt-dlp's underlying
/// urllib does NOT honour Windows WinHTTP proxy automatically — only HTTP_PROXY /
/// HTTPS_PROXY env vars or an explicit --proxy argument. In managed environments the
/// system proxy is almost always present in the Internet Settings hive, so
/// detecting it here gets us a working out-of-the-box experience.
///
/// Detection order:
///  1. Explicit ProxyServer in registry (manual proxy configured via Settings/IE).
///  2. WinHTTP auto-proxy resolution (WPAD DHCP/DNS discovery + AutoConfigURL PAC)
///     — this is what most domain-joined machines use; the proxy never
///     appears in the ProxyServer value because the user/policy only sets a PAC URL
///     or relies on WPAD auto-detect.
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

        // Stage 1: explicit ProxyServer (legacy manual config). Cheap registry read.
        try
        {
            var fromRegistry = DetectFromRegistryWindows();
            if (fromRegistry is not null) return fromRegistry;
        }
        catch
        {
            // Registry read can throw on locked-down profiles; fall through.
        }

        // Stage 2: WinHTTP auto-proxy (WPAD / PAC) — what domain-joined machines use.
        try
        {
            return DetectViaWinHttpWindows("https://www.youtube.com/");
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? DetectFromRegistryWindows()
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

    /// <summary>
    /// Uses WinHTTP's WinHttpGetProxyForUrl to resolve the actual proxy that the OS
    /// would use for the given URL. Honours WPAD auto-detection (DHCP option 252
    /// and DNS A "wpad" lookup) and a manually set AutoConfigURL PAC. Returns null
    /// when WPAD doesn't resolve to anything or when WinHTTP is unavailable.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? DetectViaWinHttpWindows(string forUrl)
    {
        IntPtr session = IntPtr.Zero;
        try
        {
            session = WinHttpOpen("YtDlpTool/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY, null, null, 0);
            if (session == IntPtr.Zero) return null;

            // Read AutoConfigURL from registry (manual PAC) — if set, also pass
            // CONFIG_URL so WinHTTP evaluates that PAC in addition to WPAD.
            string? autoConfigUrl = null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                autoConfigUrl = key?.GetValue("AutoConfigURL") as string;
                if (string.IsNullOrWhiteSpace(autoConfigUrl)) autoConfigUrl = null;
            }
            catch { /* ignore */ }

            var opts = new WINHTTP_AUTOPROXY_OPTIONS
            {
                dwFlags = autoConfigUrl is null
                    ? WINHTTP_AUTOPROXY_AUTO_DETECT
                    : (WINHTTP_AUTOPROXY_AUTO_DETECT | WINHTTP_AUTOPROXY_CONFIG_URL),
                dwAutoDetectFlags = WINHTTP_AUTO_DETECT_TYPE_DHCP | WINHTTP_AUTO_DETECT_TYPE_DNS_A,
                lpszAutoConfigUrl = autoConfigUrl,
                lpvReserved = IntPtr.Zero,
                dwReserved = 0,
                fAutoLogonIfChallenged = true,
            };

            if (!WinHttpGetProxyForUrl(session, forUrl, ref opts, out var info))
                return null;

            try
            {
                if (info.lpszProxy == IntPtr.Zero) return null;
                var proxyList = Marshal.PtrToStringUni(info.lpszProxy);
                if (string.IsNullOrWhiteSpace(proxyList)) return null;

                // proxyList format examples:
                //   "proxy.corp:8080"
                //   "http=proxy.corp:8080;https=proxy.corp:8080"
                //   "proxy1:8080 proxy2:8080" (whitespace-separated failover)
                var first = proxyList.Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                if (first.Contains('='))
                {
                    var kv = first.Split('=', 2);
                    first = kv.Length == 2 ? kv[1] : first;
                }
                first = first.Trim();
                if (first.Length == 0) return null;

                return first.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || first.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? first
                    : $"http://{first}";
            }
            finally
            {
                if (info.lpszProxy != IntPtr.Zero) GlobalFree(info.lpszProxy);
                if (info.lpszProxyBypass != IntPtr.Zero) GlobalFree(info.lpszProxyBypass);
            }
        }
        finally
        {
            if (session != IntPtr.Zero) WinHttpCloseHandle(session);
        }
    }

    // --- P/Invoke surface for WinHTTP ---

    private const int WINHTTP_ACCESS_TYPE_NO_PROXY = 1;
    private const uint WINHTTP_AUTOPROXY_AUTO_DETECT = 0x00000001;
    private const uint WINHTTP_AUTOPROXY_CONFIG_URL = 0x00000002;
    private const uint WINHTTP_AUTO_DETECT_TYPE_DHCP = 0x00000001;
    private const uint WINHTTP_AUTO_DETECT_TYPE_DNS_A = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINHTTP_AUTOPROXY_OPTIONS
    {
        public uint dwFlags;
        public uint dwAutoDetectFlags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszAutoConfigUrl;
        public IntPtr lpvReserved;
        public uint dwReserved;
        [MarshalAs(UnmanagedType.Bool)] public bool fAutoLogonIfChallenged;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_PROXY_INFO
    {
        public uint dwAccessType;
        public IntPtr lpszProxy;
        public IntPtr lpszProxyBypass;
    }

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpOpen(
        [MarshalAs(UnmanagedType.LPWStr)] string? userAgent,
        int accessType,
        [MarshalAs(UnmanagedType.LPWStr)] string? proxy,
        [MarshalAs(UnmanagedType.LPWStr)] string? proxyBypass,
        uint flags);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetProxyForUrl(
        IntPtr session,
        [MarshalAs(UnmanagedType.LPWStr)] string url,
        ref WINHTTP_AUTOPROXY_OPTIONS options,
        out WINHTTP_PROXY_INFO info);

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpCloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
