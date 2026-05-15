using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class ErrorMapperTests
{
    [Theory]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", ErrorCategory.YouTubeRefused)]
    [InlineData("ERROR: Sign in to confirm your age", ErrorCategory.YouTubeRefused)]
    [InlineData("ERROR: HTTP Error 429: Too Many Requests", ErrorCategory.RateLimited)]
    [InlineData("ERROR: [youtube] xxxxx: Video unavailable", ErrorCategory.VideoUnavailable)]
    [InlineData("ERROR: [youtube] xxxxx: This video is private.", ErrorCategory.VideoUnavailable)]
    [InlineData("ERROR: [youtube] xxxxx: Premieres in 2 hours", ErrorCategory.PremiereUpcoming)]
    [InlineData("ERROR: unable to download webpage: <urlopen error timed out>", ErrorCategory.NetworkError)]
    [InlineData("ERROR: ffmpeg exited with code 1", ErrorCategory.UnknownError)]
    public void Map_KnownPatterns(string stderr, ErrorCategory expected)
    {
        var r = ErrorMapper.Map(stderr);
        Assert.Equal(expected, r.Category);
        Assert.False(string.IsNullOrWhiteSpace(r.UserMessage));
        Assert.False(r.UserMessage.Contains("Error", StringComparison.Ordinal),
            "user message must not contain English 'Error'");
    }

    [Fact]
    public void Map_EmptyReturnsUnknown()
    {
        var r = ErrorMapper.Map("");
        Assert.Equal(ErrorCategory.UnknownError, r.Category);
    }

    [Fact]
    public void Map_AssignsStableErrorCode()
    {
        var a = ErrorMapper.Map("ERROR: HTTP Error 403: Forbidden");
        var b = ErrorMapper.Map("ERROR: HTTP Error 403: Forbidden");
        Assert.Equal(a.ErrorCode, b.ErrorCode);
        Assert.StartsWith("E-", a.ErrorCode);
    }

    [Fact]
    public void Map_UnknownStderrWithErrorLine_SurfacesItInUserMessage()
    {
        // Fix 7 fallback: even when no rule pattern matches, if stderr carries an ERROR:
        // line we want it surfaced in the user message rather than the opaque
        // "下載失敗（錯誤代碼 E-XXXX）" of v1.1.2.
        const string stderr =
            "[youtube] abc123: Downloading webpage\n" +
            "ERROR: postprocessing: ffmpeg exited with code 8 (signal SIGABRT)\n";
        var r = ErrorMapper.Map(stderr);
        Assert.Equal(ErrorCategory.UnknownError, r.Category);
        Assert.Contains("ERROR:", r.UserMessage);
        Assert.Contains("ffmpeg", r.UserMessage);
        Assert.StartsWith("下載失敗：", r.UserMessage);
    }

    [Fact]
    public void Map_RawDetailsAttachedAndTruncated()
    {
        // RawDetails should carry the truncated/collapsed stderr for the log entry.
        var longStderr = "ERROR: HTTP Error 403: Forbidden\n" + new string('x', 600);
        var r = ErrorMapper.Map(longStderr);
        Assert.NotNull(r.RawDetails);
        Assert.True(r.RawDetails!.Length <= 500);
        Assert.DoesNotContain("\n", r.RawDetails);
    }

    [Fact]
    public void Map_PassingMentionOfTooManyRequestsInProse_DoesNotClassifyAsRateLimited()
    {
        // Fix 8 ordering: yt-dlp sometimes writes prose like "...waiting for retries..."
        // that briefly mentions Too Many Requests as context. Without an ERROR: prefix
        // we should NOT bucket it as RateLimited.
        const string stderr = "[download] previously got Too Many Requests, sleeping...";
        var r = ErrorMapper.Map(stderr);
        Assert.NotEqual(ErrorCategory.RateLimited, r.Category);
    }

    [Fact]
    public void Map_PremiereAndRateLimitedInSameStderr_PreferPremiere()
    {
        // Fix 8 ordering: more-specific cause wins over generic 429 retry chatter.
        const string stderr =
            "[download] ...Too Many Requests, retrying\n" +
            "ERROR: This live event will begin in 2 hours";
        var r = ErrorMapper.Map(stderr);
        Assert.Equal(ErrorCategory.PremiereUpcoming, r.Category);
    }
}
