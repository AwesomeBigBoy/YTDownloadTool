using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Services;

public sealed class UrlValidator
{
    // v1.5.0: m. (links shared from phones) and music. serve the same videos.
    private static readonly HashSet<string> AllowedHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "www.youtube.com", "youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be",
        };

    private static readonly Regex VideoIdPattern =
        new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    // PL…, UU…, RD…, OLAK5uy_…: letters, digits, - and _.
    private static readonly Regex ListIdPattern =
        new(@"^[A-Za-z0-9_-]{2,64}$", RegexOptions.Compiled);

    // v1.5.0: /shorts/ID, /live/ID, /embed/ID are the same video as /watch?v=ID.
    private static readonly string[] VideoIdPaths = { "/shorts/", "/live/", "/embed/" };

    public UrlValidationResult Validate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return UrlValidationResult.Fail("空網址");

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return UrlValidationResult.Fail("不是有效的網址");

        if (uri.Scheme != "https")
            return UrlValidationResult.Fail("只接受 https");

        // Reject percent-encoded host components (defence against homograph variants).
        // Done on the host only — legitimate paths/queries may contain %xx (e.g. ?list=PL%20foo).
        if (uri.Host != Uri.UnescapeDataString(uri.Host))
            return UrlValidationResult.Fail("拒絕 URL-encoded 主機名稱");

        var host = uri.IdnHost;
        if (host.Length != uri.Host.Length)
            return UrlValidationResult.Fail("拒絕含 IDN/Unicode 的主機（防 homograph）");

        if (!AllowedHosts.Contains(host))
            return UrlValidationResult.Fail($"不允許的主機：{host}");

        if (IsIpLiteral(host))
            return UrlValidationResult.Fail("不接受 IP 位址");

        string? videoId;
        var startSeconds = ParseTimeQuery(uri.Query, "t");
        var listId = GetSingleQueryValue(uri.Query, "list");
        if (listId is not null && !ListIdPattern.IsMatch(listId)) listId = null;

        if (string.Equals(host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            videoId = uri.AbsolutePath.TrimStart('/');
        }
        else
        {
            var path = uri.AbsolutePath;
            if (string.Equals(path, "/watch", StringComparison.OrdinalIgnoreCase))
            {
                var v = GetSingleQueryValue(uri.Query, "v");
                // watch?list=… with no v= is the list itself (YouTube opens its first video).
                if (v is null)
                    return listId is null
                        ? UrlValidationResult.Fail("找不到影片 ID")
                        : UrlValidationResult.OkPlaylist(listId);
                videoId = v;
            }
            else if (string.Equals(path, "/playlist", StringComparison.OrdinalIgnoreCase) ||
                     // The embedded playlist player. Checked before /embed/ID: "videoseries"
                     // is eleven letters and would pass as a video id.
                     string.Equals(path.TrimEnd('/'), "/embed/videoseries", StringComparison.OrdinalIgnoreCase))
            {
                return listId is null
                    ? UrlValidationResult.Fail("找不到播放清單 ID")
                    : UrlValidationResult.OkPlaylist(listId);
            }
            else if (VideoIdPaths.FirstOrDefault(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix)
            {
                videoId = path[prefix.Length..].TrimEnd('/');
                // A channel's live embed (/embed/live_stream?channel=…): eleven letters as
                // well, and not a video.
                if (string.Equals(videoId, "live_stream", StringComparison.OrdinalIgnoreCase))
                    return UrlValidationResult.Fail("不支援這個路徑");
            }
            else
            {
                return UrlValidationResult.Fail("不支援這個路徑");
            }
        }

        if (videoId is null || !VideoIdPattern.IsMatch(videoId))
            return UrlValidationResult.Fail("影片 ID 格式錯誤");

        var canonical = $"https://www.youtube.com/watch?v={videoId}";
        if (startSeconds is not null) canonical += $"&t={startSeconds}";
        return UrlValidationResult.Ok(canonical) with { VideoId = videoId, PlaylistId = listId };
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
