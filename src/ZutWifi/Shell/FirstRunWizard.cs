using ZutWifi.Config;
using ZutWifi.Diagnostics;
using ZutWifi.Portal;

namespace ZutWifi.Shell;

/// 门户三连测：探测认证状态 → 取内网地址 → 提交登录。向导与设置页共用这一段。
///
/// 三件刻意不做的事：
/// ① 不发注销包 —— 这套动作的用途是"配置阶段就能看到门户反馈"，而注销会真把同学正在用的会话踢下线
///    （需求锁定的是"仅手动注销"）。
/// ② 不自己判 Location —— 全部走 PortalGateway，界面上看到的判定就是状态机用的那一个判据，
///    两处各写一份早晚分叉。
/// ③ 探测到已认证时不再提交登录 —— 重复提交正是门户账号保护最爱抓的那一类。
internal static class PortalTestRun
{
    public static async Task ExecuteAsync(Func<PortalGateway> gateway, SettingsStore store,
        SecretStore secrets, Action<string> line, Action<Exception>? fault = null)
    {
        try
        {
            var s = store.Load();                                   // 调用方刚刚 Save 过，读盘上的那份
            var password = secrets.Get();                           // 密码只从 DPAPI 来，界面上的框永远不是来源
            if (string.IsNullOrWhiteSpace(s.StudentId) || string.IsNullOrWhiteSpace(password))
            {
                line("✋ 学号或密码还没填好并保存，已跳过提交：空密码去登录等于白送一次 RADIUS 失败计数。");
                return;                                             // 一个请求都不发
            }

            var gw = gateway();
            line("① 探测认证状态…");
            var state = await gw.ProbeAsync(CancellationToken.None);
            line($"   门户判定：{state}");
            if (state == AuthState.Authenticated)
            {
                line("   已经在线，不再提交登录包（重复提交会被门户当成异常行为）。");
                return;
            }

            line("② 获取内网地址…");
            var ip = await gw.GetClientIpAsync(CancellationToken.None);
            line("   本机 IP：" + (ip ?? "取不到，登录时将回退网卡地址"));

            line("③ 提交登录…");
            var r = await gw.LoginAsync(new Credential(s.StudentId, password!, s.IspSuffix),
                ip ?? "0.0.0.0", CancellationToken.None);
            line("   " + (r.IsSuccess ? "成功：" : "失败：") + (r.Reason ?? r.RawLocation ?? ""));
            if (r.IsSuccess) line("   门户认了这个账号。外网通不通由主界面的“重新检测”负责，这里不重复探测。");
            else line("   改完学号/密码/运营商后缀可以再点一次；连续失败三次这一轮才会放弃。");
        }
        catch (Exception ex)
        {
            // PortalGateway 把网络故障都吞成判定，所以走到这里的基本是实现 bug 或读盘失败：
            // 界面不许抛（它可能在 UI 线程的 async void 里），但必须留一行能看的字。
            fault?.Invoke(ex);
            line("   测试没跑完：" + ex.Message);
        }
    }
}

/// 首次配置向导：填账号 → 立即真机测一次登录 → 开机自启 → 完成。
/// 界面本体就是那张设置页（同一个控件、同一套校验、同一个"测试配置"通路），
/// 这里只加"四步"的说明和"完成"这个动作 —— 两处各写一份表单迟早分叉。
///
/// "立即测试"让配置阶段就能看到门户反馈，而不是等下次连 WiFi 才发现密码错。
/// 没点"完成"就下次还会再弹（AppContext.NeedsFirstRun 认的是 FirstRunCompleted 那个标记，
/// 不是"窗口有没有出现过"）：半途关掉的同学不该被永久留在没有向导的状态里。
public sealed class FirstRunWizard : Form
{
    private readonly SettingsStore _store;
    private readonly TransactionLog? _log;
    private readonly SettingsPage _page;
    private Func<PortalGateway>? _factory;
    private readonly HttpMessageHandler? _portalHandler;
    private HttpClient? _client;

    public bool Finished { get; private set; }
    public string TestOutput => _page.TestOutputText;
    public string PasswordPlaceholderText => _page.PasswordPlaceholderText;

