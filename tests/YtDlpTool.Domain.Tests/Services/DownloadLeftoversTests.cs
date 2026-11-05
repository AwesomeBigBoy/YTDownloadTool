using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>
/// v1.5.0: when yt-dlp writes straight to the final name, a failed or restarted download has
/// to clear yt-dlp's own work files for that name — and nothing the user keeps.
/// </summary>
public class DownloadLeftoversTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytdlp-leftovers-" + Guid.NewGuid());

    public DownloadLeftoversTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("Me at the zoo.f133.mp4")]          // one stream of a merge
    [InlineData("Me at the zoo.f140-drc.m4a")]
    [InlineData("Me at the zoo.f251.webm")]
    [InlineData("Me at the zoo.f133.mp4.part")]     // still downloading
    [InlineData("Me at the zoo.mp4.part")]
    [InlineData("Me at the zoo.part")]
    [InlineData("Me at the zoo.f133.mp4.ytdl")]     // fragment bookkeeping
    [InlineData("Me at the zoo.ytdl")]
    [InlineData("Me at the zoo.temp.mp4")]          // the merger's / a post-processor's output
    [InlineData("ME AT THE ZOO.f133.mp4")]          // the title part as Windows compares it
    [InlineData("Me at the zoo.f96.mp4.part-Frag3")] // a fragmented stream's pieces
    [InlineData("Me at the zoo.f96.mp4.part-Frag3.part")]
    public void WorkFilesOfThatName_AreLeftovers(string name)
    {
        Assert.True(DownloadLeftovers.IsLeftover(name, "Me at the zoo"));
    }

    [Theory]
    [InlineData("Me at the zoo.mp4")]               // finished downloads
    [InlineData("Me at the zoo.m4a")]
    [InlineData("Me at the zoo.webm")]
    [InlineData("Me at the zoo.jpg")]               // a picture of the user's
    [InlineData("Me at the zoo.vtt")]
    [InlineData("Me at the zoo_2.f133.mp4")]        // another name
    [InlineData("Me at the zoo (1).mp4.part")]
    [InlineData("Me at the zoo.media.mp4")]          // the intermediate of the ".media" pipeline
    [InlineData("Me at the zoo.final.mp4")]
    [InlineData("Me at the zoo.f.mp4")]
    [InlineData("Me at the zoo.F1.mp4")]            // yt-dlp writes these in lower case;
    [InlineData("Me at the zoo.Temp.mp4")]          // these are someone's own files
    public void FinishedFilesAndOtherNames_AreNot(string name)
    {
        Assert.False(DownloadLeftovers.IsLeftover(name, "Me at the zoo"));
    }

    [Fact]
    public void ATitleWithRegexCharacters_IsMatchedLiterally()
    {
        Assert.True(DownloadLeftovers.IsLeftover("a+b (live) [4K].f137.mp4.part", "a+b (live) [4K]"));
        Assert.False(DownloadLeftovers.IsLeftover("aab (live) [4K].f137.mp4.part", "a+b (live) [4K]"));
    }

    [Fact]
    public void Delete_RemovesOnlyTheLeftovers()
    {
        foreach (var name in new[] { "V.f137.mp4", "V.f140.m4a.part", "V.temp.mp4", "V.mp4", "V.m4a", "W.f137.mp4" })
            File.WriteAllText(Path.Combine(_dir, name), "x");

        DownloadLeftovers.Delete(_dir, "V");

        Assert.Equal(new[] { "V.m4a", "V.mp4", "W.f137.mp4" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }
}
