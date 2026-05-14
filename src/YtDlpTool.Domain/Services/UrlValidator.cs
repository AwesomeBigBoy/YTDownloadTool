using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Services;

public sealed class UrlValidator
{
    private static readonly HashSet<string> AllowedHosts =
        new(StringComparer.OrdinalIgnoreCase) { "www.youtube.com", "youtube.com", "youtu.be" };

    private static readonly Regex VideoIdPattern =
        new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    public UrlValidationResult Validate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return UrlValidationResult.Fail("空網址");

        if (input.Contains('%'))
            return UrlValidationResult.Fail("不接受 URL-encoded 主機");

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return UrlValidationResult.Fail("不是有效的網址");

        if (uri.Scheme != "https")
            return UrlValidationResult.Fail("只接受 https");

        var host = uri.IdnHost;
        if (host.Length != uri.Host.Length)
            return UrlValidationResult.Fail("拒絕含 IDN/Unicode 的主機（防 homograph）");

        if (!AllowedHosts.Contains(host))
            return UrlValidationResult.Fail($"不允許的主機：{host}");

        if (IsIpLiteral(host))
            return UrlValidationResult.Fail("不接受 IP 位址");

        string? videoId;
        long? startSeconds = null;

        if (string.Equals(host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            videoId = uri.AbsolutePath.TrimStart('/');
            startSeconds = ParseTimeQuery(uri.Query, "t");
        }
        else
        {
            if (!string.Equals(uri.AbsolutePath, "/watch", StringComparison.OrdinalIgnoreCase))
                return UrlValidationResult.Fail("不支援這個路徑");

            var v = GetSingleQueryValue(uri.Query, "v");
            if (v is null) return UrlValidationResult.Fail("找不到影片 ID");
            videoId = v;
            startSeconds = ParseTimeQuery(uri.Query, "t");
        }

        if (videoId is null || !VideoIdPattern.IsMatch(videoId))
            return UrlValidationResult.Fail("影片 ID 格式錯誤");

        var canonical = $"https://www.youtube.com/watch?v={videoId}";
        if (startSeconds is not null) canonical += $"&t={startSeconds}";
        return UrlValidationResult.Ok(canonical);
    }

    private static bool IsIpLiteral(string host) =>
        System.Net.IPAddress.TryParse(host, out _);

    private static string? GetSingleQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        string? found = null;
        foreach (var pair in trimmed.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var k = pair[..eq];
            if (!string.Equals(k, key, StringComparison.Ordinal)) continue;
            if (found is not null) return null; // duplicate keys → reject
            found = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return found;
    }

    private static long? ParseTimeQuery(string query, string key)
    {
        var raw = GetSingleQueryValue(query, key);
        if (raw is null) return null;
        if (long.TryParse(raw.TrimEnd('s'), out var seconds) && seconds >= 0)
            return seconds;
        return null;
    }
}
