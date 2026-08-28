using System.Reflection;
using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

/// <summary>
/// Guards --ffmpeg-location against the assumption that broke in v1.4.0: that ffmpeg.exe
/// is a sibling of the yt-dlp executable. When yt-dlp moved into bin\yt-dlp\ the derived
/// path stopped existing, and BuildFfmpegLocationArgs yielded NOTHING — dropping the flag
/// with no error anywhere, so downloads only failed later at the merge step with a
/// misleading "ffmpeg not found".
///
/// The method is private; reflection is deliberate. The behaviour worth pinning is
/// "the flag is emitted for the ffmpeg we were told about", and testing it through a full
/// download would need a real ffmpeg binary.
/// </summary>
public class YtDlpRunnerFfmpegLocationTests : IDisposable
{
    private readonly string _root;

    public YtDlpRunnerFfmpegLocationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ytdlp-ffloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "bin", "yt-dlp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string[] Invoke(YtDlpRunner runner)
    {
        var m = typeof(YtDlpRunner).GetMethod("BuildFfmpegLocationArgs",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((IEnumerable<string>)m.Invoke(runner, null)!).ToArray();
    }

    [Fact]
    public void OneDirLayout_StillEmitsFfmpegLocation()
    {
        // The real v1.4.0 layout: yt-dlp in bin\yt-dlp\, ffmpeg one level up in bin\.
        var ytDlp  = Path.Combine(_root, "bin", "yt-dlp", "yt-dlp.exe");
        var ffmpeg = Path.Combine(_root, "bin", "ffmpeg.exe");
        File.WriteAllText(ytDlp, "stub");
        File.WriteAllText(ffmpeg, "stub");

        var args = Invoke(new YtDlpRunner(ytDlp, ffmpegPath: ffmpeg));

        Assert.Equal(new[] { "--ffmpeg-location", ffmpeg }, args);
    }

    [Fact]
    public void WithoutExplicitPath_FallsBackToSibling()
    {
        // Legacy layout (yt-dlp.exe and ffmpeg.exe both in bin\) must keep working, so
        // an older config or a downgrade does not lose the flag.
        var ytDlp  = Path.Combine(_root, "bin", "yt-dlp.exe");
        var ffmpeg = Path.Combine(_root, "bin", "ffmpeg.exe");
        File.WriteAllText(ytDlp, "stub");
        File.WriteAllText(ffmpeg, "stub");

        var args = Invoke(new YtDlpRunner(ytDlp));

        Assert.Equal(new[] { "--ffmpeg-location", ffmpeg }, args);
    }

    [Fact]
    public void OneDirLayout_WithoutExplicitPath_EmitsNothing()
    {
        // Documents the exact failure mode: derive the location from the yt-dlp exe under
        // the onedir layout and the flag silently disappears. If this ever starts
        // returning two elements, the derivation was reintroduced.
        var ytDlp = Path.Combine(_root, "bin", "yt-dlp", "yt-dlp.exe");
        File.WriteAllText(ytDlp, "stub");
        File.WriteAllText(Path.Combine(_root, "bin", "ffmpeg.exe"), "stub");

        Assert.Empty(Invoke(new YtDlpRunner(ytDlp)));
    }

    [Fact]
    public void MissingFfmpeg_EmitsNothingRatherThanABadPath()
    {
        var ytDlp = Path.Combine(_root, "bin", "yt-dlp", "yt-dlp.exe");
        File.WriteAllText(ytDlp, "stub");

        Assert.Empty(Invoke(new YtDlpRunner(ytDlp, ffmpegPath: Path.Combine(_root, "bin", "nope.exe"))));
    }
}
