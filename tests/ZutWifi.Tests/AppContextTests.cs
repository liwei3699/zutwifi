using System.Reflection;
using System.Threading;
using Windows.UI.Notifications;
using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Diagnostics;
using ZutWifi.Notify;
using ZutWifi.Shell;
using ZutWifi.Tests.Support;
using ZutWifi.Wifi;

namespace ZutWifi.Tests;

/// 装配点的用例。这里唯一允许"真"的东西是 DPAPI（它只读写临时目录里的密文），
/// 其余会动到同学机器的通路全部走注入：
/// 操作系统通知面（`sink`，见 Support/RecordingSink.cs）、%APPDATA%\ZutWifi（dir）、
/// wlanapi（wifi 源）、门户网络（handler）。`sink` 是 Build 的必填参数 —— 想不起来传它就是编译错误。
public class AppContextTests : IDisposable
{
    private readonly TempSpace _tmp = new();

    /// 这一条用例用的那份记账器：装配打的每一个"操作系统出口"都记在这上面。
    private readonly RecordingSink _os = new();

    string Tmp() => _tmp.NewDir("zwctx");

    /// 绝大多数用例测的是"老用户"那个常态：目录里已经有一份配好的设置。
    /// 装配在没跑完首次向导时故意不开自动登录那条通路（见 首次向导没跑完… 那条用例），
    /// 所以不先建档的话事件通路那几条测的就不是同一条路径了。
    private static string ConfiguredDir(string dir)
    {
        new SecretStore(dir).Set("Pass@2024.");
        new SettingsStore(dir).Save(new Settings
        {
            StudentId = "202500000001", IspSuffix = "@ctcc", FirstRunCompleted = true,
        });
        return dir;
    }

    /// 走完整装配路径的那一条 helper。`aumidFailure` 非 null = "开始菜单快捷方式没写成"，
    /// 装配据此把通知判死成气泡；它经由 sink 交回，所以真的 `AumidRegistrar.Ensure()` 一次都不跑。
    private AppParts Build(string dir, IWifiSource? wifi = null, string? aumidFailure = null,
        FakeHttpHandler? portal = null, FakeHttpHandler? probe = null, IClock? clock = null)
    {
        _os.AumidFailure = aumidFailure;
        return AppContext.Build(_os.Sink, ConfiguredDir(dir), portal ?? new FakeHttpHandler(),
            probe ?? new FakeHttpHandler(), wifi ?? new FakeWifiSource(), clock ?? new FakeClock());
    }

    /// 这一条用例登记的临时目录一律在这里收走（断言抛了 xUnit 也照样调 Dispose）。
    public void Dispose() => _tmp.Dispose();

    // ---------- 单实例与首次向导判定 ----------

    [Fact]
    public void 单实例互斥量第二次申请失败()
    {
        Assert.True(Program.TryAcquireSingleInstance(out var first));
        Assert.False(Program.TryAcquireSingleInstance(out var second));
        second.Dispose();
        first.Dispose();
        Assert.True(Program.TryAcquireSingleInstance(out var third));
        third.Dispose();
    }

    /// 评审次项：拿不到锁那一支以前直接 return 0，于是 TryAcquireSingleInstance new 出来的
    /// 那个内核句柄一直留在原地。SecondInstance 现在头一件事就是把它 Dispose ——
    /// 这条用例钉的就是"句柄还回去了"，弹框那件事走接缝（测试里不许真弹一个模态框）。
    [Fact]
    public void 第二份实例退出前把自己那个互斥量句柄还掉()
    {
        Assert.True(Program.TryAcquireSingleInstance(out var owner));
        Assert.False(Program.TryAcquireSingleInstance(out var duplicate));
        try
        {
            var said = new List<string>();
            Assert.Equal(0, Program.SecondInstance(duplicate, said.Add));
            Assert.Single(said);                                              // 说一句话，就一句
            Assert.Throws<ObjectDisposedException>(() => duplicate.WaitOne(50));   // 已经 Dispose
        }
        finally { owner.Dispose(); }
    }

    [Fact]
    public void 无密码时视为需要首次向导()
    {
        var dir = Tmp();
        Assert.True(AppContext.NeedsFirstRun(dir));
        new SecretStore(dir).Set("x");
        Assert.True(AppContext.NeedsFirstRun(dir));                 // 光有密码不够：账号与"跑完向导"都要有
        new SettingsStore(dir).Save(new Settings { StudentId = "1", FirstRunCompleted = true });
        Assert.False(AppContext.NeedsFirstRun(dir));
    }

