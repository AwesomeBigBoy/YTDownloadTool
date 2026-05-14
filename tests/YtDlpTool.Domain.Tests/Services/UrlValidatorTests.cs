using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class UrlValidatorTests
{
    private readonly UrlValidator _v = new();

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42", "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLxxxx", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void Validate_AcceptsAndCanonicalizes(string input, string expected)
    {
        var result = _v.Validate(input);
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(expected, result.CanonicalUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://www.youtube.com/watch?v=abc")]                  // http not https
    [InlineData("https://www.youtube.com/watch")]                       // missing video id
    [InlineData("https://www.youtube.com.evil.com/watch?v=abc")]        // host prefix attack
    [InlineData("https://192.168.1.1/watch?v=abc")]                     // IP literal
    [InlineData("https://www.yоutube.com/watch?v=abc")]            // cyrillic homograph
    [InlineData("https://%77ww.youtube.com/watch?v=abc")]               // url-encoded host
    [InlineData("file:///C:/etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&v=other")] // multiple v=
    public void Validate_RejectsAttackVectors(string input)
    {
        var result = _v.Validate(input);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Reason);
    }
}
