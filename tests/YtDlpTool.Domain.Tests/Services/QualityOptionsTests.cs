using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class QualityOptionsTests
{
    private const long MiB = 1024 * 1024;

    private static VideoFormat V(string id, int? height, string codec, long? size, string ext = "mp4")
        => new(id, height, codec, null, ext, size, null);

    private static VideoFormat A(string id, string ext, string codec, int abr, long? size)
        => new(id, null, null, codec, ext, size, abr);

    private static IReadOnlyList<QualityChoice> Video(params VideoFormat[] f)
        => QualityOptions.Build(DownloadMode.VideoOnly, f);

    // ── video modes ────────────────────────────────────────────────────────────

    [Fact]
    public void Video_OneChoicePerHeight_HighestFirst()
    {
        var choices = Video(
            V("134", 360, "avc1.4d401e", 10 * MiB),
            V("137", 1080, "avc1.640028", 100 * MiB),
            V("248", 1080, "vp09.00.40.08", 80 * MiB, "webm"),
            V("136", 720, "avc1.4d401f", 50 * MiB));

        Assert.Equal(new int?[] { 1080, 720, 360 }, choices.Select(c => c.Format.Height));
    }

    [Fact]
    public void Video_PrefersH264AtSameHeight()
    {
        // VP9 is the LARGER file here on purpose: compatibility (Windows' built-in
        // player, PowerPoint, LINE) beats size, so H.264 wins.
        var c = Assert.Single(Video(
            V("248", 1080, "vp09.00.40.08", 120 * MiB, "webm"),
            V("137", 1080, "avc1.640028", 100 * MiB),
            V("399", 1080, "av01.0.08M.08", 60 * MiB)));

        Assert.Equal("137", c.Format.FormatId);
    }

    [Fact]
    public void Video_AboveH264_PrefersVp9OverAv1()
    {
        // YouTube has no H.264 above 1080p. VP9 decodes on every Windows 10/11 box;
        // AV1 needs an extension and is slow without hardware support.
        var c = Assert.Single(Video(
            V("401", 2160, "av01.0.12M.08", 300 * MiB),
            V("313", 2160, "vp9", 400 * MiB, "webm")));

        Assert.Equal("313", c.Format.FormatId);
    }

    [Fact]
    public void Video_SameCodecAtSameHeight_PicksTheLargerFile()
    {
        // e.g. 1080p60 vs 1080p30: the bigger stream is the better one.
        var c = Assert.Single(Video(
            V("137", 1080, "avc1.640028", 100 * MiB),
            V("299", 1080, "avc1.64002a", 150 * MiB)));

        Assert.Equal("299", c.Format.FormatId);
    }

    [Fact]
    public void Video_PrefersVideoOnlyStreamsOverCombined()
    {
        // Format 18 carries its own audio. In 無聲影片 mode it would deliver a video WITH
        // sound, and in 影片 mode merging it with bestaudio yields two audio tracks.
        var combined = new VideoFormat("18", 360, "avc1.42001E", "mp4a.40.2", "mp4", 30 * MiB, 96);

        var c = Assert.Single(Video(combined, V("134", 360, "avc1.4d401e", 10 * MiB)));

        Assert.Equal("134", c.Format.FormatId);
    }

    [Fact]
    public void Video_CombinedStreamIsUsedWhenItIsTheOnlyOneAtThatHeight()
    {
        var combined = new VideoFormat("18", 360, "avc1.42001E", "mp4a.40.2", "mp4", 30 * MiB, 96);

        var c = Assert.Single(Video(combined));

        Assert.Equal("18", c.Format.FormatId);
    }

    [Fact]
    public void AudioAndVideo_LabelAddsM4aSize()
    {
        var c = Assert.Single(QualityOptions.Build(DownloadMode.AudioAndVideo, new[]
        {
            V("137", 1080, "avc1.640028", 100 * MiB),
            A("140", "m4a", "mp4a.40.2", 129, 5 * MiB),
            A("251", "webm", "opus", 160, 7 * MiB),
        }));

        Assert.Equal("1080p · 約 105 MB", c.Label);
    }

    [Fact]
    public void VideoOnly_LabelIsTheVideoSizeAlone()
    {
        var c = Assert.Single(QualityOptions.Build(DownloadMode.VideoOnly, new[]
        {
            V("137", 1080, "avc1.640028", 100 * MiB),
            A("140", "m4a", "mp4a.40.2", 129, 5 * MiB),
        }));

        Assert.Equal("1080p · 約 100 MB", c.Label);
    }

    [Fact]
    public void UnknownSize_OmitsSize()
    {
        // HLS formats carry no size. "約 ?" is noise; just show the resolution.
        var c = Assert.Single(QualityOptions.Build(DownloadMode.AudioAndVideo, new[]
        {
            V("232", 720, "avc1.4d401f", null),
            A("140", "m4a", "mp4a.40.2", 129, 5 * MiB),
        }));

        Assert.Equal("720p", c.Label);
    }

    [Fact]
    public void Labels_Mark8K4KAnd2K()
    {
        var choices = Video(
            V("402", 4320, "av01.0.16M.08", null),
            V("313", 2160, "vp9", null, "webm"),
            V("271", 1440, "vp9", null, "webm"));

        Assert.Equal(new[] { "4320p (8K)", "2160p (4K)", "1440p (2K)" }, choices.Select(c => c.Label));
    }

    [Fact]
    public void Video_VerticalVideo_IsLabelledAndDefaultedByItsShorterSide()
    {
        // A Short is 1080x1920. Users know it as "1080p"; labelling by height would
        // read "1920p" and make DefaultIndex skip it as "above 1080".
        var choices = Video(
            new VideoFormat("299", 1920, "avc1.64002a", null, "mp4", 40 * MiB, null, Width: 1080),
            new VideoFormat("298", 1280, "avc1.4d4020", null, "mp4", 20 * MiB, null, Width: 720));

        Assert.Equal(new[] { "1080p · 約 40 MB", "720p · 約 20 MB" }, choices.Select(c => c.Label));
        Assert.Equal(0, QualityOptions.DefaultIndex(choices));
    }

    [Fact]
    public void Video_IgnoresFormatsWithoutHeight()
    {
        var choices = Video(
            V("x", null, "avc1", 1000),
            V("y", 0, "avc1", 1000),
            V("134", 360, "avc1.4d401e", 10 * MiB));

        Assert.Equal("134", Assert.Single(choices).Format.FormatId);
    }

    [Fact]
    public void Video_NoVideoFormats_Empty()
    {
        var audioOnly = new[] { A("140", "m4a", "mp4a.40.2", 129, 5 * MiB) };

        Assert.Empty(QualityOptions.Build(DownloadMode.VideoOnly, audioOnly));
        Assert.Empty(QualityOptions.Build(DownloadMode.AudioAndVideo, audioOnly));
    }

    // ── audio mode ─────────────────────────────────────────────────────────────

    [Fact]
    public void Audio_M4aFirstThenMp3()
    {
        var choices = QualityOptions.Build(DownloadMode.AudioOnly, new[]
        {
            A("251", "webm", "opus", 160, 7 * MiB),
            A("140", "m4a", "mp4a.40.2", 129, 5 * MiB),
            A("139", "m4a", "mp4a.40.5", 49, 2 * MiB),
            V("137", 1080, "avc1.640028", 100 * MiB),
        });

        Assert.Equal(2, choices.Count);
        Assert.Equal("140", choices[0].Format.FormatId);
        // YouTube reports format 140 at ~129.5 kbps (container included); it is 128 kbps AAC,
        // which is what a player shows (v1.5.0).
        Assert.Equal("M4A · 128 kbps · 約 5 MB", choices[0].Label);
        Assert.Equal("251", choices[1].Format.FormatId);
        Assert.Equal("MP3（轉檔）", choices[1].Label);
    }

    [Fact]
    public void Audio_OnlyNonM4aSource_OffersMp3Alone()
    {
        var c = Assert.Single(QualityOptions.Build(DownloadMode.AudioOnly, new[]
        {
            A("251", "webm", "opus", 160, 7 * MiB),
        }));

        Assert.Equal("MP3（轉檔）", c.Label);
    }

    [Fact]
    public void Audio_NoAudioFormats_Empty()
    {
        Assert.Empty(QualityOptions.Build(DownloadMode.AudioOnly, new[] { V("137", 1080, "avc1", 100 * MiB) }));
    }

    // ── defaults and formatting ────────────────────────────────────────────────

    [Fact]
    public void DefaultIndex_PicksBestAtOrBelow1080()
    {
        // 4K by default means multi-GB downloads on office networks; 1080p is the
        // sensible default, and the higher ones stay one click away.
        var choices = Video(
            V("313", 2160, "vp9", null, "webm"),
            V("271", 1440, "vp9", null, "webm"),
            V("137", 1080, "avc1.640028", null),
            V("136", 720, "avc1.4d401f", null));

        Assert.Equal(2, QualityOptions.DefaultIndex(choices));
    }

    [Fact]
    public void DefaultIndex_NothingAtOrBelow1080_PicksLast()
    {
        var choices = Video(V("313", 2160, "vp9", null, "webm"), V("271", 1440, "vp9", null, "webm"));

        Assert.Equal(1, QualityOptions.DefaultIndex(choices));
    }

    [Fact]
    public void DefaultIndex_Audio_IsFirst()
    {
        var choices = QualityOptions.Build(DownloadMode.AudioOnly, new[]
        {
            A("251", "webm", "opus", 160, 7 * MiB),
            A("140", "m4a", "mp4a.40.2", 129, 5 * MiB),
        });

        Assert.Equal(0, QualityOptions.DefaultIndex(choices));
    }

    [Fact]
    public void DefaultIndex_Empty_IsMinusOne()
    {
        Assert.Equal(-1, QualityOptions.DefaultIndex(Array.Empty<QualityChoice>()));
    }

    [Fact]
    public void Choice_ToStringIsLabel()
    {
        // ComboBox items announce ToString() to screen readers; a record's default
        // ToString read out the whole object graph.
        var c = Assert.Single(Video(V("137", 1080, "avc1.640028", null)));

        Assert.Equal("1080p", c.ToString());
    }

    [Fact]
    public void ShortLabel_Video_UsesTheShorterSide()
    {
        // The queue row's tag must agree with what the dropdown said, Shorts included.
        var shortVideo = new VideoFormat("299", 1920, "avc1.64002a", null, "mp4", null, null, Width: 1080);

        Assert.Equal("1080p", QualityOptions.ShortLabel(DownloadMode.AudioAndVideo, shortVideo));
        Assert.Equal("1080p", QualityOptions.ShortLabel(DownloadMode.VideoOnly, shortVideo));
    }

    [Fact]
    public void ShortLabel_Audio_NamesTheOutputFormat()
    {
        Assert.Equal("M4A", QualityOptions.ShortLabel(DownloadMode.AudioOnly, A("140", "m4a", "mp4a.40.2", 129, null)));
        Assert.Equal("MP3", QualityOptions.ShortLabel(DownloadMode.AudioOnly, A("251", "webm", "opus", 160, null)));
    }

    [Theory]
    [InlineData(105L * 1024 * 1024, "105 MB")]
    [InlineData(1288490189L, "1.2 GB")]
    [InlineData(512L * 1024, "512 KB")]
    [InlineData(900L, "900 B")]
    public void FormatSize_Uses1024StepsAndOneDecimal(long bytes, string expected)
    {
        Assert.Equal(expected, QualityOptions.FormatSize(bytes));
    }

    // ── PreferredIndex: the remembered 畫質 / 音質 (v1.5.0) ──────────────────────

    private static IReadOnlyList<QualityChoice> Heights(params int[] heights)
        => Video(heights.Select(h => V("v" + h, h, "avc1", h * MiB)).ToArray());

    private static int HeightAt(IReadOnlyList<QualityChoice> choices, int index)
        => choices[index].Format.Height!.Value;

    private static IReadOnlyList<QualityChoice> AudioChoices()
        => QualityOptions.Build(DownloadMode.AudioOnly, new[]
        {
            A("140", "m4a", "mp4a.40.2", 129, 5 * MiB),
            A("251", "webm", "opus", 160, 6 * MiB),
        });

    [Fact]
    public void Preferred_Video_SameResolutionWhenTheVideoHasIt()
    {
        var choices = Heights(2160, 1440, 1080, 720, 480);
        Assert.Equal(720, HeightAt(choices, QualityOptions.PreferredIndex(choices, "720p")));
    }

    [Fact]
    public void Preferred_Video_NextLowerWhenTheVideoLacksIt()
    {
        // Never more than the user chose: a 720p preference must not turn into a 1080p download.
        var choices = Heights(1080, 480, 360);
        Assert.Equal(480, HeightAt(choices, QualityOptions.PreferredIndex(choices, "720p")));
    }

    [Fact]
    public void Preferred_Video_LowestWhenEveryChoiceIsHigher()
    {
        var choices = Heights(2160, 1080);
        Assert.Equal(1080, HeightAt(choices, QualityOptions.PreferredIndex(choices, "480p")));
    }

    [Fact]
    public void Preferred_Video_Best_IsTheHighestChoice()
    {
        var choices = Heights(2160, 1080, 720);
        Assert.Equal(0, QualityOptions.PreferredIndex(choices, "best"));
    }

    [Fact]
    public void Preferred_Video_ShortsCompareByTheShorterSide()
    {
        // A vertical 1080x1920 Short is "1080p" in the dropdown, so a remembered 1080p picks it.
        var choices = Video(
            new VideoFormat("v1920", 1920, "avc1", null, "mp4", 90 * MiB, null, Width: 1080),
            new VideoFormat("v1280", 1280, "avc1", null, "mp4", 40 * MiB, null, Width: 720));
        Assert.Equal("v1920", choices[QualityOptions.PreferredIndex(choices, "1080p")].Format.FormatId);
    }

    // ── letterboxed films, HDR, storyboards ────────────────────────────────────

    private static VideoFormat Wide(string id, int width, int height, string codec = "avc1.640028", long size = 40 * MiB,
        string? range = null)
        => new(id, height, codec, null, codec.StartsWith("avc") ? "mp4" : "webm", size, null, Width: width, DynamicRange: range);

    [Fact]
    public void Video_LetterboxedStreams_AreNamedByTheirWidth()
    {
        // A 2.4:1 film is encoded 2560x1066, 1920x800, 1280x534 — YouTube's 1440p, 1080p
        // and 720p. Named by the shorter side, the 2560-wide stream read "1066p".
        var choices = Video(
            Wide("400", 2560, 1066, "vp09.00.50.08", 60 * MiB),
            Wide("137", 1920, 800),
            Wide("136", 1280, 534));

        Assert.Equal(new[] { "1440p (2K)", "1080p", "720p" }, choices.Select(c => c.Label.Split(" · ")[0]));
        Assert.Equal("1440p", QualityOptions.ShortLabel(DownloadMode.VideoOnly, choices[0].Format));
    }

    [Fact]
    public void Video_PortraitTallerThanNineBySixteen_IsNamedByItsWidth()
    {
        // A phone screen recording, 1080x2400: YouTube's 1080p, not "1350p" — which also
        // pushed a 1080p preference down to the next class.
        var c = Assert.Single(Video(Wide("p", 1080, 2400)));
        Assert.StartsWith("1080p", c.Label);
        Assert.Equal(0, QualityOptions.PreferredIndex(new[] { c }, "1080p"));
    }

    [Fact]
    public void Video_FourByThreeAndSquare_KeepTheirHeight()
    {
        var choices = Video(Wide("a", 1440, 1080), Wide("b", 720, 720), Wide("c", 640, 480));
        Assert.Equal(new[] { "1080p", "720p", "480p" }, choices.Select(c => c.Label.Split(" · ")[0]));
    }

    [Fact]
    public void Preferred_Video_1080pCeiling_SkipsALetterboxed1440pStream()
    {
        // As "1066p" the 1440p-class stream slipped under a 1080p ceiling.
        var choices = Video(Wide("400", 2560, 1066, "vp09.00.50.08", 60 * MiB), Wide("137", 1920, 800));

        Assert.Equal("137", choices[QualityOptions.PreferredIndex(choices, "1080p")].Format.FormatId);
        Assert.Equal("137", choices[QualityOptions.DefaultIndex(choices)].Format.FormatId);
    }

    [Fact]
    public void Video_SdrBeatsALargerHdrStreamAtTheSameHeight()
    {
        // At 1440p/2160p YouTube lists HDR (VP9 profile 2) next to SDR VP9. HDR is the larger
        // file and looks washed out on screens and in players without HDR.
        var c = Assert.Single(Video(
            Wide("337", 3840, 2160, "vp09.02.51.10.01.09.16.09.00", 500 * MiB, "HDR10"),
            Wide("313", 3840, 2160, "vp9", 400 * MiB, "SDR")));

        Assert.Equal("313", c.Format.FormatId);
        Assert.DoesNotContain("HDR", c.Label);
    }

    [Fact]
    public void Video_HdrWhenNothingElseExists_IsLabelledHdr()
    {
        var c = Assert.Single(Video(Wide("337", 3840, 2160, "vp09.02.51.10.01.09.16.09.00", 500 * MiB, "HDR10")));
        Assert.StartsWith("2160p (4K) HDR", c.Label);
    }

    [Fact]
    public void Video_Vp9Profile2WithoutARange_CountsAsHdr()
    {
        // Older yt-dlp output has no dynamic_range; profile 2 (10-bit) VP9 is YouTube's HDR.
        var c = Assert.Single(Video(
            Wide("337", 3840, 2160, "vp09.02.51.10.01.09.16.09.00", 500 * MiB),
            Wide("313", 3840, 2160, "vp09.00.51.08.01.01.01.01.00", 400 * MiB)));
        Assert.Equal("313", c.Format.FormatId);
    }

    [Theory]
    [InlineData(DownloadMode.AudioAndVideo)]
    [InlineData(DownloadMode.VideoOnly)]
    [InlineData(DownloadMode.AudioOnly)]
    public void StoryboardImages_AreNeverOffered(DownloadMode mode)
    {
        // yt-dlp lists the seek-bar preview images as formats: sb0…, mhtml, with vcodec and
        // acodec "none", which MapToMetadata turns into null.
        var storyboard = new VideoFormat("sb0", 90, null, null, "mhtml", null, null, Width: 160);
        Assert.Empty(QualityOptions.Build(mode, new[] { storyboard }));
    }

    // ── v1.5.0: what the file name says ────────────────────────────────────────

    [Fact]
    public void FileNameLabel_Video_IsTheResolution()
    {
        Assert.Equal("1080p", QualityOptions.FileNameLabel(DownloadMode.AudioAndVideo, Wide("137", 1920, 1080)));
        Assert.Equal("1080p", QualityOptions.FileNameLabel(DownloadMode.AudioAndVideo, Wide("short", 1080, 1920)));
        Assert.Equal("1440p", QualityOptions.FileNameLabel(DownloadMode.AudioAndVideo, Wide("400", 2560, 1066, "vp09.00.50.08")));
    }

    [Fact]
    public void FileNameLabel_SilentVideo_SaysSo()
    {
        // Next to the 影片 file of the same quality, and the one without sound is the one to tell apart.
        Assert.Equal("1080p 無聲", QualityOptions.FileNameLabel(DownloadMode.VideoOnly, Wide("137", 1920, 1080)));
    }

    [Fact]
    public void FileNameLabel_HdrVideo_SaysSo()
    {
        Assert.Equal("2160p HDR", QualityOptions.FileNameLabel(DownloadMode.AudioAndVideo,
            Wide("337", 3840, 2160, "vp09.02.51.10.01.09.16.09.00", 500 * MiB, "HDR10")));
    }

    [Fact]
    public void FileNameLabel_M4a_IsItsBitrate()
    {
        // The nominal bitrate a player shows: YouTube's 129.5 for its 128 kbps AAC, 48.8 for 48.
        Assert.Equal("128kbps", QualityOptions.FileNameLabel(DownloadMode.AudioOnly, A("140", "m4a", "mp4a.40.2", 130, 5 * MiB)));
        Assert.Equal("48kbps", QualityOptions.FileNameLabel(DownloadMode.AudioOnly, A("139", "m4a", "mp4a.40.5", 49, 1 * MiB)));
        // Far from any standard bitrate: as reported.
        Assert.Equal("140kbps", QualityOptions.FileNameLabel(DownloadMode.AudioOnly, A("x", "m4a", "mp4a.40.2", 140, 5 * MiB)));
    }

    [Theory]
    // The MP3 is encoded at the first standard bitrate at or above its source, so the name is true.
    [InlineData(160, 160)]
    [InlineData(150, 160)]
    [InlineData(70, 80)]
    [InlineData(48, 48)]
    [InlineData(400, 320)]
    public void Mp3_IsEncodedAtAStandardBitrateAtOrAboveTheSource(int sourceKbps, int expected)
    {
        var source = A("251", "webm", "opus", sourceKbps, 6 * MiB);
        Assert.Equal(expected, QualityOptions.Mp3Kbps(source));
        Assert.Equal(expected + "kbps", QualityOptions.FileNameLabel(DownloadMode.AudioOnly, source));
    }

    [Fact]
    public void Mp3_WithoutAKnownSourceBitrate_Is192()
    {
        Assert.Equal(192, QualityOptions.Mp3Kbps(new VideoFormat("251", null, null, "opus", "webm", null, null)));
    }

    [Theory]
    [InlineData("M4A", 0)]
    [InlineData("MP3", 1)]
    public void Preferred_Audio_SameKind(string remembered, int expected)
    {
        Assert.Equal(expected, QualityOptions.PreferredIndex(AudioChoices(), remembered));
    }

    [Fact]
    public void Preferred_Audio_KindMissing_FallsBackToTheFirst()
    {
        var onlyM4a = QualityOptions.Build(DownloadMode.AudioOnly, new[] { A("140", "m4a", "mp4a.40.2", 129, 5 * MiB) });
        Assert.Equal(0, QualityOptions.PreferredIndex(onlyM4a, "MP3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("M4A")]     // an audio preference on a video list
    public void Preferred_NothingUsable_IsTheDefault(string? remembered)
    {
        var choices = Heights(2160, 1440, 1080, 720);
        Assert.Equal(QualityOptions.DefaultIndex(choices), QualityOptions.PreferredIndex(choices, remembered));
    }

    [Fact]
    public void Preferred_VideoPreferenceOnAnAudioList_IsTheDefault()
    {
        Assert.Equal(0, QualityOptions.PreferredIndex(AudioChoices(), "720p"));
    }

    [Fact]
    public void Preferred_Empty_IsMinusOne()
    {
        Assert.Equal(-1, QualityOptions.PreferredIndex(Array.Empty<QualityChoice>(), "720p"));
    }
}
