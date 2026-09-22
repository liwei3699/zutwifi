using System.Threading;
using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Diagnostics;
using ZutWifi.Portal;
using ZutWifi.Shell;
using ZutWifi.Tests.Support;
using ZutWifi.Wifi;

namespace ZutWifi.Tests;

/// Task 16 只验三件事：按钮打到哪个动作、可用性是不是完全照呈现规则、代码画的图标能不能用。
/// 视觉细节（字号、列宽、配色好不好看）一律不断言 —— 那要人在屏幕前看，推迟到 Task 20。
///
/// 按"真按钮"的用例都要先 Shown()：PerformClick 在没有窗口句柄的控件上什么都不做（实测），
/// 而 ListView 的选中项同样只有建了句柄才作数。挪到屏幕外 + 透明度 0，桌面上什么都看不见。
///
/// 托盘现在**必须**拿一份 sink（`NotificationSink`，没有默认值）：它自己再也不碰 `NotifyIcon` 的气泡，
/// 所以这里递进去的记账器一装，这一整个文件都不可能有能力往同学桌面上弹一条提示。
public class MainFormWiringTests : IDisposable
{
    private readonly TempSpace _tmp = new();
    private readonly RecordingSink _os = new();

    /// 四个动作各数一次的替身。全部同步完成，所以 PerformClick 返回时在途标记已经落回去。
    private sealed class Spy : ILoginCommands
    {
        public int Login, Logout, Reprobe, Recover;
        public Task LoginAsync(CancellationToken ct) { Login++; return Task.CompletedTask; }
        public Task LogoutAsync(CancellationToken ct) { Logout++; return Task.CompletedTask; }
        public Task ReprobeAsync(CancellationToken ct) { Reprobe++; return Task.CompletedTask; }
        public Task RecoverReloginAsync(CancellationToken ct) { Recover++; return Task.CompletedTask; }
    }

    /// 命令挂在 await 上不放行的替身：用来验"在途时不再接第二次点击""处理程序是 await 而不是 .Wait()"。
    private sealed class PendingSpy : ILoginCommands
    {
        private readonly TaskCompletionSource _gate = new();
        public int Login, Logout;
        public void Release() => _gate.SetResult();
        public Task LoginAsync(CancellationToken ct) { Login++; return _gate.Task; }
        public Task LogoutAsync(CancellationToken ct) { Logout++; return _gate.Task; }
        public Task ReprobeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RecoverReloginAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private string TmpDir() => _tmp.NewDir("zw");

    /// 这一条用例登记的临时目录（含断言失败时）都从这里收走。
    public void Dispose() => _tmp.Dispose();

    private static MainForm BuildFormAt(TempSpace tmp, ILoginCommands cmd, TransactionLog? log = null,
        Settings? settings = null)
    {
        var dir = tmp.NewDir("zw");
        var store = new SettingsStore(dir);
        if (settings is not null) store.Save(settings);
        return new MainForm(cmd, store, new SecretStore(dir), log);
    }

    private MainForm BuildForm(ILoginCommands cmd, TransactionLog? log = null, Settings? settings = null) =>
        BuildFormAt(_tmp, cmd, log, settings);

    /// 把窗口挪到屏幕外、透明度 0 再 Show：控件拿到真句柄，走的是真机那一条路径，桌面上什么都没有。
    private static MainForm Shown(MainForm form)
    {
        form.Opacity = 0;
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        return form;
    }

    private MainForm ShownForm(ILoginCommands cmd, TransactionLog? log = null, Settings? settings = null)
        => Shown(BuildForm(cmd, log, settings));