    [Fact]
    public void 数据目录落在当前用户的APPDATA下()
        => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZutWifi"),
            AppContext.DataDir);

    // ---------- 一份日志 ----------

    [Fact]
    public async Task 门户与主窗口共用装配时那一份事务日志()
    {
        var dir = Tmp();
        new SecretStore(dir).Set("Pass@2024.");
        var portal = new FakeHttpHandler();
        portal.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        portal.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        portal.EnqueueRaw(Fixtures.Read("login_success.txt"));
        var probe = new FakeHttpHandler();
        probe.Enqueue(200, Array.Empty<(string, string)>(), "Microsoft NCSI");
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabbccddeeff") };

        var parts = Build(dir, wifi, portal: portal, probe: probe);
        using (parts)
        {
            await parts.Coord.RequestLoginAsync(CancellationToken.None);

            Assert.Equal(AppPhase.Online, parts.Coord.Current.Phase);
            Assert.Equal(new[] { NoticeKind.LoginSucceeded }, _os.PushedToasts);   // 协调器→通知器这条线接通了
            Assert.Equal(0, parts.Tray.ShellBalloonTipsShown);                     // 一条都没弹到桌面上

            var written = string.Join('\n', parts.Log.RecentLines());
            Assert.Contains("Probe", written);                              // 网关写的就是这一份
            Assert.Contains("Login", written);
            Assert.DoesNotContain("Pass@2024", written);                    // 密码不进日志
            Assert.Contains("upass=***(len=", written);                     // 只以脱敏长度出现

            parts.Form.RefreshLog();
            Assert.True(parts.Form.LogRowCount >= 3,
                $"主窗口只读到 {parts.Form.LogRowCount} 行：它拿的不是装配用的那一份日志");
        }
    }

    [Fact]
    public void 无线事件通路上的异常必须落进装配给的这一份日志()
    {
        var dir = Tmp();
        var wifi = new FakeWifiSource();
        var parts = Build(dir, wifi);
        using (parts)
        {
            // 装配若忘了开事件通路（parts.Arm），Raise 根本进不到协调器，这条就会红。
            // 订阅者抛异常模拟的是真机上"这一轮没跑完"（DPAPI 读不到、界面已销毁等）。
            parts.Arm();
            parts.Coord.StatusChanged += _ => throw new InvalidOperationException("订阅者炸了");
            wifi.Raise(new AccessPoint("zut-stu", "10.1.1.1", "aabbccddeeff"));

            var seen = "";
            var arrived = WaitUntil(() =>
            {
                seen = string.Join('\n', parts.Log.RecentLines().Where(l => l.Contains("Coordinator")));
                return seen.Length > 0;
            });
            Assert.True(arrived, "事件通路的异常没落进日志：装配是不是忘了把日志交给协调器？");
            Assert.Contains("InvalidOperationException", seen);
            Assert.Contains("订阅者炸了", seen);
        }
    }

    // ---------- 两个 HTTP 客户端 ----------

    [Fact]
    public void 装配自己建的两个客户端都禁自动重定向且探测超时五秒()
    {
        var dir = Tmp();
        // 这条不注入 handler：走的就是生产那一条通路，两个 handler 是装配自己 new 出来的，
        // 那个开关可以直接读出来核对。（用替身时读不到 —— 替身本来就不可能跟跳转，那是假绿。）
        var parts = AppContext.Build(_os.Sink, dir, wifi: new FakeWifiSource());
        using (parts)
        {
            Assert.NotNull(parts.PortalHandler);
            Assert.NotNull(parts.ProbeHandler);
            Assert.False(parts.PortalHandler!.AllowAutoRedirect);   // 判据就住在 Location 头里
            Assert.False(parts.ProbeHandler!.AllowAutoRedirect);    // 跟下去会把门户劫持页读成"已上网"
            Assert.Equal(TimeSpan.FromSeconds(15), parts.PortalHttp.Timeout);
            Assert.Equal(TimeSpan.FromSeconds(5), parts.ProbeHttp.Timeout);
        }
    }

    [Fact]
    public async Task 两个客户端都把302原样交给调用方而不是自己跟掉()
    {
        var dir = Tmp();
        var portal = new FakeHttpHandler();
        portal.Enqueue(302, new[] { ("Location", "http://1.1.1.1:80/3.htm") }, "");
        var probe = new FakeHttpHandler();
        probe.Enqueue(302, new[] { ("Location", "http://portal.example.com/") }, "");

        var parts = Build(dir, portal: portal, probe: probe);
        using (parts)
        {
            using (var resp = await parts.PortalHttp.GetAsync("http://1.1.1.1/eportal/"))
                Assert.Equal(System.Net.HttpStatusCode.Found, resp.StatusCode);
            Assert.Single(portal.Requests);                       // 一次都没被"跟随跳转"消耗掉

            using (var resp = await parts.ProbeHttp.GetAsync("http://www.msftconnecttest.com/connecttest.txt"))
                Assert.Equal(System.Net.HttpStatusCode.Found, resp.StatusCode);
            Assert.Single(probe.Requests);
        }
    }

    // ---------- 通知装配 ----------

    [Fact]
    public void 注册AUMID失败时通知永久走气泡并把原因留在设置与日志里()
    {
        var dir = Tmp();
        var parts = Build(dir, aumidFailure: "开始菜单目录建不出来");
        using (parts)
        {
            Assert.True(parts.Notifier.BalloonOnly);                       // 不等一个永远不会来的异常
            Assert.Equal(_os.Sink.ShowBalloon, parts.Notifier.BalloonFallback);   // 气泡的出口只有注入的那一个
            Assert.True(new SettingsStore(dir).Load().NotifierFallbackUsed);
            Assert.Equal("202500000001", new SettingsStore(dir).Load().StudentId);  // 回写没顺手抹掉别的字段
            Assert.Contains("开始菜单目录建不出来", Join(parts));
            Assert.Equal(1, _os.AumidCalls);                               // 装配只问一次，而且问的是记账器

            parts.Notifier.Faulted!("Toast 投递失败：通知中心被组策略禁用");
            Assert.Contains("Toast 投递失败", Join(parts));

            // 判死之后那一条通知走的是气泡：桌面上看不见，但记账器里必须有。
            parts.Notifier.Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");
            Assert.Equal(new[] { ("校园网登录失败", (string?)"Radius 认证失败（账号或密码错误） · 点击查看详情") },
                _os.Balloons);
            Assert.Empty(_os.ToastAttempts);                               // BalloonOnly 下一条都不该去碰通知中心
        }
    }

    [Fact]
    public void 首次运行没有设置文件时只判标记不去新建它()
    {
        // settings.json 的建档归向导（CompleteFirstRun）。装配这里若是新建，
        // 就等于把"用户还没填完"的中间态落进磁盘，也会和 Load 返回默认值那条锁分支撞车。
        var dir = Tmp();
        _os.AumidFailure = "注册失败";
        var parts = AppContext.Build(_os.Sink, dir, new FakeHttpHandler(), new FakeHttpHandler(),
            new FakeWifiSource(), new FakeClock());
        using (parts)
        {
            Assert.True(parts.Notifier.BalloonOnly);                       // 本次运行的判定照样生效
            Assert.False(File.Exists(new SettingsStore(dir).FilePath));
        }
    }

    [Fact]
    public void 首次向导没跑完时不给门户交空凭据()
    {
        // 没配完就开自动通路 = 拿空学号空密码去敲门户，一轮退避打完是四次失败提交，
        // 而连续失败正是把账号打进 RADIUS 锁定的那条路。
        var dir = Tmp();
        var portal = new FakeHttpHandler();
        var wifi = new FakeWifiSource();
        var parts = AppContext.Build(_os.Sink, dir, portal, new FakeHttpHandler(), wifi, new FakeClock());
        using (parts)
        {
            parts.Arm();                                           // 窗口出来了，通路这才开
            wifi.Raise(new AccessPoint("zut-stu", "10.1.1.1", "aabbccddeeff"));   // 人连上了校园网
            Thread.Sleep(300);                                       // 事件通路是 async void，给它在途时间
            Assert.Empty(portal.Requests);
            Assert.Contains("不自动登录", Join(parts));               // 为什么什么都没发生，日志里要说清
        }
    }

    [Fact]
    public void 配好之后无线事件才会驱动自动登录()
    {
        var dir = Tmp();
        var portal = new FakeHttpHandler();
        var wifi = new FakeWifiSource();
        var parts = Build(dir, wifi, portal: portal);                // Build 的目录是"已跑完向导"的那份
        using (parts)
        {
            parts.Arm();                                           // 规矩④：通路等窗口句柄之后才开
            wifi.Raise(new AccessPoint("zut-stu", "10.1.1.1", "aabbccddeeff"));
            Assert.True(WaitUntil(() => portal.Requests.Count > 0), "装配没把协调器接到无线源上");
            Assert.Equal(9002, portal.Requests[0].Uri.Port);          // 第一件事就是 9002 的认证状态探测
        }
    }

    /// 注册成功（sink 回 null）时那条最主要的通路仍然是通知中心：装配不许因为"气泡更容易测"
    /// 就把生产偏好改掉。这一条同时钉住"通知器要的是注入的那份通道工厂"——
    /// 真那一份（`ToastDelivery.OpenChannel` → `CreateToastNotifier`）只在 `NotificationSink.Production()` 里。
    [Fact]
    public void 注册成功时仍然优先走通知中心()
    {
        var dir = Tmp();
        var parts = Build(dir);
        using (parts)
        {
            parts.Arm();
            Assert.False(parts.Notifier.BalloonOnly);
            Assert.False(new SettingsStore(dir).Load().NotifierFallbackUsed);
            Assert.Empty(parts.Log.RecentLines());                         // 没失败就别往日志里塞噪音

            parts.Notifier.Notify(NoticeKind.LoginSucceeded, "zut-stu · 耗时 1.8s");
            Assert.Equal(_os.Sink.OpenToastChannel, parts.Notifier.ChannelFactory);   // 通道工厂=注入的那一份
            Assert.Equal(new[] { ("校园网已登录", "zut-stu · 耗时 1.8s", NoticeKind.LoginSucceeded) },
                _os.ToastAttempts);                                          // 走的是通知中心
            Assert.Single(_os.PushedToasts);                                 // 而且真的推出去了
            Assert.Empty(_os.Balloons);                                      // 一条都没回退成气泡
        }
    }

    /// 通知中心把这条通知判死（Setting 非 Enabled / 通道打不开）时，回退仍然是"看得见"的那一条，
    /// 而且回退的那一次也只落在注入的气泡出口上 —— 生产那条偏好与回退契约在装配这一层都得成立。
    [Theory]
    [InlineData(NotificationSetting.DisabledByGroupPolicy)]
    [InlineData(NotificationSetting.DisabledForUser)]
    public void 通知中心被判死时装配把回退交给注入的气泡出口而不是托盘图标(NotificationSetting setting)
    {
        var dir = Tmp();
        _os.ToastSetting = setting;
        var parts = Build(dir);                                     // 注册成功，但通知器自己说不能用
        using (parts)
        {
            parts.Notifier.Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");
            Assert.Empty(_os.PushedToasts);                          // 明知是死通道就不该再推
            Assert.Single(_os.Balloons);                             // 气泡接手
            Assert.Equal(0, parts.Tray.ShellBalloonTipsShown);       // 一次都没弹到同学桌面上
            Assert.Contains(setting.ToString(), Join(parts));            // 为什么降级，日志里要说清是哪一种
        }
    }

    // ---------- 守门：这一整套装配用例都不许碰操作系统通知面 ----------
    //
    // 这一条是"同学桌面被刷满 校园网登录失败 气泡"那次事故的回归网，也是这轮改动的立项理由。
    // 现场是**故意 armed 的**：AUMID 注册失败（本机真实情况：开始菜单里没有那个快捷方式）
    // + 一条真的会失败的登录 ⇒ 协调器确实发出一条 LoginFailed。也就是说"该弹的通知一条都不许少"，
    // 只是那一下必须落在注入的记账器里，而不是桌面上。
    // 谁要是把真气泡接回托盘图标（`TrayApp.ShowBalloon` 里再写一句 `NotifyIcon.ShowBalloonTip`）、
    // 或者把装配那两句接线改回 `notifier.BalloonFallback = tray.ShowBalloon`，
    // 下面几条断言立刻红 —— 红法见 notification-isolation 报告里的变异记录。
    [Fact]
    public async Task 装配好的组合根在测试里一次都不碰操作系统通知面()
    {
        var dir = Tmp();
        var portal = new FakeHttpHandler();
        // 首扣 + 3 轮退避（每轮 取IP + 被拒）= 4 × (a70 + reject)，第一轮前面还要先探测一次未认证。
        for (var i = 0; i < 4; i++)
        {
            if (i == 0) portal.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
            portal.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
            portal.EnqueueRaw(Fixtures.Read("login_reject_pwerr.txt"));
        }
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabbccddeeff") };
        // aumidFailure 非 null = 真 registrar 一次都不跑（开始菜单没被写、SHChangeNotify 没被喊），
        // 而装配照样据此把通知中心判死 —— 这正是本机这次事故的那一格。
        var parts = Build(dir, wifi, aumidFailure: "测试宿主：开始菜单里没有 ZutWifi 的快捷方式", portal: portal);
        using (parts)
        {
            await parts.Coord.RequestLoginAsync(CancellationToken.None);

            // ── ① 现场确实是 armed 的：真的失败、真的判死通知中心、真的该给用户一条通知 ──
            Assert.Equal(AppPhase.GiveUp, parts.Coord.Current.Phase);
            Assert.True(parts.Notifier.BalloonOnly);
            Assert.True(_os.Balloons.Any(b => b.Title == "校园网登录失败"),
                "该给用户的那一条没落进注入的气泡出口 ⇒ 它多半被直接接到真图标上了。" +
                $"（托盘真图标本次已弹 {parts.Tray.ShellBalloonTipsShown} 条气泡）");

            // ── ② 而那一下一次都没有打到操作系统上 ──
            Assert.Equal(0, parts.Tray.ShellBalloonTipsShown);                     // 真图标：零条气泡
            Assert.Equal(_os.Sink.ShowBalloon, parts.Notifier.BalloonFallback);    // 出口只有注入的那一个
            Assert.Equal(_os.Sink.OpenToastChannel, parts.Notifier.ChannelFactory);
            Assert.Equal(1, _os.AumidCalls);                                       // 开始菜单只被问过记账器
            Assert.Contains("测试宿主：开始菜单里没有", Join(parts));                 // 原因来自 sink ⇒ 真 Ensure() 没跑
            Assert.False(parts.Form.Visible, "通知把主窗口 Show 出来了：测试桌面上多了一个窗口");
        }
    }

    /// 反方向的同一件事：注册成功时通知走的是通知中心，而"通知中心"在测试里只是记账器上的一个列表。
    /// 真的 `CreateToastNotifier` 全仓只有 `NotificationSink.Production()` 那一处；记账器连
    /// 真图标的接线口（`BindShellBalloon`）都没有，所以装配就算想绑也绑不上任何东西。
    [Fact]
    public void 装配在测试里递出的那份出口永远不是生产那一份()
    {
        var dir = Tmp();
        var parts = Build(dir);                                     // aumidFailure = null ⇒ 注册成功
        using (parts)
        {
            Assert.Null(_os.Sink.BindShellBalloon);
            Assert.Equal(_os.Sink.ShowBalloon, parts.Notifier.BalloonFallback);
            Assert.Equal(_os.Sink.OpenToastChannel, parts.Notifier.ChannelFactory);

            parts.Notifier.Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");
            parts.Notifier.Notify(NoticeKind.AuthExpired, "需要重新登录");

            Assert.Equal(2, _os.ToastAttempts.Count);               // 两条都在记账器里
            Assert.Equal(new[] { NoticeKind.LoginFailed, NoticeKind.AuthExpired }, _os.PushedToasts);
            Assert.Empty(_os.Balloons);                             // 通道可用 ⇒ 不该回退成气泡
            Assert.Equal(0, parts.Tray.ShellBalloonTipsShown);      // 桌面上什么都没弹
        }
    }

    /// 上面两条守门用例数的是"这一次跑没打到桌面"，靠的是托盘那一个计数器
    /// （`TrayApp.ShellBalloonTipsShown`）。计数器有一格够不着：谁要是把 `NotifyIcon.ShowBalloonTip`
    /// **另写一处**（最典型的那一句就是"在 `ShowBalloon` 里除了转给 sink，再顺手补一发真的"），
    /// 计数器一次都不会涨、记账器也照样收满，于是全套件绿而同学的桌面重新被弹。
    /// 这一条源码级守卫补的就是那一格 —— 与 `SelfTestTests.诊断这一段只有一个HttpClient构造点`
    /// 同一套走法：运行时替身看不见的东西，只能对着源码钉。
    [Fact]
    public void 真气泡在源码里只有一个落点装配也不许把回退接到托盘上()
    {
        var tray = SrcCodeLines("Shell/TrayApp.cs");
        var wiring = SrcCodeLines("AppContext.cs");

        // ── ① 真那一句全仓只许出现一次，而且必须待在 PresentOnTrayIcon 里 ──
        Assert.Equal(1, Count(tray, "_icon.ShowBalloonTip("));
        Assert.Contains(SrcSegment(tray, "internal void PresentOnTrayIcon", "private void OnStatus"),
            l => l.Contains("_icon.ShowBalloonTip(", StringComparison.Ordinal));
        var showBalloon = SrcSegment(tray, "public void ShowBalloon", "internal void PresentOnTrayIcon");
        Assert.DoesNotContain(showBalloon, l => l.Contains("ShowBalloonTip", StringComparison.Ordinal));
        Assert.DoesNotContain(showBalloon, l => l.Contains("PresentOnTrayIcon", StringComparison.Ordinal));
        Assert.Contains(showBalloon, l => l.Contains("_sink.ShowBalloon(title, body)", StringComparison.Ordinal));

        // ── ② 组合根不许再自己接回退：真图标只能从 sink 的那个接线口走过去 ──
        Assert.DoesNotContain(wiring, l => l.Contains("PresentOnTrayIcon", StringComparison.Ordinal)
                                           && !l.Contains("BindShellBalloon", StringComparison.Ordinal));
        Assert.DoesNotContain(wiring, l => l.Contains("notifier.BalloonFallback", StringComparison.Ordinal)
                                           || l.Contains("notifier.ChannelFactory", StringComparison.Ordinal));
        // 组合根只走"带 sink 的那一个构造"；裸 aumid 那一个装着真投递，是通知层自测用的。
        Assert.Equal(1, Count(wiring, "new Notifier(AumidRegistrar.Aumid, sink)"));
        Assert.Equal(0, Count(wiring, "new Notifier(AumidRegistrar.Aumid)"));
        // 操作系统那三张脸（真气泡 / 真通知中心通道 / 真开始菜单注册）都不许在装配文件里出现
        Assert.DoesNotContain(wiring, l => l.Contains("ToastDelivery", StringComparison.Ordinal)
                                           || l.Contains("AumidRegistrar.Ensure", StringComparison.Ordinal)
                                           || l.Contains("ShowBalloonTip", StringComparison.Ordinal));

        // ── ③ 生产那一份全仓只许被装配点递出去一次（多一处 = 多一个能把真出口带进单测的人）──
        Assert.Equal(1, ProductionCallSites());
    }

    /// 装配在窗口句柄存在之前一个回调都不许引进来（评审 C1）。
    ///
    /// 两半各钉一头，缺一头都不算修到：
    /// ① 没句柄时把无线事件打进来 —— 装配此时根本还没订阅，所以门户一个请求都不该收到，
    ///    而窗口的句柄也不该被工作线程造出来（控件一旦被跨线程碰，最先露馅的就是句柄归属）。
    /// ② 真把窗口 Show 出来（生产路径上就是 Application.Run 那一步）之后再打事件 ——
    ///    通路必须已经开着，并且回调是封送回 UI 线程生效的。
    /// 交叉线程检查全程打开：任何一处没封送，在 UI 线程的消息泵里就当场抛 InvalidOperationException，
    /// 由 AssertHealthy 收口。
    [Fact]
    public void 事件通路等窗口句柄存在之后再开()
    {
        var previous = Control.CheckForIllegalCrossThreadCalls;
        Control.CheckForIllegalCrossThreadCalls = true;
        var portal = new FakeHttpHandler();
        var wifi = new FakeWifiSource();
        try
        {
            using var ui = new UiThread(_tmp, dir => Build(dir, wifi, portal: portal), createHandle: false);
            var form = ui.Parts.Form;
            int? handleOwner = null;
            ui.OnUi(() => form.HandleCreated += (_, _) => handleOwner = Environment.CurrentManagedThreadId);

            // ── ① 句柄还不存在（Application.Run 还没走到造句柄那一步）──
            Assert.False(form.IsHandleCreated, "这条用例的前提：窗口句柄还没造");
            Assert.False(ui.Parts.Started, "Build 阶段就该把事件通路留着，开了就是拿工作线程改控件");
            FromWorker(() =>
            {
                wifi.Raise(new AccessPoint("zut-stu", "10.1.1.1", "aabbccddeeff"));
                return 0;
            });
            Thread.Sleep(300);                                       // 事件通路是 async void，给它在途时间
            Assert.Empty(portal.Requests);                           // 还没订阅 ⇒ 什么都不该发生
            Assert.False(form.IsHandleCreated,
                "事件在句柄存在之前就被就地执行了：控件的句柄是在 wlanapi 通知线程上造出来的");

            // ── ② 窗口真的出现了：同一条装配路径必须把通路开起来 ──
            ui.Show();
            Assert.True(form.IsHandleCreated);
            Assert.Equal(ui.ThreadId, handleOwner);                  // 句柄归 UI 线程
            Assert.True(WaitUntil(() => ui.Parts.Started),
                "窗口都 Shown 了装配还不开事件通路：现在只有手动 parts.Arm() 才有界面更新");
            ui.Drain();

            FromWorker(() =>
            {
                wifi.Raise(new AccessPoint("zut-stu", "10.1.1.2", "aabbccddeeff"));
                return 0;
            });
            Assert.True(WaitUntil(() => portal.Requests.Count > 0), "窗口都出来了事件通路还没开");
            Assert.True(WaitUntil(() => ui.Parts.Coord.Current.Phase != AppPhase.Idle));
            ui.Drain();                                              // FIFO：跑完即说明前面封送的那几笔已生效
            Assert.True(ui.OnUi(() => ui.Parts.Form.LogRowCount) >= 1,
                "界面没被画过：状态更新没封送到 UI 线程");
            Assert.DoesNotContain("没跑完", Join(ui.Parts));           // 后台异常会被协调器咽进日志
            ui.AssertHealthy();
        }
        finally { Control.CheckForIllegalCrossThreadCalls = previous; }
    }

    /// 硬约束：Notifier.Activated 跑在 WinRT 线程池上。Release 下直接碰控件不会报错，
    /// 只会随机把界面弄坏，所以这条先把交叉线程检查打开并自证它真的生效，
    /// 再从工作线程打进回调：任何一处没封送都会当场抛 InvalidOperationException。
    [Fact]
    public void 通知点击回调切回UI线程而不是在工作线程直接碰控件()
    {
        var previous = Control.CheckForIllegalCrossThreadCalls;
        Control.CheckForIllegalCrossThreadCalls = true;
        try
        {
            using var ui = new UiThread(_tmp, dir => Build(dir));
            ui.Parts.Arm();                                        // Activated 也是开闸之后才接上的
            var form = ui.Parts.Form;

            // 机制自检：这道闸在本宿主必须真的生效，否则下面那句"没抛异常"毫无意义。
            var armed = Unwrap(Record.Exception(() => FromWorker(() => { var _ = form.Handle; return 0; })));
            Assert.IsType<InvalidOperationException>(armed);

            var err = Record.Exception(() => FromWorker(() =>
            {
                ui.Parts.Notifier.Activated!(NoticeKind.LoginFailed);
                return 0;
            }));
            Assert.Null(err);                                              // 直接碰控件就是在这一行红

            ui.Parts.Log.Write(new TransactionRecord("Login", "POST", "http://1.1.1.1/eportal", 302,
                "http://1.1.1.1:80/3.htm", "装配自检的这一行", null));
            ui.Drain();
            Assert.Equal("日志", ui.OnUi(() => form.SelectedTabText));        // 封过去的那一笔生效了
            Assert.Contains("装配自检", ui.OnUi(() => form.LastSelectedLogRow));
            ui.AssertHealthy();
        }
        finally { Control.CheckForIllegalCrossThreadCalls = previous; }
    }

    // ---------- 白名单只看实时无线源 ----------

    [Fact]
    public void 装配出来的界面仍然只看实时无线源判白名单()
    {
        var dir = Tmp();
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabb") };
        var parts = Build(dir, wifi);
        using (parts)
        {
            // 状态里那句 Online/zut-stu 有可能是被放弃的那一轮留下的：按钮必须跟着人走。
            parts.Tray.ApplyStatus(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 5));
            Assert.True(parts.Form.LogoutButtonEnabled);

            wifi.Current = new AccessPoint("隔壁的5G", "10.0.0.9", "aabb");
            parts.Tray.ApplyStatus(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 5));
            Assert.False(parts.Form.LogoutButtonEnabled);
            Assert.False(parts.Form.LoginButtonEnabled);
        }
    }

    // ---------- 退出收尾 ----------

    [Fact]
    public void 托盘退出停掉心跳定时器并释放无线源()
    {
        var dir = Tmp();
        var wifi = new WatchWifi();
        var parts = Build(dir, wifi);
        parts.Arm();                                                  // 心跳也是开闸之后才有的
        Assert.True(parts.Ticker.Enabled);                              // 60 秒刷新是活着的

        parts.Tray.Exit();                                                 // 托盘"退出"只发退出请求，不掐进程
        Assert.False(parts.Ticker.Enabled);
        Assert.True(wifi.Disposed);

        var err = Record.Exception(() => parts.Tray.Exit());               // 重复退出不许再收尾一次
        Assert.Null(err);
        parts.Dispose();
    }

    [Fact]
    public void 收尾之后再来的状态更新不许抛出()
    {
        var dir = Tmp();
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabb") };
        var parts = Build(dir, wifi);
        parts.Shutdown();
        var err = Record.Exception(() =>
        {
            parts.ShowMainWindow();
            parts.CheckLogHealth();
            parts.Shutdown();
        });
        Assert.Null(err);
        parts.Dispose();
    }

    // ---------- 诊断不可用要让人看见 ----------

    /// 诊断坏了要让人看得见 —— 而且那一句"看得见"走的还是装配注入的气泡出口。
    /// 以前这里换的是 `parts.WarnBalloon`（第二条可替换委托），等于默认"托盘那一句是真气泡"；
    /// 现在整条路上只剩 sink 一个出口，所以这一条测的就是生产那一条。
    [Fact]
    public void 日志写不下去时只提醒用户一次()
    {
        var dir = Tmp();
        var parts = Build(dir);
        using (parts)
        {
            Assert.False(parts.DiagnosticsWarned);
            parts.CheckLogHealth();
            Assert.Empty(_os.Balloons);                                      // 还没坏，别乱弹

            File.WriteAllText(Path.Combine(dir, "logs"), "占位：让日志目录建不出来");
            parts.Log.Write(new TransactionRecord("Probe", "GET", "http://1.1.1.1:9002/0", null, null, "x", null));
            Assert.True(parts.Log.WriteFailures >= 1);

            parts.CheckLogHealth();
            parts.CheckLogHealth();
            Assert.Single(_os.Balloons);                                     // 只说一次，说多了就成噪音
            Assert.Contains("诊断", _os.Balloons[0].Title + _os.Balloons[0].Body);
            Assert.True(parts.DiagnosticsWarned);
            Assert.Equal(0, parts.Tray.ShellBalloonTipsShown);               // 说归说，桌面上什么都没弹
        }
    }

    // ---------- 自检通道 ----------
    //
    // 这里原来有一条"占位版不许崩"的用例：它直接调生产入口 SelfTest.RunAsync(withLogout: false)。
    // Task 18 那条通道上线了 —— 同一个调用现在会真敲门户、真读无线接口、真往 %APPDATA% 写文件，
    // 放在单测里就是一次"没人盯着就把同学的网络注销/登录一遍"的事故。
    // 同一个断言（每一档都不抛、退出码分得开）已经搬到 SelfTestTests：那里目录在临时路径、
    // 门户喂真机回放、无线源是替身。Program 的第一条分支照旧，只是不在自动化套件里出网。

    // ---------- 一条命令的门：托盘与界面共用 ----------

    /// 会卡在门户那一发上的 handler：人造一个"命令在途"的窗口（真机上一条登录退避要跑约 22 秒）。
    /// 只记 URI，不判内容 —— 这里要数的是"到底有几条命令真的发出去了"。
    private sealed class HoldPortal : System.Net.Http.HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new();
        public List<string> Uris { get; } = [];
        public void Release() => _release.TrySetResult();

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage req, CancellationToken ct)
        {
            lock (Uris) Uris.Add(req.RequestUri!.Query);
            await _release.Task;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            { Content = new System.Net.Http.StringContent("held") };
        }

        public int Count { get { lock (Uris) return Uris.Count; } }
        public bool Any(Func<string, bool> pred) { lock (Uris) return Uris.Any(pred); }
    }

    private AppParts BuildGated(string dir, HoldPortal portal, IWifiSource wifi)
        => AppContext.Build(_os.Sink, ConfiguredDir(dir), portal, new FakeHttpHandler(), wifi, new FakeClock());

    /// 三条用例都跑在带句柄的 UI 线程上：命令本身不进事件通路（是界面/托盘主动发的），
    /// 但协调器一路 Set 出来的状态要碰控件 —— 没句柄时那些笔会掉到线程池上就地执行（评审 C1
    /// 抓的就是这个），收尾时 Form.Dispose 直接抛。装配现在也不允许那种走法。
    [Fact]
    public async Task 托盘连点两条命令时第二条被拒而不是排队()
    {
        var hold = new HoldPortal();
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabb") };
        using var ui = new UiThread(_tmp, d => BuildGated(d, hold, wifi));
        var parts = ui.Parts;
        parts.Arm();

        var login = parts.Tray.MenuLoginAsync();
        Assert.True(WaitUntil(() => hold.Count > 0), "托盘的登录根本没打到门户");

        var logout = parts.Tray.MenuLogoutAsync();
        Assert.True(Completed(logout), "第二条命令排进了队列：那正是"
            + "「托盘[注销]在一次登录退避之后 22 秒才真的发出去」这一条");
        Assert.False(logout.Result);                                     // 拒了，不是跑了
        Assert.False(hold.Any(q => q.Contains("a=Logout")), "被拒的那一条还是给门户交了注销包");
        Assert.Contains("一条命令在途", Join(parts));                     // 拒在日志里看得见
        Assert.True(parts.Gate.IsBusy);                                   // 门还开着第一条

        hold.Release();
        Assert.True(await login);                                        // 先那一条是正常跑完的
        Assert.True(WaitUntil(() => !parts.Gate.IsBusy), "命令跑完了没把门放开");
    }

    /// 评审 C2 的另一半：门是共用的，所以界面在途时托盘那一条同样进不去。
    /// 界面上的按钮这里用真按钮（PerformClick）—— 它打的正是装配包进 MainForm 的那个命令对象。
    [Fact]
    public void 界面命令在途时托盘再点一条也进不去()
    {
        var previous = Control.CheckForIllegalCrossThreadCalls;
        Control.CheckForIllegalCrossThreadCalls = true;
        var dir = Tmp();
        var hold = new HoldPortal();
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabb") };
        try
        {
            using var ui = new UiThread(_tmp, d => BuildGated(d, hold, wifi));
            ui.Parts.Arm();
            ui.Show();
            ui.OnUi(() => ui.Parts.Form.SimulateLoginClick());           // 界面发起一条登录
            Assert.True(WaitUntil(() => hold.Count > 0), "界面上的登录没打到门户");

            var refused = ui.Parts.Tray.MenuLogoutAsync();
            Assert.True(Completed(refused), "托盘那条排到了界面那条命令后面（会晚 22 秒真发注销）");
            Assert.False(refused.Result);
            Assert.False(hold.Any(q => q.Contains("a=Logout")));
            hold.Release();
            Assert.True(WaitUntil(() => !ui.Parts.Gate.IsBusy), "命令跑完了没把门放开");
            ui.Drain();
            Assert.False(ui.Parts.Gate.IsBusy);                           // 跑完了要把门放回去
            ui.AssertHealthy();
        }
        finally { Control.CheckForIllegalCrossThreadCalls = previous; }
    }

    /// 反方向：托盘在途时界面上再点一次也不许排队，而且那四个按钮此时必须已经被画死
    /// （评审 C2 说的那一半缺口：以前那面旗只住在 MainForm 里，托盘跑起来界面毫无反应）。
    [Fact]
    public void 托盘命令在途时界面上再点一次也不排队()
    {
        var hold = new HoldPortal();
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabb") };
        using var ui = new UiThread(_tmp, d => BuildGated(d, hold, wifi));
        var parts = ui.Parts;
        parts.Arm();
        // 界面先收到一个"本来可以按注销"的状态：门一开，这四个按钮就该被画死。
        ui.OnUi(() => parts.Form.Apply(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 5),
            ssidWhitelisted: true));
        Assert.True(ui.OnUi(() => parts.Form.LogoutButtonEnabled));

        var tray = parts.Tray.MenuLoginAsync();
        Assert.True(WaitUntil(() => hold.Count > 0));
        Assert.True(ui.OnUi(() => parts.Form.CommandInFlight), "托盘在途，界面上看不见");
        Assert.False(ui.OnUi(() => parts.Form.LogoutButtonEnabled), "托盘命令在途时界面上的注销还亮着");
        Assert.False(ui.OnUi(() => parts.Form.LoginButtonEnabled));
        Assert.False(ui.OnUi(() => parts.Form.ExportButtonEnabled));

        // 界面上那条命令走的就是 RunAsync（绕开按钮的可用性，直接问门答不答应）
        var fromForm = ui.OnUi(() => parts.Form.SimulateLogoutCommand());
        Assert.True(Completed(fromForm), "界面那条命令排进了托盘那条后面");
        Assert.False(hold.Any(q => q.Contains("a=Logout")));
        Assert.Contains("一条命令在途", Join(parts));               // 拒的是界面上那一条，日志里看得见
        hold.Release();
        Assert.True(tray.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(WaitUntil(() => !parts.Gate.IsBusy), "命令跑完了没把门放开");
        ui.Drain();
        // 门放开之后按钮回到呈现规则那一套：这一轮以"登录失败/放弃"收尾，那条规则放行重试（登录）。
        Assert.True(ui.OnUi(() => parts.Form.LoginButtonEnabled), "门放开之后按钮没照呈现规则恢复");
    }

    /// 在 ms 毫秒内跑完返回 true；没跑完返回 false —— 没跑完就说明它真的去排队/发包了，正是要抓的那种。
    static bool Completed(Task command, int ms = 1500) => Task.WaitAny(command, Task.Delay(ms)) == 0;

    // ---------- 60 秒那一拍也走同一把门 ----------
    //
    // 派单 17-1：装配armed 的那条 60 秒定时器直接调 `coord.RefreshAsync(CancellationToken.None)`，
    // 从旁边绕过了共享闸门 —— 于是一次门户刷新可以正跑在一条在途登录中间。
    // 这一条刻意**不**手工驱动刷新方法：只把真定时器的周期调短，让它自己那一拍撞上来
    // （Task 13 的教训：装的 delegate 与测的方法是两个方法时，故障就测不出来）。

    [Fact]
    public async Task 定时刷新那一拍撞上在途命令时不打门户()
    {
        var dir = Tmp();
        var portal = new FakeHttpHandler();
        // 先老老实实登一次：探测未认证 → 取 IP → 登录成功。
        // 界面必须停在 Online —— 不然刷新在相位门那儿就自己回去了，这条用例就成了自证。
        portal.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        portal.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        portal.EnqueueRaw(Fixtures.Read("login_success.txt"));
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.133.126.113", "aabbccddeeff") };
        var parts = Build(dir, wifi, portal: portal, probe: OnlineProbe());
        using (parts)
        {
            parts.Arm();
            await parts.Coord.RequestLoginAsync(CancellationToken.None);
            Assert.Equal(AppPhase.Online, parts.Coord.Current.Phase);      // 前提核对：刷新这一路真的会发请求

            var held = new TaskCompletionSource();
            var inFlight = parts.Gate.RunAsync("测试·在途", _ => held.Task);
            Assert.True(parts.Gate.IsBusy);

            portal.Requests.Clear();
            parts.Ticker.Interval = 20;                                    // 真定时器自己那一拍
            parts.Ticker.Enabled = true;
            await Task.Delay(300);                                         // 十几个周期
            Assert.Empty(portal.Requests);                                 // 一个包都没发 = 拒了，而不是排到后面
            Assert.True(parts.Gate.IsBusy);                                // 被拒的那一条也没把门抢走

            held.SetResult();
            await inFlight;
            Assert.True(WaitUntil(() => portal.Requests.Count > 0),
                "门放开之后定时刷新再也没回来：这一轮的拒绝变成了永久停摆");
            parts.Ticker.Enabled = false;
        }
    }

    /// 外网通了才算 Online —— Degraded 也能过刷新的相位门，但那一档测的就不是"正常在跑的那一圈"了。
    static FakeHttpHandler OnlineProbe()
    {
        var probe = new FakeHttpHandler();
        probe.Enqueue(200, Array.Empty<(string, string)>(), "Microsoft NCSI");
        return probe;
    }

    /// 反过来说明上面那条测的确实是门：门空着的时候，同一拍必须打到门户。
    [Fact]
    public async Task 门空着时定时刷新那一拍照常打到门户()
    {
        var dir = Tmp();
        var portal = new FakeHttpHandler();
        portal.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        portal.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        portal.EnqueueRaw(Fixtures.Read("login_success.txt"));
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.133.126.113", "aabbccddeeff") };
        var parts = Build(dir, wifi, portal: portal, probe: OnlineProbe());
        using (parts)
        {
            parts.Arm();
            await parts.Coord.RequestLoginAsync(CancellationToken.None);
            Assert.Equal(AppPhase.Online, parts.Coord.Current.Phase);
            portal.Requests.Clear();
            parts.Ticker.Interval = 20;
            parts.Ticker.Enabled = true;
            Assert.True(WaitUntil(() => portal.Requests.Count > 0, 3000),
                "门空着、界面 Online，60 秒那一拍却没打到门户：那上面根本没接刷新");
            parts.Ticker.Enabled = false;
        }
    }

    // ---------- 装配分两步：第一步的产物上没有"开闸"这件事 ----------
    //
    // 规矩④原来只是 Build 末尾的一句注释 + 一个 Started 标志：`parts.Start()` 在
    // new 完 AppParts 之后、ticker/托盘/Shown 那七八句接线还没跑完的时候就已经调得动，
    // 而派单要的是"这一格在类型上不存在"。所以 Build = Prepare(...).Complete()：
    // Prepare 交回来的那个东西**没有**任何开闸入口，能开闸的那一步只在 Complete 里挂到窗口的 Shown 上。
    // 用反射来钉（不是字符串比对源码）：改名、加一个 public 出口、把 Arm 挪回 AppParts 之外，都会红。
    [Fact]
    public void 装配第一步的产物上没有开闸入口而开闸本身不对外()
    {
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var prepare = typeof(AppContext).GetMethod("Prepare", flags);
        Assert.NotNull(prepare);                       // 第一步必须单独存在（Build = Prepare(...).Complete()）

        var staged = prepare!.ReturnType;
        Assert.NotEqual(typeof(AppParts), staged);     // 第一步不能直接把能开闸的那个类型交出去
        Assert.DoesNotContain(staged.GetMethods(flags).Select(m => m.Name),
            n => n is "Start" or "Arm" or "Begin" or "Run" or "Enable");
        Assert.NotNull(staged.GetMethod("Complete", flags));

        // 开闸那一步：存在，但不是 public —— Program.cs 与任何调用方都拿不到它，只有窗口的 Shown 够得着
        var arm = typeof(AppParts).GetMethod("Arm", flags);
        Assert.NotNull(arm);
        Assert.False(arm!.IsPublic);
        Assert.Null(typeof(AppParts).GetMethod("Start", BindingFlags.Instance | BindingFlags.Public));
    }

    /// 源码级：全装配文件里开闸只出现在窗口 Shown 那一句里，别处一次都没有。
    [Fact]
    public void 开闸只挂在窗口的Shown上()
    {
        var wiring = SrcCodeLines("AppContext.cs");
        var calls = wiring.Where(l => l.Contains(".Arm(", StringComparison.Ordinal)).ToList();
        Assert.Single(calls);
        Assert.True(calls[0].Contains("Shown +=", StringComparison.Ordinal),
            $"唯一那一次开闸不在窗口的 Shown 上：{calls[0]}");
        Assert.Equal(1, Count(wiring, "Shown +="));          // 全装配文件里就这一句订阅，别处一次都没有
    }

    // ---------- 小工具 ----------

    static string Join(AppParts parts) => string.Join('\n', parts.Log.RecentLines());

    static bool WaitUntil(Func<bool> done, int millis = 4000)
    {
        var deadline = Environment.TickCount64 + millis;
        while (!done() && Environment.TickCount64 < deadline) Thread.Sleep(20);
        return done();
    }

    static Exception? Unwrap(Exception? ex) =>
        ex is AggregateException agg ? agg.Flatten().InnerExceptions.FirstOrDefault() : ex;

    static T FromWorker<T>(Func<T> body)
    {
        var t = Task.Run(body);
        Assert.True(t.Wait(TimeSpan.FromSeconds(5)), "工作线程被挡住了（在同步等 UI 线程？）");
        return t.Result;
    }

    /// 从测试输出目录往上找仓库根（与 `SelfTestTests.RepoRoot` 同一套走法）。
    /// 找不到就明说而不是静默通过 —— 一条会静默通过的守卫比没有守卫更糟。
    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(System.AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ZutWifi.sln"))) return d.FullName;
        throw new FileNotFoundException("找不到仓库根（ZutWifi.sln）：这条源码级守卫无法工作");
    }

    /// `src/ZutWifi/<rel>` 的代码行（丢掉空行与 `//`、`///` 开头的那一些）：
    /// 文档注释里出现 `NotifyIcon.ShowBalloonTip` 是在说明"别这么写"，不该被读成违规。
    static List<string> SrcCodeLines(string rel)
    {
        var path = Path.Combine(RepoRoot(), "src", "ZutWifi", rel);
        Assert.True(File.Exists(path), $"找不到源码文件 {path}：这条守卫扫了个空");
        return [.. File.ReadAllLines(path).Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))];
    }

    /// 两个锚点之间的那一段代码行（含上锚、不含下锚）：锚点找不到直接红，不许静默放过。
    static List<string> SrcSegment(List<string> lines, string from, string to)
    {
        var i = lines.FindIndex(l => l.Contains(from, StringComparison.Ordinal));
        Assert.True(i >= 0, $"源码里找不到锚点 `{from}`：这一格的形状变了，守卫得跟着改（不能装作扫过了）");
        var j = lines.FindIndex(i + 1, l => l.Contains(to, StringComparison.Ordinal));
        Assert.True(j > i, $"锚点 `{from}` 之后找不到 `{to}`：次序变了，同上");
        return [.. lines[i..j]];
    }

    static int Count(IEnumerable<string> lines, string needle) =>
        lines.Count(l => l.Contains(needle, StringComparison.Ordinal));

    /// 全仓（src 那一边，跳过 bin/obj 与注释行）里 `NotificationSink.Production()` 的调用点数。
    /// 这个数必须是 1，而且那一个就在 Program.cs —— 多一处就等于多一个人能把真出口带进单测。
    static int ProductionCallSites()
    {
        var root = Path.Combine(RepoRoot(), "src");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(files.Count >= 10, $"src 下只扫到 {files.Count} 个 .cs：这条守卫扫了个空");
        return files.Sum(f => Count([.. File.ReadAllLines(f).Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))],
            "NotificationSink.Production()"));
    }

    /// Shutdown 之后要被检查的无线源替身（真源 WifiSentinel 同样实现 IDisposable）。
    private sealed class WatchWifi : IWifiSource, IDisposable
    {
        public AccessPoint? Current { get; set; }
        public event Action<AccessPoint?> Changed = _ => { };
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    // ---------- shell 往返：AUMID 必须真的落进 .lnk 文件本身 ----------
    //
    // 装配那一路在单测里永远走的是记账器（`sink.EnsureAumid` 被替身接走，见上面
    // `装配好的组合根在测试里一次都不碰操作系统通知面` 那条守门用例），
    // 于是 IPersistFile/IPropertyStore 这一格在整个套件里从来没被执行过 —— 把它改坏全绿。
    // 这一条补的就是那一格：往**临时目录**（不是开始菜单，不动同学的机器）写一份真快捷方式，
    // 再从磁盘把它读回来。NotifyIcon / Toast 那两张脸这里一次都不碰。
    //
    // 两个分支不是"看情况放水"，而是同一句契约的两侧，缺任何一侧都不算测到：
    // ① 属性库给得到的机器（同学那种正常桌面，也是 Task 20 要跑的那一步）：
    //    走完整个往返，并核对磁盘上的字节确实是 UTF-16LE（VT_LPWSTR 该长的样子）。
    // ② 属性库给不到的机器（本仓当前这台沙箱宿主，实测 2026-09-20：ShellLink 的
    //    IPropertyStore QI 返回 E_NOINTERFACE 0x80004002，与 NotifierTextTests 那条 Skip 同源）：
    //    注册必须**判失败并带回原因**，而且不许留下半份快捷方式 —— 装配正是拿这个返回值
    //    决定"通知中心判死、只走气泡"的。这条分支测的是"测不了的时候不许装作测过"。
    [Fact]
    public void 写进临时目录的快捷方式读回同一个AUMID而且字节是utf16le()
    {
        var dir = _tmp.NewDir("zwaumid");
        var lnk = Path.Combine(dir, "ZutWifi.lnk");
        var err = AumidRegistrar.EnsureIn(dir);

        if (err is not null)
        {
            Assert.True(Directory.Exists(dir), $"目录没建起来：{err}");
            Assert.False(File.Exists(lnk),
                $"注册报了失败却留下了 .lnk（装配会读到一份自己判死却仍在磁盘上的快捷方式）：{err}");
            return;
        }

        Assert.True(File.Exists(lnk), "快捷方式没写出来");
        // 读回来走的是同一道接缝：重新 Load 一个 ShellLink 实例，再向它的 IPropertyStore 要那个键，
        // 所以读到的是文件里的内容，不是刚写进内存的那一份。
        Assert.Equal(AumidRegistrar.Aumid, AumidRegistrar.ReadAumid(lnk));
        // 字节级证据：那串名字在磁盘上就是 UTF-16LE，既没退成 ANSI，也没停在某个进程内缓存里。
        var raw = File.ReadAllBytes(lnk);
        Assert.Contains(AumidRegistrar.Aumid, System.Text.Encoding.Unicode.GetString(raw));
        Assert.DoesNotContain(AumidRegistrar.Aumid, System.Text.Encoding.ASCII.GetString(raw));
    }

    /// 带消息泵的 STA UI 线程：装配在它上面跑，控件的归属线程就是它 —— 和真机一样。
    /// 窗口只造句柄、挪到屏幕外、透明度 0，桌面上什么都看不见。
    /// createHandle=false 时**不**造句柄：那才是"Build 之后、Application.Run 还没走到造句柄"
    /// 的真机那一段窗口，装配在这段里开了事件通路就是在拿工作线程改控件。
    ///
    /// 封送与收尾都走 _pump 那个自建的小控件，不走 Parts.Form：被测代码要是真的把控件句柄造到了
    /// 工作线程上（就是这条用例要抓的那一件事），投给 Parts.Form 的 ExitThread 就永远没人处理，
    /// 于是"UI 线程没能退出"会把真正那条断言的失败原因盖掉。
    private sealed class UiThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly ApplicationContext _context = new();
        private readonly Exception?[] _fault = new Exception?[1];
        private readonly Control _pump = new Label();
        private readonly bool _createHandle;
        public AppParts Parts { get; private set; } = null!;
        public int ThreadId { get; private set; }

        public UiThread(TempSpace tmp, Func<string, AppParts> build, bool createHandle = true)
        {
            _createHandle = createHandle;
            var dir = tmp.NewDir("zwctx");          // 登记在用例的 TempSpace 上：红了也会删
            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                try
                {
                    ThreadId = Environment.CurrentManagedThreadId;
                    Parts = build(dir);
                    var f = Parts.Form;
                    f.Opacity = 0;
                    f.ShowInTaskbar = false;
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-20000, -20000);
                    _pump.Location = new Point(-20000, -20000);
                    _ = _pump.Handle;                                   // 这个线程的"邮筒"，句柄一定属于本线程
                    if (_createHandle) _ = f.Handle;                    // 交叉线程封送要有句柄才走 BeginInvoke
                    ready.Set();
                    Application.Run(_context);
                }
                catch (Exception ex) { _fault[0] = ex; ready.Set(); }
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(15)), "UI 线程没起来");
            Assert.Null(_fault[0]);
            Assert.Equal(_createHandle, Parts.Form.IsHandleCreated);
        }

        /// 在 UI 线程上把窗口真 Show 出来：Load/Shown 这些生产路径上的时机都会跑到。
        public void Show() => OnUi(() => Parts.Form.Show());

        public void Drain() => Post(() => { });

        public void OnUi(Action body) => Post(body);

        public T OnUi<T>(Func<T> body)
        {
            T result = default!;
            Post(() => result = body());
            return result;
        }

        private void Post(Action body)
        {
            var done = new ManualResetEventSlim(false);
            Exception? err = null;
            _pump.BeginInvoke(new Action(() =>
            {
                try { body(); } catch (Exception ex) { err = ex; } finally { done.Set(); }
            }));
            if (!done.Wait(TimeSpan.FromSeconds(10))) _fault[0] ??= new TimeoutException("UI 线程没有处理封送请求");
            if (err is not null) _fault[0] ??= err;
            Assert.True(done.IsSet, "UI 线程没有处理封送请求");
            Assert.Null(err);
        }

        public void AssertHealthy()
        {
            Drain();
            Assert.True(_thread.IsAlive, "UI 线程已经死了");
            Assert.Null(_fault[0]);
        }

        public void Dispose()
        {
            try { Post(() => _context.ExitThread()); } catch (Exception) { }
            var exited = !_thread.IsAlive || _thread.Join(TimeSpan.FromSeconds(10));
            if (exited)
            {
                // 收尾的异常一律咽掉：finally 里再抛一次会把测试体真正的那条断言失败盖住
                // （句柄被工作线程抢走时 Parts.Dispose 就是会抛，那正是被测的毛病，不是这里要报的）。
                try { Parts.Dispose(); } catch (Exception ex) { _fault[0] ??= ex; }
                try { _pump.Dispose(); } catch (Exception ex) { _fault[0] ??= ex; }
            }
            Assert.True(exited, "UI 线程没能退出");
        }
    }
}
