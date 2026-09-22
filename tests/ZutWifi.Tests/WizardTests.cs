using ZutWifi;
using ZutWifi.Config;
using ZutWifi.Diagnostics;
using ZutWifi.Portal;
using ZutWifi.Shell;
using ZutWifi.Tests.Support;

namespace ZutWifi.Tests;

/// 向导的"立即测试"是唯一会在配置阶段真给门户交凭据的动作，所以逐条钉：
/// 走的是探测→取 IP→登录这三步、零 Cookie、永不发注销、密码只从 DPAPI 里取、
/// 门户已经认证了就绝不再交一个登录包（重复提交正是账号保护的来源）。
/// 真注册表与开始菜单都不碰：自启与网关两条通路都是构造参数注入的接缝。
public class WizardTests
{
    static string Tmp() => Path.Combine(Path.GetTempPath(), "zwwiz" + Guid.NewGuid().ToString("N"));

    private sealed class AutoStartSpy
    {
        public List<bool> Writes { get; } = [];
        public string? Ensure(bool enabled) { Writes.Add(enabled); return null; }
    }

    private static (FirstRunWizard wizard, AutoStartSpy spy) NewWizard(string dir, FakeHttpHandler h)
    {
        var spy = new AutoStartSpy();
        return (new FirstRunWizard(new SettingsStore(dir), new SecretStore(dir),
            () => new PortalGateway(new HttpClient(h), "1.1.1.1"), spy.Ensure), spy);
    }