    /// 等一条命令的续跑落回 UI 线程：这个线程自己就是 UI 线程，所以要靠 DoEvents 把
    /// WindowsFormsSynchronizationContext 收到的那一笔投递跑掉（真机上是消息循环在做同一件事）。
    private static bool PumpUntil(Func<bool> done, int millis = 3000)
    {
        var deadline = Environment.TickCount64 + millis;
        while (!done() && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        return done();
    }

    /// 真协调器，只有无线源是替身：托盘那侧的白名单判定要连着 LoginCoordinator 一起验。
    private static LoginCoordinator Coord(FakeWifiSource wifi) =>
        new(new PortalGateway(new HttpClient(new FakeHttpHandler()), "1.1.1.1"), wifi,
            () => new Credential("id", "pw", "@cmcc"), new Settings(),
            new FakeClock(), new FakeProbe(true));

    // ---------- 事件接线 ----------

    [Fact]
    public void 四个按钮各自打到协调器的四个动作()
    {
        var spy = new Spy();
        using var form = ShownForm(spy);
        form.SimulateLoginClick(); form.SimulateLogoutClick();
        form.SimulateReprobeClick(); form.SimulateRecoverClick();
        Assert.Equal(1, spy.Login); Assert.Equal(1, spy.Logout);
        Assert.Equal(1, spy.Reprobe); Assert.Equal(1, spy.Recover);
    }

    [Fact]
    public void 命令在途时不再接第二次点击跑完再照呈现规则恢复()
    {
        var spy = new PendingSpy();
        using var form = ShownForm(spy);
        form.Apply(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 12), ssidWhitelisted: true);
        Assert.True(form.LogoutButtonEnabled);

        form.SimulateLogoutClick();                     // 卡在 await 上：协调器那把锁在退避时能握 22 秒
        Assert.Equal(1, spy.Logout);
        Assert.False(form.LogoutButtonEnabled);         // 在途期间四个动作一律点不动
        form.SimulateLogoutClick();
        Assert.Equal(1, spy.Logout);                    // 不排队：排队等于上一轮跑完再给门户补一个注销包

        spy.Release();
        Assert.True(PumpUntil(() => !form.CommandInFlight), "在途标记没收尾（续跑没回到 UI 线程？）");
        Assert.True(form.LogoutButtonEnabled);          // 恢复的是呈现规则算出来的那一套，不是"全亮"
    }

    [Fact]
    public void 命令在途时新状态照常上色但按钮保持禁用()
    {
        var spy = new PendingSpy();
        using var form = ShownForm(spy);
        form.SimulateLoginClick();
        Assert.True(form.CommandInFlight);

        // 失败态本应放行登录；命令还在途，所以按钮必须仍是死的（否则第二次点击会排进同一条锁）
        form.Apply(new AppStatus(AppPhase.Failed, "zut-stu", "10.1.1.1", "登录已中止"), ssidWhitelisted: true);
        Assert.Equal("失败：登录已中止", form.StatusText);
        Assert.False(form.LoginButtonEnabled);
        Assert.False(form.ReprobeButtonEnabled);

        spy.Release();
        Assert.True(PumpUntil(() => !form.CommandInFlight));
        Assert.True(form.LoginButtonEnabled);
    }

    // ---------- 可用性完全出自 StatusPresenter ----------

    [Fact]
    public void 状态应用后按呈现规则设置可用性()
    {
        using var form = BuildForm(new Spy());
        form.Apply(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 4211), ssidWhitelisted: true);
        Assert.False(form.LoginButtonEnabled);
        Assert.True(form.LogoutButtonEnabled);
        Assert.Equal(4211, form.OnlineSecondsShown);
    }

    [Fact]
    public void 非白名单SSID时三个动作都不可用()
    {
        using var form = BuildForm(new Spy());
        form.Apply(new AppStatus(AppPhase.Idle, "TP-LINK"), ssidWhitelisted: false);
        Assert.False(form.LoginButtonEnabled);
        Assert.False(form.LogoutButtonEnabled);
        Assert.False(form.ReprobeButtonEnabled);
    }

    /// 主窗口不许自己再判一次该不该亮：九个阶段 × 白名单真/假，逐个和呈现规则对一遍。
    [Theory]
    [InlineData(AppPhase.Idle, true)]
    [InlineData(AppPhase.Probing, true)]
    [InlineData(AppPhase.AcquiringIp, true)]
    [InlineData(AppPhase.LoggingIn, true)]
    [InlineData(AppPhase.Verifying, true)]
    [InlineData(AppPhase.Online, true)]
    [InlineData(AppPhase.Degraded, true)]
    [InlineData(AppPhase.Failed, true)]
    [InlineData(AppPhase.GiveUp, true)]
    [InlineData(AppPhase.Online, false)]
    [InlineData(AppPhase.Failed, false)]
    public void 按钮可用性永远等于呈现规则给的那一套(AppPhase phase, bool whitelisted)
    {
        using var form = BuildForm(new Spy());
        var s = new AppStatus(phase, "zut-stu", "10.1.1.1", "一个原因", 60);
        form.Apply(s, whitelisted);
        var p = StatusPresenter.Of(s, whitelisted);
        Assert.Equal(p.LoginEnabled, form.LoginButtonEnabled);
        Assert.Equal(p.LogoutEnabled, form.LogoutButtonEnabled);
        Assert.Equal(p.ReprobeEnabled, form.ReprobeButtonEnabled);
        Assert.Equal(p.Text, form.StatusText);
        Assert.Equal(s.OnlineSeconds, form.OnlineSecondsShown);
    }

