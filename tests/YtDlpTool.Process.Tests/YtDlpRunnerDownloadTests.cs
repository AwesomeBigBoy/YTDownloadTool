using YtDlpTool.Domain.Models;
using YtDlpTool.Process;
using YtDlpTool.Process.Tests.Helpers;

namespace YtDlpTool.Process.Tests;

public class YtDlpRunnerDownloadTests
{
    [Fact]
    public async Task Download_CompletesAndProducesFile()
    {
        // v1.1.23: TTY mode means we don't capture stdout, so live progress
        // reports (via the IProgress<ProgressReport> callback) are no longer
        // emitted — the trade-off accepted to bypass endpoint security software's
        // "redirected stdout = malware-like" block on AD hosts. What we still
        // assert: process exits 0 and the final file lands at the path
        // implied by SaveDirectory + SanitizedFileStem.
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
                SanitizedFileStem: "Fake_Test_Video");

            var result = await runner.DownloadAsync(request);

            Assert.True(result.IsSuccess, result.ErrorStderr);
            Assert.NotNull(result.OutputFilePath);
            Assert.True(File.Exists(result.OutputFilePath));
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
}
