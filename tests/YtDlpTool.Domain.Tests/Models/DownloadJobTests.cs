using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Tests.Models;

public class DownloadJobTests
{
    private static DownloadJob Failed()
    {
        var job = new DownloadJob(
            url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            title: "Title",
            thumbnailUrl: "file:///thumb.jpg",
            mode: DownloadMode.AudioAndVideo,
            chosenFormat: new VideoFormat("299", 1080, "avc1", null, "mp4", 1_000, null),
            subtitleLanguageCodes: new[] { "en" },
            clipRange: new TimeRange(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9)),
            saveDirectory: @"C:\Downloads");
        job.MarkDownloading();
        job.ReportProgress(42, 1000, TimeSpan.FromSeconds(3));
        job.MarkFailed("下載失敗", "E-UNKNOWN");
        return job;
    }

    [Fact]
    public void CloneForRetry_AsksForExactlyTheSameDownload()
    {
        // v1.5.0: 「重試」 on a failed row re-queues what the user asked for — same link,
        // format, subtitles, clip and folder.
        var failed = Failed();

        var again = failed.CloneForRetry();

        Assert.Equal(failed.Url, again.Url);
        Assert.Equal(failed.Title, again.Title);
        Assert.Equal(failed.ThumbnailUrl, again.ThumbnailUrl);
        Assert.Equal(failed.Mode, again.Mode);
        Assert.Equal(failed.ChosenFormat, again.ChosenFormat);
        Assert.Equal(failed.SubtitleLanguageCodes, again.SubtitleLanguageCodes);
        Assert.Equal(failed.ClipRange, again.ClipRange);
        Assert.Equal(failed.SaveDirectory, again.SaveDirectory);
    }

    [Fact]
    public void CloneForRetry_IsANewJobThatHasNotRunYet()
    {
        // A new id keeps the queue's per-job bookkeeping (cancellation, 429 retries, log
        // job_hash) separate from the failed attempt.
        var failed = Failed();

        var again = failed.CloneForRetry();

        Assert.NotEqual(failed.Id, again.Id);
        Assert.Equal(JobStatus.Pending, again.Status);
        Assert.Equal(0, again.Progress);
        Assert.Null(again.FailureReason);
        Assert.Null(again.FailureCode);
        Assert.Null(again.OutputFilePath);
    }
}