    // ---------- 白名单只看实时无线源（不看状态里那个可能已经过期的 SSID） ----------

    [Fact]
    public void 人离开校园网之后托盘不再点亮按钮()
    {
        var wifi = new FakeWifiSource { Current = new AccessPoint("TP-LINK_5G", "192.168.1.20", "aabbccddeeff") };
        using var form = BuildForm(new Spy());
        using var tray = new TrayApp(form, Coord(wifi), new SettingsStore(TmpDir()), _os.Sink);
        // 中止的那一轮发布的是"已经被放弃的"接入点（LoginCoordinator.Aborted 用的还是旧 ap）：
        // 拿 status.Ssid 判白名单，就会在用户已经离开的网络上把三个按钮点亮。
        var abandoned = new AppStatus(AppPhase.Failed, "zut-stu", "10.1.1.1", "登录已中止");

        tray.ApplyStatus(abandoned);
        Assert.Equal("gray", tray.LastColorKey);
        Assert.False(form.LoginButtonEnabled);
        Assert.False(form.LogoutButtonEnabled);
        Assert.False(form.ReprobeButtonEnabled);

        wifi.Current = new AccessPoint("zut-stu", "10.1.1.1", "aabbccddeeff");   // 真的还连在校园网上
        tray.ApplyStatus(abandoned);
        Assert.Equal("red", tray.LastColorKey);
        Assert.True(form.LoginButtonEnabled);
    }

    [Fact]
    public void 协调器读到的实时接入点就是判定用的那一个()
    {
        var dir = TmpDir();
        var store = new SettingsStore(dir);
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabb") };
        var coord = Coord(wifi);
        Assert.True(TrayApp.LiveSsidWhitelisted(coord, store));
        wifi.Current = new AccessPoint("hotel-5G", "10.0.0.9", "aabb");
        Assert.False(TrayApp.LiveSsidWhitelisted(coord, store));
        wifi.Current = null;                                                     // 断开
        Assert.False(TrayApp.LiveSsidWhitelisted(coord, store));
    }

