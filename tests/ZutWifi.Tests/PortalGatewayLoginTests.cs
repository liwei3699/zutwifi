using System.Web;
using ZutWifi.Diagnostics;
using ZutWifi.Portal;
using ZutWifi.Tests.Support;
namespace ZutWifi.Tests;
public class PortalGatewayLoginTests
{
    static readonly Credential Cred = new("202500000001", "Pass@2024.", "@cmcc");
    const string Ip = "10.133.126.113";

    static PortalGateway Gw(FakeHttpHandler h) => new(new HttpClient(h), "1.1.1.1");

    [Fact]
    public async Task Location含3htm判成功()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        var r = await Gw(h).LoginAsync(Cred, Ip, default);
        Assert.True(r.IsSuccess);
        Assert.Contains("3.htm", r.RawLocation!);
    }

    [Fact]
    public async Task Location含2htm时解出中文原因与错误码()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("login_reject_pwerr.txt"));
        var r = await Gw(h).LoginAsync(Cred, Ip, default);
        Assert.Equal(PortalOutcome.Rejected, r.Outcome);
        Assert.Equal(7, r.ErrorCode);
        Assert.Equal("Radius 认证失败（账号或密码错误）", r.Reason);
    }

    [Fact]
    public async Task IP已在线时透出错误码2()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("login_ip_online.txt"));
        Assert.Equal(2, (await Gw(h).LoginAsync(Cred, Ip, default)).ErrorCode);
    }

    [Fact]
    public async Task 表单字段与浏览器抓包逐字一致()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        await Gw(h).LoginAsync(Cred, Ip, default);
        var form = HttpUtility.ParseQueryString(h.Requests[0].Body!);
        Assert.Equal(",0,202500000001@cmcc", form["DDDDD"]);
        Assert.Equal("Pass@2024.", form["upass"]);       // 原样透传：模板一个字符都不补（抓包里的 %2C 属于密码本身）
        Assert.Equal("00", form["para"]);
        Assert.Equal("123456", form["0MKKey"]);
        Assert.Equal("", form["Login"]);
    }

    [Fact]
    public async Task 密码自带前导逗号时不多切一份字段()
    {
        // 真机那位用户的密码就是 `,XXX@XXXX.`。这一条盯的是"别把密码自己的逗号当协议分隔符再补一个"——
        // 补了之后门户切字段切歪，回的是 `userid error2`（说的是 userid，看起来跟密码无关，最难往这想）。
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        await Gw(h).LoginAsync(Cred with { Password = ",Pass@2024." }, Ip, default);
        Assert.Equal(",Pass@2024.", HttpUtility.ParseQueryString(h.Requests[0].Body!)["upass"]);
    }

    [Fact]
    public async Task 登录请求不带任何Cookie()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        await Gw(h).LoginAsync(Cred, Ip, default);
        Assert.Null(h.Requests[0].CookieHeader);
    }

    [Fact]
    public async Task 非302响应视为传输异常而不是失败原因()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(500, Array.Empty<(string, string)>(), "gateway error");
        var r = await Gw(h).LoginAsync(Cred, Ip, default);
        Assert.Equal(PortalOutcome.TransportError, r.Outcome);
        Assert.Contains("500", r.Reason!);
    }

    [Fact]
    public async Task 既非3htm也非2htm的跳转判传输异常而不是失败()
    {
        // 成功/失败只由 3.htm、2.htm 两个标记证明。跳到别处（改版、被网关拦截、会话过期页）
        // 认证结果未知，不能伪造一个 ErrorCode=-1 的"门户拒绝"让上层去退避重登。
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1:80/9.htm?wlanuserip=10.133.126.113") }, "");
        var r = await Gw(h).LoginAsync(Cred, Ip, default);
        Assert.Equal(PortalOutcome.TransportError, r.Outcome);
        Assert.Null(r.ErrorCode);
    }

    [Fact]
    public async Task 二htm但没有ErrorMsg时仍是拒绝而不是传输异常()
    {
        // 收口 3.htm/2.htm 判据时不能把"2.htm 且解不出码"一起推到 Transport 那一侧。
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1:80/2.htm?wlanuserip=10.133.126.113") }, "");
        var r = await Gw(h).LoginAsync(Cred, Ip, default);
        Assert.Equal(PortalOutcome.Rejected, r.Outcome);
        Assert.Equal(-1, r.ErrorCode);
        Assert.Equal("门户返回未知错误", r.Reason);
    }

    [Fact]
    public async Task 登录请求为POST且媒体类型不带charset()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        var spy = new RequestSpy { InnerHandler = h };
        await new PortalGateway(new HttpClient(spy), "1.1.1.1").LoginAsync(Cred, Ip, default);
        Assert.Equal(HttpMethod.Post, h.Requests[0].Method);
        // 抓包原文只有裸媒体类型；StringContent(…, Encoding.ASCII, …) 会追加 "; charset=us-ascii"。
        Assert.Equal("application/x-www-form-urlencoded", spy.ContentType);
        Assert.DoesNotContain("charset", spy.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 响应体解码用同一份GBK通道()
    {
        // 与下面 真实GBK中文消息… 一条用同一份常量：那串字节是离线独立算出后逐字钉住的（不是测试内自编码自解码），
        // 这里让 ReadBodyAsync 走一遍门户的中文通道：编码注册若挪到 GetEncoding(936) 之后、
        // 或静态初始化被改掉，这条立刻红。
        const string B64 = "1cu6xdLRzaO7+qOsx+u1vdfU1vq3/s7xz7XNs73Jt9E=";
        const string Msg = "账号已停机，请到自助服务系统缴费";
        using var resp = new HttpResponseMessage { Content = new ByteArrayContent(Convert.FromBase64String(B64)) };
        Assert.Equal(Msg, await PortalGateway.ReadBodyAsync(resp, default));
    }

    [Fact]
    public void ErrorMsg解码兼容base64与纯数字两种门户变体()
    {
        Assert.Equal((7, "7"), PortalGateway.DecodePortalError("http://1.1.1.1/2.htm?ErrorMsg=Nw%3D%3D"));
        Assert.Equal((2, "2"), PortalGateway.DecodePortalError("http://1.1.1.1/2.htm?ErrorMsg=2"));
        Assert.Equal<(int?, string)>((null, ""), PortalGateway.DecodePortalError("http://1.1.1.1/2.htm"));
    }

    [Fact]
    public void 真实GBK中文消息走base64解码并落入未知码文案()
    {
        // 常量 = GBK(936) 编码下"账号已停机，请到自助服务系统缴费"的 base64（离线用 PowerShell 独立算出后逐字钉住，
        // 不让测试用同一编码自编码自解码，才真正覆盖门户的 GBK 通道）。含 + / = 三种字符，顺带钉住 URL 转义往返。
        const string B64 = "1cu6xdLRzaO7+qOsx+u1vdfU1vq3/s7xz7XNs73Jt9E=";
        const string Msg = "账号已停机，请到自助服务系统缴费";
        var loc = "http://1.1.1.1:80/2.htm?ACLogOut=5&ErrorMsg=" + Uri.EscapeDataString(B64);
        var (code, text) = PortalGateway.DecodePortalError(loc);
        Assert.Null(code);
        Assert.Equal(Msg, text);
        Assert.Equal("门户返回：" + Msg, PortalErrorCodes.Describe(code ?? -1, text));
    }

    // ── 修轮 1 / Finding 2：认证标记只许落在 Location 的路径段上 ──
    // Contains("3.htm") 同时吃掉 13.htm、23.htm 与 ?next=/3.htm：认证成功是本套判据里最危险的假阳性
    // ——它会停止重登、把托盘置成"已连接"，而账号其实根本没上去。所以比对只看 ? 之前、且要求以 /3.htm 结尾。

    [Theory]
    [InlineData("http://1.1.1.1:80/13.htm")]              // 门户真有 13.htm 这类页面时不许读成成功
    [InlineData("http://1.1.1.1:80/23.htm")]
    [InlineData("http://1.1.1.1/portal?next=/3.htm")]     // 3.htm 出现在查询参数里，不算跳转目标
    [InlineData("http://1.1.1.1:80/index.html?url=http://x/3.htm")]
    public async Task 成功标记只认路径末尾的3htm(string location)
    {
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", location) }, "");
        var r = await Gw(h).LoginAsync(Cred, Ip, default);
        Assert.Equal(PortalOutcome.TransportError, r.Outcome);   // 未知就是未知
        Assert.Null(r.ErrorCode);
        Assert.Contains("无认证判据", r.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://1.1.1.1:80/12.htm")]              // 后缀里含 2.htm 的别的页面
    [InlineData("http://1.1.1.1/redirect?to=/2.htm")]     // 2.htm 只在查询参数里
    public async Task 拒绝标记只认路径末尾的2htm(string location)
    {
        // 假拒绝比假成功更常触发：它会带着 ErrorCode=-1 走"退避重登"，把未知响应变成有依据的失败。
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", location) }, "");
        var r = await Gw(h).LoginAsync(Cred, Ip, default);
        Assert.Equal(PortalOutcome.TransportError, r.Outcome);
        Assert.Null(r.ErrorCode);
    }

    [Theory]
    [InlineData("http://1.1.1.1:80/3.htm?wlanuserip=10.133.126.113&session=", PortalOutcome.Success)]
    [InlineData("http://1.1.1.1:80/2.htm?wlanuserip=10.133.126.113&ACLogOut=5&ErrorMsg=Nw%3D%3D", PortalOutcome.Rejected)]
    [InlineData("http://1.1.1.1:80/9.htm?wlanuserip=10.133.126.113", PortalOutcome.TransportError)]
    public async Task 收口路径段之后真机形态的判定一个不变(string location, PortalOutcome expected)
    {
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", location) }, "");
        var r = await Gw(h).LoginAsync(Cred, Ip, default);
        Assert.Equal(expected, r.Outcome);
        if (expected == PortalOutcome.Rejected) Assert.Equal(7, r.ErrorCode);   // ErrorMsg 在查询段里，剥掉它才判路径
        if (expected == PortalOutcome.Success) Assert.Contains("3.htm", r.RawLocation!, StringComparison.Ordinal);
    }

    // ── 修轮 1 / Finding 1：日志写不下去不许打断登录 ──

    [Fact]
    public async Task 日志写不下去时登录不抛且仍返回成功判定()
    {
        var blocked = WriteBlockedLogPath();
        try
        {
            var log = new TransactionLog(new FakeClock(), blocked);
            var h = new FakeHttpHandler();
            h.Enqueue(302, new[] { ("Location", "http://1.1.1.1:80/3.htm?wlanuserip=10.133.126.113") }, "");
            var r = await new PortalGateway(new HttpClient(h), "1.1.1.1", log).LoginAsync(Cred, Ip, default);
            Assert.Equal(PortalOutcome.Success, r.Outcome);      // 判定与写盘无关
            Assert.True(log.WriteFailures >= 1);                 // 但这次写坏要能被 App 侧看到
        }
        finally { File.Delete(blocked); }
    }

    [Fact]
    public async Task 日志写不下去且门户不可达时仍返回传输异常而不是抛出IO异常()
    {
        // 异常出口里的 Record 同样在 catch 过滤器之外：写日志失败会顶掉 HttpRequestException 的分类，
        // 让"门户不可达"变成一个没人接的 IOException。
        var blocked = WriteBlockedLogPath();
        try
        {
            var log = new TransactionLog(new FakeClock(), blocked);
            var h = new FakeHttpHandler();
            h.EnqueueThrow(_ => new HttpRequestException("no route to host"));
            var r = await new PortalGateway(new HttpClient(h), "1.1.1.1", log).LoginAsync(Cred, Ip, default);
            Assert.Equal(PortalOutcome.TransportError, r.Outcome);
            Assert.Contains("no route to host", r.Reason!, StringComparison.Ordinal);
            Assert.True(log.WriteFailures >= 1);
        }
        finally { File.Delete(blocked); }
    }

    /// 一个"存在但写不下去"的日志目录：路径位置上放着一个普通文件，CreateDirectory/AppendAllText 必抛。
    /// 真机上对应的是 %APPDATA%\ZutWifi\logs 被杀软或第二个实例占住——诊断包不会替我们兜住这一手。
    static string WriteBlockedLogPath()
    {
        var p = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(p, "我是一个文件，不是目录");
        return p;
    }

    /// 只旁观请求、不改行为：SentRequest 记的是方法/URI/体/Cookie，Content-Type 得在请求离开前就地取。
    sealed class RequestSpy : DelegatingHandler
    {
        public string? ContentType { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ContentType = request.Content?.Headers.ContentType?.ToString();
            return base.SendAsync(request, ct);
        }
    }
}
