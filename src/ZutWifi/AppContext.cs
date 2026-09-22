using System.Timers;
using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Diagnostics;
using ZutWifi.Notify;
using ZutWifi.Portal;
using ZutWifi.Shell;
using ZutWifi.Wifi;

namespace ZutWifi;

/// 组合根：全进程唯一一处 new 业务对象的地方。
///
/// 三条装配期就得定死的规矩，都写在这一个方法里，别处不再判断：
/// ① 事务日志全进程只有一份。它的写盘守卫是**实例级**的，两份日志就是两条线程往同一个
///    按天滚动的文件里交错追加 —— 那正是这份日志唯一要防的事。所以同一份实例同时给
///    PortalGateway、LoginCoordinator、MainForm、Notifier 的回退记账和设置页的测试通路。
/// ② LoginCoordinator 的 log 是最后一个可选参数，必须传：不传时它那条"事件这一轮没跑完"的
///    catch-and-record 通路什么都不写，一次 DPAPI 或凭据失败就彻底隐形，托盘只是"卡住了"。
/// ③ 两个 HttpClient 都必须禁自动重定向，探测那侧超时 5 秒（探针重试 3 轮，8 秒时一个死网络
///    要 52 秒才报出来）。
/// ④ 装配只负责"接好线"，不负责"开闸"：任何能往界面上回调的事件源（无线通知、60 秒心跳、
///    Toast 点击）都要等窗口的句柄存在之后再开。开闸那一步（AppParts.Arm，internal）
///    只由 Build 里挂在窗口 Shown 上的那一句触发；装配本身拆成 Prepare + Complete 两步，
///    中间那个类型上根本没有开闸的入口（见 PreparedParts）。
/// ⑤ "同时只有一条命令在途"这条规矩只写一次：门（CommandGate）由装配 new 一把，
///    界面那四个按钮、托盘菜单那三条、60 秒定时器那一拍，以及无线源的每一次读取
///    （心跳与 wlanapi 通知回调）打的都是同一把，谁都不许从旁边绕过去，也不许排队。
/// ⑥ 会打到操作系统上的那三件事（通知中心、托盘气泡、开始菜单里那次 AUMID 注册）只有一个来源：
///    传进来的那个 `NotificationSink`。它没有默认值，所以"忘记换替身"是编译错误而不是桌面事故 ——
///    上一版这三条都是可选参数，一次装配路径上的单测就把同学的桌面刷满了"校园网登录失败"。
public static class AppContext
{
    /// 门户单次请求超时。一次登录最坏要跑 4 次提交（首扣 + 2s/5s/15s 退避），
    /// 这个数决定的是"界面最晚多久给出结论"。
    public const int PortalTimeoutSeconds = 15;

    /// 外网探测超时：探针本身重试 3 轮 × 2 个目标，所以这里必须短。
    public const int ProbeTimeoutSeconds = 5;

    /// 60 秒刷一次认证状态与在线时长（只刷新，绝不自动登录）。
    private const int RefreshIntervalMs = 60_000;

    public static string DataDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZutWifi");

    /// 三样缺一样都算没配完：账号、能解出来的密码、以及用户真的点过"完成"。
    /// 只看"窗口有没有弹过"是不够的 —— 半途关掉的人下次启动还得见到向导。
    public static bool NeedsFirstRun(string? dir = null)
    {
        var d = dir ?? DataDir;
        var s = new SettingsStore(d).Load();
        return string.IsNullOrWhiteSpace(s.StudentId)
               || new SecretStore(d).Get() is null
               || !s.FirstRunCompleted;
    }

