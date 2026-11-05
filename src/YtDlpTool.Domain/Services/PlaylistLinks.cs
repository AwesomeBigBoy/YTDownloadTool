namespace YtDlpTool.Domain.Services;

/// <summary>v1.5.0: the address that reads the playlist a pasted video was opened from.</summary>
public static class PlaylistLinks
{
    // 稍後觀看, 喜歡的影片, YouTube Music 喜歡的音樂: they exist only for a signed-in viewer.
    private static readonly HashSet<string> NeedSignIn = new(StringComparer.Ordinal) { "WL", "LL", "LM" };

    /// <summary>
    /// The watch page with list=, not /playlist?list=: YouTube refuses a Mix (RD…) on the
    /// playlist page as "This playlist type is unviewable", while yt-dlp --yes-playlist
    /// reads both kinds from the watch page (measured 2026-11-04: RDdQw4w9WgXcQ, 404
    /// videos). Null for lists that need sign-in, so no link is offered for them.
    /// </summary>
    public static string? ForVideo(string? videoId, string? listId) =>
        string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(listId) || NeedSignIn.Contains(listId)
            ? null
            : $"https://www.youtube.com/watch?v={videoId}&list={listId}";
}