    [Fact]
    public void 托盘图标颜色跟着呈现规则走()
    {
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabb") };
        using var form = BuildForm(new Spy());
        using var tray = new TrayApp(form, Coord(wifi), new SettingsStore(TmpDir()), _os.Sink);

        tray.ApplyStatus(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 5));
        Assert.Equal("green", tray.LastColorKey);
        tray.ApplyStatus(new AppStatus(AppPhase.Failed, "zut-stu", "10.1.1.1", "认证已失效"));
        Assert.Equal("red", tray.LastColorKey);
        wifi.Current = new AccessPoint("隔壁的网络", "10.1.1.1", "aabb");         // 人已经不在校园网上了
        tray.ApplyStatus(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 5));
        Assert.Equal("gray", tray.LastColorKey);
    }

    /// 失败原因整段进悬停文字：NotifyIcon.Text 上限 63 字符，超了老版本 WinForms 直接抛。
    [Fact]
    public void 长原因不会把托盘悬停文字撑爆()
    {
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabb") };
        using var form = BuildForm(new Spy());
        using var tray = new TrayApp(form, Coord(wifi), new SettingsStore(TmpDir()), _os.Sink);
        var longReason = string.Concat(Enumerable.Repeat("门户回了一句很长的原因", 20));
        var err = Record.Exception(() =>
            tray.ApplyStatus(new AppStatus(AppPhase.Failed, "zut-stu", "10.1.1.1", longReason)));
        Assert.Null(err);
    }

    // ---------- 关窗与退出 ----------

    [Fact]
    public void 关窗默认收进托盘而不是退出()
    {
        using var form = ShownForm(new Spy());
        form.Close();
        Assert.False(form.ClosedForReal);
        Assert.False(form.Visible);
    }

    [Fact]
    public void 设置里关掉收托盘之后关窗就是真退出()
    {
        using var form = ShownForm(new Spy(), settings: new Settings { CloseToTray = false });
        form.Close();
        Assert.True(form.ClosedForReal);
    }

    [Fact]
    public void 托盘退出把收尾交回装配方而不是直接掐进程()
    {
        var form = ShownForm(new Spy());
        using var tray = new TrayApp(form, Coord(new FakeWifiSource()), new SettingsStore(TmpDir()), _os.Sink);
        var exits = 0;
        tray.ExitRequested += () => exits++;

        tray.Exit();
        Assert.Equal(1, exits);                       // 装配方在这里停 ticker、释放 Mutex/命名管道
        Assert.True(form.ClosedForReal);              // 消息循环自然结束 → Program 的 finally 才有的跑
        tray.Exit();
        Assert.Equal(1, exits);                       // 幂等：重复退出不再发第二遍
    }

    /// Task 17 接的是这三样：`Show`（Toast 点击）、`ShowBalloon`（Notifier 的回退出口）、
    /// 以及"托盘自己不许是那条出口的终点"。第三样以前测不到 —— `ShowBalloon` 里面就是
    /// `NotifyIcon.ShowBalloonTip`，装配把它接到通知器上，每一次失败登录都在同学桌面上多一条气泡。
    /// 现在它是注入的 sink 的一个转发：调用了一整轮，图标一次都没弹。
    [Fact]
    public void 托盘对外形状正好是Task17要接的那几个委托()
    {
        using var form = BuildForm(new Spy());
        using var tray = new TrayApp(form, Coord(new FakeWifiSource()), new SettingsStore(TmpDir()), _os.Sink);

        Action show = tray.Show;                                          // Toast 点击要从 WinRT 线程调它
        Action<string, string?> balloon = tray.ShowBalloon;               // Notifier.BalloonFallback 的形状
        Assert.NotNull(show); Assert.NotNull(balloon);

        balloon("校园网登录失败", "Radius 认证失败（账号或密码错误） · 点击查看详情");
        balloon("没有正文的那一条", null);
        Assert.Equal(new[] { ("校园网登录失败", (string?)"Radius 认证失败（账号或密码错误） · 点击查看详情"),
                             ("没有正文的那一条", null) }, _os.Balloons);   // 落在记账器里
        Assert.Equal(0, tray.ShellBalloonTipsShown);                       // 真图标一次都没弹
    }

    // ---------- 通知点击：切到日志页并选中最后一行（spec 第 6 节） ----------

    [Fact]
    public void 跳到最新日志时切到日志页并选中最后一行()
    {
        var log = new TransactionLog(new FakeClock(), Path.Combine(TmpDir(), "logs"));
        Write(log, "Login", "探测门户");
        Write(log, "Login", "提交凭据");
        Write(log, "Coordinator", "无线事件这一轮没跑完");

        using var form = ShownForm(new Spy(), log);
        form.Apply(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 3), ssidWhitelisted: true);
        Assert.Equal(3, form.LogRowCount);
        Assert.NotEqual("日志", form.SelectedTabText);

        form.JumpToLatestLog();
        Assert.Equal("日志", form.SelectedTabText);
        Assert.Equal(1, form.LogSelectedCount);
        Assert.Contains("无线事件这一轮没跑完", form.LastSelectedLogRow);
    }

    [Fact]
    public void 没有日志时跳转也只是切页不抛出()
    {
        using var form = BuildForm(new Spy());
        var err = Record.Exception(() => form.JumpToLatestLog());
        Assert.Null(err);
        Assert.Equal(0, form.LogRowCount);
    }

    /// 布局只验次序，不验好看：头部那两行一旦反了，主界面就是"下面一行大字结论"。
    [Fact]
    public void 状态行排在明细行上面()
    {
        using var form = ShownForm(new Spy());
        form.Apply(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 5), ssidWhitelisted: true);
        var labels = Walk(form).OfType<Label>().ToList();
        var status = labels.Single(l => l.Text == form.StatusText && l.Font.SizeInPoints > 13);
        var detail = labels.Single(l => l.Text.Contains("SSID zut-stu"));
        Assert.True(status.Top < detail.Top, $"状态行 Top={status.Top} 应当在明细行 Top={detail.Top} 之上");
    }

    [Fact]
    public void 四个动作按钮与导出按钮都在窗口上()
    {
        using var form = ShownForm(new Spy());
        var texts = Walk(form).OfType<Button>().Select(b => b.Text).ToList();
        Assert.Contains("登录", texts);
        Assert.Contains("注销", texts);
        Assert.Contains("重新检测", texts);
        Assert.Contains("注销并重登", texts);
        Assert.Contains("导出诊断包", texts);
    }

    /// 四个页签按计划排：状态 / 日志 / 设置 / 关于，日志固定在第 2 页（跳转靠它）。
    [Fact]
    public void 页签次序与名字对得上()
    {
        using var form = ShownForm(new Spy());
        var tabs = form.Controls.OfType<TabControl>().Single();
        Assert.Equal(new[] { "状态", "日志", "设置", "关于" }, tabs.TabPages.Cast<TabPage>().Select(p => p.Text));
    }

    private static IEnumerable<Control> Walk(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var deeper in Walk(child)) yield return deeper;
        }
    }

    private static void Write(TransactionLog log, string stage, string summary) =>
        log.Write(new TransactionRecord(stage, "POST", "http://1.1.1.1/eportal", 302,
            "http://1.1.1.1/a70.htm", summary, null));

    // ---------- 代码画的图标 ----------

    [Theory]
    [InlineData("green")]
    [InlineData("yellow")]
    [InlineData("orange")]
    [InlineData("red")]
    [InlineData("gray")]
    public void 五种配色都能造出图标(string color)
    {
        var icon = IconFactory.For(color);             // 缓存归进程所有，用例不 Dispose
        Assert.NotNull(icon);
        Assert.True(icon.Width >= 16, $"{color}：图标宽度 {icon.Width}");
        Assert.True(icon.Height >= 16, $"{color}：图标高度 {icon.Height}");
    }

    [Fact]
    public void 配色认不出来时落回灰色而不是抛()
    {
        Assert.Equal(IconFactory.ColorOf("gray"), IconFactory.ColorOf("没有这个配色"));
        Assert.NotNull(IconFactory.For("没有这个配色"));
    }

    [Fact]
    public void 同一个配色每次拿到的是同一个图标()
        => Assert.Same(IconFactory.For("green"), IconFactory.For("green"));

    /// 五种配色必须互不相同：全落回同一个色就等于托盘永远只有一种脸色。
    [Fact]
    public void 五种配色是五种不同颜色()
    {
        var keys = new[] { "green", "yellow", "orange", "red", "gray" };
        Assert.Equal(keys.Length, keys.Select(IconFactory.ColorOf).Distinct().Count());
    }

    /// 圆点画的必须就是那一种颜色：Clone 若与原句柄共享，DestroyIcon 之后这里就是一张脏图。
    [Theory]
    [InlineData("green")]
    [InlineData("yellow")]
    [InlineData("orange")]
    [InlineData("red")]
    [InlineData("gray")]
    public void 图标画的确实是那一种颜色(string key)
    {
        using var bmp = IconFactory.For(key).ToBitmap();
        Assert.Equal(IconFactory.ColorOf(key), bmp.GetPixel(8, 8));       // 圆点正中心
    }

    /// 托盘每分钟换一次颜色：新图标画出来、原句柄当场还掉，不能顺手把已经缓存好的那几个弄坏。
    [Fact]
    public void 换过一轮新配色之后老图标仍然画得出来()
    {
        for (var i = 0; i < 50; i++) IconFactory.For("一次性配色" + i);
        using var bmp = IconFactory.For("green").ToBitmap();
        Assert.Equal(IconFactory.ColorOf("green"), bmp.GetPixel(8, 8));
    }

    // ---------- 跨线程：Toast 的 Activated 与协调器的事件都在非 UI 线程上回调 ----------

    [Fact]
    public void 后台线程调Apply与跳转会切回UI线程生效()
    {
        using var ui = new PumpedForm(_tmp, new Spy());
        var caller = Task.Run(() =>
        {
            ui.Form.Apply(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 777), true);
            ui.Form.JumpToLatestLog();
        });
        Assert.True(caller.Wait(2000), "从后台线程调用时卡住了：一定是哪里在同步等 UI 线程");
        ui.Drain();                                     // BeginInvoke 是 FIFO，空委托跑完即说明上面两笔已生效
        Assert.True(ui.OnUi(() => ui.Form.LogoutButtonEnabled));
        Assert.False(ui.OnUi(() => ui.Form.LoginButtonEnabled));
        Assert.Equal(777, ui.OnUi(() => ui.Form.OnlineSecondsShown));
        Assert.Equal("日志", ui.OnUi(() => ui.Form.SelectedTabText));
        ui.AssertUiThreadHealthy();
    }

    [Fact]
    public void 后台线程调托盘的状态应用也切回UI线程()
    {
        var wifi = new FakeWifiSource { Current = new AccessPoint("zut-stu", "10.1.1.1", "aabb") };
        using var ui = new PumpedForm(_tmp, new Spy());
        using var tray = new TrayApp(ui.Form, Coord(wifi), new SettingsStore(TmpDir()), _os.Sink);

        var caller = Task.Run(() =>
            tray.ApplyStatus(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1", null, 9)));
        Assert.True(caller.Wait(2000), "托盘应用状态时阻塞了");
        ui.Drain();
        Assert.Equal("green", tray.LastColorKey);
        Assert.True(ui.OnUi(() => ui.Form.LogoutButtonEnabled));
        ui.AssertUiThreadHealthy();
    }

    /// 一个带消息泵的 UI 线程 + 一个只造了句柄（没有窗口出现在桌面上）的 MainForm。
    /// 不 Show 就没有闪窗，也就没有"测试跑起来同学看到一堆窗口"的问题。
    private sealed class PumpedForm : IDisposable
    {
        private readonly Thread _thread;
        private readonly ApplicationContext _context = new();
        private readonly Exception?[] _uiError = new Exception?[1];
        private readonly ManualResetEventSlim _ready = new(false);
        public MainForm Form { get; private set; } = null!;

        public PumpedForm(TempSpace tmp, ILoginCommands cmd, TransactionLog? log = null)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    Form = BuildFormAt(tmp, cmd, log);
                    _ = Form.Handle;                    // 造句柄：跨线程封送要有句柄才走 Invoke 那条路
                    _ready.Set();
                    Application.Run(_context);
                }
                catch (Exception ex) { _uiError[0] = ex; _ready.Set(); }
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(10)), "UI 线程没起来");
            Assert.True(Form.IsHandleCreated, "句柄没造出来，这条用例测不到封送");
            Assert.Null(_uiError[0]);
        }

        /// 排空前面所有已入队的封送：BeginInvoke 按 FIFO 处理，所以空委托跑完就说明前面的都生效了。
        public void Drain() => OnUi(() => { });

        public T OnUi<T>(Func<T> body) => Run(() => body());

        public void OnUi(Action body) => Run(() => { body(); return 0; });

        private R Run<R>(Func<R> body)
        {
            var done = new ManualResetEventSlim(false);
            R result = default!;
            Exception? error = null;
            Form.BeginInvoke(new Action(() =>
            {
                try { result = body(); } catch (Exception ex) { error = ex; } finally { done.Set(); }
            }));
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "UI 线程没有处理封送请求（消息泵挂了？）");
            Assert.Null(error);
            return result;
        }

        /// UI 线程的消息循环里逃出来的异常（比如跨线程直接碰控件）会终止 Application.Run，这里收口。
        public void AssertUiThreadHealthy()
        {
            Drain();
            Assert.True(_thread.IsAlive, "UI 线程已经死了");
            Assert.Null(_uiError[0]);
        }

        public void Dispose()
        {
            if (_thread.IsAlive) OnUi(() => _context.ExitThread());
            Assert.True(_thread.Join(TimeSpan.FromSeconds(10)), "UI 线程没能退出");
            Form.Dispose();
        }
    }
}
