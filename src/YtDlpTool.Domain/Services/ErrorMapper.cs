using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Services;

public static class ErrorMapper
{
    private record Rule(ErrorCategory Cat, string Code, string Message, bool CanRetry, Regex Pattern);

    // Rule ordering matters. Premiere / VideoUnavailable are placed BEFORE RateLimited
    // because yt-dlp's 429-retry messages occasionally mention "Too Many Requests" in
    // contexts where the actual cause is a premiere or an unavailable video. We want
    // the more-specific category to win.
    private static readonly Rule[] Rules =
    {
        new(ErrorCategory.PremiereUpcoming,   "E-PREMIER",
            "這是預定首播的影片，請首播開始後再下載", false,
            new(@"Premieres in|This live event will begin|premiere", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.VideoUnavailable,   "E-VIDUNAV",
            "這部影片無法下載（可能已被刪除、設為私人或下架）", false,
            new(@"Video unavailable|This video is private|members[- ]only|removed|has been deleted",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        // RateLimited: tighten the regex so "Too Many Requests" in passing prose doesn't
        // false-positive. Requires an ERROR: line containing the 429 phrase, or the
        // explicit "HTTP Error 429" token.
        new(ErrorCategory.RateLimited,        "E-RATE001",
            "YouTube 暫時限制了下載速度，稍後再試", true,
            new(@"(ERROR:[^\n]*\b(HTTP Error 429|Too Many Requests)\b)|\bHTTP Error 429\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.YouTubeRefused,     "E-AUTH001",
            "YouTube 拒絕了這次請求，影片可能有年齡或地區限制（本工具不支援登入下載）", false,
            new(@"HTTP Error 403|Sign in to confirm", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.NetworkError,       "E-NET001",
            "網路連線中斷，請檢查網路後重試", true,
            new(@"urlopen error|timed out|Connection (refused|reset)|Could not resolve host|getaddrinfo",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.DiskFull,           "E-DISK001",
            "磁碟空間不足，請清理後再試", false,
            new(@"No space left on device|disk full|enough space",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.ComponentMissing,   "E-COMP001",
            "處理元件異常，請至『設定→進階→重新下載元件』修復", false,
            new(@"ffmpeg.*not found|ffprobe.*not found|cannot find.*ffmpeg",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };

    public static MappedError Map(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return new MappedError(ErrorCategory.UnknownError, "下載失敗（無錯誤訊息）", "E-UNKNOWN", false);

        var raw = TruncateDetails(stderr);
        foreach (var r in Rules)
            if (r.Pattern.IsMatch(stderr))
                return new MappedError(r.Cat, r.Message, r.Code, r.CanRetry, raw);

        var code = $"E-{HashCode(stderr)}";

        // Fix C: surface common AD-environment failures BEFORE the generic ERROR-line
        // fallback, so the more specific category wins. These typically come from
        // ProcessSandbox (Fix B) bubbling up a Win32Exception hint, or from urllib's
        // SSL / proxy errors that include "...Error:" tokens that would otherwise be
        // swallowed by the generic ERROR-line fallback.
        if (stderr.Contains("AppLocker", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("被防毒隔離", StringComparison.Ordinal) ||
            stderr.Contains("找不到可執行檔", StringComparison.Ordinal) ||
            stderr.Contains("存取被拒", StringComparison.Ordinal))
        {
            return new MappedError(ErrorCategory.ComponentMissing,
                "處理元件無法啟動：可能被防毒/AppLocker 阻擋。請聯絡 IT 將 YtDlpTool.exe、yt-dlp.exe、ffmpeg.exe 加入白名單，或將整個資料夾移到使用者個人目錄。",
                "E-BLOCK01", false, raw);
        }

        if (stderr.Contains("certificate verify failed", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("CERTIFICATE_VERIFY_FAILED", StringComparison.Ordinal) ||
            stderr.Contains("SSLError", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("SSL: ", StringComparison.OrdinalIgnoreCase))
        {
            return new MappedError(ErrorCategory.NetworkError,
                "SSL 憑證驗證失敗：企業網路可能有 SSL 攔截/檢查。請聯絡 IT，或於設定→進階確認代理伺服器設定。",
                "E-SSL01", false, raw);
        }

        if (stderr.Contains("ProxyError", StringComparison.OrdinalIgnoreCase) ||
            (stderr.Contains("proxy", StringComparison.OrdinalIgnoreCase) &&
             stderr.Contains("403", StringComparison.Ordinal)))
        {
            return new MappedError(ErrorCategory.NetworkError,
                "代理伺服器拒絕連線：企業 proxy 可能要求認證。請聯絡 IT 或於設定中手動設定 proxy。",
                "E-PROXY01", false, raw);
        }

        // Fallback diagnostic: when no rule matches but stderr does contain an ERROR: line,
        // surface that line in the user message so the bug report doesn't reduce to
        // "下載失敗（錯誤代碼 E-XXXXX）". This still avoids the literal English word "Error"
        // (we keep "ERROR:" in caps so existing assertions remain satisfied).
        var errLine = FindErrorLine(stderr);
        if (errLine is not null)
        {
            var clean = errLine.Trim();
            if (clean.Length > 160) clean = clean.Substring(0, 160) + "…";
            return new MappedError(ErrorCategory.UnknownError, $"下載失敗：{clean}", code, false, raw);
        }

        // Final fallback: instead of just a hash code, surface the last non-empty
        // line of stderr so the user at least sees what blew up.
        var lastLine = stderr.Split('\n').Reverse()
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        if (!string.IsNullOrEmpty(lastLine))
        {
            if (lastLine.Length > 160) lastLine = lastLine.Substring(0, 160) + "…";
            return new MappedError(ErrorCategory.UnknownError,
                $"下載失敗：{lastLine}", code, false, raw);
        }

        return new MappedError(ErrorCategory.UnknownError,
            $"下載失敗（錯誤代碼 {code}）", code, false, raw);
    }

    /// <summary>
    /// Returns the first line containing "ERROR:" (case-insensitive). Used for the
    /// unrecognised-stderr fallback message. Returns null if no such line exists.
    /// </summary>
    private static string? FindErrorLine(string stderr)
    {
        var lines = stderr.Split('\n');
        foreach (var line in lines)
            if (line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                return line;
        return null;
    }

    /// <summary>
    /// Truncate stderr to 500 chars and collapse all whitespace runs (including newlines)
    /// to single spaces so it fits cleanly on a single log line.
    /// </summary>
    private static string TruncateDetails(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "";
        var collapsed = Regex.Replace(stderr, @"\s+", " ").Trim();
        if (collapsed.Length > 500) collapsed = collapsed.Substring(0, 500);
        return collapsed;
    }

    private static string HashCode(string s)
    {
        // Stable 6-hex from SHA-256 prefix; not used for security, only correlation.
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 3).ToUpperInvariant();
    }
}
