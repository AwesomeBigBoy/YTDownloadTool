using YtDlpTool.Domain.Models;
using YtDlpTool.Process;
using YtDlpTool.Process.Tests.Helpers;

namespace YtDlpTool.Process.Tests;

/// <summary>
/// v1.5.0: reading a playlist's videos in one call (--flat-playlist) — titles and lengths
/// straight from the playlist page, without parsing every video first.
/// </summary>
public class YtDlpRunnerPlaylistTests
{
    private const string Playlist = "https://www.youtube.com/playlist?list=PLFAKE00000001";

    [Fact]
    public async Task FetchPlaylist_ListsTheVideosInOrder()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());

        var result = await runner.FetchPlaylistAsync(Playlist);

        Assert.True(result.IsSuccess, result.ErrorStderr);
        var p = result.Playlist!;
        Assert.Equal("Fake Playlist", p.Title);
        Assert.Equal(new[] { "AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC", "DDDDDDDDDDD" },
            p.Entries.Select(e => e.VideoId));
        Assert.Equal("First", p.Entries[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(61), p.Entries[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(3725), p.Entries[2].Duration);
        Assert.Null(p.Entries[3].Duration);
        Assert.Equal("https://www.youtube.com/watch?v=AAAAAAAAAAA", p.Entries[0].Url);
    }

    [Fact]
    public async Task FetchPlaylist_PrivateAndDeletedVideos_AreMarkedUnavailable()
    {
        // YouTube keeps them in the list as "[Private video]" / "[Deleted video]"; they can
        // never be downloaded, so the list offers them unticked.
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());

        var result = await runner.FetchPlaylistAsync(Playlist);

        Assert.Equal(new[] { true, false, true, false }, result.Playlist!.Entries.Select(e => e.IsAvailable));
    }

    [Fact]
    public async Task FetchPlaylist_SkipsGapsAndEntriesThatAreNotVideos()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());

        var result = await runner.FetchPlaylistAsync("https://www.youtube.com/playlist?list=PLFAKEMIXED");

        Assert.True(result.IsSuccess, result.ErrorStderr);
        Assert.Equal(new[] { "EEEEEEEEEEE", "FFFFFFFFFFF", "GGGGGGGGGGG" }, result.Playlist!.Entries.Select(e => e.VideoId));
    }

    [Fact]
    public async Task FetchPlaylist_LiveAndUpcomingVideos_AreNotOffered()
    {
        // An upcoming premiere cannot be downloaded yet; a live stream never ends, so it
        // would hold a download slot until someone cancels it.
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());

        var result = await runner.FetchPlaylistAsync("https://www.youtube.com/playlist?list=PLFAKEMIXED");

        Assert.Equal(new[] { PlaylistEntryState.Live, PlaylistEntryState.Upcoming, PlaylistEntryState.Available },
            result.Playlist!.Entries.Select(e => e.State));
        Assert.Equal(new[] { false, false, true }, result.Playlist.Entries.Select(e => e.IsAvailable));
    }

    [Fact]
    public async Task FetchPlaylist_AListThatIsJustOneVideo_SaysSo()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());

        var result = await runner.FetchPlaylistAsync("https://www.youtube.com/watch?v=HHHHHHHHHHH&list=NOTAPLAYLIST");

        Assert.False(result.IsSuccess);
        Assert.True(result.NotAPlaylist);
    }

    [Fact]
    public async Task FetchPlaylist_Failure_ReportsYtDlpsError()
    {
        var runner = new YtDlpRunner(FakeYtDlpLocator.Path());

        var result = await runner.FetchPlaylistAsync("https://www.youtube.com/playlist?list=PLAYLISTERROR");

        Assert.False(result.IsSuccess);
        Assert.Contains("The playlist does not exist", result.ErrorStderr);
    }
}
