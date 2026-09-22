using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Portal;
using ZutWifi.Wifi;

namespace ZutWifi.Diagnostics;

/// 命令行自检通道（`ZutWifi.exe --selftest [--with-logout]`）：把 Task 13/14 那两轮真机验证
/// 固化成一条同学在自己机器上就能敲的命令，每一步同时进控制台和
/// `%APPDATA%\ZutWifi\logs\selftest-<时间戳>.log`（后者由“导出诊断包”一起打进 zip）。
///
/// 五条约束，每一条都对应一种“在别人机器上失效”的样子：
/// ① 每行先落盘再上屏，落盘失败要被明确说出来（见 ⑩）：静默少写的自检日志比没有日志更难查。
///    写控制台这一侧本身会抛（从资源管理器双击进来时根本没有控制台），那一侧同样只记账、不崩。
/// ② 不许挂死：每一次门户/外网调用都过一层 WithBudgetAsync（单次默认 15 秒，整轮 120 秒封顶）。
///    超时是一行结论，不是一次等待。
/// ③ 不许冒充通过：三档退出码见下面，0 只在整轮跑完且没有任何问题时给出。
/// ④ 探测客户端与组合根共用 AppContext.NewNoRedirect（禁自动跳转 + 探测侧 5 秒），
///    而且**只有一个构造点**（NewPortalClient / NewProbeClient 两条命名接缝）：换成内联的
///    new HttpClient() 会让这一轮的"通"读成假绿 —— 认证前那张门户劫持页回的就是 200。
///    ⑨ 那一行把当下这一个客户端的超时与跳转开关印进日志，另有一条用例在源码上钉"不许有第二个构造点"。
/// ⑤ 不该提交的包绝不提交：凭据不全、或不在白名单网络上时 ⑦ 直接跳过并把原因写进行里 ——
///    空密码去敲门户是给 RADIUS 失败计数器添一笔，陌生网络上交账号是漏凭据。
///
/// 判定只看门户当场返回的 Location 与内网 9002，互联网只作旁证：外网这根线拔了也能跑完并落盘。
///
/// 文件分成两半：这一份是正常通路（①–⑨ 依次跑），`SelfTestSession.cs` 是记账台与超时闸门
/// （"这一轮跑不动了怎么办"那一侧）。Global Constraints 把 250 行当作该拆的信号。
public static partial class SelfTest
{
    /// 0 = 整轮跑完且没有任何问题。
    public const int PassExitCode = 0;

    /// 1 = 跑完了，但发现了问题（问题清单在结论行里，逐条都在日志里）。
    public const int ProblemsExitCode = 1;

    /// 78 = 这一轮没测成：自检自身出异常、整轮超时、或自检日志有一行没落下来。
    /// 沿用占位版本从 sysexits 借来的 EX_CONFIG 那一档：占位时它的意思是“还没实现”，
    /// 现在是“这一轮的结论不可信”——名字换了，语义没换：它永远不等于“通过”，
    /// 而“测过了但有问题”仍然是 1，发布脚本与人都能把这三档分开看。
    public const int IncompleteExitCode = 78;

    internal const int DefaultCallBudgetMs = AppContext.PortalTimeoutSeconds * 1000;
    internal const int DefaultOverallBudgetMs = 120_000;

    /// 到点之后再多等的这点时间：只给“肯听取消信号”的调用一个自己收尾的机会。
    /// 真正的上限靠 WhenAny 放弃等待 —— 见 WithBudgetAsync。
    internal const int GraceMs = 1_000;

    /// 自检用到的全部外部依赖。生产只走 RunAsync(bool)（这个记录全是默认值）；
    /// 单测逐样替换 —— 与 AppContext.Build 的那串可选参数同一套思路。
    internal sealed record Harness(
        string Dir, IClock Clock, IWifiSource? Wifi,
        HttpMessageHandler? PortalHandler, HttpMessageHandler? ProbeHandler,
        Action<string>? Emit = null,
        int CallBudgetMs = DefaultCallBudgetMs,
        int OverallBudgetMs = DefaultOverallBudgetMs)
    {
        /// 生产那一份依赖，全类只在这里点一次名：真数据目录、真系统时钟、真无线源、
        /// 两个真客户端（处理器给 null ⇒ 走 NewPortalClient/NewProbeClient 那两条命名接缝）、
        /// 以及 Emit=null ⇒ 上屏走 Session.ConsoleLine → Console.Out 那条真通路。
        /// 单独成一个方法有两个用处：用例可以逐项读一遍（换掉其中任何一件都得在断言里看得见），
        /// 端到端用例还能从它出发只 `with` 掉"离线必须换"的那三样，剩下的一整套走真机那一条。
        internal static Harness Production() =>
            new(AppContext.DataDir, new SystemClock(), null, null, null);
    }

