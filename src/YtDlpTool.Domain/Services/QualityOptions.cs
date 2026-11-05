using System.Globalization;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

/// <summary>One row of the 畫質 / 音質 dropdown.</summary>
public sealed record QualityChoice(string Label, VideoFormat Format)
{
    // ComboBox items are announced to screen readers through ToString(); the record's
    // generated ToString would read out every property of the format.
    public override string ToString() => Label;
}

/// <summary>
/// Decides what the quality dropdown offers for a parsed video (v1.5.0).
///
/// Before v1.5.0 the list showed only the three highest resolutions, each the largest
/// file at that height — so a 4K video offered no way to pick 720p, the largest file was
/// sometimes a stream that already carried audio (a 「無聲影片」 that played sound), and
/// vertical Shorts read "1920p". This class is pure so all of that stays pinned by tests.
/// </summary>
public static class QualityOptions
{
    public static IReadOnlyList<QualityChoice> Build(DownloadMode mode, IReadOnlyList<VideoFormat> formats) =>
        mode == DownloadMode.AudioOnly
            ? BuildAudio(formats)
            : BuildVideo(formats, addAudioSize: mode == DownloadMode.AudioAndVideo);

    /// <summary>
    /// Index to preselect: the best video choice at or below 1080p (4K by default means
    /// multi-gigabyte downloads on office networks), else the last; the first audio
    /// choice; -1 for an empty list.
    /// </summary>
    public static int DefaultIndex(IReadOnlyList<QualityChoice> choices)
    {
        if (choices.Count == 0) return -1;
        if (choices[0].Format.Height is null) return 0;
        for (var i = 0; i < choices.Count; i++)
        {
            if (ResolutionOf(choices[i].Format) <= 1080) return i;
        }
        return choices.Count - 1;
    }

    /// <summary>Preference meaning "the highest quality this video has" (the batch dialog's 最高畫質).</summary>
    public const string Best = "best";

    /// <summary>
    /// v1.5.0: index to preselect for a remembered preference. "1080p": the best choice at
    /// or below that resolution (never more than the user chose), else the lowest. "best":
    /// the highest. "M4A" / "MP3": that kind of audio. A preference that does not fit the
    /// list — none yet, an audio one on a video list — falls back to DefaultIndex.
    /// </summary>
    public static int PreferredIndex(IReadOnlyList<QualityChoice> choices, string? preference)
    {
        if (choices.Count == 0) return -1;
        if (string.IsNullOrWhiteSpace(preference)) return DefaultIndex(choices);

        if (choices[0].Format.Height is null)
        {
            for (var i = 0; i < choices.Count; i++)
            {
                if (string.Equals(ShortLabel(DownloadMode.AudioOnly, choices[i].Format), preference,
                        StringComparison.OrdinalIgnoreCase)) return i;
            }
            return DefaultIndex(choices);
        }

        if (string.Equals(preference, Best, StringComparison.OrdinalIgnoreCase)) return 0;
        if (!TryParseResolution(preference, out var wanted)) return DefaultIndex(choices);
        for (var i = 0; i < choices.Count; i++)
        {
            if (ResolutionOf(choices[i].Format) <= wanted) return i;   // highest first
        }
        return choices.Count - 1;
    }

