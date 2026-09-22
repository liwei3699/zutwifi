using ZutWifi.Config;
using ZutWifi.Diagnostics;
using ZutWifi.Portal;
using ZutWifi.Wifi;

namespace ZutWifi.Core;

/// 唯一决策者。UI 只读状态、只发命令，不判断"该不该登录"。
///
/// 取消语义（贯穿本文件）：用户在序列跑一半时切了 WiFi，就是要这一轮作废。
/// 所以每个 await 之后、每次发布状态、每次发通知之前都先看一眼 ct，命中即静默 return。
/// 少了这道闸，被取消的探测会以 AuthState.Unknown 返回，接着照常往下走登录并弹一条
/// 凭空出现的"门户不可达"提示——那是假故障，会把人引向错误的排查方向。
///
/// 但 ct 只覆盖命令通路（按钮、托盘菜单）。事件通路的令牌恒为 None：Start() 派出去的那一轮
/// 没人能取消，所以"过期就收手"必须由协调器自己认——每个 await 之后拿 ap 和 wifi.Current 比一次
/// （Abandoned → LeftBehind）。只写 ct 闸门，等于真机主路径上一道闸都没生效。
///
/// 静默中止有两条例外（界面上"停在进行中"再也点不动，就是评审指出的那条死路）：
/// 登录包已经交给门户之后再收手，必须落一个可操作的终态（Aborted → Failed + 原因）；
/// 一个包都没交出去就收手，则落 AbortedBeforeSubmit → Idle（带 SSID）。
/// Probing / AcquiringIp / LoggingIn / Verifying 这四个相位都把登录和重新检测禁用，
/// 而会话键与刷新的相位门又让后面的事件和 60 秒定时器都救不回来，只有换接入点才能出来。
/// 见这两个方法各自的注释。
///
/// 第三格同类的问题不在"中止"这一族里，而是取凭据：密码读不出来时门户一个包都没收到，
/// 界面却停在 LoggingIn（或者被谁兜成一句"登录已中止"，那是假话）。它有自己的终态与自己的日志行，
/// 见 TryCredentials 与 CredentialFailureReason。
public sealed class LoginCoordinator(
    PortalGateway portal, IWifiSource wifi, Func<Credential> credentials, Settings settings,
    IClock clock, IConnectivityProbe probe,
    Action<AppStatus>? publish = null, Action<NoticeKind, string?>? notify = null,
    TransactionLog? log = null) : ILoginCommands
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(1.5);
    /// 提交之后被中止时界面上那一句原因（非空是硬要求：StatusPresenter 靠它放行登录/重新检测）。
    private const string AbortReason = "登录已中止";

    /// 密码读不出来时界面上那一句原因。非空同样是硬要求（放行按钮），但这句话的内容必须与
    /// AbortReason 分得开：一个说的是"你按掉了那一轮"，另一个说的是"这台机器解不出密码，去重填一次"，
    /// 混成一句 = 同学按第二下、第三下，每一下都得到同一个假结论。
    private const string CredentialFailureReason = "密码读取失败，请重新填写";

    private readonly SemaphoreSlim _serialize = new(1, 1);
    private readonly TransactionLog? _log = log;       // 事件通路异常与凭据读取失败的共同出口（见 OnWifiChanged / TryCredentials）

    private string? _sessionKey;                       // SSID|IP，登录包一提交就记，同一次连接不再提交第二次
    private string? _lastSsid;
    private DateTimeOffset _lastHandled = DateTimeOffset.MinValue;
    private string? _lastEventKey;                     // 事件去抖：同一次连接只跑一遍

    public AppStatus Current { get; private set; } = new(AppPhase.Idle);
    public event Action<AppStatus>? StatusChanged;
    public AccessPoint? WifiCurrent => wifi.Current;

    /// 事件入口带去抖：wlanapi 的一次连接常连着报多条通知（唤醒、漫游、轮询补报也一样），
    /// 裸转发会让同一次连接挨两遍登录——正是门户"认证IP已在线"和账号保护的来源。
    /// 1.5 秒窗口内同一 SSID+IP 只放行第一次；换了接入点（新 IP）立刻放行，不受窗口影响。
    public void Start() => wifi.Changed += OnWifiChanged;

    /// 事件通路只有一个入口，也只在这里兜异常。
    /// 原来写的是 `_ = HandleWifiChanged(...)`：凭据拿不到（DPAPI 解不开、账户被删）时异常掉进
    /// 一条没人观察的任务，界面既不提示也不更新——同学看到的就是"托盘卡住了"，而诊断包里
    /// 连"它为什么卡住"都查不出来。现在 catch 住并落一行 stage=Coordinator 的记录。
    private async void OnWifiChanged(AccessPoint? ap)
    {
        var key = ap?.Ssid + "|" + ap?.Ipv4;
        if (key == _lastEventKey && clock.UtcNow - _lastHandled < DebounceWindow) return;
        _lastEventKey = key;
        _lastHandled = clock.UtcNow;                    // 时间戳在派发之前就记：一轮登录本身要好几秒，
        try                                             // 窗口按"事件到达"算而不是按"处理完"算
        {
            await HandleWifiChanged(ap, CancellationToken.None);
        }
        catch (Exception ex) { LogFault(ex, ap); }      // 后台读失败不许把托盘带崩，但必须留痕
    }

    private void LogFault(Exception ex, AccessPoint? ap) =>
        _log?.Write(new TransactionRecord("Coordinator", "EVENT",
            $"wifi://{ap?.Ssid ?? "?"}/{ap?.Ipv4 ?? "?"}", null, null,
            "无线事件这一轮没跑完", $"{ex.GetType().Name}: {ex.Message}"));

    public Task HandleWifiChanged(AccessPoint? ap, CancellationToken ct)
    {
        _lastSsid = ap?.Ssid;
        return RunAsync(ap, ct, userTriggered: false);
    }

    public Task RequestLoginAsync(CancellationToken ct) => RunAsync(wifi.Current, ct, userTriggered: true);
    public Task RequestReprobeAsync(CancellationToken ct) => RunAsync(wifi.Current, ct, userTriggered: true);
    Task ILoginCommands.LoginAsync(CancellationToken ct) => RequestLoginAsync(ct);
    Task ILoginCommands.ReprobeAsync(CancellationToken ct) => RequestReprobeAsync(ct);
    Task ILoginCommands.LogoutAsync(CancellationToken ct) => RequestLogoutAsync(ct);
    Task ILoginCommands.RecoverReloginAsync(CancellationToken ct) => RequestRecoverReloginAsync(ct);

    private async Task RunAsync(AccessPoint? ap, CancellationToken ct, bool userTriggered)
    {
        if (ct.IsCancellationRequested) return;
        await _serialize.WaitAsync(ct);
        try { await RunLockedAsync(ap, ct, userTriggered); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 排队等锁时被取消：同样静默，不发布任何状态。
        }
        finally { _serialize.Release(); }
    }

    /// 登录序列本体。调用方必须已持有 _serialize —— RequestRecoverReloginAsync 要在同一把锁里
    /// 串起"注销 → 重登"，所以这里拆成锁外取锁、锁内干活两截。
    private async Task RunLockedAsync(AccessPoint? ap, CancellationToken ct, bool userTriggered)
    {
        // 拿到锁之后的第一件事是确认这一轮还该不该做：事件是排队的，轮到它的时候用户可能已经
        // 换过好几个网络了。继续跑就是拿旧接入点的身份去提交账号，还会把新事件发布的状态盖回去。
        if (Abandoned(ap, ct, userTriggered)) return;

        if (ap is null || !SsidMatcher.IsCampus(ap.Ssid, settings.SsidWhitelist))
        {
            _sessionKey = null;
            // 切走 SSID 只停动作，不发注销包（用户选的"仅手动注销"）。
            // Idle 不带 Reason：界面上一次的原因必须跟着清掉，否则 idle 状态配着一句"门户不可达"。
            Set(new AppStatus(AppPhase.Idle, ap?.Ssid), ct);
            return;
        }

        var key = ap.Ssid + "|" + ap.Ipv4;
        // 会话身份优先于界面阶段：这个 SSID+IP 的登录包既然真的交过了，重复事件就只许走刷新。
        // 上一版这里还要求 Current.Phase 停在 Online/Degraded/GiveUp —— 可"提交之后被取消/被判过期"
        // 的那一轮恰好停在 LoggingIn/Verifying，于是下一次事件又完完整整登了一遍。
        if (!userTriggered && key == _sessionKey)
        {
            await RefreshLockedAsync(ct);        // 只刷新显示
            return;
        }

        Set(new AppStatus(AppPhase.Probing, ap.Ssid, ap.Ipv4), ct);
        if (Abandoned(ap, ct, userTriggered)) { AbortedBeforeSubmit(ap); return; }
        var state = await portal.ProbeAsync(ct);
        if (Abandoned(ap, ct, userTriggered)) { AbortedBeforeSubmit(ap); return; }
        // Unknown（超时、答非所问）在这里按"没证明已认证"处理，继续走登录：
        // 门户登录是幂等的，重复会话只会回 认证IP已在线（HandleFailureAsync 认这个码）。
        // 判据反过来写才是危险的——把 Unknown 当已认证会让账号一直停留在没上去的状态。
        if (state == AuthState.Authenticated)
        {
            _sessionKey = key;
            var seconds = await portal.GetOnlineSecondsAsync(ct);
            if (Abandoned(ap, ct, userTriggered)) { AbortedBeforeSubmit(ap); return; }
            Set(new AppStatus(AppPhase.Online, ap.Ssid, ap.Ipv4, null, seconds,
                OnlineAtTickMs: Monotonic.TickMs), ct);
            return;
        }

        Set(new AppStatus(AppPhase.AcquiringIp, ap.Ssid, ap.Ipv4), ct);
        if (Abandoned(ap, ct, userTriggered)) { AbortedBeforeSubmit(ap); return; }
        var ip = await portal.GetClientIpAsync(ct) ?? ap.Ipv4;   // 页面无 ss5 字段时回退网卡地址
        if (Abandoned(ap, ct, userTriggered)) { AbortedBeforeSubmit(ap); return; }

        Set(new AppStatus(AppPhase.LoggingIn, ap.Ssid, ip), ct);
        // 这道闸门还在登录包之前，所以是 Idle 而不是 Aborted：过了这一行才算"包已交出"。
        if (Abandoned(ap, ct, userTriggered)) { AbortedBeforeSubmit(ap); return; }
        // 凭据到这一刻才取（不是开轮时）：中途重填过密码就该拿新那一份。
        // 取不到就走它自己的终态，绝不让它逃出去被兜成"登录已中止"——那是在替一次根本没发生的提交下结论。
        var started = clock.UtcNow;
        if (!TryCredentials(ap, ip, ct, out var credential)) return;
        PortalResult result;
        try
        {
            result = await portal.LoginAsync(credential, ip, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消落在请求途中：包很可能已经到了门户。会话键照样记下——多记一次的代价是这一轮
            // 不再自动重登（用户点一下就能重来），少记一次的代价是向门户重复提交同一个账号。
            _sessionKey = key;
            Aborted(ap, ip);
            return;
        }
        // 登录包已经真的提交出去了 ⇒ 会话键先落账，再过任何取消/过期闸门。
        // 顺序反过来（上一版）就是"提交成功了却没记账"，同一次连接的下一次事件会再交一遍——
        // 重复提交正是门户账号保护最怕的那一侧。
        _sessionKey = key;
        // 从这里到底部，"这一轮没跑完"绝不能再以"停在 LoggingIn/Verifying"的形式出现：
        // StatusPresenter 在这两个阶段把登录/重新检测全禁用，而会话键已记 ⇒ 同键事件只走刷新，
        // 刷新在非 Online/Degraded 时一个请求都不发，60 秒定时器也救不回来。
        // 所以这一段里每一道 Abandoned 闸门命中时都要落一个可操作的终态（Aborted）；
        // 连被取消的异常也一并兜住 —— 真机上探针和 Task.Delay 在取消时抛的是
        // TaskCanceledException，让它逃出去就等于把界面留在那个点不动的格子上。
        try
        {
            if (Abandoned(ap, ct, userTriggered)) { Aborted(ap, ip); return; }
            if (result.IsSuccess) { await SucceedAsync(ap, ip, started, userTriggered, ct); return; }
            await HandleFailureAsync(result, ap, key, userTriggered, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Aborted(ap, ip); }
    }

    /// 成功收尾。会话键在登录包提交那一刻就已经记下了（RunLockedAsync），这里不重复写。
    /// 两道闸门走 Abandoned 而不是只看 ct：事件通路的令牌恒为 None，只判 ct 就等于这一段
    /// 在真机主路径上根本没有闸门 —— 用户换了接入点之后，旧 SSID 照样亮绿并弹一条"登录成功"。
    private async Task SucceedAsync(AccessPoint ap, string ip, DateTimeOffset started,
        bool userTriggered, CancellationToken ct)
    {
        Set(new AppStatus(AppPhase.Verifying, ap.Ssid, ip), ct);
        var millis = (int)(clock.UtcNow - started).TotalMilliseconds;
        settings.LastLoginAt = clock.UtcNow;
        settings.LastLoginMillis = millis;
        if (await probe.IsOnlineAsync(ct))
        {
            if (Abandoned(ap, ct, userTriggered)) { Aborted(ap, ip); return; }
            // 刚上线确实是 0 秒，但这里带上了锚点：界面上那个数字从这一刻起自己往前走（见 AppStatus.SecondsAt）。
            Set(new AppStatus(AppPhase.Online, ap.Ssid, ip, null, 0,
                OnlineAtTickMs: Monotonic.TickMs), ct);
            Notify(NoticeKind.LoginSucceeded, $"{ap.Ssid} · 耗时 {millis / 1000.0:0.0}s", ct);
        }
        else
        {
            if (Abandoned(ap, ct, userTriggered)) { Aborted(ap, ip); return; }
            // 认证成功和外网通不通是两件事，只做前者会谎报成功；这里既不静默也不报失败。
            // 时长也带上锚点：会话是真建立起来了，橙色那一格不该挂着一个停在 00:00:00 的数字。
            const string why = "门户返回成功，外网未通，可点重新检测";
            Set(new AppStatus(AppPhase.Degraded, ap.Ssid, ip, why, 0,
                OnlineAtTickMs: Monotonic.TickMs), ct);
            Notify(NoticeKind.Degraded, why, ct);
        }
    }

    private Task RefreshLockedAsync(CancellationToken ct) => RefreshCoreAsync(ct);

    /// 只刷新显示与认证状态，永不触发登录 —— 保守策略的另一半。
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        await _serialize.WaitAsync(ct);
        try { await RefreshCoreAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _serialize.Release(); }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var ap = wifi.Current ?? new AccessPoint(_lastSsid ?? "", Current.Ip ?? "", "");
        if (!SsidMatcher.IsCampus(ap.Ssid, settings.SsidWhitelist)) return;
        if (Current.Phase is not (AppPhase.Online or AppPhase.Degraded)) return;
        var state = await portal.ProbeAsync(ct);
        if (ct.IsCancellationRequested) return;
        switch (state)
        {
            case AuthState.Authenticated:
            {
                var seconds = await portal.GetOnlineSecondsAsync(ct);
                if (ct.IsCancellationRequested) return;
                Set(new AppStatus(AppPhase.Online, ap.Ssid, ap.Ipv4, null, seconds,
                    OnlineAtTickMs: Monotonic.TickMs), ct);
                break;
            }
            case AuthState.Unauthenticated:
                _sessionKey = null;
                Set(new AppStatus(AppPhase.Failed, ap.Ssid, ap.Ipv4, "认证已失效"), ct);
                Notify(NoticeKind.AuthExpired, "需要重新登录", ct);
                break;
            // Unknown：这一轮什么都没证明，也什么都没否定——保持界面现状，别拿它当结论。
        }
    }

    private RetryPolicy? _retry;
    private RetryPolicy Retry => _retry ??= new RetryPolicy(settings.MaxRetries);

    /// 门户拒绝登录后的三条分支，全部按"绝不惹学校账号保护"的口径写：
    /// ① ErrorMsg=2（认证IP已在线）不重试、更不自动注销，只把一键注销重登摆给用户；
    /// ② 其余拒绝/传输错误按 2s/5s/15s 退避，一次连接事件至多再提交 3 次；
    /// ③ 预算用尽进 GiveUp 并只发一条失败通知，之后这一轮连接不再自动尝试（_sessionKey 已记下，
    ///    重复事件走刷新分支，刷新永不提交登录）。
    /// 进来就意味着首扣已经提交，所以这里的每一道闸门都是"提交之后收手"，一律落 Aborted 终态；
    /// 过期判定复用同一个 Abandoned 谓词（上一版这里三处只判 ct、一处单独判 LeftBehind，
    /// 两套机制并写就会漏 —— 事件通路的 ct 永远是 None）。
    private async Task HandleFailureAsync(PortalResult result, AccessPoint ap, string key,
        bool userTriggered, CancellationToken ct)
    {
        if (result.ErrorCode == 2)            // IP 已在线：可能是旧租约残留，决定权交给用户
        {
            // 自动注销在这里是危险的：注销的是"别人"（或上一台设备）的会话，
            // 而门户说的"已在线"也可能只是它自己没清干净——踢掉谁都不该由程序决定。
            _sessionKey = key;
            Set(new AppStatus(AppPhase.Failed, ap.Ssid, ap.Ipv4, result.Reason, CanRecoverRelogin: true), ct);
            Notify(NoticeKind.AlreadyOnlineElsewhere, "可能是旧租约残留，点一下可注销并重登", ct);
            return;
        }

        var ip = ap.Ipv4;
        // RetryPolicy.NextDelay 以"已重试次数"为下标（0→2s、1→5s、2→15s、3→null）；
        // 首扣已在 RunLockedAsync 发生，所以这里从 0 开始，一共 3 次重试 = 至多 4 次提交。
        for (var retries = 0; Retry.NextDelay(retries) is { } wait; retries++)
        {
            Set(new AppStatus(AppPhase.LoggingIn, ap.Ssid, ip,
                $"{result.Reason}（已失败 {retries + 1} 次，{wait.TotalSeconds:0} 秒后重试）"), ct);
            await clock.Delay(wait, ct);
            // 退避期间用户切了 WiFi，或这一轮的令牌被取消：
            // 继续提交就是给已经不存在的会话多打门户包，而且会把新事件的状态盖掉。
            if (Abandoned(ap, ct, userTriggered)) { Aborted(ap, ip); return; }
            ip = await portal.GetClientIpAsync(ct) ?? ap.Ipv4;
            if (Abandoned(ap, ct, userTriggered)) { Aborted(ap, ip); return; }
            // 重试也是"取凭据 → 交包"这一格：解不开密码同样落 CredentialFailureReason，
            // 而不是把这一轮判成 GiveUp（那是"门户拒绝了 4 次"，同学会去查密码对不对，方向正好错反）。
            if (!TryCredentials(ap, ip, ct, out var credential)) return;
            var again = await portal.LoginAsync(credential, ip, ct);
            if (Abandoned(ap, ct, userTriggered)) { Aborted(ap, ip); return; }
            if (again.IsSuccess) { await SucceedAsync(ap, ip, clock.UtcNow, userTriggered, ct); return; }
            // 重试中途变成"IP 已在线"：说明前面那次其实上去过，同样交给人工，不再退避。
            if (again.ErrorCode == 2) { await HandleFailureAsync(again, ap, key, userTriggered, ct); return; }
            result = again;
        }

        _sessionKey = key;
        Set(new AppStatus(AppPhase.GiveUp, ap.Ssid, ip, result.Reason), ct);
        Notify(NoticeKind.LoginFailed, result.Reason, ct);
    }

    /// 当前连接的 AP 已经不是这一轮的那个了（切 SSID、换 IP、断开）。
    /// 按值比对接入点，不按引用：WifiSentinel 每轮都新建 AccessPoint 实例。
    private bool LeftBehind(AccessPoint ap)
        => wifi.Current is not { } now || now.Ssid != ap.Ssid || now.Ipv4 != ap.Ipv4;

    /// 这一轮该不该就地收手。两半各管一条通路：
    /// ① ct —— 只有命令通路（按钮/托盘）真的会取消；
    /// ② LeftBehind —— 事件通路的令牌恒为 None，"用户已经换了网络"只能拿 wifi.Current 认。
    /// 登录序列里每一个"要不要收手"的判断都走这一个谓词（成功收尾与退避循环以前只判 ct，那等于没判），
    /// 不再另立第二套。用户自己点的按钮不算过期：他要的就是"对当前这个接入点再来一轮"。
    private bool Abandoned(AccessPoint? ap, CancellationToken ct, bool userTriggered) =>
        ct.IsCancellationRequested || (!userTriggered && ap is not null && LeftBehind(ap));

    /// 用户点"注销"。全程序只有这里和 RequestRecoverReloginAsync 会发注销包。
    public async Task RequestLogoutAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        await _serialize.WaitAsync(ct);
        try
        {
            var ap = wifi.Current;
            if (ap is null) { Notify(NoticeKind.LogoutFailed, "当前没有已连接的无线接口", ct); return; }
            var r = await portal.LogoutAsync(ap.MacNoSeparator, ct);
            if (ct.IsCancellationRequested) return;
            if (r.IsSuccess)
            {
                // 注销成功后 _sessionKey 必须清空，否则同一 IP 再连上来会被当成"这一轮已经登过了"。
                _sessionKey = null;
                // Idle 不带 Reason：界面对 Idle 的读法是"Reason ⇒ 上一次的结果"，
                // 塞一句"已注销"就把这条契约拆了（presenter 会把它当结论显示）。
                // 注销成功这句话由 NoticeKind.LogoutSucceeded 通知带出去，一处说话就够。
                Set(new AppStatus(AppPhase.Idle, ap.Ssid, ap.Ipv4), ct);
                Notify(NoticeKind.LogoutSucceeded, ap.Ssid, ct);
            }
            else Notify(NoticeKind.LogoutFailed, r.Reason ?? r.Outcome.ToString(), ct);
            // 失败时状态原样不动：账号还上着网，把界面改成"失败"会让人以为掉线了。
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _serialize.Release(); }
    }

    /// 一键恢复"认证IP已在线"：先注销掉那个挡路的旧会话，再走一遍正常登录。
    /// 全程在同一把锁里，登录序列复用 RunLockedAsync，所以不会有第二个事件插进来。
    public async Task RequestRecoverReloginAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        await _serialize.WaitAsync(ct);
        try
        {
            var ap = wifi.Current;
            if (ap is null) return;
            await portal.LogoutAsync(ap.MacNoSeparator, ct);   // 结果不阻塞重登：登录本身幂等
            if (ct.IsCancellationRequested) return;
            await clock.Delay(TimeSpan.FromSeconds(3), ct);    // 给网关一点把旧会话清干净的时间
            if (ct.IsCancellationRequested) return;
            _sessionKey = null;                                // 这一次一定要真的重登，别走刷新捷径
            await RunLockedAsync(ap, ct, userTriggered: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _serialize.Release(); }
    }

    /// 登录包已经交出去了、这一轮却被中止（取消或换了网络）时的终态。
    /// 界面上必须留下一个还能按的按钮：LoggingIn / Verifying 在 StatusPresenter 下把登录和重新检测
    /// 全禁用，而这一轮的会话键已经记下 ⇒ 同键事件只走刷新、刷新此时什么都不做，
    /// 于是"停在正在登录…"就是永久死路（只有换接入点或断开才能出来）。
    /// Failed + 非空 Reason 既让界面把话说清楚，也放行两个按钮；会话键保持已记录，
    /// 所以"可操作"只对人开放，程序绝不自动再交第二个包。中止不发通知：那一下是人自己按的。
    /// 这条发布刻意不走 Set —— 触发它的正是"令牌已取消"，过了 Set 的闸门就等于什么都没发出去。
    private void Aborted(AccessPoint ap, string ip) =>
        Publish(new AppStatus(AppPhase.Failed, ap.Ssid, ip, AbortReason));

    /// 登录包还没交出去就收手（被取消，或这一轮已被新接入点取代）时的终态：回 Idle 并带上 SSID。
    /// 为什么不像 Aborted 那样落 Failed —— 门户什么都没收到，"登录已中止"是在替一次不存在的提交下结论；
    /// 为什么绝不能什么都不发 —— Probing / AcquiringIp / LoggingIn 在 StatusPresenter 下把"登录"和
    /// "重新检测"全禁用，而这一轮之后没人再把界面挪走：同一个接入点的事件只会走刷新，
    /// 刷新的相位门（只在 Online/Degraded 才动）又让它一个请求都不发，60 秒定时器同样救不回来，
    /// 于是只能等人换个网络才解锁 —— 与上一轮修掉的那条死路同一个形状，只是发生在提交之前。
    /// Idle 让 presenter 放行 登录/重新检测：这一轮什么都没做，界面就该回到"可以再做一次"。
    /// 只在还没被新接入点取代时救援：LeftBehind 为真说明用户已经连上别处，那一头的事件正在（或已经）
    /// 往界面上写它自己的状态，这里补一条带旧 SSID 的 Idle 只会把它盖回去
    /// （慢处理期间换了接入点… 那条用例钉的就是这个）。
    /// 与 Aborted 同样走 Publish 而不是 Set —— 触发它的正是"令牌已取消"，Set 的闸门会原样吃掉这条发布。
    private void AbortedBeforeSubmit(AccessPoint ap)
    {
        if (LeftBehind(ap)) return;
        Publish(new AppStatus(AppPhase.Idle, ap.Ssid, ap.Ipv4));
    }

    /// 取凭据那一处的唯一出口（首扣与退避里的每一次重试都走它）。
    ///
    /// 为什么要接住：`SecretStore.Get` 只兜 DPAPI 那两类异常（COMException / CryptographicException），
    /// 其余的全照样往上抛 —— secret.bin 被别的进程独占、网盘正在同步、目录被删、盘没了。
    /// 以前它是 `portal.LoginAsync(credentials(), ...)` 的实参，抛出来就往门外逃，结果两种都不好：
    /// ① 界面上停在"正在登录…"（LoggingIn 把登录与重新检测全禁用，会话键又没记 ⇒ 只能等人换网络）；
    /// ② 或者被某层 catch-all 兜成一句"登录已中止"—— 门户一个包都没收到，这句话是假的，
    ///    而按钮亮着，人按第二下、第三下只会拿到同一个假结论。
    /// 这两种都缺同一样东西：一句能照着做的实话，和一行查得出来的记录。这里一次给齐：
    /// Failed + 非空原因（presenter 放行按钮，去设置页重填密码是真出路）+ 一行 Coordinator 记录（异常原文）。
    /// 走 Set 而不是 Publish：这不是取消通路（取消由上面那道 Abandoned 闸门管，行为一字未动），
    /// 所以它照样守"令牌已取消之后不往界面漏普通状态"那条规矩。
    private bool TryCredentials(AccessPoint ap, string ip, CancellationToken ct, out Credential credential)
    {
        try
        {
            credential = credentials();
            return true;
        }
        catch (Exception ex)
        {
            credential = null!;                                    // 返回 false 时没人会读它
            _log?.Write(new TransactionRecord("Coordinator", "CREDENTIAL", $"wlan://{ap.Ssid}/{ip}",
                null, null, "读取密码失败，这一轮没有向门户提交登录包（界面上给的是重填提示）",
                $"{ex.GetType().Name}: {ex.Message}"));
            Set(new AppStatus(AppPhase.Failed, ap.Ssid, ip, CredentialFailureReason), ct);
            return false;
        }
    }

    /// 除两条中止终态（Aborted / AbortedBeforeSubmit，它们必须越过取消闸门）之外唯一的发布出口：
    /// 取消后一个普通状态都不许漏给界面（见类注释的取消语义）。
    private void Set(AppStatus s, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        Publish(s);
    }

    private void Publish(AppStatus s)
    {
        Current = s;
        publish?.Invoke(s);
        StatusChanged?.Invoke(s);
    }

    private void Notify(NoticeKind kind, string? detail, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        notify?.Invoke(kind, detail);
    }
}
