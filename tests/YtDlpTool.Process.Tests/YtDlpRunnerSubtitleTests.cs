using YtDlpTool.Process;
using YtDlpTool.Process.Tests.Helpers;

namespace YtDlpTool.Process.Tests;

/// <summary>
/// v1.1.13 — exercises the new <see cref="YtDlpRunner.DownloadSubtitlesOnlyAsync"/>
/// method that downloads subtitle sidecars in a STANDALONE yt-dlp invocation
/// (--skip-download). The companion media-download path must NEVER pass
/// --write-subs, so we also assert that here against the FakeYtDlp args log.
/// </summary>
public class YtDlpRunnerSubtitleTests
{
    [Fact]
    public async Task DownloadSubtitlesOnly_EmptyLangList_NoYtDlpCall_ReturnsSuccess()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-sub-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var res = await runner.DownloadSubtitlesOnlyAsync(
                "https://www.youtube.com/watch?v=FAKE0001234",
                Array.Empty<string>(),
                temp,
                "stem");

            Assert.True(res.IsSuccess);
            Assert.Empty(res.SubtitleFilePaths);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task DownloadSubtitlesOnly_PassesSkipDownloadAndWriteSubs()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-sub-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var res = await runner.DownloadSubtitlesOnlyAsync(
                "https://www.youtube.com/watch?v=FAKE0001234",
                new[] { "en", "zh-Hant" },
                temp,
                "Fake_Sub_Stem");

            Assert.True(res.IsSuccess, res.ErrorMessage);
            Assert.Equal(2, res.SubtitleFilePaths.Count);

            // Find the args log the fake dropped next to the first sub.
            var firstSub = res.SubtitleFilePaths[0];
            var argsLog = firstSub + ".args";
            Assert.True(File.Exists(argsLog), $"expected fake args log at {argsLog}");
            var received = await File.ReadAllLinesAsync(argsLog);

            Assert.Contains("--skip-download", received);
            Assert.Contains("--write-subs", received);
            Assert.Contains("--write-auto-subs", received);
            Assert.Contains("--sub-langs", received);
            // Lang code list is comma-joined and follows --sub-langs.
            var langIdx = Array.IndexOf(received, "--sub-langs");
            Assert.True(langIdx >= 0 && langIdx + 1 < received.Length);
            Assert.Equal("en,zh-Hant", received[langIdx + 1]);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Download_MediaOnly_OmitsSubtitleArgs()
    {
        // v1.1.13: media download must NEVER carry --write-subs / --sub-langs,
        // because bundling them changes the YouTube extractor path yt-dlp follows
        // for the media URL (heavier, JS-obfuscated, rate-limited). Subs are
        // downloaded in a separate yt-dlp call now.
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-media-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var format = new YtDlpTool.Domain.Models.VideoFormat(
                "299", 1080, "avc1", null, "mp4", 120_000_000, null);
            // Even when the request carries subtitle codes (legacy callers may
            // still pass them), DownloadAsync must NOT forward --write-subs.
            var request = new DownloadRequest(
                Url: "https://www.youtube.com/watch?v=FAKE0001234",
                Mode: YtDlpTool.Domain.Models.DownloadMode.VideoOnly,
                ChosenFormat: format,
                SubtitleLanguageCodes: new[] { "en", "zh-Hant" },
                ClipRange: null,
                SaveDirectory: temp,
                SanitizedFileStem: "Fake_Media_NoSubs");

            var result = await runner.DownloadAsync(request);
            Assert.True(result.IsSuccess, result.ErrorStderr);

            var argsLog = result.OutputFilePath + ".args";
            Assert.True(File.Exists(argsLog));
            var received = await File.ReadAllLinesAsync(argsLog);
            Assert.DoesNotContain("--write-subs", received);
            Assert.DoesNotContain("--write-auto-subs", received);
            Assert.DoesNotContain("--sub-langs", received);
            Assert.DoesNotContain("--embed-subs", received);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
