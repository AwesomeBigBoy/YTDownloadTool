using YtDlpTool.Domain.Models;
using YtDlpTool.Process;
using YtDlpTool.Process.Tests.Helpers;

namespace YtDlpTool.Process.Tests;

public class YtDlpRunnerDownloadTests
{
    [Fact]
    public async Task Download_ReportsProgressAndCompletes()
    {
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