    /// 探测→未认证、取 IP→真机登录页、登录→3.htm。
    private static FakeHttpHandler ReplaySuccess()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        return h;
    }

    [Fact]
    public async Task 立即测试三步都出结果并报到成功()
    {
        var dir = Tmp();
        var h = ReplaySuccess();
        var (wizard, _) = NewWizard(dir, h);
        using (wizard)
        {
            wizard.SetStudentId("202500000001");
            wizard.SetPassword("Pass@2024.");
            wizard.SetIsp("@ctcc");

            await wizard.SimulateRunTestAsync();

            var output = wizard.TestOutput;
            Assert.Contains("Unauthenticated", output);          // ① 门户判定
            Assert.Contains("10.133.126.113", output);           // ② 内网地址
            Assert.Contains("成功", output);                       // ③ 登录判据（Location 结尾 3.htm）
        }
    }

    [Fact]
    public async Task 立即测试从不带Cookie也从不发注销包()
    {
        var dir = Tmp();
        var h = ReplaySuccess();
        var (wizard, _) = NewWizard(dir, h);
        using (wizard)
        {
            wizard.SetStudentId("202500000001");
            wizard.SetPassword("Pass@2024.");

            await wizard.SimulateRunTestAsync();

            Assert.Equal(3, h.Requests.Count);                   // 探测 / 取 IP / 登录，一个不多
            Assert.All(h.Requests, r => Assert.Null(r.CookieHeader));
            Assert.DoesNotContain(h.Requests, r => r.Uri.Query.Contains("a=Logout"));
            Assert.DoesNotContain(h.Requests, r => (r.Body ?? "").Contains("ACLogOut"));
            Assert.Contains(h.Requests, r => r.Method == HttpMethod.Post);
        }
    }

    [Fact]
    public async Task 点立即测试前先把页面上的值落盘()
    {
        var dir = Tmp();
        var h = ReplaySuccess();
        var (wizard, spy) = NewWizard(dir, h);
        using (wizard)
        {
            wizard.SetStudentId("202500000001");
            wizard.SetIsp("@ctcc");
            wizard.SetPassword("Pass@2024.");

            await wizard.SimulateRunTestAsync();

            Assert.Equal("@ctcc", new SettingsStore(dir).Load().IspSuffix);
            Assert.Equal("Pass@2024.", new SecretStore(dir).Get());
            Assert.NotEmpty(spy.Writes);                          // 自启意图也照样走注入的那条通路
        }
    }

    [Fact]
    public async Task 登录用的是DPAPI里那份密码而不是框里的明文()
    {
        var dir = Tmp();
        new SecretStore(dir).Set("Stored@Pw");
        var h = ReplaySuccess();
        var (wizard, _) = NewWizard(dir, h);
        using (wizard)
        {
            wizard.SetStudentId("202500000001");
            wizard.SetPassword("");                               // 框里空的 = 不改，用盘上那份
            Assert.Equal("已保存（留空表示不修改）", wizard.PasswordPlaceholderText);

            await wizard.SimulateRunTestAsync();

            var body = h.Requests[2].Body!;
            // 发出去的就是 DPAPI 里那一份，一字不加（模板不再补前导逗号）。
            Assert.Contains(Uri.EscapeDataString("Stored@Pw"), body);
            Assert.DoesNotContain("Stored@Pw", body);            // 表单体里只有百分号编码后的那一份
        }
    }

    [Fact]
    public async Task 没填密码时立即测试一个请求都不发()
    {
        // 空密码等于白送一次 RADIUS 失败计数；三次退避打完就是把账号打进锁定。
        var dir = Tmp();
        var h = new FakeHttpHandler();
        var (wizard, _) = NewWizard(dir, h);
        using (wizard)
        {
            wizard.SetStudentId("202500000001");

            await wizard.SimulateRunTestAsync();

            Assert.Empty(h.Requests);
            Assert.Contains("密码", wizard.TestOutput);
        }
    }

    [Fact]
    public async Task 门户已认证时不再提交第二个登录包()
    {
        var dir = Tmp();
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        var (wizard, _) = NewWizard(dir, h);
        using (wizard)
        {
            wizard.SetStudentId("202500000001");
            wizard.SetPassword("Pass@2024.");

            await wizard.SimulateRunTestAsync();

            Assert.Single(h.Requests);                             // 只有探测那一次
            Assert.Contains("Authenticated", wizard.TestOutput);
            Assert.DoesNotContain(h.Requests, r => r.Method == HttpMethod.Post);
        }
    }

    [Fact]
    public async Task 门户拒绝时输出中文原因而不是笼统失败()
    {
        var dir = Tmp();
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_reject_pwerr.txt"));
        var (wizard, _) = NewWizard(dir, h);
        using (wizard)
        {
            wizard.SetStudentId("202500000001");
            wizard.SetPassword("WrongPass!");

            await wizard.SimulateRunTestAsync();

            Assert.Contains("失败", wizard.TestOutput);
            Assert.Contains("Radius 认证失败（账号或密码错误）", wizard.TestOutput);
        }
    }

    [Fact]
    public void 完成之前不算跑完首次配置()
    {
        var dir = Tmp();
        using var wizard = new FirstRunWizard(new SettingsStore(dir), new SecretStore(dir),
            gatewayFactory: () => new PortalGateway(new HttpClient(new FakeHttpHandler()), "1.1.1.1"),
            applyAutoStart: _ => null);
        wizard.SetStudentId("202500000001");
        wizard.SetPassword("Pass@2024.");
        wizard.SimulateSave();
        Assert.True(AppContext.NeedsFirstRun(dir));               // 填完了但没点完成，下次启动还得进来

        wizard.SimulateFinish();
        Assert.True(wizard.Finished);
        Assert.True(new SettingsStore(dir).Load().FirstRunCompleted);
        Assert.False(AppContext.NeedsFirstRun(dir));
    }

    [Fact]
    public void 完成时不把空密码写成密文()
    {
        var dir = Tmp();
        using var wizard = new FirstRunWizard(new SettingsStore(dir), new SecretStore(dir),
            gatewayFactory: () => new PortalGateway(new HttpClient(new FakeHttpHandler()), "1.1.1.1"),
            applyAutoStart: _ => null);
        wizard.SetStudentId("202500000001");
        wizard.SimulateFinish();
        Assert.True(new SettingsStore(dir).Load().FirstRunCompleted);
        Assert.Null(new SecretStore(dir).Get());                  // 什么都没填，密文里也不该有空串
        Assert.True(AppContext.NeedsFirstRun(dir));               // 但"没密码"仍然算没配完
    }

    // ---------- 向导也要往事务日志里留一行（评审 I3） ----------

    /// 同一条门户通路，装配那边给的日志必须传进来：同一次连接里点设置页的"测试配置"会留记录，
    /// 而**第一次**登录（最可能就是填错的那一次）反而什么都不留，诊断包就是空的。
    /// 这里刻意不注入 gatewayFactory —— 走的就是向导自己 new 的那一个门户客户端，
    /// 只有这样"日志有没有接到网关上"才测得到（注入的工厂自己 new 的网关测的是它自己）。
    [Fact]
    public async Task 向导里的立即测试给门户调用留一行脱敏记录()
    {
        var dir = Tmp();
        var log = new TransactionLog(new FakeClock(), Path.Combine(dir, "logs"));
        var h = ReplaySuccess();
        using var wizard = new FirstRunWizard(new SettingsStore(dir), new SecretStore(dir),
            applyAutoStart: _ => null, log: log, portalHandler: h);
        wizard.SetStudentId("202500000001");
        wizard.SetPassword("Pass@2024.");

        await wizard.SimulateRunTestAsync();

        var written = string.Join('\n', log.RecentLines());
        Assert.Contains("Login", written);                         // 三条门户调用都落在同一份日志里
        Assert.Contains("upass=***(len=", written);                // 密码只以长度出现
        Assert.Matches(@"upass=\*\*\*\(len=\d+\)", written);
        Assert.DoesNotContain("Pass@2024", written);               // 明文一个字符都不许进日志
    }

    /// 网关之外抛出来的异常（读盘、实现 bug）由设置页那条 ReportFault 通路记账，
    /// 而向导里的就是同一个设置页 —— 日志没传进页面的话这一行会消失。
    [Fact]
    public async Task 向导里测试抛出的异常也落进同一份日志()
    {
        var dir = Tmp();
        var log = new TransactionLog(new FakeClock(), Path.Combine(dir, "logs"));
        var h = new FakeHttpHandler();
        h.EnqueueThrow(_ => new InvalidDataException("门户答非所问"));
        using var wizard = new FirstRunWizard(new SettingsStore(dir), new SecretStore(dir),
            applyAutoStart: _ => null, log: log, portalHandler: h);
        wizard.SetStudentId("202500000001");
        wizard.SetPassword("Pass@2024.");

        await wizard.SimulateRunTestAsync();

        var written = string.Join('\n', log.RecentLines());
        Assert.Contains("测试配置没跑完", written);                  // 设置页那条 ReportFault 的原文
        Assert.Contains("门户答非所问", written);
        Assert.Contains("测试没跑完", wizard.TestOutput);           // 界面上同样看得见
    }

    /// 评审 I4：向导的"完成"也是一次 Load→改一个字段→Save。文件正被编辑器/网盘独占时
    /// 那一次写回去就是把同学刚存好的门户地址、重试上限与白名单整份刷成出厂值。
    /// 这里要的是：什么都不写、不装成"已完成"（下一次启动向导还会再来）、也不把向导顶掉。
    [Fact]
    public void 完成时文件被独占就不写回也不装成已完成()
    {
        var dir = Tmp();
        var store = new SettingsStore(dir);
        store.Save(new Settings
        {
            StudentId = "202500000001", PortalHost = "1.2.3.4", MaxRetries = 1,
            SsidWhitelist = ["zut-stu", "zut-teacher"],
        });
        using var wizard = new FirstRunWizard(store, new SecretStore(dir),
            gatewayFactory: () => new PortalGateway(new HttpClient(new FakeHttpHandler()), "1.1.1.1"),
            applyAutoStart: _ => null);
        var before = File.ReadAllText(store.FilePath);

        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(Record.Exception(wizard.SimulateFinish));

        Assert.False(wizard.Finished);
        Assert.Equal(before, File.ReadAllText(store.FilePath));
        Assert.Equal("1.2.3.4", new SettingsStore(dir).Load().PortalHost);
        Assert.True(AppContext.NeedsFirstRun(dir));               // 没写成 ⇒ 下次启动仍然弹向导
    }
}
