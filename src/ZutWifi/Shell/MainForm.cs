using System.Text.RegularExpressions;
using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Diagnostics;

namespace ZutWifi.Shell;

/// 主窗口。它唯一的职责是把 StatusPresenter 算出来的那一套（文案、配色、可用性）画上去，
/// 一个业务判断都不做 —— 该不该登录、按钮该不该亮，都是协调器与呈现规则的事。
///
/// 线程：协调器的 StatusChanged 在 wlanapi 通知线程/定时器线程上触发，
/// Notifier.Activated 在 WinRT 线程池上触发（见 Notifier.Activated 的注释）。
/// 所以外部进来的每一个入口都先过 OnUi 这道闸再碰控件。
public sealed class MainForm : Form
{
    private readonly ILoginCommands _cmd;
    private readonly SettingsStore _store;
    private readonly SecretStore _secrets;
    private readonly TransactionLog? _log;

    private readonly Label _status = new()
    {
        Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
        AutoSize = false,
        Dock = DockStyle.Top,
        Height = 34,
    };
    private readonly Label _detail = new()
    {
        Font = new Font("Microsoft YaHei UI", 9F),
        Dock = DockStyle.Top,
        Height = 24,
        ForeColor = Color.FromArgb(90, 90, 90),
    };
    private readonly Button _login = new() { Text = "登录", Width = 110, Height = 36, Margin = new Padding(0, 0, 10, 0) };
    private readonly Button _logout = new() { Text = "注销", Width = 110, Height = 36, Margin = new Padding(0, 0, 10, 0) };
    private readonly Button _reprobe = new() { Text = "重新检测", Width = 110, Height = 36, Margin = new Padding(0, 0, 10, 0) };
    private readonly Button _recover = new() { Text = "注销并重登", Width = 120, Height = 36, Visible = false };
    private readonly ListView _events = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, GridLines = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _logPage = new("日志");
    private readonly Button _export = new() { Text = "导出诊断包", Width = 120, Height = 32 };
    private readonly Label _about = new() { Dock = DockStyle.Fill, Padding = new Padding(18) };
    private readonly PictureBox _aboutLogo = new()
    {
        Dock = DockStyle.Top, Height = 132, SizeMode = PictureBoxSizeMode.Zoom,
        Image = AppBrand.Logo(),
    };

    private AppStatus? _last;                 // 最近一次 Apply 收到的状态，命令跑完要照它重画
    private Presentation? _presentation;
    private readonly CommandGate _gate;       // "同时只有一条命令在途"那面旗（评审 C2：与托盘共用一把）
    private bool _paintedBlocked;             // 界面上最后一次画出来的那个在途状态（见 CommandInFlight）
    private bool _realExit;

