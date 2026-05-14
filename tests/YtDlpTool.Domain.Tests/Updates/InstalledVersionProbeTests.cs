using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Domain.Tests.Updates;

public class InstalledVersionProbeTests
{
    [Fact]
    public void Compare_NewerOnRemote_ReturnsTrue()
    {
        Assert.True(InstalledVersionProbe.IsRemoteNewer("2026.04.01", "2026.05.01"));
        Assert.True(InstalledVersionProbe.IsRemoteNewer("1.2.3", "1.2.4"));
        Assert.True(InstalledVersionProbe.IsRemoteNewer("1.2.3", "2.0.0"));
    }

    [Fact]
    public void Compare_SameOrOlderRemote_ReturnsFalse()
    {
        Assert.False(InstalledVersionProbe.IsRemoteNewer("1.2.3", "1.2.3"));
        Assert.False(InstalledVersionProbe.IsRemoteNewer("1.2.3", "1.2.2"));
        Assert.False(InstalledVersionProbe.IsRemoteNewer("2026.05.01", "2026.04.30"));
    }

    [Fact]
    public void Compare_EmptyLocal_TreatsAsOlder()
    {
        Assert.True(InstalledVersionProbe.IsRemoteNewer("", "1.0.0"));
    }
}
