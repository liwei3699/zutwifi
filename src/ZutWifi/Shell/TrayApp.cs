using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Notify;
using ZutWifi.Wifi;

namespace ZutWifi.Shell;

/// 托盘：图标颜色 = 呈现规则的配色，菜单 = 协调器的四个动作。
/// 它不判断任何事，只把 StatusPresenter 的输出翻译成"右下角那一个点"。
/// 唯一例外是"一次只放一条命令"这条：那把门不在这里另造，用的是装配递进来的那一把（CommandGate），
/// 所以界面上的按钮、这里的菜单、60 秒定时器那一拍，以及无线源的每一次读取
/// （WifiSentinel 的心跳与 wlanapi 通知回调）打的是同一个"已经有命令在途就别再进一条"。
/// 谁都不许排队：排进来的那一条会在 22 秒之后对着一个可能已经不是刚才那个的会话真的发一个包。
public sealed class TrayApp : IDisposable
{
    /// NotifyIcon.Text 的上限是 63 个字符（超了现代 WinForms 会截断，老版本直接抛）。
    /// 失败态的文案要把"原因"整段带进来，长度不可控，所以在这里夹一刀而不是赌它不长。
    private const int MaxIconText = 63;

    private readonly NotifyIcon _icon = new() { Visible = true, Text = "ZutWifi" };
    private readonly MainForm _form;
    private readonly LoginCoordinator _coord;
    private readonly SettingsStore _store;
    private readonly CommandGate _gate;

    /// 装配注入的那份操作系统出口。托盘**不**决定一条通知怎么出去：气泡只是转给它的 ShowBalloon。
    private readonly NotificationSink _sink;
    private bool _exiting;
    private bool _disposed;

    /// 真往桌面上弹了几条气泡（`NotifyIcon.ShowBalloonTip` 的次数）。
    /// 生产里它的价值只是"多一个整数自增"，但它让"单测一次都不许碰操作系统通知面"这一条
    /// **可断言**：任何一处把气泡绕开注入的出口直接接回图标的改动，都会把
    /// `AppContextTests.装配好的组合根在测试里一次都不碰操作系统通知面` 弄红。
    internal int ShellBalloonTipsShown { get; private set; }

    /// sink 是**必填**参数（排在可选的 gate 之前，所以忘不掉）：托盘不许自己决定通知怎么出去。
    /// gate 不传时这里自己 new 一把：单独一个界面（Task 16 的那些用例）够用，
    /// 但它只管得住自己 —— 装配那条路上一定要把界面共用的那一把传进来，见 AppContext.Build。
    /// 整个构造是 internal 的：`NotificationSink` 带着那一层 internal 的 WinRT 触点接口，
    /// 而构造它的只有组合根与单测（`InternalsVisibleTo`）—— 别人本来也不该自己去建一个托盘。
    internal TrayApp(MainForm form, LoginCoordinator coord, SettingsStore store, NotificationSink sink,
        CommandGate? gate = null)
    {
        _form = form; _coord = coord; _store = store; _sink = sink; _gate = gate ?? new CommandGate();
        _icon.Icon = IconFactory.For("gray");
        _icon.ContextMenuStrip = BuildMenu();
        _icon.DoubleClick += (_, _) => Show();
        coord.StatusChanged += OnStatus;
        ApplyStatus(coord.Current);        // 装配完就是最新读数，别让托盘先灰着等第一次事件
    }

    /// 白名单判定：只看**实时无线源**，不看状态里那个 Ssid。
    /// 中止的那一轮发布的是已经被放弃的接入点（LoginCoordinator.Aborted 用的还是旧 ap），
    /// 拿 status.Ssid 判就会在用户已经离开的网络上把三个按钮点亮 —— 那正是最不该点亮的时刻。
    internal static bool LiveSsidWhitelisted(LoginCoordinator coord, SettingsStore store) =>
        coord.WifiCurrent is { } ap && SsidMatcher.IsCampus(ap.Ssid, store.Load().SsidWhitelist);

    /// 一次状态更新：图标、悬停文字、主窗口。可能从 wlanapi 通知线程/定时器线程进来。
    internal void ApplyStatus(AppStatus s)
    {
        // 只判一次：图标和窗口必须拿到同一个结论，中途再读一次盘可能正好换网，
        // 于是托盘亮绿而窗口按钮是灰的。
        var whitelisted = LiveSsidWhitelisted(_coord, _store);
        var p = StatusPresenter.Of(s, whitelisted);
        LastColorKey = p.ColorKey;
        OnUi(() =>
        {
            if (_disposed) return;
            _icon.Icon = IconFactory.For(p.ColorKey);
            _icon.Text = Clamp("ZutWifi · " + p.Text);
            // 窗口藏着也照画：收进托盘之后再打开，不能让人看到上一次的状态。
            if (!_form.IsDisposed) _form.Apply(s, whitelisted);
        });
    }

    internal string LastColorKey { get; private set; } = "gray";

    /// 打开主窗口。Task 17 把它接到 Notifier.Activated（WinRT 线程池）与二次启动唤醒，
    /// 所以两条线程都要能调它。
    public void Show() => OnUi(() =>
    {
        if (_disposed || _form.IsDisposed) return;
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Show();
        _form.BringToFront();
        _form.Activate();
    });

    /// Toast 注册失败时那条"看得见"的路（Notifier.BalloonFallback 与诊断不可用的提醒都打这里）。
    /// Notifier 可能在任意线程上发通知，所以这里也要封送。
    ///
    /// 注意这一句里**没有** `NotifyIcon.ShowBalloonTip`：整件事只是转给注入的 sink。
    /// 以前这里是直接弹真图标的，而装配又把 `notifier.BalloonFallback` 接到这个方法上 ——
    /// 于是任何一条走完整装配路径的用例都会在同学桌面上真弹一条。真那一句在下面
    /// `PresentOnTrayIcon` 里，而它只被 `NotificationSink.Production()` 那份出口绑得到（见 sink 的注释）。
    public void ShowBalloon(string title, string? body) => OnUi(() => _sink.ShowBalloon(title, body));

