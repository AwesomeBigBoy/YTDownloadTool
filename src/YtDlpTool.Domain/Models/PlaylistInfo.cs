namespace YtDlpTool.Domain.Models;

/// <summary>v1.5.0: whether a playlist's video can be downloaded now.</summary>
public enum PlaylistEntryState
{
    Available,
    /// <summary>"[Private video]" / "[Deleted video]" placeholders, or needs sign-in.</summary>
    Unavailable,
    /// <summary>Streaming now: the download would never end and hold a slot.</summary>
    Live,
    /// <summary>A premiere or stream that has not started.</summary>
    Upcoming,
}

/// <summary>v1.5.0: one video of a playlist, as listed on the playlist page.</summary>
public sealed record PlaylistEntry(string VideoId, string Title, TimeSpan? Duration, PlaylistEntryState State)
{
    public bool IsAvailable => State == PlaylistEntryState.Available;

    public string Url => "https://www.youtube.com/watch?v=" + VideoId;
}

/// <summary>v1.5.0: a playlist read with yt-dlp --flat-playlist.</summary>
public sealed record PlaylistInfo(string Id, string Title, IReadOnlyList<PlaylistEntry> Entries);
