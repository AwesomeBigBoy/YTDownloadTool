using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class SupportedSubtitlesTests
{
    [Theory]
    [InlineData("zh-TW", "繁體中文")]
    [InlineData("zh-Hant", "繁體中文")]
    [InlineData("zh-Hant-TW", "繁體中文")]
    [InlineData("en", "英文")]
    [InlineData("en-US", "英文")]
    [InlineData("en-GB", "英文")]
    [InlineData("ja", "日文")]
    [InlineData("ja-JP", "日文")]
    [InlineData("vi", "越南文")]
    [InlineData("vi-VN", "越南文")]
    [InlineData("th", "泰文")]
    [InlineData("th-TH", "泰文")]
    [InlineData("id", "印尼文")]
    [InlineData("id-ID", "印尼文")]
    [InlineData("fil", "菲律賓文")]
    [InlineData("tl", "菲律賓文")]
    [InlineData("ko", "韓文")]
    [InlineData("ko-KR", "韓文")]
    [InlineData("hi", "印度文")]
    [InlineData("hi-IN", "印度文")]
    public void GetDisplayName_KnownCodes_ReturnsZhTwName(string code, string expected)
    {
        Assert.Equal(expected, SupportedSubtitles.GetDisplayName(code));
    }

    [Theory]
    [InlineData("EN")]            // case-insensitive lookup
    [InlineData("ZH-tw")]
    public void GetDisplayName_CaseInsensitive(string code)
    {
        Assert.NotNull(SupportedSubtitles.GetDisplayName(code));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh-CN")]         // Simplified Chinese is not in the whitelist
    [InlineData("ru")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void GetDisplayName_UnknownCodes_ReturnsNull(string code)
    {
        Assert.Null(SupportedSubtitles.GetDisplayName(code));
    }
}
