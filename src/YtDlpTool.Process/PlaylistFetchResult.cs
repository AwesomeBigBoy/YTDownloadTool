using YtDlpTool.Domain.Models;

namespace YtDlpTool.Process;

/// <summary>v1.5.0: outcome of <see cref="YtDlpRunner.FetchPlaylistAsync"/>.</summary>
/// <param name="NotAPlaylist">yt-dlp answered with one video instead of a list (a Mix it cannot expand).</param>
public sealed record PlaylistFetchResult(
    bool IsSuccess,
    PlaylistInfo? Playlist,
    string? ErrorStderr,
    bool NotAPlaylist = false);
