using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("Hello World", "Hello World")]
    [InlineData("a:b/c\\d|e?f*g\"h<i>j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("  spaces  ", "spaces")]
    [InlineData("CON", "_CON")]
    [InlineData("aux.txt", "_aux.txt")]
    [InlineData("normal.mp4", "normal.mp4")]
    public void Sanitize_StandardCases(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_RemovesControlChars()
    {
        // BEL (0x07), tab-like control in the middle of "helloworld" should be stripped
        var result = FileNameSanitizer.Sanitize("helloworld");
        Assert.Equal("helloworld", result);
    }

    [Fact]
    public void Sanitize_RemovesRightToLeftOverride()
    {
        var result = FileNameSanitizer.Sanitize("photo‮gnp.exe");
        Assert.DoesNotContain('‮', result);
    }

    [Fact]
    public void Sanitize_Truncates_To200()
    {
        var input = new string('a', 500);
        var result = FileNameSanitizer.Sanitize(input);
        Assert.Equal(200, result.Length);
    }

    [Fact]
    public void Sanitize_PreservesExtensionWhenTruncating()
    {
        var input = new string('a', 500) + ".mp4";
        var result = FileNameSanitizer.Sanitize(input);
        Assert.EndsWith(".mp4", result);
        Assert.Equal(200, result.Length);
    }

    [Fact]
    public void Sanitize_EmptyReturnsFallback()
    {
        var result = FileNameSanitizer.Sanitize("   ");
        Assert.StartsWith("video_", result);
    }
}