    /// 秒表那一走。`System.Windows.Forms.Timer` 只在 UI 线程上触发，所以这一拍**不可能**碰门户，
    /// 也不需要任何封送 —— 它存在的唯一意义是把"两次刷新之间"那段时间画出来。
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 1000 };
    private readonly Func<long> _tickMs;

    /// gate 是装配那一把（界面上的四个按钮与托盘上的三条菜单共用的门）。
    /// 不传时这里自己 new 一把：单独一个窗口（Task 16 的那些用例）够用，
    /// 但它只管得住自己 —— 装配那条路上一定要把共用的一把传进来，见 AppContext.Build 的规矩⑤。
    /// monotonic 是给用例的：传一个假计数器就能"拨快"秒表，不必真等一秒。
    public MainForm(ILoginCommands cmd, SettingsStore store, SecretStore secrets, TransactionLog? log,
        CommandGate? gate = null, Func<long>? monotonic = null)
    {
        _cmd = cmd; _store = store; _secrets = secrets; _log = log;
        _gate = gate ?? new CommandGate(log);
        _tickMs = monotonic ?? (() => Monotonic.TickMs);
        _ticker.Tick += (_, _) => Render();           // 只重画，不判断、不出网
        // 门的每一次开合都要重画一遍按钮：托盘发起的那条命令同样得让这里的四个按钮变死，
        // 否则"只有一条在途"在界面上看不见，人还会以为再点一次能插进去。
        // 画成什么由门当参数递过来：这一笔可能被排到 UI 线程上做，那时 IsBusy 已经是另一个值了。
        _gate.Changed += busy => OnUi(() => Render(busy));
        Text = "ZutWifi 校园网自动登录";
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(660, 460);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = new Font("Microsoft YaHei UI", 9F);
        // 标题栏与任务栏上那个图标。取不到就不设，留着 WinForms 的默认图标 ——
        // 一个图标不值得让登录程序起不来（AppBrand 里那条 null 约定）。
        if (AppBrand.Icon() is { } brandIcon) Icon = brandIcon;

        // 同一个 Dock=Top 的两位，后加入的贴边更靠外：所以 _detail 在前、_status 在后。
        var header = new Panel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(18, 14, 18, 6) };
        header.Controls.Add(_detail);
        header.Controls.Add(_status);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(18, 8, 18, 8) };
        actions.Controls.AddRange([_login, _logout, _reprobe, _recover]);

        _events.Columns.Add("时间", 92);
        _events.Columns.Add("事件", 92);
        _events.Columns.Add("详情", 500);
        _logPage.Controls.Add(_events);

        var statusPage = new TabPage("状态"); statusPage.Controls.Add(BuildStatusPanel());
        // 设置页拿的是装配那一份日志（全进程只有一份，见 AppContext.Build）：
        // 在设置页里点一次"测试配置"也要在诊断包里留下记录。
        var settingsPage = new TabPage("设置");
        settingsPage.Controls.Add(new SettingsPage(_store, _secrets, _log) { Dock = DockStyle.Fill });
        var aboutPage = new TabPage("关于");
        // 校徽 + 文字。图取不到（资源没打进来）时那一格是空的，文字照旧 —— 见 AppBrand 的 null 约定。
        // 先加 Fill 再加 Top：后加入的贴边更靠外，反过来文字会被挤到图下面去。
        _about.Text = AboutText();
        aboutPage.Controls.Add(_about);
        aboutPage.Controls.Add(_aboutLogo);
        _tabs.TabPages.AddRange([statusPage, _logPage, settingsPage, aboutPage]);

        // 导出按钮走 RightToLeft 流：不用绝对坐标，宽窗窄窗都贴右边。
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(18, 10, 18, 10),
        };
        _export.Click += async (_, _) => await ExportBundleAsync();
        bar.Controls.Add(_export);

        Controls.Add(_tabs); Controls.Add(bar); Controls.Add(actions); Controls.Add(header);

        // 四个按钮 → 协调器的四个动作。await 而不是 .Wait()：见 RunAsync 的注释。
        _login.Click += async (_, _) => await RunAsync("界面·登录", _cmd.LoginAsync);
        _logout.Click += async (_, _) => await RunAsync("界面·注销", _cmd.LogoutAsync);
        _reprobe.Click += async (_, _) => await RunAsync("界面·重新检测", _cmd.ReprobeAsync);
        _recover.Click += async (_, _) => await RunAsync("界面·注销并重登", _cmd.RecoverReloginAsync);
    }

    // ---------- 只读外观（测试与 Task 17 用） ----------

    public bool LoginButtonEnabled => _login.Enabled;
    public bool LogoutButtonEnabled => _logout.Enabled;
    public bool ReprobeButtonEnabled => _reprobe.Enabled;
    public bool ExportButtonEnabled => _export.Enabled;
    public string StatusText => _status.Text;
    public int? OnlineSecondsShown { get; private set; }
    public string SelectedTabText => _tabs.SelectedTab?.Text ?? "";
    /// "还有一条命令在途"，而且连界面一起算：门放开之后那一笔"画成不在途"可能还在 UI 线程的
    /// 队里排着，那时四个按钮看着仍是死的。轮询这个属性的人（单测、装配的收尾）要的从来就是
    /// "界面已经恢复了"，不是"锁刚松开"—— 上一版的 _busy 就是在重画那一笔里落回去的，这个语义得留着。
    internal bool CommandInFlight => _gate.IsBusy || _paintedBlocked;
    internal int LogRowCount => _events.Items.Count;
    public string AboutTextShown => _about.Text;
    public string DetailText => _detail.Text;
    internal bool TickerEnabled => _ticker.Enabled;
    /// 秒表那一拍做的事，全在这里：只重画。用例调它，就等于等过了一秒而不必真的等。
    internal void SimulateSecondTick() => Render();
    public bool AboutShowsLogo => _aboutLogo.Image is not null;
    internal int LogSelectedCount => _events.SelectedItems.Count;
    internal string LastSelectedLogRow =>
        _events.SelectedItems.Count == 0 ? "" : _events.SelectedItems[0].SubItems[2].Text;

    public void SimulateLoginClick() => _login.PerformClick();
    public void SimulateLogoutClick() => _logout.PerformClick();
    public void SimulateReprobeClick() => _reprobe.PerformClick();
    public void SimulateRecoverClick() { _recover.Visible = true; _recover.PerformClick(); }

    /// 单测入口：绕开"按钮此刻亮不亮"，直接走界面那条命令通路（同一把门）。
    /// 评审 C2 要钉的是门本身：托盘在途时按钮本来就该被画死，那时 PerformClick 什么都不做，
    /// 用它证不了"第二条命令被拒而不是排队"。
    internal Task SimulateLogoutCommand() => RunAsync("界面·注销", _cmd.LogoutAsync);

    /// 界面发起一条命令。
    ///
    /// 绝不 .Wait()/.Result：LoginCoordinator 用一把**非重入**的 SemaphoreSlim 串行化命令，
    /// 退避重试期间它能握着这把锁约 22 秒（2s+5s+15s 再加请求本身）。在 UI 线程上同步等锁，
    /// 等到的不是结果而是整个界面（含托盘，因为 NotifyIcon 的消息也归这个线程泵）冻住 22 秒。
    ///
    /// 门是 CommandGate（与托盘菜单共用同一把，评审 C2）：第二条命令排进协调器那把锁，
    /// 等于上一轮刚结束又给门户交一个包，而且是在一个可能已经不是刚才那个的会话上。
    /// 同学连点"注销并重登"五下，就该只发五分之一那么多次凭据提交。
    /// 界面发起的动作全走这一条（四个动作 + Task 18 的导出诊断包）：一把门，不是每加一个按钮就新造一把。
    private async Task RunAsync(string from, Func<CancellationToken, Task> command)
    {
        try
        {
            await _gate.RunAsync(from, command);
        }
        catch (Exception ex)
        {
            // 不弹框：一个模态框能把界面（和自动化测试）钉在这里。异常落进事务日志，
            // 按钮照常恢复，用户看得见下一次结果。
            _log?.Write(new TransactionRecord("UI", "COMMAND", $"click://{from}", null, null,
                "界面命令没跑完", $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private Control BuildStatusPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        flow.Controls.Add(new Label
        {
            Text = "点“日志”页可看到每次门户交互的原始判定。",
            AutoSize = true,
            Padding = new Padding(0, 10, 0, 0),
        });
        p.Controls.Add(flow);
        return p;
    }

    /// 界面上的一切文字与可用性都从这一进来。
    /// ssidWhitelisted 必须由调用方按**实时无线源**算好传进来（见 TrayApp.LiveSsidWhitelisted）：
    /// 状态里那个 Ssid 可能是已经被放弃的接入点，拿它判按钮就等于在用户已经离开的网络上放行登录。
    public void Apply(AppStatus s, bool ssidWhitelisted) => OnUi(() =>
    {
        _last = s;
        _presentation = StatusPresenter.Of(s, ssidWhitelisted);
        Render();
        RefreshLog();
    });

    /// 只画，不判断：所有值都取自 _presentation（StatusPresenter 的输出）。
    /// "命令在途"这个临时状态不在本窗口里，它就是那把共用的门（CommandGate）：
    /// 托盘发起的那条命令也一样把这里四个按钮画死，反过来这条命令在途时托盘那三条同样点不动。
    /// busyOrNull：门开合时把"这一次该按哪个在途状态画"当参数递进来（那一笔可能要排到 UI 线程上做），
    /// 其余调用（Apply 收到的新状态）没有临时状态要说，就读门的当前值。
    private void Render(bool? busyOrNull = null)
    {
        var blocked = busyOrNull ?? _gate.IsBusy;
        _paintedBlocked = blocked;            // 先记下这一笔画到哪个状态（下面可能因为没状态而什么都不画）
        if (_presentation is not { } p || _last is not { } s) return;
        OnlineSecondsShown = s.SecondsAt(_tickMs());
        // 秒表只在"确实在线"的两格里走：失败/空闲那一格挂着一个自己往上爬的数字，
        // 比不显示更容易让人误判。
        var ticking = s.Phase is AppPhase.Online or AppPhase.Degraded && s.OnlineAtTickMs is not null;
        if (ticking && !_ticker.Enabled) _ticker.Start();
        else if (!ticking && _ticker.Enabled) _ticker.Stop();
        _status.Text = p.Text;
        _status.ForeColor = IconFactory.ColorOf(p.ColorKey);
        _detail.Text = $"SSID {s.Ssid ?? "-"}    IP {s.Ip ?? "-"}    在线 {StatusPresenter.FormatDuration(OnlineSecondsShown)}"
            + (s.Reason is null ? "" : $"    {s.Reason}");
        _login.Text = StatusPresenter.LoginText(s.Phase);
        // 命令在途时四个动作一律点不动（呈现规则此时让位给"别在那把锁前排队"）。
        // 导出也归这把门管：它打包的就是这四个动作正在写的那同一份日志，
        // 导出跑到一半放进来一条登录，包里的关键那几行正好是半截的。
        _login.Enabled = !blocked && p.LoginEnabled;
        _logout.Enabled = !blocked && p.LogoutEnabled;
        _reprobe.Enabled = !blocked && p.ReprobeEnabled;
        _recover.Visible = p.RecoverVisible && !blocked;
        _export.Enabled = !blocked;
    }

    public void RefreshLog()
    {
        if (_log is null) return;
        _events.BeginUpdate();
        _events.Items.Clear();
        foreach (var line in _log.RecentLines(300)) _events.Items.Add(ToItem(line));
        _events.EndUpdate();
    }

    /// 通知点击时调用：切到日志页并选中最后一行（spec 第 6 节）。UI 线程安全。
    public void JumpToLatestLog() => OnUi(() =>
    {
        RefreshLog();
        var index = _tabs.TabPages.IndexOf(_logPage);
        if (index >= 0) _tabs.SelectedIndex = index;
        if (_events.Items.Count == 0) return;
        var last = _events.Items[^1];
        _events.SelectedIndices.Clear();
        _events.SelectedIndices.Add(last.Index);
        last.EnsureVisible();
    });

    private static ListViewItem ToItem(string line)
    {
        var m = Regex.Match(line, @"\[([^\]]+)\]\s+(\w+)\s+(.*)");
        return m.Success
            ? new ListViewItem([m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value])
            : new ListViewItem(["", "", line]);
    }

    // ---------- 导出诊断包 ----------

    /// 两条接缝，只给单测（生产全走默认值）：打包本体与结果呈现。
    /// 呈现这条不换掉就得在测试里弹一个模态框，用例会被钉死在桌面上。
    /// 写成字段而不是属性：WinForms 的分析器会把 Form 上的公共属性当作设计器可序列化成员（WFO1000）。
    internal Func<string, string?>? RunExport;
    internal Action<string, bool>? ExportPresenter;

    /// Task 18：真的打包 —— 读盘、读日志、跑三条只读命令（netsh/ipconfig/route）。
    /// 两条规矩同时成立，缺一不可：
    /// ① 打包不占 UI 线程：枚举目录、读几份日志、等三个子进程（每项上限 5 秒）全在 Task.Run 里，
    ///    UI 线程只管 await 回来之后那个框 —— 与 RunAsync 同一条规矩，同步等就是窗口连托盘一起冻住。
    /// ② 它走 RunAsync 那一把门（也就是与托盘共用的那一把，所以整段没有自己写在途标记）：
    ///    界面上永远只有一条命令在途，导出期间四个动作点不动，反过来也一样；
    ///    连点五下导出不会跑出五份 zip。
    internal Task ExportBundleAsync() => RunAsync("界面·导出诊断包", async _ =>
    {
        var dir = Path.GetDirectoryName(_store.FilePath) ?? "";
        var run = RunExport ?? (t => DiagnosticsBundle.Build(t, _store, _log));
        string? target = null, error;
        try
        {
            (target, error) = await Task.Run(() =>
            {
                var t = DiagnosticsBundle.NewTargetPath(dir);
                return (t, run(t));
            });
        }
        catch (Exception ex)
        {
            // Build 自己已经把异常换成文字了，走到这里只剩"接缝被换坏了"这一种。
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        (ExportPresenter ?? ShowExportResult)(
            error is null ? ExportOkBody(target!) : ExportBadBody(error), error is null);
    });

    private static string ExportOkBody(string target) =>
        "诊断包已生成：\n" + target + "\n\n" +
        "把这个 zip 整个发给维护者就够了：里面是最近的运行日志与自检记录、设置（任何像密码的字段都已打成 ***）、" +
        "WLAN/IP/路由三份网络快照和版本信息。\n" +
        "快照里会带着 MAC 地址、本机机器名与当前 Windows 用户名（排障要用它们核对是哪个会话），" +
        "所以只发给维护者，别贴进群组。\n" +
        "本包不含密码，也不含密码的密文文件 secret.bin（它只留在本机，而且只对当前 Windows 用户可解）。";

    private string ExportBadBody(string error) =>
        "诊断包没生成：" + error + "\n\n兜底做法：把下面这个日志目录整个打包发给维护者：\n" +
        (_log?.LogDirectory ?? Path.GetDirectoryName(_store.FilePath) ?? "（没有日志目录）");

    private void ShowExportResult(string body, bool ok) => MessageBox.Show(this, body, "ZutWifi",
        MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

    private static string AboutText() =>
        "ZutWifi 1.0.0\n\n连接 zut-stu 时在后台完成校园网认证，不打开浏览器。\n" +
        "密码只用 Windows DPAPI 加密保存在本机当前用户下，不会进入日志或诊断包。\n" +
        "换电脑或重装系统后需要重新填一次密码。\n\n" +
        "持续登录失败时：设置页点“测试配置”，或点“导出诊断包”把 zip 发给维护者。\n\n" +
        AppBrand.UnofficialNotice;

    /// 秒表那个计时器不在 `components` 里（这个窗口没有设计器文件），所以得自己还。
    /// 不还会怎样：窗口关掉之后它还在跑，每一拍往一个已 Dispose 的控件上发消息。
    protected override void Dispose(bool disposing)
    {
        if (disposing) _ticker.Dispose();
        base.Dispose(disposing);
    }

    /// 关窗收进托盘；托盘"退出"或设置里关掉该行为时才真退出。
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_realExit && _store.Load().CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ClosedForReal = true;
        base.OnFormClosed(e);
    }

    /// 真退出的那一条路径（托盘菜单"退出"走这里）。UI 线程安全。
    public void ExitForReal() => OnUi(() =>
    {
        _realExit = true;
        Close();
    });

    /// 窗口是否真的关掉了（收进托盘不算）。测试与 Task 17 的收尾判定用。
    public bool ClosedForReal { get; private set; }

    /// 可能从别的线程进来的入口都走这里。
    /// BeginInvoke 而不是 Invoke：调用方可能就是"UI 线程正等着它完成"的那一侧，同步等会自己等自己。
    /// 句柄还没造（单元测试里直接 new 出来的窗口、或窗口已经关了）时 InvokeRequired 恒为 false，就地执行。
    private void OnUi(Action body)
    {
        if (!InvokeRequired) { body(); return; }
        try { BeginInvoke(new Action(body)); }
        // 窗口正在销毁：这一笔状态/这一次跳转已经没人看了，不能让它把后台线程带崩。
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
}
