using ZutWifi.Portal;
using ZutWifi.Tests.Support;
namespace ZutWifi.Tests;
public class PortalGatewayLogoutTests
{
    static PortalGateway Gw(FakeHttpHandler h) => new(new HttpClient(h), "1.1.1.1");

    [Fact]
    public async Task ACLogOut为1判注销成功()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("logout_success.txt"));
        Assert.True((await Gw(h).LogoutAsync("02a1b2c3d4e5", default)).IsSuccess);
    }

    [Fact]
    public async Task ACLogOut为2判失败并给中文原因()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1:/2.htm?ACLogOut=2") }, "");
        var r = await Gw(h).LogoutAsync("02a1b2c3d4e5", default);
        Assert.Equal(PortalOutcome.Rejected, r.Outcome);
        Assert.Equal("Radius 注销失败", r.Reason);
    }

    [Fact]
    public async Task 注销请求为空表单体且URL带MAC()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("logout_success.txt"));
        await Gw(h).LogoutAsync("02a1b2c3d4e5", default);
        Assert.Equal("", h.Requests[0].Body);
        Assert.Contains("a=Logout", h.Requests[0].Uri.Query);
        Assert.Contains("mac=02a1b2c3d4e5", h.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task 在线时长从sec字段取()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        Assert.Equal(4211, await Gw(h).GetOnlineSecondsAsync(default));
    }

    [Fact]
    public async Task 未认证时在线时长为null()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        Assert.Null(await Gw(h).GetOnlineSecondsAsync(default));
    }

    [Fact]
    public async Task 非二xx响应里的sec不可信归null()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(503, Array.Empty<(string, string)>(), "<html>sec=99</html>");
        Assert.Null(await Gw(h).GetOnlineSecondsAsync(default));
    }

    [Fact]
    public async Task sec超出int范围时归null而不是抛异常逃出探测循环()
    {
        // int.Parse 在这里抛 OverflowException，它不属于网关 catch 的三种异常，会一路冒到 60 秒刷新循环里。
        var h = new FakeHttpHandler();
        h.Enqueue(200, Array.Empty<(string, string)>(), "<html>sec=99999999999999</html>");
        Assert.Null(await Gw(h).GetOnlineSecondsAsync(default));
    }

    [Fact]
    public async Task sec取int上限边界值仍要正常解析()
    {
        // 反向兜底：上一条改成 null 之后，别把合法的大数一起判死。
        var h = new FakeHttpHandler();
        h.Enqueue(200, Array.Empty<(string, string)>(), "<html>sec=2147483647</html>");
        Assert.Equal(int.MaxValue, await Gw(h).GetOnlineSecondsAsync(default));
    }

    [Fact]
    public async Task ACLogOut为10不得误判注销成功()
    {
        // 门户 ACLogOut 只有 0–5，登录拒绝也复用同名参数；Contains("ACLogOut=1") 会吃掉 =10/=12。
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1:801/eportal/?a=Logout&ACLogOut=10") }, "");
        Assert.Equal(PortalOutcome.TransportError, (await Gw(h).LogoutAsync("02a1b2c3d4e5", default)).Outcome);
    }

    [Fact]
    public async Task ACLogOut为20不得误判Radius注销失败()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1:801/eportal/?a=Logout&ACLogOut=20&ErrorMsg=Mg%3D%3D") }, "");
        var r = await Gw(h).LogoutAsync("02a1b2c3d4e5", default);
        Assert.Equal(PortalOutcome.TransportError, r.Outcome);
        Assert.Null(r.ErrorCode);
    }

    [Fact]
    public async Task 注销的非跳转响应即使带ACLogOut也判传输异常并说明状态码()
    {
        // 判据只在 3xx 的 Location 上成立；门户若用 200 + 页面回话，结果未知。
        var h = new FakeHttpHandler();
        h.Enqueue(200, new[] { ("Location", "http://1.1.1.1/2.htm?ACLogOut=1") }, "<html>已注销</html>");
        var r = await Gw(h).LogoutAsync("02a1b2c3d4e5", default);
        Assert.Equal(PortalOutcome.TransportError, r.Outcome);
        Assert.Contains("200", r.Reason!);
    }

    [Fact]
    public async Task 无Location的跳转只说无Location()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(302, Array.Empty<(string, string)>(), "");
        var r = await Gw(h).LogoutAsync("02a1b2c3d4e5", default);
        Assert.Equal(PortalOutcome.TransportError, r.Outcome);
        Assert.Contains("无 Location", r.Reason!);
    }

    [Fact]
    public async Task 带Location但无判据时句子不再自称无Location()
    {
        const string Loc = "http://1.1.1.1/2.htm?ACLogOut=7";
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", Loc) }, "");
        var r = await Gw(h).LogoutAsync("02a1b2c3d4e5", default);
        Assert.Equal(PortalOutcome.TransportError, r.Outcome);
        Assert.Contains(Loc, r.Reason, StringComparison.Ordinal);   // 真正收到的 loc 要进日志
        Assert.DoesNotContain("无 Location", r.Reason!, StringComparison.Ordinal); // 旧文案在这里一边说"无 Location"一边打印 loc
    }
}
