namespace YtDlpTool.Domain.Services;

public sealed record UrlValidationResult(bool IsValid, string? CanonicalUrl, string? Reason)
{
    /// <summary>v1.5.0: the 11-character video id; null for a playlist page or a failure.</summary>
    public string? VideoId { get; init; }

    /// <summary>
    /// v1.5.0: the list= id — of the playlist page itself, or of the playlist a video was
    /// opened from (watch?v=…&amp;list=…).
    /// </summary>
    public string? PlaylistId { get; init; }

    /// <summary>v1.5.0: a playlist page rather than one video; CanonicalUrl is the playlist URL.</summary>
    public bool IsPlaylist => IsValid && VideoId is null && PlaylistId is not null;

    public static UrlValidationResult Ok(string canonical) => new(true, canonical, null);
    public static UrlValidationResult Fail(string reason) => new(false, null, reason);

    public static UrlValidationResult OkPlaylist(string listId) =>
        new(true, "https://www.youtube.com/playlist?list=" + listId, null) { PlaylistId = listId };
}