    /// 测试通路（gatewayFactory）与开机自启（applyAutoStart）都是接缝：
    /// 单测里喂真机回放并换掉注册表写入，绝不在同学机器上动这两样。
    ///
    /// log 是装配那一份（全进程只有一份，见 AppContext 规矩①）。向导不管日志就等于是说：
    /// 同学**这辈子第一次**登录门户——最容易填错、也最需要事后复盘的那一次——什么都不留。
    /// portalHandler 是同一条路上的第二个接缝：换掉 handler 才能测到"向导自己 new 的那个网关
    /// 有没有接上日志"（换掉整个 gatewayFactory 的话，测的就是工厂自己了）。
    public FirstRunWizard(SettingsStore store, SecretStore secrets,
        Func<PortalGateway>? gatewayFactory = null, Func<bool, string?>? applyAutoStart = null,
        TransactionLog? log = null, HttpMessageHandler? portalHandler = null)
    {
        _store = store; _factory = gatewayFactory; _log = log; _portalHandler = portalHandler;
        Text = "ZutWifi 首次配置";
        ClientSize = new Size(680, 600);
        MinimumSize = new Size(600, 460);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = true;
        Font = new Font("Microsoft YaHei UI", 9F);

        var header = new Label
        {
            Dock = DockStyle.Top, Height = 74, Padding = new Padding(18, 12, 18, 6),
            Text = "第一次使用要填三样：学号、校园网密码、运营商后缀。\n" +
                   "填好点“测试配置”会真登录一次并把门户的判定显示出来 —— 现在就能看到对不对，" +
                   "不用等下次连 WiFi。\n密码只用 Windows DPAPI 加密存在本机当前用户下，换电脑要重填。",
        };

        // 同一个接缝往下传：向导里的"测试配置"和设置页里的必须是同一条通路，日志也是同一份。
        _page = new SettingsPage(_store, secrets, _log, applyAutoStart, Gateway);
        _page.Dock = DockStyle.Fill;

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(18, 10, 18, 10),
        };
        var finish = new Button { Text = "完成", Width = 110, Height = 34 };
        finish.Click += (_, _) => { CompleteFirstRun(); DialogResult = DialogResult.OK; Close(); };
        var later = new Button { Text = "以后再说", Width = 110, Height = 34 };
        later.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bar.Controls.Add(finish);
        bar.Controls.Add(later);

        Controls.Add(_page);
        Controls.Add(bar);
        Controls.Add(header);
    }

    /// 测试通路可注入（单测喂真机回放，Task 18 的自检也可以复用这一条）。
    public void UseGateway(Func<PortalGateway> factory) => _factory = factory;

    /// 每次点"测试配置"都新建一个 PortalGateway（它自己无状态），但底层那个 HttpClient 只建一次：
    /// 客户端是要带连接池与句柄的东西，点一次漏一个就是白漏。
    /// 日志与超时用的是装配那一套（AppContext.PortalTimeoutSeconds / 规矩①的那一份 log）：
    /// 向导里点一次"立即测试"，诊断包里就该有对应的那几行。
    private PortalGateway Gateway() => _factory?.Invoke()
        ?? new PortalGateway(_client ??= AppContext.NewNoRedirectClient(
            AppContext.PortalTimeoutSeconds, _portalHandler), _store.Load().PortalHost, _log);

    public void SetStudentId(string v) => _page.SetStudentId(v);
    public void SetPassword(string v) => _page.SetPassword(v);
    public void SetIsp(string v) => _page.SetIsp(v);
    public void SetSsids(string v) => _page.SetSsids(v);
    public void SetAutoStart(bool v) => _page.SetAutoStart(v);
    public void SimulateSave() => _page.SimulateSave();
    public Task SimulateRunTestAsync() => _page.SimulateTestClick();
    public void SimulateFinish() => CompleteFirstRun();

    /// 只写那一个标记，其余字段照原样留着（读-改-写走 SettingsStore.TryUpdate，评审 I4）：
    /// 盘上那份读不出来的时候宁可什么都不写 —— 无条件 Load→Save 会把同学刚存好的
    /// 门户地址、重试上限与 SSID 白名单整份刷成出厂值，而这一刻正是文件被编辑器/网盘占住的时候。
    /// 没写成就不算"完成"：下一次启动向导还要再来，比悄悄留下一个假的完成标记好。
    private void CompleteFirstRun()
    {
        bool wrote;
        string? why;
        try { wrote = _store.TryUpdate(s => s.FirstRunCompleted = true, out why); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { wrote = false; why = ex.Message; }          // 写不下去同样只说一句话，不把向导顶掉
        if (!wrote)
        {
            _page.ShowHint("“完成”没记进设置：" + why);
            return;
        }
        Finished = true;
    }

    /// 窗口被 X 掉（而不是点了完成）时不算跑完：下一次启动还要弹。
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!Finished && e.CloseReason == CloseReason.UserClosing) DialogResult = DialogResult.Cancel;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _client?.Dispose();
        base.Dispose(disposing);
    }
}
