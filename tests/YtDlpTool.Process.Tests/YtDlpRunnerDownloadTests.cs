using YtDlpTool.Domain.Models;
using YtDlpTool.Process;
using YtDlpTool.Process.Tests.Helpers;

namespace YtDlpTool.Process.Tests;

public class YtDlpRunnerDownloadTests
{
    [Fact]
    public async Task Download_PostProcessingLines_AreReportedAsProcessing()
    {
        // yt-dlp prints "[Merger] …", "[EmbedThumbnail] …", "Deleting original file …"
        // once the bytes are in; the row says 處理中 from there on instead of a frozen speed.
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var reports = new List<ProgressReport>();
            var request = new DownloadRequest(
                Url: "https://www.youtube.com/watch?v=FAKE0001234",
                Mode: DownloadMode.AudioAndVideo,
                ChosenFormat: new VideoFormat("299", 1080, "avc1", null, "mp4", 120_000_000, null),
                SubtitleLanguageCodes: Array.Empty<string>(),
                ClipRange: null,
                SaveDirectory: temp,
                SanitizedFileStem: "Fake_Test_Video");

            var result = await runner.DownloadAsync(request, new SyncProgress(reports.Add));

            Assert.True(result.IsSuccess, result.ErrorStderr);
            var firstProcessing = reports.FindIndex(r => r.Processing);
            Assert.True(firstProcessing > 0, "no processing report");
            Assert.All(reports.Skip(firstProcessing), r => Assert.True(r.Processing));
            Assert.Equal(100, reports[firstProcessing].Percent);
            Assert.DoesNotContain(reports.Take(firstProcessing), r => r.Processing);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private sealed class SyncProgress(Action<ProgressReport> report) : IProgress<ProgressReport>
    {
        public void Report(ProgressReport value) => report(value);
    }

    [Fact]
    public async Task Download_ReportsProgressAndCompletes()
    {
        // v1.1.27: pipe-based progress capture restored after the v1.1.23 TTY
        // detour. yt-dlp's --progress-template lines come through stdout pipe;
        // YtDlpRunner.ParseProgress maps them to ProgressReport via the
        // IProgress callback. The fake exe emits 11 progress lines (0%..100%).
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var progressValues = new List<double>();
            var progress = new Progress<ProgressReport>(r => progressValues.Add(r.Percent));

            var format = new VideoFormat("299", 1080, "avc1", null, "mp4", 120_000_000, null);
            var request = new DownloadRequest(
                Url: "https://www.youtube.com/watch?v=FAKE0001234",
                Mode: DownloadMode.VideoOnly,
                ChosenFormat: format,
                SubtitleLanguageCodes: Array.Empty<string>(),
                ClipRange: null,
                SaveDirectory: temp,
                SanitizedFileStem: "Fake_Test_Video");

            var result = await runner.DownloadAsync(request, progress);

            Assert.True(result.IsSuccess, result.ErrorStderr);
            Assert.NotNull(result.OutputFilePath);
            Assert.True(File.Exists(result.OutputFilePath));
            Assert.NotEmpty(progressValues);
            Assert.Contains(100.0, progressValues);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Download_WithForceOverwrite_PassesFlagToYtDlp()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var format = new VideoFormat("299", 1080, "avc1", null, "mp4", 120_000_000, null);
            var request = new DownloadRequest(
                Url: "https://www.youtube.com/watch?v=FAKE0001234",
                Mode: DownloadMode.VideoOnly,
                ChosenFormat: format,
                SubtitleLanguageCodes: Array.Empty<string>(),
                ClipRange: null,
                SaveDirectory: temp,
                SanitizedFileStem: "Fake_Overwrite",
                ForceOverwrite: true);

            var result = await runner.DownloadAsync(request);
            Assert.True(result.IsSuccess, result.ErrorStderr);
            Assert.NotNull(result.OutputFilePath);

            var argsLog = result.OutputFilePath + ".args";
            Assert.True(File.Exists(argsLog), $"expected fake to emit args log at {argsLog}");
            var received = await File.ReadAllLinesAsync(argsLog);
            Assert.Contains("--force-overwrites", received);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Download_Mp3_IsEncodedAtTheBitrateItsNameSays()
    {
        // v1.5.0: the file name carries the MP3's bitrate, so yt-dlp is told it
        // (--audio-quality 160K) instead of its default VBR (about 130 kbps).
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var request = new DownloadRequest(
                Url: "https://www.youtube.com/watch?v=FAKE0001234",
                Mode: DownloadMode.AudioOnly,
                ChosenFormat: new VideoFormat("251", null, null, "opus", "webm", 6_000_000, 160),
                SubtitleLanguageCodes: Array.Empty<string>(),
                ClipRange: null,
                SaveDirectory: temp,
                SanitizedFileStem: "Fake_Mp3");

            var result = await runner.DownloadAsync(request);
            Assert.True(result.IsSuccess, result.ErrorStderr);

            var received = (await File.ReadAllLinesAsync(result.OutputFilePath + ".args")).ToList();
            var at = received.IndexOf("--audio-quality");
            Assert.True(at >= 0, "no --audio-quality");
            Assert.Equal("160K", received[at + 1]);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Download_M4a_IsNotReencoded()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var request = new DownloadRequest(
                Url: "https://www.youtube.com/watch?v=FAKE0001234",
                Mode: DownloadMode.AudioOnly,
                ChosenFormat: new VideoFormat("140", null, null, "mp4a.40.2", "m4a", 5_000_000, 129),
                SubtitleLanguageCodes: Array.Empty<string>(),
                ClipRange: null,
                SaveDirectory: temp,
                SanitizedFileStem: "Fake_M4a");

            var result = await runner.DownloadAsync(request);
            Assert.True(result.IsSuccess, result.ErrorStderr);
            Assert.DoesNotContain("--audio-quality", await File.ReadAllLinesAsync(result.OutputFilePath + ".args"));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Download_WithoutForceOverwrite_OmitsFlag()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var format = new VideoFormat("299", 1080, "avc1", null, "mp4", 120_000_000, null);
            var request = new DownloadRequest(
                Url: "https://www.youtube.com/watch?v=FAKE0001234",
                Mode: DownloadMode.VideoOnly,
                ChosenFormat: format,
                SubtitleLanguageCodes: Array.Empty<string>(),
                ClipRange: null,
                SaveDirectory: temp,
                SanitizedFileStem: "Fake_NoOverwrite");

            var result = await runner.DownloadAsync(request);
            Assert.True(result.IsSuccess, result.ErrorStderr);
            var argsLog = result.OutputFilePath + ".args";
            Assert.True(File.Exists(argsLog));
            var received = await File.ReadAllLinesAsync(argsLog);
            Assert.DoesNotContain("--force-overwrites", received);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    // ── v1.5.0: one retry for a 403 that arrives after the data started flowing ──

    private static DownloadRequest ForbiddenCase(string temp, string videoId) => new(
        Url: "https://www.youtube.com/watch?v=" + videoId,
        Mode: DownloadMode.VideoOnly,
        ChosenFormat: new VideoFormat("299", 1080, "avc1", null, "mp4", 120_000_000, null),
        SubtitleLanguageCodes: Array.Empty<string>(),
        ClipRange: null,
        SaveDirectory: temp,
        SanitizedFileStem: "Fake_Test_Video");

    private static int DownloadRuns(string temp) =>
        File.ReadAllLines(Path.Combine(temp, "fake-download-runs.txt")).Length;

    [Fact]
    public async Task Download_ForbiddenAfterDataStarted_RetriesOnceAndSucceeds()
    {
        // Field run 2026-11-04: a 1080p download got "HTTP Error 403: Forbidden" at 7.8%
        // (20 MB in, at 9 MiB/s); the same download succeeded on six other attempts.
        // yt-dlp does not retry that 403 itself. A second run re-extracts fresh URLs and
        // --continue resumes the partial file.
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var result = await runner.DownloadAsync(ForbiddenCase(temp, "MIDSTREAM403"));

            Assert.True(result.IsSuccess, result.ErrorStderr);
            Assert.Equal(2, DownloadRuns(temp));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Download_ForbiddenBeforeAnyData_IsNotRetried()
    {
        // Refused before the first byte (an age or region restriction, a removed video):
        // a second run would only be refused again.
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var result = await runner.DownloadAsync(ForbiddenCase(temp, "START403"));

            Assert.False(result.IsSuccess);
            Assert.Contains("403", result.ErrorStderr);
            Assert.Equal(1, DownloadRuns(temp));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Download_ForbiddenMidStreamTwice_GivesUpAfterOneRetry()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());
        var temp = Path.Combine(Path.GetTempPath(), "ytdlp-dl-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        try
        {
            var result = await runner.DownloadAsync(ForbiddenCase(temp, "ALWAYSMIDSTREAM403"));

            Assert.False(result.IsSuccess);
            Assert.Contains("403", result.ErrorStderr);
            Assert.Equal(2, DownloadRuns(temp));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