    private static bool TryParseResolution(string s, out int value)
    {
        var digits = s.EndsWith('p') || s.EndsWith('P') ? s[..^1] : s;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    /// <summary>
    /// Compact name of the chosen quality for the queue row's tag — "1080p", "M4A", "MP3" —
    /// worded the same way as the dropdown it was picked from.
    /// </summary>
    public static string ShortLabel(DownloadMode mode, VideoFormat format) =>
        mode == DownloadMode.AudioOnly
            ? (IsM4a(format) ? "M4A" : "MP3")
            : ResolutionOf(format) + "p";

    /// <summary>
    /// v1.5.0: what the file name says after the title — 「1080p」, 「1080p 無聲」 (so it is
    /// not mistaken for the 影片 file of the same quality), 「2160p HDR」, an M4A's own
    /// bitrate 「129kbps」, or the bitrate an MP3 is encoded at (<see cref="Mp3Kbps"/>).
    /// Empty when there is nothing to say.
    /// </summary>
    public static string FileNameLabel(DownloadMode mode, VideoFormat format)
    {
        if (mode == DownloadMode.AudioOnly)
        {
            if (!IsM4a(format)) return Mp3Kbps(format) + "kbps";
            return format.AudioBitrateKbps is > 0 and var kbps ? NominalKbps(kbps) + "kbps" : "";
        }
        if (format.Height is not > 0) return "";
        var label = ResolutionOf(format) + "p";
        if (IsHdr(format)) label += " HDR";
        if (mode == DownloadMode.VideoOnly) label += " 無聲";
        return label;
    }

    private static readonly int[] Mp3Bitrates = { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };

    /// <summary>
    /// v1.5.0: the bitrate a player shows. YouTube's abr includes the container — format 140
    /// is 128 kbps AAC reported as ~129.5, 139 is 48 reported as ~48.8 — so a value within 5 %
    /// of a standard bitrate is shown as that one; anything else as reported.
    /// </summary>
    private static int NominalKbps(int reported)
    {
        var nearest = Mp3Bitrates.MinBy(b => Math.Abs(b - reported));
        return Math.Abs(nearest - reported) <= nearest * 0.05 ? nearest : reported;
    }

    /// <summary>
    /// v1.5.0: the bitrate an MP3 is encoded at — the first standard one at or above its
    /// source's, so converting keeps about what the source has and the file name can say it
    /// (yt-dlp's default VBR came out near 130 kbps from a 160 kbps source); 192 when the
    /// source's is unknown.
    /// </summary>
    public static int Mp3Kbps(VideoFormat source)
    {
        var kbps = source.AudioBitrateKbps ?? 0;
        if (kbps <= 0) return 192;
        foreach (var b in Mp3Bitrates)
        {
            if (b >= kbps) return b;
        }
        return Mp3Bitrates[^1];
    }

    /// <summary>Human file size in 1024 steps with at most one decimal: "105 MB", "1.2 GB".</summary>
    public static string FormatSize(long bytes)
    {
        double v = bytes;
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[u];
    }

    private static IReadOnlyList<QualityChoice> BuildVideo(IReadOnlyList<VideoFormat> formats, bool addAudioSize)
    {
        // AudioAndVideo merges the chosen stream with bestaudio[ext=m4a] (YtDlpRunner),
        // so the file the user gets is roughly the two sizes added together.
        var audioSize = addAudioSize ? BestAudio(formats, m4aOnly: true)?.FileSizeBytes : 0;

        return formats
            .Where(f => f.VideoCodec is not null && f.Height is > 0)
            .GroupBy(ResolutionOf)
            .OrderByDescending(g => g.Key)
            .Select(g => g
                // A stream that already carries audio would put sound into 無聲影片 and a
                // second audio track into 影片; use it only when nothing else exists.
                .OrderBy(f => f.AudioCodec is null ? 0 : 1)
                // HDR is the larger file and looks washed out without an HDR screen and
                // player; SDR first, HDR (labelled) only when it is all there is.
                .ThenBy(f => IsHdr(f) ? 1 : 0)
                .ThenBy(f => CodecRank(f.VideoCodec!))
                .ThenByDescending(f => f.FileSizeBytes ?? 0)
                .First())
            .Select(f => new QualityChoice(VideoLabel(f, audioSize), f))
            .ToList();
    }

    private static IReadOnlyList<QualityChoice> BuildAudio(IReadOnlyList<VideoFormat> formats)
    {
        var choices = new List<QualityChoice>();

        // M4A first: it is saved as-is (no re-encode) and plays everywhere.
        if (BestAudio(formats, m4aOnly: true) is { } m4a)
        {
            var label = "M4A";
            if (m4a.AudioBitrateKbps is > 0 and var kbps) label += $" · {NominalKbps(kbps)} kbps";
            if (m4a.FileSizeBytes is { } size) label += $" · 約 {FormatSize(size)}";
            choices.Add(new QualityChoice(label, m4a));
        }

        // Anything else is converted to MP3 by yt-dlp (YtDlpRunner.InferAudioFormat).
        // The MP3's size is not knowable in advance, so the label does not guess.
        var other = formats
            .Where(f => IsAudioOnly(f) && !IsM4a(f))
            .OrderByDescending(f => f.AudioBitrateKbps ?? 0)
            .FirstOrDefault();
        if (other is not null) choices.Add(new QualityChoice("MP3（轉檔）", other));

        return choices;
    }

    private static string VideoLabel(VideoFormat f, long? audioSize)
    {
        var p = ResolutionOf(f);
        var label = p + "p" + p switch
        {
            >= 4320 => " (8K)",
            >= 2160 => " (4K)",
            >= 1440 => " (2K)",
            _ => "",
        };
        if (IsHdr(f)) label += " HDR";
        if (f.FileSizeBytes is { } video)
            label += " · 約 " + FormatSize(video + (audioSize ?? 0));
        return label;
    }

    /// <summary>
    /// The "p" number people know, as YouTube names its streams. Portrait: the width — a
    /// 1080x1920 Short and a 1080x2400 screen recording are both 1080p. Landscape: the
    /// height, unless the frame is wider than 16:9 — a 1440x1080 (4:3) video is 1080p, a
    /// letterboxed 2560x1066 film 1440p (the height a 16:9 frame of that width would have),
    /// not "1066p".
    /// </summary>
    private static int ResolutionOf(VideoFormat f)
    {
        var h = f.Height ?? 0;
        if (f.Width is not (> 0 and var w)) return h;
        if (h > w) return w;
        return Math.Max(h, (int)Math.Round(w * 9 / 16.0, MidpointRounding.AwayFromZero));
    }

    private static bool IsHdr(VideoFormat f)
    {
        if (f.DynamicRange is { Length: > 0 } range)
            return !range.Equals("SDR", StringComparison.OrdinalIgnoreCase);
        // No dynamic_range reported: VP9 profile 2 (10-bit) is how YouTube serves HDR.
        var codec = f.VideoCodec ?? "";
        return codec.StartsWith("vp09.02", StringComparison.OrdinalIgnoreCase) ||
               codec.StartsWith("vp9.2", StringComparison.OrdinalIgnoreCase);
    }

    private static int CodecRank(string codec)
    {
        if (codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("h264", StringComparison.OrdinalIgnoreCase)) return 0;
        if (codec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase)) return 1;
        if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static VideoFormat? BestAudio(IReadOnlyList<VideoFormat> formats, bool m4aOnly) =>
        formats
            .Where(f => IsAudioOnly(f) && (!m4aOnly || IsM4a(f)))
            .OrderByDescending(f => f.AudioBitrateKbps ?? 0)
            .FirstOrDefault();

    private static bool IsAudioOnly(VideoFormat f) => f.AudioCodec is not null && f.VideoCodec is null;

    private static bool IsM4a(VideoFormat f) => f.Extension is "m4a" or "mp4";
}