    /// Program.Main 里 `--selftest` 那一句调的就是这一个：纯转发，退出码由这一轮的收尾算出来。
    public static Task<int> RunAsync(bool withLogout) => RunAsync(withLogout, Harness.Production());

    internal static async Task<int> RunAsync(bool withLogout, Harness h)
    {
        using var s = new Session(h);
        try
        {
            await RunStepsAsync(withLogout, h, s);
        }
        catch (Exception ex)
        {
            // 到这一步只剩一条通路：把“这一轮没跑完”记下来，然后按 78 退出。
            // 抛回 Program.Main 等于连自检都跑不起来（占位版本第一条就钉过这件事）。
            s.Incomplete = true;
            try { s.Line($"自检自身异常（这一轮没跑完）：{ex.GetType().Name} {ex.Message}"); }
            catch (Exception) { /* 连记账都记不上，只剩退出码 */ }
        }
        try { return s.Finish(); } catch (Exception) { return IncompleteExitCode; }
    }

    // 两个 HTTP 客户端的构造点、以及⑨ 那道现场闸门：见 SelfTestClients.cs（全目录唯一的客户端构造点）。

    // ---------- 步骤本体 ----------

    private static async Task RunStepsAsync(bool withLogout, Harness h, Session s)
    {
        var store = new SettingsStore(h.Dir);
        var secrets = new SecretStore(h.Dir);
        var version = typeof(SelfTest).Assembly.GetName().Version?.ToString() ?? "?";
        s.Line($"==== ZutWifi 自检 ==== 版本={version} 系统={RuntimeInformation.OSDescription} " +
               $"机器名={Environment.MachineName} 用户={Environment.UserName}");
        s.Line($"数据目录={h.Dir} 单次上限={h.CallBudgetMs}ms 整轮上限={h.OverallBudgetMs / 1000}s");

        // ── ① 设置 ──
        var hasFile = File.Exists(store.FilePath);
        var set = store.Load();
        s.Line($"① 设置：settings.json 在={hasFile} 学号长度={set.StudentId.Length} 首次向导完成={set.FirstRunCompleted} " +
               $"门户={set.PortalHost} 白名单=[{string.Join(",", set.SsidWhitelist)}] 运营商后缀={set.IspSuffix} 重试上限={set.MaxRetries}");
        if (!hasFile) s.Problem("没有 settings.json：这台机器还没配置过（先把程序开起来走完首次向导）");
        if (string.IsNullOrWhiteSpace(set.StudentId)) s.Problem("学号是空的：设置页里填好并保存");
        if (hasFile && !set.FirstRunCompleted)
            s.Problem("首次向导没点过“完成”：自动登录那条通路是关着的（托盘与界面上的手动按钮照常可用）");

        // ── ② 密码（DPAPI）──
        var secretPath = Path.Combine(h.Dir, "secret.bin");
        var secretExists = File.Exists(secretPath);
        string? password = null;
        try { password = secrets.Get(); }
        catch (Exception ex) { s.Problem($"读密码时抛了（DPAPI 之外的故障）：{ex.GetType().Name} {ex.Message}"); }
        var hasPassword = !string.IsNullOrEmpty(password);
        s.Line($"② 密码：secret.bin 在={secretExists} 当前用户可解出={hasPassword} 长度={(password?.Length ?? 0)}" +
               "（密文只对这台机器上的当前 Windows 用户有效；密码本身只报长度，不进日志也不进诊断包）");
        if (!hasPassword) s.Problem(secretExists
            ? "密码文件在，但当前用户解不出来：换过 Windows 账户或重装过系统，去设置页重填一次"
            : "还没有保存过密码：设置页里填一次并保存");

        // ── ③ 无线接口 ──
        // 注入的假源没有心跳可开、也没有句柄要还；真的那一个必须 Start 才读得到当前值。
        var ownSentinel = h.Wifi is null;
        var source = h.Wifi ?? new WifiSentinel();
        try
        {
            (source as WifiSentinel)?.Start();
            var ap = source.Current;
            var hit = SsidMatcher.IsCampus(ap?.Ssid, set.SsidWhitelist);
            s.Line($"③ 无线接口：SSID=[{ap?.Ssid ?? "-"}] IP=[{ap?.Ipv4 ?? "-"}] MAC=[{ap?.MacNoSeparator ?? "-"}] 白名单命中={hit}");
            if (source is WifiSentinel ws && ws.ReadNativeDetail() is { } detail)
                s.Line($"   wlanapi 明细：空口 SSID=[{detail.Ssid}] 配置文件名=[{detail.ProfileName}] 接口 GUID={detail.InterfaceId}" +
                       "（这两个字段不一样，是常见的困惑来源）");
            if (ap is null) s.Problem("读不到无线接口：没连上任何 WiFi，或 wlanapi 通路不可用（驱动 / WLAN AutoConfig 服务）");
            else if (!hit) s.Problem($"当前 SSID=[{ap.Ssid}] 不在白名单里：不会向陌生网络提交账号（设置页里核对白名单）");

            await PortalStepsAsync(withLogout, h, s, set, password, ap, hit);
        }
        finally
        {
            if (ownSentinel && source is IDisposable d)
            {
                try { d.Dispose(); } catch (Exception) { /* 收尾失败不改结论 */ }
            }
        }
    }

