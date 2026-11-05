namespace YtDlpTool.Process;

/// <summary>v1.5.0: the parts of yt-dlp's --flat-playlist --dump-single-json the app reads.</summary>
public sealed class YtDlpPlaylistDto
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public YtDlpPlaylistEntryDto[]? Entries { get; set; }
}

public sealed class YtDlpPlaylistEntryDto
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public double? Duration { get; set; }
    // "private", "needs_auth", "subscriber_only", "premium_only", "public", … — often absent.
    public string? Availability { get; set; }
    // "is_live", "is_upcoming", "was_live", "not_live", "post_live" — often absent.
    public string? LiveStatus { get; set; }
}
