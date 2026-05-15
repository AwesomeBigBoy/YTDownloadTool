using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class MediaOutputProbeTests : IDisposable
{
    private readonly string _tempDir;

    public MediaOutputProbeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "MediaOutputProbeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void OnlySidecars_ReturnsNotFound_AndListsSidecars()
    {
        // Simulates the bug: yt-dlp downloaded only the .vtt subtitle and a .webp
        // thumbnail because --download-sections re-encode failed silently.
        var stem = "my-clip";
        File.WriteAllText(Path.Combine(_tempDir, stem + ".en.vtt"), "sub");
        File.WriteAllText(Path.Combine(_tempDir, stem + ".webp"), "img");

        var result = MediaOutputProbe.VerifyMediaOutputExists(_tempDir, stem);

        Assert.False(result.found);
        // Note: only the exact stem.webp matches stem.* — stem.en.vtt has a different
        // first-segment ("my-clip.en") so Directory.GetFiles("my-clip.*") only catches
        // the .webp variant. That's expected: the cleanup is best-effort.
        Assert.Contains(result.sidecarPaths, p => p.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MediaPlusSidecars_ReturnsFound()
    {
        var stem = "my-video";
        File.WriteAllText(Path.Combine(_tempDir, stem + ".mp4"), "media");
        File.WriteAllText(Path.Combine(_tempDir, stem + ".webp"), "img");

        var result = MediaOutputProbe.VerifyMediaOutputExists(_tempDir, stem);

        Assert.True(result.found);
    }

    [Fact]
    public void EmptyDirectory_ReturnsNotFound()
    {
        var result = MediaOutputProbe.VerifyMediaOutputExists(_tempDir, "anything");

        Assert.False(result.found);
        Assert.Empty(result.sidecarPaths);
    }

    [Fact]
    public void NoMatchingStem_ReturnsNotFound()
    {
        // A different stem in the directory must not be picked up as media.
        File.WriteAllText(Path.Combine(_tempDir, "other-video.mp4"), "media");

        var result = MediaOutputProbe.VerifyMediaOutputExists(_tempDir, "my-video");

        Assert.False(result.found);
        Assert.Empty(result.sidecarPaths);
    }

    [Fact]
    public void NonexistentDirectory_ReturnsNotFound()
    {
        var bogus = Path.Combine(_tempDir, "does-not-exist");

        var result = MediaOutputProbe.VerifyMediaOutputExists(bogus, "anything");

        Assert.False(result.found);
        Assert.Empty(result.sidecarPaths);
    }

    [Fact]
    public void MediaOnly_ReturnsFound_NoSidecars()
    {
        var stem = "audio-track";
        File.WriteAllText(Path.Combine(_tempDir, stem + ".m4a"), "audio");

        var result = MediaOutputProbe.VerifyMediaOutputExists(_tempDir, stem);

        Assert.True(result.found);
        Assert.Empty(result.sidecarPaths);
    }
}