    /// 装配（两步合成一步的对外入口）：dir / 两个 handler / wifi / clock / log 这些可选参数存在的
    /// 唯一理由是让单测能在不动同学机器的前提下跑通同一条装配路径：dir 不进 %APPDATA%、
    /// 两个 handler 不出网、wifi 不开 wlanapi 句柄、log 用外面已经建好的那一份
    /// （首次向导要在装配之前就拿到它，见 NewLog）。
    ///
    /// `sink` 是唯一**没有默认值**的那个参数，它存在的理由和其它几个相反：其它几个是"能换掉"，
    /// 这一个换不掉就是编译错误。操作系统通知面（通知中心、托盘气泡、开始菜单里那次快捷方式写入）
    /// 只有这一入口，而生产那一份（`NotificationSink.Production()`）全仓只在 `Program.Main` 出现一次。
    /// 上一版这三个出口全是可选参数，"忘记传替身"的代价是同学桌面上真弹一条"校园网登录失败"，
    /// 现在已经发生过一次了。
    ///
    /// 返回时事件通路还是关着的（规矩④）：生产靠窗口 Shown 自己开，
    /// 单测里要么 parts.Arm()（internal，只有单测够得着），要么把窗口真 Show 出来。
    /// 拆成 Prepare + Complete 两步是为了"没接完线的时候开不了闸"这件事不再靠自觉：
    /// 见 PreparedParts，以及 装配第一步的产物上没有开闸入口而开闸本身不对外 那条用例。
    internal static AppParts Build(NotificationSink sink, string? dir = null,
        HttpMessageHandler? portalHandler = null, HttpMessageHandler? probeHandler = null,
        IWifiSource? wifi = null, IClock? clock = null, TransactionLog? log = null) =>
        Prepare(sink, dir, portalHandler, probeHandler, wifi, clock, log).Complete();

