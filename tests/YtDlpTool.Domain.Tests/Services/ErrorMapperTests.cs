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
}
