using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

/// <summary>
/// Whether a download gets YouTube's thumbnail embedded as its cover (yt-dlp
/// --embed-thumbnail).
///
/// v1.5.0, the user's call: 音訊 only. Music players and Explorer show an audio file's
/// cover; a video player shows the video. On PCs with endpoint security embedding was the
/// single largest cost of a small 影片 download — mutagen rewrites the .mp4 in place and each
/// step is held ~3 s (37.4 s → 18.0 s for a 19-s clip without it, measured 2026-11-04).
/// 無聲影片 never had one (v1.1.32: the ffprobe step failed on installs without ffprobe).
/// A 擷取片段 goes through ffmpeg's cut, which never carried it.
/// </summary>
public static class CoverArt
{
    public static bool Embeds(DownloadMode mode, bool hasClip) => !hasClip && mode == DownloadMode.AudioOnly;
}
