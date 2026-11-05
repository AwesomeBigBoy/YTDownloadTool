using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

public class ProgressThrottleTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly ProgressThrottle _throttle = new(TimeSpan.FromMilliseconds(250));
    private readonly Guid _job = Guid.NewGuid();

    [Fact]
    public void FirstReportPublishes() =>
        Assert.True(_throttle.ShouldPublish(_job, 1, T0));

    [Fact]
    public void ReportsInsideIntervalAreDropped()
    {
        _throttle.ShouldPublish(_job, 1, T0);

        Assert.False(_throttle.ShouldPublish(_job, 2, T0.AddMilliseconds(100)));
        Assert.False(_throttle.ShouldPublish(_job, 3, T0.AddMilliseconds(249)));
    }

    [Fact]
    public void ReportAfterIntervalPublishes()
    {
        _throttle.ShouldPublish(_job, 1, T0);

        Assert.True(_throttle.ShouldPublish(_job, 2, T0.AddMilliseconds(250)));
    }

    [Fact]
    public void DroppedReportsDoNotPushTheNextOneBack()
    {
        // The interval counts from the last PUBLISHED report. If dropped ones reset the
        // clock, a steady stream every 200 ms would never publish again.
        _throttle.ShouldPublish(_job, 1, T0);
        _throttle.ShouldPublish(_job, 2, T0.AddMilliseconds(200));

        Assert.True(_throttle.ShouldPublish(_job, 3, T0.AddMilliseconds(260)));
    }

    [Fact]
    public void HundredPercentAlwaysPublishes()
    {
        _throttle.ShouldPublish(_job, 1, T0);

        Assert.True(_throttle.ShouldPublish(_job, 100, T0.AddMilliseconds(10)));
    }

    [Fact]
    public void JobsAreIndependent()
    {
        _throttle.ShouldPublish(_job, 1, T0);

        Assert.True(_throttle.ShouldPublish(Guid.NewGuid(), 1, T0.AddMilliseconds(10)));
    }

    [Fact]
    public void ForgetResets()
    {
        _throttle.ShouldPublish(_job, 1, T0);
        _throttle.Forget(_job);

        Assert.True(_throttle.ShouldPublish(_job, 2, T0.AddMilliseconds(10)));
    }
}
