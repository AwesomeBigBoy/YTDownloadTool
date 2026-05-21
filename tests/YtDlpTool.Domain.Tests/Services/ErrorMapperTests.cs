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
        // v1.3.0-alpha3 raised the cap from 500 → 4096 so download-stage stderr
        // is captured in full.
        var longStderr = "ERROR: HTTP Error 403: Forbidden\n" + new string('x', 600);
        var r = ErrorMapper.Map(longStderr);
        Assert.NotNull(r.RawDetails);
        Assert.True(r.RawDetails!.Length <= 4096);
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

    [Fact]
    public void Map_AppLockerBlock_ReturnsBlockedMessage()
    {
        // Fix C: ProcessSandbox (Fix B) bubbles up a friendly hint when AppLocker /
        // WDAC blocks the child binary. ErrorMapper should bucket it as
        // ComponentMissing with code E-BLOCK01 and a whitelist instruction.
        const string stderr = "Process.Start failed: AppLocker 群組原則拒絕執行此程式 — Some Win32 detail";
        var r = ErrorMapper.Map(stderr);
        Assert.Equal(ErrorCategory.ComponentMissing, r.Category);
        Assert.Equal("E-BLOCK01", r.ErrorCode);
        Assert.Contains("白名單", r.UserMessage);
    }

    [Fact]
    public void Map_SslFailure_ReturnsSslMessage()
    {
        // Fix C: HTTPS inspection / TLS interception surfaces as a
        // CERTIFICATE_VERIFY_FAILED line. We want a dedicated E-SSL01 with a
        // proxy hint instead of dumping the raw traceback on the user.
        const string stderr =
            "[youtube] abc123: Downloading webpage\n" +
            "ssl.SSLCertVerificationError: [SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed: self signed certificate in certificate chain (_ssl.c:1129)\n";
        var r = ErrorMapper.Map(stderr);
        Assert.Equal(ErrorCategory.NetworkError, r.Category);
        Assert.Equal("E-SSL01", r.ErrorCode);
        Assert.Contains("SSL", r.UserMessage);
    }

    [Fact]
    public void Map_RuleMatch_PreservesRawDetails()
    {
        // Fix 2: the rule-match path must propagate the truncated raw stderr so the
        // download.failed log entry actually contains the yt-dlp diagnosis. A unique
        // marker proves the stderr survived end-to-end (and isn't just the rule's
        // canned message).
        const string stderr =
            "[youtube] abc123: Downloading webpage MARKER_XYZ\n" +
            "ERROR: HTTP Error 429: Too Many Requests\n";
        var r = ErrorMapper.Map(stderr);
        Assert.Equal(ErrorCategory.RateLimited, r.Category);
        Assert.NotNull(r.RawDetails);
        Assert.Contains("MARKER_XYZ", r.RawDetails);
    }

    [Fact]
    public void Map_RawDetails_TruncatedAt4096Chars()
    {
        // v1.3.0-alpha3: cap bumped from 500 → 4096 so download-stage stderr
        // (~2KB from yt-dlp's multi-client retry chatter) is captured in full.
        // Test with a stderr larger than 4096 so we exercise the truncation.
        var stderr = "ERROR: something exploded\n" + new string('x', 5000);
        var r = ErrorMapper.Map(stderr);
        Assert.NotNull(r.RawDetails);
        Assert.True(r.RawDetails!.Length <= 4096,
            $"RawDetails length {r.RawDetails.Length} exceeded 4096-char cap");
    }

    [Fact]
    public void Map_UnrecognisedStderr_SurfacesLastLine()
    {
        // Fix C: if none of the rules match and there is no ERROR: line, surface
        // the LAST non-empty line of stderr instead of an opaque hash code so the
        // user at least sees what failed.
        const string stderr =
            "[generic] some-host: Requesting header\n" +
            "[generic] some-host: Downloading webpage\n" +
            "WeirdCustomError: thingy did not work\n";
        var r = ErrorMapper.Map(stderr);
        Assert.Equal(ErrorCategory.UnknownError, r.Category);
        Assert.StartsWith("下載失敗：", r.UserMessage);
        Assert.Contains("WeirdCustomError", r.UserMessage);
    }
}