    /// ④–⑨：门户这一段。每一次调用都带上限，任何一步取不到都只是少一个证据，不是崩。
    private static async Task PortalStepsAsync(bool withLogout, Harness h, Session s, Settings set,
        string? password, AccessPoint? ap, bool whitelisted)
    {
        var (portalHttp, portalOwned) = NewPortalClient(h.PortalHandler);
        var (probeHttp, probeOwned) = NewProbeClient(h.ProbeHandler);
        try
        {
            var gw = new PortalGateway(portalHttp, set.PortalHost, s.AppLog);

            // ── ④ 探测 ──
            var state = await s.Budgeted("④ 认证状态探测", ct => gw.ProbeAsync(ct), AuthState.Unknown);
            s.Line($"④ 认证状态（GET http://{set.PortalHost}:9002/0）：{state}" +
                   " —— Unauthenticated=门户在拦（正常，下面去认证），Authenticated=已经在线，Unknown=门户不通或响应不认");

            var credentialReady = !string.IsNullOrWhiteSpace(set.StudentId) && !string.IsNullOrEmpty(password);
            var onCampus = ap is not null && whitelisted;
            var alreadyAuthenticated = state == AuthState.Authenticated;

            // ── ⑤ 注销（只在 --with-logout 时；这是唯一会断网的步骤）──
            var logoutOk = false;
            if (!withLogout) s.Line("⑤ 注销：本轮没要求（要测这一支加 --with-logout；它真会把当前会话踢下线）");
            else if (!onCampus) s.Line("⑤ 注销：跳过——没有可注销的校园网会话（原因见 ③）");
            else
            {
                var lo = await s.Budgeted("⑤ 注销", ct => gw.LogoutAsync(ap!.MacNoSeparator, ct),
                    PortalResult.Transport("超时"));
                logoutOk = lo.IsSuccess;
                s.Line($"⑤ 注销：{lo.Outcome} —— {lo.Reason ?? lo.RawLocation ?? "门户没给原因"}");
                if (!logoutOk) s.Problem("注销没成功：" + (lo.Reason ?? lo.RawLocation ?? "无原因"));
                else await s.WaitAsync(TimeSpan.FromSeconds(3), "注销成功，等 3 秒让 AC 侧把会话清掉再登录");
            }

            // 已认证且这次没成功注销 → 不能再交一份凭据（重复提交是门户账号保护最爱抓的那一类）。
            var skipBecauseAuthenticated = alreadyAuthenticated && !(withLogout && logoutOk);
            var attemptLogin = credentialReady && onCampus && !skipBecauseAuthenticated;

            // ── ⑥ 门户侧本机 IP ──
            string? ip = null;
            if (!attemptLogin) s.Line("⑥ 门户侧本机 IP：跳过（⑦ 不会提交登录，这一步只是多敲一次门户页面）");
            else
            {
                ip = await s.Budgeted("⑥ 取门户侧 IP", ct => gw.GetClientIpAsync(ct), null);
                s.Line($"⑥ 门户侧本机 IP：{ip ?? "取不到（登录时回退网卡地址）"}（网卡 IP：{ap?.Ipv4 ?? "-"}）");
            }

            // ── ⑦ 登录 ──
            var authedByLogin = false;
            if (!credentialReady) s.Line("⑦ 登录：跳过——凭据不全（见 ①②）。空学号空密码去敲门户是白送一次 RADIUS 失败计数");
            else if (!onCampus) s.Line("⑦ 登录：跳过——不在白名单网络上（见 ③），不把账号交给陌生网络");
            else if (skipBecauseAuthenticated) s.Line("⑦ 登录：跳过——④ 已判定认证通过而这次没有成功注销；重复提交会被门户当成异常行为");
            else
            {
                var r = await s.Budgeted("⑦ 登录",
                    ct => gw.LoginAsync(new Credential(set.StudentId, password!, set.IspSuffix),
                        ip ?? ap!.Ipv4, ct),
                    PortalResult.Transport("超时"));
                authedByLogin = r.IsSuccess;
                s.Line($"⑦ 登录：{r.Outcome} —— {r.Reason ?? r.RawLocation ?? "无原因"}" +
                       (r.ErrorCode is null ? "" : $"（门户错误码 {r.ErrorCode}）"));
                if (!r.IsSuccess) s.Problem($"门户没认这次登录（{r.Outcome}）：{r.Reason ?? r.RawLocation ?? "无原因"}");
            }

            // ── ⑧ 复检（这才是判定，⑦ 只是门户当场说了什么）──
            var again = await s.Budgeted("⑧ 认证状态复检", ct => gw.ProbeAsync(ct), AuthState.Unknown);
            var secs = await s.Budgeted("⑧ 在线时长", ct => gw.GetOnlineSecondsAsync(ct), null);
            s.Line($"⑧ 认证状态复检：{again} 在线秒数={(secs is null ? "取不到" : secs.Value.ToString(CultureInfo.InvariantCulture))}");
            var authOk = again == AuthState.Authenticated;
            if (!authOk) s.Problem($"⑧ 复检：门户说现在不是已认证状态（{again}）" +
                                   (authedByLogin ? "，可 ⑦ 刚报过成功 —— 会话被 AC 拒了或状态还没同步" : ""));

            // ── ⑨ 互联网旁证 ──
            var online = await s.Budgeted("⑨ 互联网旁证", ct => new HttpConnectivityProbe(probeHttp).IsOnlineAsync(ct), false);
            s.Line($"⑨ 互联网旁证：{(online ? "通" : "不通")}" +
                   $"（探测客户端：禁自动跳转={SwitchText(probeOwned)}，单次超时={probeHttp.Timeout.TotalSeconds:0.##}s，" +
                   "目标 msftconnecttest + bing；这一项只是旁证，判定以 ⑧ 为准）");
            ProbeClientHealth(s, probeHttp, probeOwned);
            if (!online) s.Problem("外网不通：认证过了却上不了网（DNS 没恢复、还在认证前状态、或校园网侧没生效）");

            if (s.OverallCancelled)
            {
                s.Incomplete = true;
                s.Line($"整轮自检用到了 {h.OverallBudgetMs / 1000} 秒上限，上面几步是按“取不到”收的场，结论不可信");
            }
        }
        finally
        {
            portalHttp.Dispose(); probeHttp.Dispose();
            portalOwned?.Dispose(); probeOwned?.Dispose();
        }
    }

    // Session（记账台：Line/Problem/Finish/Budgeted）与 WithBudgetAsync 在 SelfTestSession.cs；
    // 两个客户端的构造点与 ProbeClientHealth/SwitchText 在 SelfTestClients.cs。
}