    /// 全仓唯一一处 `NotifyIcon.ShowBalloonTip`：把一条通知真的画到右下角那个图标上。
    /// 它存在托盘里，但**只有生产那份 sink 被装配绑得上它**（`AppContext.Build` 里那一句
    /// `BindShellBalloon`），所以递进记账器的用例永远够不到这一句 —— 想碰它得自己写一次
    /// `NotificationSink.Production()`，而那种写法在守门用例里立刻会红。
    /// 收尾之后（图标已经 Dispose）再来的一条就不弹了。
    internal void PresentOnTrayIcon(string title, string? body) => OnUi(() =>
    {
        if (_disposed) return;
        ShellBalloonTipsShown++;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body ?? "";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    });

    private void OnStatus(AppStatus s) => ApplyStatus(s);

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => Show());
        menu.Items.Add("登录", null, async (_, _) => await MenuLoginAsync());
        menu.Items.Add("注销", null, async (_, _) => await MenuLogoutAsync());
        menu.Items.Add("重新检测", null, async (_, _) => await MenuReprobeAsync());
        menu.Items.Add(new ToolStripSeparator());
        var autostart = new ToolStripMenuItem("开机自启") { Checked = _store.Load().AutoStart, CheckOnClick = true };
        autostart.CheckedChanged += (_, _) =>
        {
            // 读-改-写走 TryUpdate（评审 I4 同一处毛病，托盘也是 Load→改一个字段→Save）：
            // 盘上那份读不出来就什么都不写，也不去动注册表 —— 那一下勾选没存下，说给用户听。
            bool wrote;
            string? why;
            try { wrote = _store.TryUpdate(s => s.AutoStart = autostart.Checked, out why); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { wrote = false; why = ex.Message; }
            if (!wrote) { ShowBalloon("开机自启没存下", why); return; }
            StartupRegistry.Ensure(autostart.Checked);
        };
        menu.Items.Add(autostart);
        menu.Items.Add("退出", null, (_, _) => Exit());
        return menu;
    }

    // ---------- 菜单上的三条命令 ----------
    //
    // 公开成方法而不是只挂在 ToolStripMenuItem 上：PerformClick 在没进消息泵的菜单项上什么都不做，
    // 而"另一个界面在途时这里点不动"这条规矩（评审 C2）必须在装配的测试里钉得住。
    // 返回 false = 被那把共用的门拒了。

    public Task<bool> MenuLoginAsync() => RunMenuAsync("登录", _coord.RequestLoginAsync);
    public Task<bool> MenuLogoutAsync() => RunMenuAsync("注销", _coord.RequestLogoutAsync);
    public Task<bool> MenuReprobeAsync() => RunMenuAsync("重新检测", _coord.RequestReprobeAsync);

    /// 一律 await，绝不 .Wait()：协调器那把非重入的锁在退避期间能握着约 22 秒（2s+5s+15s 再加
    /// 请求本身），菜单处理程序里 .Wait() 一次就是把托盘冻 22 秒。
    /// 门是共用的（装配传进来的那把），所以界面在途时这里进不来，反过来也一样。
    private async Task<bool> RunMenuAsync(string name, Func<CancellationToken, Task> command)
    {
        try { return await _gate.RunAsync("托盘·" + name, command); }
        catch (Exception)
        {
            // 菜单不弹框：协调器侧的异常已经落进事务日志，这里只保证不把进程带崩。
            // 返回 true 说的是"这一条被受理了"（它确实打进去了，只是没跑成），
            // 与 false 那句"门被占着，什么都没发给门户"是两件事，别混成一个。
            return true;
        }
    }

    /// 托盘"退出"。
    ///
    /// 这里**不用** Environment.Exit：那会跳过 Program.Main 的 finally，于是 60 秒 ticker 没人
    /// Dispose、单实例 Mutex（和唤醒二次实例用的命名管道）也还被人持有 —— 下次启动会被自己的
    /// 残骸判成"已经在运行"。收尾次序：① 先摘图标，②通知装配方停心跳并释放互斥量/管道，
    /// ③ 真关窗口让消息循环自己结束，Program 的 finally 就是这么被跑到的。
    public void Exit() => OnUi(() =>
    {
        if (_exiting || _disposed) return;
        _exiting = true;
        _icon.Visible = false;
        try { ExitRequested?.Invoke(); }
        finally { if (!_form.IsDisposed) _form.ExitForReal(); }
    });

    /// 装配方在这里停掉 60 秒 ticker、释放 Mutex / 命名管道 / WifiSentinel。
    /// 事件在 UI 线程上触发，且只触发一次。
    public event Action? ExitRequested;

    /// 可能从别的线程进来（Toast 的 Activated、Notifier 的兜底气泡、wlanapi 通知线程），
    /// 一律封送到 UI 线程执行；窗口句柄还不存在时（测试里、或还没进消息循环）就地执行。
    private void OnUi(Action body)
    {
        if (!_form.IsHandleCreated || !_form.InvokeRequired) { body(); return; }
        try { _form.BeginInvoke(new Action(body)); }
        catch (ObjectDisposedException) { }                  // 窗口正在销毁：这一笔已经没人看了
        catch (InvalidOperationException) { }
    }

    private static string Clamp(string text) => text.Length <= MaxIconText ? text : text[..MaxIconText];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _coord.StatusChanged -= OnStatus;
        try { _icon.Visible = false; } catch (ObjectDisposedException) { }
        _icon.Dispose();
    }
}