    /// 装配第一步：把对象建出来、把线接好，但一个事件源都还没开。
    /// 交回来的 PreparedParts 上**没有**任何"开闸/启动"的入口 —— 这一步与下一步之间
    /// 不存在一个"已经能 Start 了"的中间态（派单 17-2：原来那一格只有一句注释挡着）。
    internal static PreparedParts Prepare(NotificationSink sink, string? dir = null,
        HttpMessageHandler? portalHandler = null, HttpMessageHandler? probeHandler = null,
        IWifiSource? wifi = null, IClock? clock = null, TransactionLog? log = null)
    {
        var root = dir ?? DataDir;
        Directory.CreateDirectory(root);
        var store = new SettingsStore(root);
        var secrets = new SecretStore(root);
        // 读没读到要记一笔（评审 I4 的同一处毛病）：Load() 在读不出来时给的是默认值，
        // 后面那句"只为一个诊断标记"的回写若照它落盘，就把同学整份设置刷成出厂状态了。
        var diskReadable = store.TryLoad(out var settings, out _);
        var now = clock ?? new SystemClock();

        // ── 规矩①：一份日志。传进来就用那一份，没传才在这里建 —— 建了也还是只有一份。 ──
        log ??= new TransactionLog(now, System.IO.Path.Combine(root, "logs"));

        // ── 规矩③：两个客户端都不跟跳转 ──
        var (portalHttp, portalOwned) = NewNoRedirect(PortalTimeoutSeconds, portalHandler);
        var (probeHttp, probeOwned) = NewNoRedirect(ProbeTimeoutSeconds, probeHandler);
        // ── 规矩⑤：一把门，两个界面共用 ──
        // 那面"有命令在途"的旗原本住在 MainForm 里，只管得住窗口按钮，托盘菜单从旁边绕了过去。
        // 现在它就是这个 CommandGate：MainForm 与 TrayApp 各拿同一把，谁都不再自己立旗。
        // 无线源也拿同一把（下面 new WifiSentinel(gate)）：60 秒那一拍与 wlanapi 通知回调那一拍
        // 撞上一条在途登录时同样**直接放弃这一轮**，不排到协调器那把非重入锁后面。
        // 它必须排在无线源之前建：门是"谁和谁共用"这件事的唯一来源，晚建一步就有人拿到 null。
        var gate = new CommandGate(log);

        var portal = new PortalGateway(portalHttp, settings.PortalHost, log);
        var connectivity = new HttpConnectivityProbe(probeHttp);
        var source = wifi ?? new WifiSentinel(gate);

        // ── 通知：三条出口全部来自 sink（真的那一份只有 Program 递得进来）──
        var notifier = new Notifier(AumidRegistrar.Aumid, sink);
        notifier.Faulted = reason => log.Write(new TransactionRecord("Notify", "TOAST",
            "notice://toast", null, null, "通知降级成气泡，这一条说了为什么", reason));
        // Ensure() 返回 null = 成功；返回一句原因 = 开始菜单快捷方式没写成，而通知中心对未注册的
        // AUMID 的表现是"Show 正常返回、Toast 永远不出现"，等异常是等不到的，只能在这里就判死。
        // 这一句现在是问 sink 而不是问 AumidRegistrar：默认值那种"不传就用真的"的写法
        // 正是上一轮把同学的桌面刷满气泡的地方。
        var aumidError = sink.EnsureAumid();
        var fallbackUsed = aumidError is not null;
        if (settings.NotifierFallbackUsed != fallbackUsed)
        {
            settings.NotifierFallbackUsed = fallbackUsed;   // 内存里那份一定要对（界面与诊断包读它）
            TryPersistFlag(store, settings, diskReadable);
        }
        if (aumidError is not null)
        {
            notifier.BalloonOnly = true;
            // 原因本身要留在磁盘上：真机排障时"为什么没进通知中心"只有这一句有价值。
            log.Write(new TransactionRecord("Notify", "AUMID", "shell://start-menu", null, null,
                "通知中心不可用（AUMID 注册没成功），本次运行只用托盘气泡", aumidError));
        }

        // ── 规矩②：日志参数必须传，而且要放在最后那个可选位上 ──
        var coord = new LoginCoordinator(portal, source,
            () => new Credential(settings.StudentId, secrets.Get() ?? "", settings.IspSuffix),
            settings, now, connectivity, notify: notifier.Notify, log: log);

        var form = new MainForm(coord, store, secrets, log, gate);
        var tray = new TrayApp(form, coord, store, sink, gate);
        // 气泡的真落点在这里、也只有在这里接上：NotifyIcon 归托盘所有，所以非得建完托盘才能绑。
        // 记账器那份 sink 没有 BindShellBalloon 这一格（它不是 Production() 造出来的），
        // 于是这一句在单测里是空的 —— 托盘的真图标从此没有任何一条路径接得到通知器。
        sink.BindShellBalloon?.Invoke(tray.PresentOnTrayIcon);

        // 首次向导还没跑完就不要开自动登录那条通路：那时凭据是空的，一开自动通路就是拿
        // 空学号空密码去敲门户，一轮退避打完 = 白送四次失败提交，正是会把账号打进 RADIUS
        // 锁定的那一种。手动按钮仍然可用（那一下是人自己按的，看得见结果）。
        var autoLoginAllowed = !NeedsFirstRun(root);

        var ticker = new System.Timers.Timer(RefreshIntervalMs);
        var parts = new AppParts(form, tray, coord, ticker, log, notifier, source,
            portalHttp, probeHttp, portalOwned, probeOwned, store, autoLoginAllowed, gate);

        // 托盘"退出"不再掐进程：它把收尾交回这里，Program 的 finally 才有机会跑（停表、放互斥量）。
        tray.ExitRequested += parts.Shutdown;
        // 日志写不下去时状态一有变化就说一次（只说一次）：诊断是这个程序唯一的支援手段。
        // 它挂在协调器的事件上，而协调器的事件要开闸之后才可能响，所以挂在这里不会早碰控件。
        coord.StatusChanged += _ => parts.CheckLogHealth();

        // ── 规矩⑤的另一半：60 秒那一拍过的就是同一把门 ──
        // 上一版这里直接 `coord.RefreshAsync(CancellationToken.None)`，从旁边绕过了闸：
        // 一次门户刷新于是可以正跑在一条在途登录中间（探测与取时长的包夹在退避的缝里出去），
        // 而界面那侧判"有没有人在跑"的门完全看不见它。现在门被占着就什么都不发（RunAsync 返回 false，
        // 那一笔由门自己写进诊断包），下一拍再来 —— 绝不排到 22 秒之后对着可能已经变了的会话敲门户。
        // 异常照原样抛到这里：这一次刷新失败了要说得出，不能只让门"亮了一下"。
        ticker.Elapsed += async (_, _) =>
        {
            parts.CheckLogHealth();
            try
            {
                await gate.RunAsync("定时刷新", ct => coord.RefreshAsync(ct));
            }
            catch (Exception ex)
            {
                log.Write(new TransactionRecord("Timer", "REFRESH", "timer://60s", null, null,
                    "定时刷新没跑完", $"{ex.GetType().Name}: {ex.Message}"));
            }
        };

        // ── 规矩④：到这里一个事件源都还没开，而且这里也开不了（类型上没有那个入口）──
        // 生产路径上 Build 之后还有一句 Application.Run(form)，控件的句柄是那一步才造出来的；
        // 而 OnUi 那三道闸在"句柄还不存在"时一律退回就地执行（MainForm/TrayApp/AppParts 都这么写）。
        // 两边合起来的结果就是：wlanapi 通知线程或线程池上的一条事件，会在同学的界面上直接改控件。
        // Release 下它不报错，只是随机把界面弄坏 —— 所以开闸这件事必须等句柄真的在了。
        // 时机就用窗口自己的 Shown：那时 Load 与句柄都已经过去，窗口也真的可见（托盘里启动同样会走到）。
        // 那一句订阅在 PreparedParts.Complete() 里装：接线与"唯一那一次开闸挂载"之间不再有出口。
        return new PreparedParts(parts);
    }

