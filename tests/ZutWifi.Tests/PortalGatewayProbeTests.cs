using ZutWifi.Portal;
using ZutWifi.Tests.Support;
namespace ZutWifi.Tests;
public class PortalGatewayProbeTests
{
    const string Host = "1.1.1.1";

    [Fact]
    public async Task 九零二跳回门户即未认证()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        Assert.Equal(AuthState.Unauthenticated, await new PortalGateway(new HttpClient(h), Host).ProbeAsync(default));
    }

    [Fact]
    public async Task 九零二返回Logout页即已认证()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        Assert.Equal(AuthState.Authenticated, await new PortalGateway(new HttpClient(h), Host).ProbeAsync(default));
    }

    [Fact]
    public async Task 既非跳转也无Logout字样时归为Unknown而不是未认证()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(200, Array.Empty<(string, string)>(), "<html>something else</html>");
        Assert.Equal(AuthState.Unknown, await new PortalGateway(new HttpClient(h), Host).ProbeAsync(default));
    }

    [Fact]
    public async Task 非二xx响应的Logout字样不可信归为Unknown()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(503, Array.Empty<(string, string)>(), "<html>Logout</html>");
        Assert.Equal(AuthState.Unknown, await new PortalGateway(new HttpClient(h), Host).ProbeAsync(default));
    }

    [Fact]
    public async Task 三零二跳向外站即使带Logout字样也归Unknown而不是未认证()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://portal.example.com/login") }, "<html>Logout</html>");
        Assert.Equal(AuthState.Unknown, await new PortalGateway(new HttpClient(h), Host).ProbeAsync(default));
    }

    [Fact]
    public async Task 探测不可达归为Unknown()
    {
        var h = new FakeHttpHandler();
        h.EnqueueThrow(_ => new HttpRequestException("unreachable"));
        Assert.Equal(AuthState.Unknown, await new PortalGateway(new HttpClient(h), Host).ProbeAsync(default));
    }

    [Fact]
    public async Task 从门户页面解析服务端注入的本机IP()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        Assert.Equal("10.133.126.113", await new PortalGateway(new HttpClient(h), Host).GetClientIpAsync(default));
    }

    [Fact]
    public async Task 页面没有IP字段时返回null让上层回退网卡IP()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(200, Array.Empty<(string, string)>(), "<html>nothing</html>");
        Assert.Null(await new PortalGateway(new HttpClient(h), Host).GetClientIpAsync(default));
    }
}
