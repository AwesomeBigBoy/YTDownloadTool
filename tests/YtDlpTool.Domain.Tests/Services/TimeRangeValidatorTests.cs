using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class TimeRangeValidatorTests
{
    [Theory]
    [InlineData("00:00:00", "00:01:30")]
    [InlineData("01:23:45", "02:34:56")]
    public void Validate_Accepts_GoodRanges(string s, string e)
    {
        var r = TimeRangeValidator.Parse(s, e, videoDuration: TimeSpan.FromHours(3));
        Assert.True(r.IsValid, r.Reason);
        Assert.NotNull(r.Range);
    }

    [Theory]
    [InlineData("1:2:3", "00:01:30")]       // bad format
    [InlineData("00:60:00", "00:01:30")]    // 60 min invalid? actually 60 should be rejected (use hh:mm:ss canonical)
    [InlineData("00:01:00", "00:01:00")]    // start == end
    [InlineData("00:02:00", "00:01:30")]    // start > end
    public void Validate_Rejects_BadFormatOrOrder(string s, string e)
    {
        var r = TimeRangeValidator.Parse(s, e, videoDuration: TimeSpan.FromHours(3));
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Validate_Rejects_EndPastDuration()
    {
        var r = TimeRangeValidator.Parse("00:00:00", "01:00:00", videoDuration: TimeSpan.FromMinutes(30));
        Assert.False(r.IsValid);
        Assert.Contains("超過", r.Reason);
    }

    [Fact]
    public void Validate_Rejects_LongerThan8Hours()
    {
        var r = TimeRangeValidator.Parse("00:00:00", "09:00:00", videoDuration: TimeSpan.FromHours(10));
        Assert.False(r.IsValid);
        Assert.Contains("8", r.Reason);
    }
}
