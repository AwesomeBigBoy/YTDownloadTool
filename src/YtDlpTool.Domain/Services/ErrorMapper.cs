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
        // Timeout — only fires when YtDlpRunner explicitly tagged the stderr with
        // a "[timeout after Ns]" prefix. The 30s metadata fetch timeout in managed
        // networks with no outbound youtube.com access is by far the most common cause.
        new(ErrorCategory.NetworkError,      "E-TIMEOUT01",
            "解析網址逾時（無法連到 YouTube）。可能原因：DNS 解析失敗、目前網路封鎖 *.youtube.com / *.googlevideo.com、HTTPS 設定問題、或網路斷線。可至 設定 → 進階 → 開啟「允許不受信任憑證」後重試，或改用其他網路。", true,
            new(@"\[timeout after \d+s\]", RegexOptions.Compiled)),

        // PO Token requirement: YouTube blocks many HTTPS formats unless the client
        // sends a Proof-of-Origin token. yt-dlp logs "...require a GVS PO Token which
        // was not provided. They will be skipped..." per skipped client. When ALL
        // clients are skipped the download fails. Place this rule FIRST so the
        // user-facing message reflects the actual cause instead of falling through
        // to the generic YouTubeRefused/AUTH001 "不支援登入下載".
        new(ErrorCategory.YouTubeRefused,     "E-PO-TOKEN",
            "YouTube 對此影片要求驗證碼 (PO Token)，請改試其他畫質或稍後再試；若仍失敗，可能需更新 yt-dlp 元件（設定→進階→重新下載元件）", false,
            new(@"GVS PO Token|require a PO Token|PO Token (?:was|is) not provided|missing.*po.?token",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

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

        // v1.3.3: zero-output timeout. When yt-dlp.exe times out AND wrote
        // nothing at all (the stderr equals just the YtDlpRunner-injected
        // "[timeout after Ns]" marker), it points at yt-dlp.exe failing to
        // start rather than a network/SSL issue — PyInstaller-frozen Python
        // can hang on cold-start under aggressive AV scanning of %TEMP%
        // extraction, and on a machine where ffmpeg.exe probe succeeds but
        // yt-dlp.exe probe times out the diagnostic is unambiguous. We give
        // this its own code (E-TIMEOUT02) and message so the auto-prompt
        // logic doesn't mis-route it as SSL.
        if (System.Text.RegularExpressions.Regex.IsMatch(stderr.Trim(),
                @"^\[timeout after \d+s\]$"))
        {
            // v1.3.4: parameter order was (Category, UserMessage, ErrorCode, CanRetry,
            // RawDetails). v1.3.3 had this reversed — UserMessage held "E-TIMEOUT02"
            // and ErrorCode held the long explanation, so the UI showed only the bare
            // code and the log file had the full sentence in the error_code field.
            return new MappedError(ErrorCategory.NetworkError,
                "解析網址逾時，且 yt-dlp 在 30 秒內沒有產生任何輸出。" +
                "通常代表 yt-dlp 本身沒能正常啟動，可能原因：" +
                "(1) 防毒軟體阻擋了 yt-dlp.exe 的執行 — 請檢查防毒隔離區並把 yt-dlp.exe 加入信任清單；" +
                "(2) 機器資源不足或 %TEMP% 寫入受限 — 關閉其他程式後重試，或將整個資料夾移到桌面/個人目錄；" +
                "(3) yt-dlp.exe 檔案損毀 — 請至設定 → 進階 → 重新下載元件；" +
                "(4) 若以上都試過仍失敗，請開啟 cmd，切換到本資料夾的 bin 目錄，執行 " +
                "「yt-dlp.exe --version」，看是否能在系統環境下直接執行成功。如果該指令在 cmd 內也卡住沒輸出，問題在 yt-dlp.exe 本身被系統阻擋；" +
                "若 cmd 可以順利執行，請把該結果回報給開發者。",
                "E-TIMEOUT02",
                true, raw);
        }

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
                "處理元件無法啟動：可能被防毒軟體或系統政策阻擋。請確認 YtDlpTool.exe、yt-dlp.exe、ffmpeg.exe 在這台機器允許執行，或將整個資料夾移到使用者個人目錄（如 Downloads 或桌面）。",
                "E-BLOCK01", false, raw);
        }

        // v1.3.2: detect EE-key-too-weak BEFORE the generic SSL pattern; this
        // particular failure has a more specific actionable message (point at
        // the AllowUntrustedCertificates setting directly).
        if (stderr.Contains("key too weak", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("EE certificate", StringComparison.OrdinalIgnoreCase))
        {
            return new MappedError(ErrorCategory.NetworkError,
                "目前網路使用的 HTTPS 憑證金鑰長度過短，無法被安全規則接受。若你信任目前的網路環境，請至 設定 → 進階 → 開啟「允許不受信任憑證」後重試。",
                "E-SSL02", false, raw);
        }

        if (stderr.Contains("certificate verify failed", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("CERTIFICATE_VERIFY_FAILED", StringComparison.Ordinal) ||
            stderr.Contains("SSLError", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("SSL: ", StringComparison.OrdinalIgnoreCase))
        {
            return new MappedError(ErrorCategory.NetworkError,
                "HTTPS 憑證驗證失敗。如果你信任目前的網路環境，可至 設定 → 進階 → 開啟「允許不受信任憑證」後重試。",
                "E-SSL01", false, raw);
        }

        if (stderr.Contains("ProxyError", StringComparison.OrdinalIgnoreCase) ||
            (stderr.Contains("proxy", StringComparison.OrdinalIgnoreCase) &&
             stderr.Contains("403", StringComparison.Ordinal)))
        {
            return new MappedError(ErrorCategory.NetworkError,
                "代理伺服器拒絕連線。請至 設定 → 代理伺服器 確認設定，或改用其他網路後重試。",
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
    /// Truncate stderr and collapse all whitespace runs (including newlines)
    /// to single spaces so it fits cleanly on a log line. v1.3.0-alpha3 bumped
    /// the cap from 500 to 4096 chars so download-stage failures (which can
    /// have ~2KB of stderr from yt-dlp's multiple-client retry chatter) are
    /// captured in full rather than cut off mid-sentence.
    /// </summary>
    private static string TruncateDetails(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "";
        var collapsed = Regex.Replace(stderr, @"\s+", " ").Trim();
        if (collapsed.Length > 4096) collapsed = collapsed.Substring(0, 4096);
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