    /// 装配第一步交回来的东西：全都建好、全都接好，唯独**没有**开闸的入口。
    ///
    /// 这一格原来靠一句注释与一个 `Started` 布尔挡着：`parts.Start()` 是 public，
    /// 在 `new AppParts(...)` 之后、ticker 处理器与 Shown 挂载之前那七八句还没跑完的时候
    /// 就调得动 —— 谁在中间插一句（或者像上一版那样从别处拿 parts 去开），开闸就落在
    /// 一个接了一半的窗体上，那是评审 C1 那条通路在装配这一层的翻版。
    /// 现在中间态的类型上没这个方法，误用从"读注释自觉"变成"编译不过"。
    internal sealed class PreparedParts(AppParts parts)
    {
        private int _hooked;

        /// 第二步（也是唯一一步）：把开闸挂到窗口的 Shown 上，并把装配好的那一外交给调用方。
        /// 幂等：重复调用不会多挂一次订阅（多挂一次就是多跑一次 Arm，而 Arm 自己也是幂等的）。
        public AppParts Complete()
        {
            if (Interlocked.Exchange(ref _hooked, 1) == 0)
                parts.Form.Shown += (_, _) => parts.Arm();
            return parts;
        }
    }

    /// 一个绝不自动跟跳转的 HttpClient。跳转必须原样留在响应里：
    /// 门户那侧判据就住在 Location 头（跟了就读不到 /3.htm），探测那侧跟下去会把门户劫持页
    /// 读成 200 = "已上网"。返回那个自建的 handler，是为了让装配与单测都能核对这个开关；
    /// 测试换成自己的 handler 时返回 null（替身本来就不可能跟跳转）。
    internal static (HttpClient client, HttpClientHandler? owned) NewNoRedirect(
        int timeoutSeconds, HttpMessageHandler? substitute = null)
    {
        if (substitute is not null)
            return (new HttpClient(substitute) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) }, null);
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return (new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) }, handler);
    }

    /// 给界面外的那个通路（设置页的"测试配置"、向导）用的同一种客户端。
    public static HttpClient NewNoRedirectClient(int timeoutSeconds) => NewNoRedirect(timeoutSeconds).client;

    /// 同上，但换掉底下的 handler：单测要在**不换掉网关**的前提下测这条通路
    /// （网关自己 new 出来的客户端才会接到那份日志，注入一个工厂就什么也测不到了）。
    internal static HttpClient NewNoRedirectClient(int timeoutSeconds, HttpMessageHandler? substitute) =>
        NewNoRedirect(timeoutSeconds, substitute).client;

    /// 事务日志的唯一构造点（规矩①）。为什么单独有一个方法：日志要在**装配之前**就存在 ——
    /// 首次向导跑在 Build 之前（不然无线源一启动就会先自动登一次，紧接着向导又登一次，
    /// 同一次连接给门户交两份凭据），而它那一趟恰恰最需要留记录。
    /// Program 在这里拿一份，交给向导，再交给 Build：全进程仍然是同一份。
    public static TransactionLog NewLog(string? dir = null, IClock? clock = null) =>
        new(clock ?? new SystemClock(), System.IO.Path.Combine(dir ?? DataDir, "logs"));

    /// 只在标记真的变了、而且磁盘上那份**这次真的读出来了**时才回写。
    /// 为什么不能无条件 Save：SettingsStore.Load 在文件被别的进程独占时（编辑器开着、网盘正在同步）
    /// 返回的是默认值，那一次 Save 就把同学的学号与设置抹成了出厂状态 —— 而这里只是为一个诊断标记。
    /// 首次运行也没有写的必要：那会儿是向导在建档，抢它的顺序只会多一次可丢的写。
    /// 写失败也不许挡住启动：这一个标记的全部作用就是让诊断包知道通知走了哪条路。
    private static void TryPersistFlag(SettingsStore store, Settings settings, bool diskReadable)
    {
        if (!diskReadable || !System.IO.File.Exists(store.FilePath)) return;
        try { store.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 最多是下一次启动重新判一遍通知通道，界面上这一次运行的 BalloonOnly 已经定了。
        }
    }

    /// 开机自启：把设置里的意图同步进 HKCU\...\Run（exe 换过位置也要能修好）。
    /// 返回值在这里没人接：启动阶段弹框会把人钉住，而写不进去只影响"下次开机不自启"这一件事。
    public static void AutoStartIfNeeded(SettingsStore store) => StartupRegistry.Ensure(store.Load().AutoStart);
}

