using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Services;

public static class ErrorMapper
{
    private record Rule(ErrorCategory Cat, string Code, string Message, bool CanRetry, Regex Pattern);

    private static readonly Rule[] Rules =
    {
        new(ErrorCategory.RateLimited,        "E-RATE001",
            "YouTube 暫時限制了下載速度，稍後再試", true,
            new(@"HTTP Error 429|Too Many Requests", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.YouTubeRefused,     "E-AUTH001",
            "YouTube 拒絕了這次請求，影片可能有年齡或地區限制（本工具不支援登入下載）", false,
            new(@"HTTP Error 403|Sign in to confirm", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.PremiereUpcoming,   "E-PREMIER",
            "這是預定首播的影片，請首播開始後再下載", false,
            new(@"Premieres in|This live event will begin|premiere", RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.VideoUnavailable,   "E-VIDUNAV",
            "這部影片無法下載（可能已被刪除、設為私人或下架）", false,
            new(@"Video unavailable|This video is private|members[- ]only|removed|has been deleted",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.NetworkError,       "E-NET001",
            "網路連線中斷，請檢查網路後重試", true,
            new(@"urlopen error|timed out|Connection (refused|reset)|Could not resolve host|getaddrinfo",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.DiskFull,           "E-DISK001",
            "磁碟空間不足，請清理後再試", false,
            new(@"No space left on device|disk full|enough space",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        new(ErrorCategory.ComponentMissing,   "E-COMP001",
            "處理元件缺失或損毀，請從設定→關於→修復元件重新下載", false,
            new(@"ffmpeg.*not found|ffprobe.*not found|cannot find.*ffmpeg",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };

    public static MappedError Map(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return new MappedError(ErrorCategory.UnknownError, "下載失敗（無錯誤訊息）", "E-UNKNOWN", false);

        foreach (var r in Rules)
            if (r.Pattern.IsMatch(stderr))
                return new MappedError(r.Cat, r.Message, r.Code, r.CanRetry);

        return new MappedError(ErrorCategory.UnknownError,
            $"下載失敗（錯誤代碼 E-{HashCode(stderr)}）", $"E-{HashCode(stderr)}", false);
    }

    private static string HashCode(string s)
    {
        // Stable 6-hex from SHA-256 prefix; not used for security, only correlation.
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 3).ToUpperInvariant();
    }
}
