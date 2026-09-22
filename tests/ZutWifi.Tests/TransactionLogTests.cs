using ZutWifi.Diagnostics;
using ZutWifi.Tests.Support;
namespace ZutWifi.Tests;
public class TransactionLogTests
{
    [Fact]
    public void 表单里的密码只留长度不留明文()
    {
        var red = TransactionLog.RedactForm("DDDDD=%2C0%2C202500000001%40cmcc&upass=%2CSecret123&R1=0");
        Assert.DoesNotContain("Secret123", red);
        Assert.Contains("upass=***(len=12)", red);
        Assert.Contains("DDDDD=%2C0%2C202500000001%40cmcc", red);
        Assert.Contains("R1=0", red);
    }

    [Fact]
    public void 一行摘要含阶段状态与Location()
    {
        var line = new TransactionLog(new FakeClock()).LineFor(new TransactionRecord(
            "Login", "POST", "http://1.1.1.1:801/eportal/", 302, "http://1.1.1.1/3.htm", "成功", null));
        Assert.Contains("Login", line);
        Assert.Contains("status=302", line);
        Assert.Contains("3.htm", line);
        Assert.Contains("result=成功", line);
    }

    [Fact]
    public void 写盘按天分文件且可回读最近若干行()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        var log = new TransactionLog(new FakeClock(), dir);
        log.Write(new TransactionRecord("Probe", "GET", "u", 302, null, "未认证", null));
        log.Write(new TransactionRecord("Login", "POST", "u", 302, "3.htm", "成功", null));
        Assert.Single(Directory.GetFiles(dir, "app-*.log"));
        Assert.EndsWith("app-20260919.log", Directory.GetFiles(dir, "app-*.log").Single());
        var lines = log.RecentLines(10);
        Assert.Equal(2, lines.Count);
        Assert.Contains("Probe", lines[0]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 超过五份日志时删除最旧的()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (var d = 1; d <= 7; d++)
            File.WriteAllText(Path.Combine(dir, $"app-202609{d:00}.log"), "old");
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero));
        new TransactionLog(clock, dir).Write(new TransactionRecord("Probe", "GET", "u", 302, null, "x", null));
        Assert.Equal(5, Directory.GetFiles(dir, "app-*.log").Length);
        Assert.False(File.Exists(Path.Combine(dir, "app-20260901.log")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task 门户登录交互被记进日志()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        var clock = new FakeClock();
        var log = new TransactionLog(clock, dir);
        var h = new Support.FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1/3.htm") }, "");
        var gw = new Portal.PortalGateway(new HttpClient(h), "1.1.1.1", log);
        await gw.LoginAsync(new Portal.Credential("id", "pw", "@cmcc"), "10.1.1.1", default);
        var written = File.ReadAllText(Directory.GetFiles(dir, "app-*.log").Single());
        Assert.Contains("Login", written);
        Assert.DoesNotContain("pw", written);          // 明文密码绝不落盘
        Assert.Contains("len=", written);              // 只留长度
        Directory.Delete(dir, true);
    }

    // ── 以下为交付要求补的三条：脱敏口径逐字钉死 + 五类调用都要留痕 ──

    /// 这个程序要发给同学，日志文件是他们被要求发回来的东西：密码只要落一次盘，每份诊断包都在漏凭据。
    /// 用带 @ 和 . 的密码，是为了同时钉住 len 记的是百分号编码后的长度而不是原文长度。
    [Theory]
    [InlineData("http://1.1.1.1:80/3.htm")]                      // 认证成功
    [InlineData("http://1.1.1.1:80/2.htm?ErrorMsg=Nw%3D%3D")]    // 被门户拒绝（密码错）
    public async Task 登录落盘的行里只有upass长度没有密码(string location)
    {
        const string Password = "Pass@2026.";             // 百分号编码后是 %2CPass%402026.，共 15 字符
        var dir = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        var log = new TransactionLog(new FakeClock(), dir);
        var h = new Support.FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", location) }, "");
        var gw = new Portal.PortalGateway(new HttpClient(h), "1.1.1.1", log);

        await gw.LoginAsync(new Portal.Credential("202500000001", Password, "@cmcc"), "10.133.126.113", default);

        var written = File.ReadAllText(Directory.GetFiles(dir, "app-*.log").Single());
        Assert.DoesNotContain(Password, written);             // 原文
        Assert.DoesNotContain("Pass%402026", written);        // 编码后同样不许出现
        Assert.Contains("upass=***(len=12)", written);
        // 账号不是秘密，复盘全靠它：脱敏不能顺手把 DDDDDD 也抹掉。
        Assert.Contains("DDDDD=%2C0%2C202500000001%40cmcc", written);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 外来换行不许把一次调用劈成两行()
    {
        // Location 与异常消息都是外来文本：只要混进 CR/LF，"一次调用一行"这个复盘前提就塌了。
        var line = new TransactionLog(new FakeClock()).LineFor(new TransactionRecord(
            "Login", "POST", "http://1.1.1.1:801/eportal/", 302, "http://1.1.1.1/2.htm\r\nX: 伪造的一行",
            "被拒 form=upass=***(len=3)", "门户不可达"));
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("Login POST", line);
        Assert.Contains("error=门户不可达", line);
    }

    [Fact]
    public async Task 五类门户调用各留一行且POST带脱敏表单()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        var log = new TransactionLog(new FakeClock(), dir);
        var h = new Support.FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1/login") }, "");   // Probe → 未认证
        h.Enqueue(200, Array.Empty<(string, string)>(), "ss5=\"10.133.126.113\""); // GetIp
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1:80/3.htm") }, ""); // Login
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1/index.html?ACLogOut=1") }, ""); // Logout
        h.Enqueue(200, Array.Empty<(string, string)>(), "<html>Logout sec=3600</html>");    // OnlineSeconds
        var gw = new Portal.PortalGateway(new HttpClient(h), "1.1.1.1", log);
        var cred = new Portal.Credential("202500000001", "Secret99", "@cmcc");

        Assert.Equal(Portal.AuthState.Unauthenticated, await gw.ProbeAsync(default));
        Assert.Equal("10.133.126.113", await gw.GetClientIpAsync(default));
        Assert.True((await gw.LoginAsync(cred, "10.133.126.113", default)).IsSuccess);
        Assert.True((await gw.LogoutAsync("02a1b2c3d4e5", default)).IsSuccess);
        Assert.Equal(3600, await gw.GetOnlineSecondsAsync(default));

        var lines = log.RecentLines(50);
        Assert.Equal(5, lines.Count);
        Assert.Contains("Probe GET", lines[0]);
        Assert.Contains("9002", lines[0]);
        Assert.Contains("status=302", lines[0]);
        Assert.Contains("result=Unauthenticated", lines[0], StringComparison.Ordinal);
        Assert.Contains("GetIp", lines[1]);
        Assert.Contains("ip=10.133.126.113", lines[1]);
        Assert.Contains("Login", lines[2]);
        // Location 经 Uri 往返：:80 这个默认端口会被抹掉，落盘的是规范化后的地址。
        Assert.Contains("location=http://1.1.1.1/3.htm", lines[2], StringComparison.Ordinal);
        Assert.Contains("form=", lines[2]);
        Assert.Contains("upass=***(len=8)", lines[2], StringComparison.Ordinal); // Secret99 原样透传 = 8 字符
        Assert.Contains("Logout", lines[3]);
        Assert.Contains("ACLogOut=1", lines[3]);
        Assert.Contains("OnlineSeconds GET", lines[4]);
        Assert.Contains("seconds=3600", lines[4]);
        Assert.DoesNotContain("Secret99", string.Join('\n', lines));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task 门户不可达时异常原因进日志而不是静默()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        var log = new TransactionLog(new FakeClock(), dir);
        var h = new Support.FakeHttpHandler();
        h.EnqueueThrow(_ => new HttpRequestException("no route to host"));
        Assert.Equal(Portal.PortalOutcome.TransportError,
            (await new Portal.PortalGateway(new HttpClient(h), "1.1.1.1", log)
                .LoginAsync(new Portal.Credential("202500000001", "Secret99", "@cmcc"), "10.1.1.1", default)).Outcome);
        var line = log.RecentLines(10).Single();
        Assert.Contains("error=no route to host", line, StringComparison.Ordinal);
        Assert.Contains("status=-", line);
        Assert.Contains("upass=***(len=8)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret99", line);
        Directory.Delete(dir, true);
    }

    // ── 修轮 1 / Finding 1：日志写不下去时绝不抛，且失败要可见 ──
    // 这份日志是 60 秒定时器里的附属品：账号还在认证，磁盘故障不许把它变成异常冒出去；
    // 但静默吞掉也不许——写坏必须有一个不依赖日志本身就能读到的信号给 App 侧用。

    [Fact]
    public void 日志路径被一个普通文件占住时不抛且失败计数可见()
    {
        // Windows 上可稳定复现的"目录写不下去"：Dir 位置存在同名普通文件（真机等价物是目录被杀软/第二个实例占住）。
        var blocked = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "我是一个文件，不是目录");
        try
        {
            var log = new TransactionLog(new FakeClock(), blocked);
            var ex = Record.Exception(() => log.Write(RecordOf("Login")));
            Assert.Null(ex);                                       // 诊断不许打断登录
            Assert.True(log.WriteFailures >= 1);                   // 但必须留下可读的痕迹
            Assert.False(string.IsNullOrEmpty(log.FirstWriteFailure)); // 痕迹里带原因，否则无从排查
            Assert.Empty(log.RecentLines());                       // 此刻日志自身是读不到的——计数是唯一通道
        }
        finally { File.Delete(blocked); }
    }

    [Fact]
    public void 当天日志名被同名目录占住时不抛且原因可见()
    {
        // 这一条卡的是 CreateDirectory 成功、AppendAllText 才失败的那条出口。
        var dir = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "app-20260919.log"));
        try
        {
            var log = new TransactionLog(new FakeClock(), dir);
            var ex = Record.Exception(() => log.Write(RecordOf("Login")));
            Assert.Null(ex);
            Assert.Equal(1, log.WriteFailures);                    // 写失败的那一次不许被算成成功
            Assert.False(string.IsNullOrEmpty(log.FirstWriteFailure));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 裁剪删不掉被占用的旧文件时当前行照样落盘且不抛()
    {
        // Prune 在守卫范围内：旧文件被别的进程握着句柄（杀软扫描、第二个实例）时 Delete 抛 IOException，
        // 代价只能是"这一轮没裁成"，不能是整行日志丢掉 + 定时器里飞异常。
        var dir = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (var d = 1; d <= 7; d++) File.WriteAllText(Path.Combine(dir, $"app-202609{d:00}.log"), "old");
        var log = new TransactionLog(new FakeClock(), dir);
        Exception? thrown;
        int failures;
        using (File.Open(Path.Combine(dir, "app-20260901.log"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            thrown = Record.Exception(() => log.Write(RecordOf("Probe")));
            failures = log.WriteFailures;
        }
        Assert.Null(thrown);
        Assert.Equal(1, failures);
        var written = File.ReadAllText(Path.Combine(dir, "app-20260919.log"));
        Assert.Contains("Probe POST", written);                     // 行必须还在——写失败的是裁剪，不是这一行
        Assert.Single(log.RecentLines());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 目录在两次写之间被删掉时自动重建且不记失败()
    {
        // 目录整份被删是恢复得了的情况：下一行该正常写出，而不是把计数一路涨上去、
        // 让 App 侧误以为日志坏了（真机上这就是"用户在清理 %APPDATA%"）。
        var dir = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        var log = new TransactionLog(new FakeClock(), dir);
        log.Write(RecordOf("Probe"));
        Directory.Delete(dir, true);
        log.Write(RecordOf("Login"));
        Assert.Equal(0, log.WriteFailures);
        Assert.Single(log.RecentLines());                          // 重建后是全新的一份
        Assert.Contains("Login", log.RecentLines()[0]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 写失败后再写成功一次计数不清零且日志恢复()
    {
        // 计数器只在进程内有意义：60 秒一次探测下"曾经写坏过"这件事必须一直可见，
        // 否则 Task 17/18 想 surfaced 时恰好读到的是恢复后的那一次。
        var blocked = Path.Combine(Path.GetTempPath(), "zwlog" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "占位文件");
        var log = new TransactionLog(new FakeClock(), blocked);
        log.Write(RecordOf("Login"));
        Assert.True(log.WriteFailures >= 1);
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);
        log.Write(RecordOf("Login"));
        Assert.Contains("Login", log.RecentLines().Single());      // 同一实例自己缓过来，不用重建
        Assert.True(log.WriteFailures >= 1);
        Directory.Delete(blocked, true);
    }

    static TransactionRecord RecordOf(string stage) =>
        new(stage, "POST", "http://1.1.1.1:801/eportal/", 302, "http://1.1.1.1/3.htm", "成功", null);
}
