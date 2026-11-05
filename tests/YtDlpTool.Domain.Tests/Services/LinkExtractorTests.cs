using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>
/// v1.5.0: what a paste of several lines, a link dragged from a browser or a dropped
/// .url shortcut turns into.
/// </summary>
public class LinkExtractorTests
{
    private const string A = "AAAAAAAAAAA";
    private const string B = "BBBBBBBBBBB";
    private const string C = "CCCCCCCCCCC";
    private const string List = "PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf";

    private static string Watch(string id) => "https://www.youtube.com/watch?v=" + id;

    [Fact]
    public void Extract_EveryLinkInOrder_IgnoringOtherText()
    {
        var text = $"第一講 https://youtu.be/{A}\n第二講 → https://www.youtube.com/watch?v={B}&t=90\n講義 https://example.com/x";

        var links = LinkExtractor.Extract(text);

        Assert.Equal(new[] { Watch(A), Watch(B) }, links.Select(l => l.Url));
        Assert.All(links, l => Assert.Equal(LinkKind.Video, l.Kind));
    }

    [Fact]
    public void Extract_TheSameVideoTwice_IsListedOnce()
    {
        var links = LinkExtractor.Extract($"https://youtu.be/{A} https://www.youtube.com/watch?v={A}&list={List}");
        Assert.Equal(A, Assert.Single(links).Id);
    }

    [Fact]
    public void Extract_LinksWithoutAScheme()
    {
        var links = LinkExtractor.Extract($"youtu.be/{A}\nyoutube.com/watch?v={B}\nwww.youtube.com/shorts/{C}");
        Assert.Equal(new[] { A, B, C }, links.Select(l => l.Id));
    }

    [Fact]
    public void Extract_HttpLinks_AreTreatedAsHttps()
    {
        Assert.Equal(Watch(A), Assert.Single(LinkExtractor.Extract($"http://www.youtube.com/watch?v={A}")).Url);
    }

    [Fact]
    public void Extract_InternetShortcutFile()
    {
        // What a .url file dragged from the desktop contains.
        var text = $"[InternetShortcut]\r\nURL=https://www.youtube.com/watch?v={A}\r\nIDList=\r\n";
        Assert.Equal(A, Assert.Single(LinkExtractor.Extract(text)).Id);
    }

    [Fact]
    public void Extract_PunctuationAroundALink_IsNotPartOfIt()
    {
        var text = $"看這個：https://youtu.be/{A}。還有（https://www.youtube.com/watch?v={B}）, (https://youtu.be/{C}).";
        Assert.Equal(new[] { A, B, C }, LinkExtractor.Extract(text).Select(l => l.Id));
    }

    [Fact]
    public void Extract_PlaylistPage()
    {
        var link = Assert.Single(LinkExtractor.Extract($"https://www.youtube.com/playlist?list={List}"));
        Assert.Equal(LinkKind.Playlist, link.Kind);
        Assert.Equal(List, link.Id);
        Assert.Equal("https://www.youtube.com/playlist?list=" + List, link.Url);
    }

    [Fact]
    public void Extract_VideoOpenedFromAPlaylist_KeepsTheList()
    {
        var link = Assert.Single(LinkExtractor.Extract($"https://www.youtube.com/watch?v={A}&list={List}&index=2"));
        Assert.Equal(LinkKind.Video, link.Kind);
        Assert.Equal(Watch(A), link.Url);
        Assert.Equal(List, link.PlaylistId);
    }

    [Fact]
    public void Extract_LinksWrappedByMailLinkProtection()
    {
        // Mail systems rewrite links into their own redirector with the real address
        // percent-encoded in a parameter; copied from such a mail, the link used to be lost.
        var wrapped = $"https://nam12.safelinks.protection.outlook.com/?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3D{A}&data=05%7C01&reserved=0";
        var google = $"https://www.google.com/url?q=https://www.youtube.com/watch%3Fv%3D{B}&sa=D";

        Assert.Equal(new[] { A, B }, LinkExtractor.Extract(wrapped + "\n" + google).Select(l => l.Id));
    }

    [Fact]
    public void Extract_LinksGluedTogetherWithoutASeparator_AreAllFound()
    {
        // Copied out of a spreadsheet or a chat that dropped the line breaks.
        var text = $"https://youtu.be/{A}https://www.youtube.com/watch?v={B}http://youtu.be/{C}";
        Assert.Equal(new[] { A, B, C }, LinkExtractor.Extract(text).Select(l => l.Id));
    }

    [Fact]
    public void Extract_LookalikeHostsAreIgnored()
    {
        Assert.Empty(LinkExtractor.Extract($"https://notyoutube.com/watch?v={A} https://www.youtube.com.evil.com/watch?v={A}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("你好")]
    [InlineData("https://example.com/watch?v=AAAAAAAAAAA")]
    [InlineData("https://www.youtube.com/@SomeChannel")]
    public void Extract_NothingUsable_IsEmpty(string? text)
    {
        Assert.Empty(LinkExtractor.Extract(text));
    }
}
