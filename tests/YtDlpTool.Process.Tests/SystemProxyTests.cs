using YtDlpTool.Process;

namespace YtDlpTool.Process.Tests;

public class SystemProxyTests
{
    [Fact]
    public void DetectHttpProxy_NoConfig_DoesNotThrow()
    {
        // Smoke test: the actual return value depends on the test runner's user
        // hive (which may or may not have a proxy configured). We assert only
        // that detection completes without throwing, since the contract is
        // best-effort: any failure must be swallowed and returned as null.
        var ex = Record.Exception(() => SystemProxy.DetectHttpProxy());
        Assert.Null(ex);
    }

    [Fact]
    public void DetectHttpProxy_ReturnsNullOrWellFormedUrl()
    {
        var result = SystemProxy.DetectHttpProxy();
        if (result is null) return; // valid: no proxy configured

        Assert.StartsWith("http://", result);
        // host:port form must be present after the scheme
        var withoutScheme = result.Substring("http://".Length);
        Assert.Contains(':', withoutScheme);
    }
}
