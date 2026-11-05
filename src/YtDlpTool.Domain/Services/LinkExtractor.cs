using System.Text.RegularExpressions;

namespace YtDlpTool.Domain.Services;

public enum LinkKind
{
    Video,
    Playlist,
}

/// <summary>A YouTube link found in pasted or dropped text (v1.5.0).</summary>
/// <param name="Url">Canonical: https://www.youtube.com/watch?v=ID (no start time) or …/playlist?list=ID.</param>
/// <param name="Id">The video id, or the list id for a playlist page.</param>
/// <param name="PlaylistId">For a video opened from a playlist, that playlist's id.</param>
public sealed record LinkTarget(LinkKind Kind, string Url, string Id, string? PlaylistId = null);

/// <summary>
/// v1.5.0: finds the YouTube links in a block of text — several lines pasted at once, text
/// or a link dragged from a browser, an Internet Shortcut (.url) file. Every candidate goes
/// through <see cref="UrlValidator"/>, so the host and id rules are exactly those of a
/// typed link. Duplicates (the same video as youtu.be and watch?v=) are listed once.
/// </summary>
public static class LinkExtractor
{
    // Only ASCII URL characters, so 「。」「）」 and other full-width punctuation end a link.
    // Without a scheme, the lookbehind keeps "notyoutube.com" from matching as youtube.com;
    // with one, the host follows "//" directly, so a link glued to the previous one
    // ("…youtu.be/AAAAAAAAAAAhttps://…") still starts a new match — and a link ends where
    // the next "http://" or "https://" begins.
    private static readonly Regex Candidate = new(
        @"(?:https?://|(?<![A-Za-z0-9.\-]))(?:(?:www|m|music)\.)?(?:youtube\.com|youtu\.be)/" +
        @"(?:(?!https?://)[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%])*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<LinkTarget> Extract(string? text)
    {
        var found = new List<LinkTarget>();
        if (string.IsNullOrWhiteSpace(text)) return found;

        // Mail link protection and search redirects carry the real address percent-encoded
        // in a parameter (?url=https%3A%2F%2Fwww.youtube.com%2F…); one level of decoding
        // exposes it, and UrlValidator still checks whatever comes out.
        var texts = new List<string> { text };
        if (text.Contains('%'))
        {
            var decoded = Uri.UnescapeDataString(text);
            if (decoded != text) texts.Add(decoded);
        }

        var validator = new UrlValidator();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in texts.SelectMany(t => Candidate.Matches(t)))
        {
            // Sentence punctuation right after a link is not part of it.
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"');
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[7..];
            else if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

            var r = validator.Validate(url);
            if (!r.IsValid) continue;
            var target = r.IsPlaylist
                ? new LinkTarget(LinkKind.Playlist, r.CanonicalUrl!, r.PlaylistId!, r.PlaylistId)
                : new LinkTarget(LinkKind.Video, "https://www.youtube.com/watch?v=" + r.VideoId, r.VideoId!, r.PlaylistId);
            if (seen.Add(target.Kind + ":" + target.Id)) found.Add(target);
        }
        return found;
    }
}
