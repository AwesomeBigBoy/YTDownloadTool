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
    // Percent-encoded query values must NOT trigger the host-encoding check.
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL%20foo", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42%73", "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42")]
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

    // ── v1.5.0: the other link shapes people paste and drag ────────────────────

    [Theory]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/shorts/dQw4w9WgXcQ?si=AbCdEf")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?si=AbCdEf")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&si=AbCdEf")]
    public void Validate_AcceptsShortsLiveEmbedMobileAndMusicLinks(string input)
    {
        var result = _v.Validate(input);
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", result.CanonicalUrl);
        Assert.Equal("dQw4w9WgXcQ", result.VideoId);
        Assert.False(result.IsPlaylist);
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    [InlineData("https://m.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf&si=x")]
    public void Validate_PlaylistPage_IsAPlaylist(string input)
    {
        var result = _v.Validate(input);
        Assert.True(result.IsValid, result.Reason);
        Assert.True(result.IsPlaylist);
        Assert.Null(result.VideoId);
        Assert.Equal("PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", result.PlaylistId);
        Assert.Equal("https://www.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", result.CanonicalUrl);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf&index=3")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    public void Validate_VideoOpenedFromAPlaylist_IsTheVideoAndKeepsTheList(string input)
    {
        // The address bar carries both. The video stays what gets parsed (it is what the
        // user was watching); the list is offered next to it.
        var result = _v.Validate(input);
        Assert.True(result.IsValid, result.Reason);
        Assert.False(result.IsPlaylist);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", result.CanonicalUrl);
        Assert.Equal("PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", result.PlaylistId);
    }

    [Theory]
    // An embedded playlist player, and a watch link that names only the list.
    [InlineData("https://www.youtube.com/embed/videoseries?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    [InlineData("https://www.youtube.com/embed/videoseries/?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf&index=2")]
    [InlineData("https://www.youtube.com/watch?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    [InlineData("https://m.youtube.com/watch?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf&index=4")]
    public void Validate_PlaylistLinkWithoutAVideo_IsAPlaylist(string input)
    {
        var result = _v.Validate(input);
        Assert.True(result.IsValid, result.Reason);
        Assert.True(result.IsPlaylist);
        Assert.Null(result.VideoId);
        Assert.Equal("https://www.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", result.CanonicalUrl);
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist")]                          // no list
    [InlineData("https://www.youtube.com/embed/videoseries")]                 // "videoseries" is not a video id
    [InlineData("https://www.youtube.com/embed/live_stream?channel=UCuAXFkgsw1L7xaCfnd5JJOw")] // nor "live_stream"
    [InlineData("https://www.youtube.com/watch?index=3")]                     // neither a video nor a list
    [InlineData("https://www.youtube.com/playlist?list=PL%3Cscript%3E")]      // not a list id
    [InlineData("https://www.youtube.com/shorts/short")]                      // not a video id
    [InlineData("https://www.youtube.com/channel/UCuAXFkgsw1L7xaCfnd5JJOw")]  // channels: not supported
    [InlineData("https://www.youtube.com/@SomeChannel")]
    [InlineData("https://studio.youtube.com/watch?v=dQw4w9WgXcQ")]            // not an allowed host
    public void Validate_RejectsOtherYouTubePages(string input)
    {
        Assert.False(_v.Validate(input).IsValid);
    }
}