/// 装配出来的那一套对象，加上"谁来收尾"。
/// 名字与次序按 Task 17 的契约来：`var (form, tray, coord, ticker) = AppContext.Build(sink, ...);`
/// 照样成立，但日志、通知器、无线源和两个客户端也一并留在这里 —— 退出时不把它们放干净，
/// 下一次启动就会被自己的残骸判成"已经在运行"。
public sealed class AppParts(
    MainForm form, TrayApp tray, LoginCoordinator coord, System.Timers.Timer ticker,
    TransactionLog log, Notifier notifier, IWifiSource wifi,
    HttpClient portalHttp, HttpClient probeHttp,
    HttpClientHandler? portalHandler, HttpClientHandler? probeHandler, SettingsStore store,
    bool autoLoginAllowed, CommandGate gate) : IDisposable
{
    public MainForm Form { get; } = form;
    public TrayApp Tray { get; } = tray;
    public LoginCoordinator Coord { get; } = coord;
    public System.Timers.Timer Ticker { get; } = ticker;
    public TransactionLog Log { get; } = log;
    public Notifier Notifier { get; } = notifier;
    public IWifiSource Wifi { get; } = wifi;
    public SettingsStore Store { get; } = store;

    /// 界面上那四个按钮与托盘上那三条菜单共用的那一把命令门（规矩⑤）。
    public CommandGate Gate { get; } = gate;

    internal HttpClient PortalHttp { get; } = portalHttp;
    internal HttpClient ProbeHttp { get; } = probeHttp;
    internal HttpClientHandler? PortalHandler { get; } = portalHandler;
    internal HttpClientHandler? ProbeHandler { get; } = probeHandler;

    /// 诊断不可用的提示就落在托盘气泡上（`Tray.ShowBalloon` → 装配注入的那份 sink）：
    /// 全进程只有那一个出口，所以这里不再单独立一条"测试可替换"的委托 —— 该替换的在 sink 里。
    internal bool DiagnosticsWarned { get; private set; }
    public bool ShutDown { get; private set; }

    /// 事件通路开没开。Build 之后它是 false —— 那一段正是评审 C1 抓的位置。
    public bool Started { get; private set; }

    /// 让 `var (form, tray, coord, ticker) = AppContext.Build(sink, ...);` 这种写法继续成立。
    public void Deconstruct(out MainForm form, out TrayApp tray, out LoginCoordinator coord,
        out System.Timers.Timer ticker)
    {
        form = Form; tray = Tray; coord = Coord; ticker = Ticker;
    }

    /// 开事件通路（全类唯一一处能把 Started 立起来的地方）：60 秒心跳、协调器订阅无线源、
    /// wlanapi 通知、Toast 点击回调。
    ///
    /// internal 是有意的：能调它的只有 AppContext 自己（挂在窗口 Shown 上的那一句，
    /// 见 PreparedParts.Complete）与单测。Program.cs 与任何别处都拿不到这个入口 ——
    /// 上一版它是 public 的 Start()，于是"接线还没跑完就先开闸"这件事随时做得出来。
    ///
    /// 为什么单独有这一步、而且只在窗口的句柄造出来之后才跑（生产路径上是 form.Shown 触发）：
    /// 这三条通路的回调全部落在别的线程上
    /// （wlanapi 通知线程、Timer 的线程池线程、WinRT 线程池），而它们最终要做的事是改控件。
    /// 三道 OnUi 闸（MainForm / TrayApp / AppParts）在"句柄还不存在"时都是**就地执行** ——
    /// 那不是 bug，是单元测试里 new 出来的窗口唯一可行的走法；但它意味着：只要事件在
    /// Application.Run 造出句柄之前就进来，控件就是在被工作线程直接改。
    /// Release 下这种修改不报错，只随机把界面弄坏，并且几乎不可能复现。
    /// 幂等：Shown 只会打一次，收尾之后再来也不该把心跳重新点起来。
    internal void Arm()
    {
        if (Started || ShutDown) return;
        Started = true;
        // 先订阅，再让无线源开始报：反了会丢掉第一次连接事件。
        // 这里读属性 Coord 而不是主构造的参数 coord：参数被成员体引用一次就会被闭包捕获进实例状态，
        // 而它同时又已经在初始化 Coord —— 同一条规矩于是有了两个来源（CS9124 报的就是这个）。
        // 属性是只读的、装的正是那一份实例，所以这一改动只是把"第二个来源"去掉，行为完全一致。
        if (autoLoginAllowed) Coord.Start();
        else Log.Write(new TransactionRecord("App", "START", "app://first-run", null, null,
            "首次向导还没跑完，本次运行不自动登录（托盘与界面上的按钮照常用）", null));
        (Wifi as WifiSentinel)?.Start();          // 测试注入的替身没有心跳可开
        Ticker.Enabled = true;
        // Activated 由 WinRT 线程池回调 → 必须过 ShowMainWindow 那道封送闸再碰控件。
        Notifier.Activated = _ => ShowMainWindow();
    }

    /// Toast 被点：切到日志页并选中最后一行。
    /// 这里必须自己封送到 UI 线程 —— 回调来自 WinRT 线程池，而控件有线程归属：
    /// Release 下直接碰不会报错，只会随机把界面弄坏。BeginInvoke 而不是 Invoke：
    /// 调它那一侧不该被 UI 线程倒过来堵住（真机那侧可能正握着协调器那把锁）。
    public void ShowMainWindow() => OnUi(() =>
    {
        Tray.Show();
        Form.JumpToLatestLog();
    });

    /// 日志写坏过一次就说一句"诊断不可用"，整个进程只说一次。
    /// 不说的话，"日志目录是只读的"这件事在界面上完全隐形，而这个程序的支援能力全押在那份日志上。
    internal void CheckLogHealth()
    {
        if (DiagnosticsWarned || Log.WriteFailures == 0) return;
        DiagnosticsWarned = true;
        Tray.ShowBalloon("诊断日志写不进去",
            "日志目录可能只读或被占用：" + Log.LogDirectory +
            "\n程序还能用，但出问题时无从排查。原因：" + (Log.FirstWriteFailure ?? "未知"));
    }

    /// 退出收尾：停 60 秒心跳、释放无线源（wlanapi 句柄与它的定时器）、把图标从托盘摘掉。
    /// 幂等 —— 托盘的 ExitRequested 与 Program 的 finally 都会打到这里。
    public void Shutdown()
    {
        if (ShutDown) return;
        ShutDown = true;
        Ticker.Stop();
        (Wifi as IDisposable)?.Dispose();       // WifiSentinel.Dispose 会等在途心跳收尾
    }

    public void Dispose()
    {
        Shutdown();
        Ticker.Dispose();
        Tray.Dispose();
        Notifier.Activated = null;              // 窗口没了之后 WinRT 那边不该再有地方可以投递
        PortalHttp.Dispose();
        ProbeHttp.Dispose();
        PortalHandler?.Dispose();
        ProbeHandler?.Dispose();
        Form.Dispose();
    }

    private void OnUi(Action body)
    {
        if (!Form.IsHandleCreated || !Form.InvokeRequired) { body(); return; }
        try { Form.BeginInvoke(new Action(body)); }
        // 窗口正在销毁：这一次跳转已经没人看了，绝不能把 WinRT 的回调线程带崩。
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
}

/// 一条命令的门：同一时刻只允许一条界面命令在途，第二条**直接拒**，不排队。
///
/// 这就是 MainForm 里那个 volatile _busy 的同一套规矩，只是搬到了两个界面都够得着的地方：
/// 界面上的四个按钮和托盘上的三条菜单打的都是这一把（装配 new 一次，见 AppContext.Build 的规矩⑤）。
/// 为什么"排队"在这里比"拒绝"更坏：LoginCoordinator 里面那把 SemaphoreSlim 是排队的，
/// 一条带退避的登录能握着它约 22 秒（2s+5s+15s 再加请求本身），于是排进来的那条
/// ——比如[注销]——会在 22 秒之后对着一个可能已经不是刚才那个的会话真的发出注销包。
/// 同学要的是"我这一下没生效"，不是"我这一下晚 22 秒生效"。
///
/// 用 Interlocked 而不是再 new 一把 SemaphoreSlim：这条门要的就是"有没有人在跑"这一个布尔，
/// 计数与等待是协调器那一层的活；在这里再放一把锁就等于把同一条规矩写第二遍（还多一处死锁点）。
/// 放在这个文件里而不是单独一个类文件：它的全部意义就是"这两个界面共用同一把"，
/// 而"谁和谁共用"是装配点说了算的地方。
public sealed class CommandGate(TransactionLog? log = null)
{
    private int _inFlight;

    public bool IsBusy => Volatile.Read(ref _inFlight) != 0;

    /// 门一开一合各通知一次，参数是"这一次该按哪个在途状态重画"。它存在的唯一理由是让
    /// "另一个界面发起的命令"在界面上看得见：托盘那三条按下时，窗口里的四个按钮也必须立刻变死
    /// （评审 C2 说的那一半缺口）。
    /// 订阅方（MainForm）自己负责封送回 UI 线程 —— 这条回调可能从任何线程上打过来，
    /// 所以要把"画成什么"当参数传过去，而不是让它回头再读一次 IsBusy（见 finally 里的次序）。
    public event Action<bool>? Changed;

    /// true = 这一条真的跑了；false = 门被占着，这一条被拒（什么都没发出去）。
    /// 异常照原样抛：界面上那两个 catch（MainForm.RunAsync / TrayApp.RunMenuAsync）各有自己的说法，
    /// 这里吞一下就把"命令失败了"说成了"命令被拒了"。
    /// ct 原样递给命令：被拒的那一条根本没跑到那儿，所以取消只在"还没拿到门"之前有意义。
    public async Task<bool> RunAsync(string from, Func<CancellationToken, Task> command,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            // 拒了这件事要在诊断包里看得见：用户视角是"我点了没反应"，
            // 没有这一行的话排查的人只能猜。
            log?.Write(new TransactionRecord("UI", "BUSY", $"click://{from}", null, null,
                "已经有一条命令在途，这一下被拒了（没有排队，什么都没发给门户）", null));
            return false;
        }

        Changed?.Invoke(true);
        try { await command(ct); return true; }
        finally
        {
            // 次序是要害：先喊"画成不在途"，再把门落下。那一笔重画可能被排到 UI 线程上做，
            // 反过来就先让外面（测试、装配）在界面还没恢复的时候看见"门开了"。
            Changed?.Invoke(false);
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }
}
