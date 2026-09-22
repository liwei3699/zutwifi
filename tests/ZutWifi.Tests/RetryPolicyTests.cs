using ZutWifi.Core;
namespace ZutWifi.Tests;
public class RetryPolicyTests
{
    [Fact]
    public void 三次重试的退避是2s5s15s()
    {
        var p = new RetryPolicy(maxRetries: 3);
        Assert.Equal(TimeSpan.FromSeconds(2), p.NextDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(5), p.NextDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(15), p.NextDelay(2));
        Assert.Null(p.NextDelay(3));
    }

    [Fact]
    public void 一次连接最多提交四次() => Assert.Equal(4, new RetryPolicy(3).MaxAttempts);

    [Fact]
    public void 上限为零时不重试()
    {
        var p = new RetryPolicy(0);
        Assert.Equal(1, p.MaxAttempts);
        Assert.Null(p.NextDelay(0));
    }
}
