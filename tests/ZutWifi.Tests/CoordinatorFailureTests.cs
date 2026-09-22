using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Diagnostics;
using ZutWifi.Portal;
using ZutWifi.Shell;
using ZutWifi.Tests.Support;
using ZutWifi.Wifi;
namespace ZutWifi.Tests;
public class CoordinatorFailureTests
{
    private static readonly AccessPoint Ap = new("zut-stu", "10.133.126.113", "02a1b2c3d4e5");
    private static readonly AccessPoint Home = new("home", "192.168.1.2", "112233445566");

    static LoginCoordinator Build(FakeHttpHandler h, List<(NoticeKind, string?)> notes,
        FakeWifiSource? wifi = null, FakeClock? clock = null, List<AppStatus>? seen = null,
        Settings? settings = null, TransactionLog? log = null, Func<Credential>? credentials = null)
    {
        var w = wifi ?? new FakeWifiSource();
        w.Current ??= Ap;
        return new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"), w,
            credentials ?? (() => new Credential("id", "pw", "@cmcc")),
            settings ?? new Settings { MaxRetries = 3 },
            clock ?? new FakeClock(), new FakeProbe(true),
            seen is null ? null : s => seen.Add(s), (k, d) => notes.Add((k, d)), log);
    }

    /// 请求顺序：首次 = Probe + GetIp + Login；每次退避重试 = GetIp + Login。
    static void QueueRejectedWithBackoff(FakeHttpHandler h, string fixture, int retries)
    {
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read(fixture));
        for (var i = 0; i < retries; i++)
        {
            h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
            h.EnqueueRaw(Fixtures.Read(fixture));
        }
    }

    /// 一次成功登录恰好消耗 3 个响应。刷新用例走这个而不是 QueueUnauthThenSuccess：
    /// 后者末尾那份 online_9002 是给"探测即已认证"分支的样本，留在队列里会让刷新的 Probe
    /// 吃错响应（该判失效的那次探测读成了"还在线"），整个用例就从验证刷新变成了验证巧合。
    static void QueueLoginOnly(FakeHttpHandler h)
    {
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
    }

    [Fact]
    public async Task 密码错误退避三次后GiveUp并只发一条失败通知()
    {
        var h = new FakeHttpHandler();
        QueueRejectedWithBackoff(h, "login_reject_pwerr.txt", retries: 3);
        var notes = new List<(NoticeKind, string?)>();
        var clock = new FakeClock();
        var c = Build(h, notes, clock: clock);
        await c.HandleWifiChanged(Ap, default);
        Assert.Equal(AppPhase.GiveUp, c.Current.Phase);
        Assert.Contains("Radius 认证失败（账号或密码错误）", c.Current.Reason);
        Assert.Single(notes, n => n.Item1 == NoticeKind.LoginFailed);   // 失败通知一共只有一条
        Assert.Equal(9, h.Requests.Count);                       // 3 + 3×2
        Assert.Equal(4, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        // 退避表 2s/5s/15s 逐个钉住：循环下标从 0 起是"首扣已在上面发生"的口径，
        // 写成从 1 起会变成 5s/15s/60s 且只重试 3 次里少一次——请求数能对上，等待时间对不上。
        Assert.Equal(new[] { 2, 5, 15 }, clock.Waits.Select(w => (int)w.TotalSeconds).ToArray());
    }

    /// GiveUp 是这一轮连接的终点：之后的重复事件只许走刷新（刷新对 GiveUp 什么都不做），
    /// 绝不能再起一轮 4 次提交——用户点名要避免的就是重试风暴。没有这条断言，
    /// RunLockedAsync 里 `!userTriggered && key == _sessionKey` 那道刷新放行被删掉、
    /// 或者被改回"还要看界面停在 Online/Degraded/GiveUp"（上一版就是这么写的）都不会有任何用例报警。
    [Fact]
    public async Task GiveUp之后同一次连接的重复事件不再重登()
    {
        var h = new FakeHttpHandler();
        QueueRejectedWithBackoff(h, "login_reject_pwerr.txt", retries: 3);
        var notes = new List<(NoticeKind, string?)>();
        var c = Build(h, notes);
        await c.HandleWifiChanged(Ap, default);
        Assert.Equal(AppPhase.GiveUp, c.Current.Phase);
        var after = h.Requests.Count;
        notes.Clear();

        await c.HandleWifiChanged(Ap, default);       // 事件抖动 / 唤醒补报
        await c.RefreshAsync(default);                // 60 秒定时刷新

        Assert.Equal(after, h.Requests.Count);        // 一个门户包都没多打
        Assert.Empty(notes);
        Assert.Equal(AppPhase.GiveUp, c.Current.Phase);
        Assert.Equal(4, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
    }

    /// 保守策略唯一被允许推翻的地方：GiveUp 之后机器自己不再试，但人按下去的那一次必须真的再交一个包。
    /// 这条是"绝不自动重登"的对价——如果连人工点击都被会话键挡掉，同学就只能重启程序了。
    [Fact]
    public async Task GiveUp之后用户点登录会真的再提交一次()
    {
        var h = new FakeHttpHandler();
        QueueRejectedWithBackoff(h, "login_reject_pwerr.txt", retries: 3);   // 首扣 + 3 次退避 = 4 个包
        var notes = new List<(NoticeKind, string?)>();
        var seen = new List<AppStatus>();
        var wifi = new FakeWifiSource { Current = Ap };
        var c = Build(h, notes, wifi, seen: seen);
        await c.HandleWifiChanged(Ap, default);
        Assert.Equal(AppPhase.GiveUp, c.Current.Phase);
        Assert.Equal(4, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));

        // 人工这一轮：探测（仍未认证）+ 取 IP + 登录（这一次门户放行）
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));

        await c.RequestLoginAsync(default);

        Assert.Equal(5, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.Equal(AppPhase.Online, c.Current.Phase);
        Assert.Equal(NoticeKind.LoginSucceeded, notes[^1].Item1);
    }

    /// 把协调器开到 `登录已中止` 那一格，给下面两条用例当现场：
    /// 连上校园网 → 首扣被门户拒绝（会话键在包交出去那一刻就已记录）→ 退避等待的 2 秒里用户切走了 WiFi
    /// ⇒ 提交之后的闸门命中，落 Aborted（Failed + 非空原因，按钮放行）。
    /// 队列里只排这一轮真会用掉的 3 个响应：多一个都会让后面用例的响应错位，红得看不出原因。
    static async Task<(LoginCoordinator c, FakeHttpHandler h, FakeWifiSource wifi,
        List<(NoticeKind, string?)> notes)> DriveToAbortedLogin()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));                                 // 探测：未认证
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));       // 取 IP
        h.EnqueueRaw(Fixtures.Read("login_reject_pwerr.txt"));                           // 首扣被拒 → 进退避
        var notes = new List<(NoticeKind, string?)>();
        var wifi = new FakeWifiSource { Current = Ap };
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"), wifi,
            () => new Credential("id", "pw", "@cmcc"), new Settings { MaxRetries = 3 },
            new SwitchWifiOnFirstDelayClock(wifi, Home), new FakeProbe(true), null,
            (k, d) => notes.Add((k, d)));

        await c.HandleWifiChanged(Ap, default);           // 事件通路：ct 恒为 None，收手只能靠 LeftBehind

        // 现场核对：会话键已记（所以事件只走刷新）+ 界面可按（所以人还能救自己）。
        // 这两半同时成立才是 `登录已中止`，缺任何一半下面两条测的就不是这一格。
        Assert.Equal(AppPhase.Failed, c.Current.Phase);
        Assert.Equal("登录已中止", c.Current.Reason);
        Assert.True(StatusPresenter.Of(c.Current, ssidWhitelisted: true).LoginEnabled);
        Assert.Equal(3, h.Requests.Count);
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.Empty(notes);                              // 中止不发通知
        return (c, h, wifi, notes);
    }

    /// 【保守策略】登录包既然真的交出去过，这一轮的账号身份就记下了：同一次连接（同 SSID+IP）之后的
    /// 事件只许走刷新，而刷新在 Failed 下有相位门、一个请求都不发，60 秒定时器走的也是同一个刷新。
    /// 于是"用户切回校园网、还是那个 IP"绝不会把账号再交一遍 —— 用户点名要的定价（门户重复提交与
    /// 账号保护在最怕的那一侧）。代价是没有第二条自动出路，见下面那条逃生通道。
    /// 变异记录（下面两个变异各自都要让本用例变红；少一个就说明只钉住了一半）：
    /// ① 删掉 `_sessionKey = key`（RunLockedAsync 里包交出去那一刻的记账）→ 本用例红，
    ///    同键事件又整跑一轮登录（多出一串请求）；
    /// ② 把放行条件 `!userTriggered && key == _sessionKey` 改回"还要看界面停在 Online/Degraded/GiveUp"
    ///    （上一版的写法）→ 本用例同样红：中止这一格停在 Failed，事件会整跑一轮。
    [Fact]
    public async Task 登录已中止后同一次连接的事件不再自动重登这是保守策略()
    {
        var (c, h, wifi, notes) = await DriveToAbortedLogin();
        var before = h.Requests.Count;
        notes.Clear();

        wifi.Current = Ap;                              // 用户切回校园网，IP 没变
        await c.HandleWifiChanged(Ap, default);         // 同键事件（真机上由 WifiSentinel 派发）
        await c.RefreshAsync(default);                  // 60 秒定时器

        Assert.Equal(before, h.Requests.Count);         // 一个门户包都没多打
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.Equal(AppPhase.Failed, c.Current.Phase); // 界面原样停在那一句上，不替没做的事下结论
        Assert.Equal("登录已中止", c.Current.Reason);
        Assert.Empty(notes);                            // 也不补任何提示
    }

    /// 【逃生通道】上面那条保守策略的对价：程序绝不自动再交第二个包，但人按下去的那一下必须真的再交一个。
    /// 这一条走的是 MainForm 的 登录 按钮那条路（`ILoginCommands.LoginAsync` → `RequestLoginAsync`，
    /// userTriggered: true），不是 HandleWifiChanged —— 事件通路归事件管，人工触发不受会话键约束。
    /// 现场与上一条逐字相同（同 SSID+IP、会话键已记），所以两条的差别只剩"谁按的"，
    /// 这正是裁定的内容：政策=不自动重登，出口=用户点登录。
    /// 变异记录：放行条件从 `!userTriggered && key == _sessionKey` 退回 `key == _sessionKey`
    /// → 本用例红（人工那一轮也被当成同键事件只走刷新，登录包数停在 1、界面停在 Failed）。
    [Fact]
    public async Task 登录已中止后用户点登录会真的再交一个包这是逃生通道()
    {
        var (c, h, wifi, notes) = await DriveToAbortedLogin();
        wifi.Current = Ap;                              // 回到校园网同一个 IP：按钮这时才是放行着的

        // 人工这一轮：探测（仍未认证）+ 取 IP + 登录（这一次门户放行）
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));

        await ((ILoginCommands)c).LoginAsync(default);

        var logins = h.Requests.Where(r => r.Uri.Query.Contains("a=Login")).ToList();
        Assert.Equal(2, logins.Count);                                                    // 第二个包真的交出去了
        Assert.All(logins, r => Assert.Contains("wlanuserip=10.133.126.113", r.Uri.Query));
        Assert.Equal(6, h.Requests.Count);                                                // 3 中止 + 3 人工
        Assert.Equal(AppPhase.Online, c.Current.Phase);                                   // 并且真的走完了一轮
        Assert.Equal(NoticeKind.LoginSucceeded, notes[^1].Item1);
    }

    [Fact]
    public async Task IP已在线时不自动注销并暴露一键重登()
    {
        var h = new FakeHttpHandler();
        QueueRejectedWithBackoff(h, "login_ip_online.txt", retries: 0);
        var notes = new List<(NoticeKind, string?)>();
        var c = Build(h, notes);
        await c.HandleWifiChanged(Ap, default);
        Assert.True(c.Current.CanRecoverRelogin);
        Assert.Equal(AppPhase.Failed, c.Current.Phase);
        Assert.Equal("认证IP已在线", c.Current.Reason);          // Failed 必带非空原因（界面直接拼这句）
        Assert.Equal(NoticeKind.AlreadyOnlineElsewhere, notes[^1].Item1);
        Assert.DoesNotContain(h.Requests, r => r.Uri.Query.Contains("a=Logout"));
        Assert.Equal(3, h.Requests.Count);                       // 不再退避重试
    }

    [Fact]
    public async Task 一键重登先Logout后Probe再Login并转Online()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("logout_success.txt"));       // ① Logout
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));         // ② Probe
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));  // ③ GetIp
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));        // ④ Login
        var notes = new List<(NoticeKind, string?)>();
        var clock = new FakeClock();
        var c = Build(h, notes, clock: clock);
        await c.RequestRecoverReloginAsync(default);
        Assert.Equal(AppPhase.Online, c.Current.Phase);
        Assert.Contains("a=Logout", h.Requests[0].Uri.Query);
        Assert.Contains("a=Login", h.Requests[3].Uri.Query);
        Assert.Equal(NoticeKind.LoginSucceeded, notes[^1].Item1);
        // 注销与重登之间那 3 秒也要钉住：它是"给网关把旧会话清干净"的固定成本。
        // 写成 0 就是刚注销完立刻重投（门户大概率还认那个旧会话，回 认证IP已在线）；
        // 写成 30 秒则没人会等它。整场一键重登只该等这一段，所以逐字钉死整个等待表。
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, clock.Waits);
    }

    [Fact]
    public async Task 用户点注销成功后回Idle并发通知()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("logout_success.txt"));
        var notes = new List<(NoticeKind, string?)>();
        var seen = new List<AppStatus>();
        var wifi = new FakeWifiSource();
        var c = Build(h, notes, wifi, seen: seen);
        await c.RequestLogoutAsync(default);
        Assert.Equal(AppPhase.Idle, c.Current.Phase);
        Assert.Equal(NoticeKind.LogoutSucceeded, notes[^1].Item1);
        Assert.Null(c.Current.OnlineSeconds);
        // Idle ⇒ Reason 必须为空，这是界面对它的硬契约（presenter 会把非空 Reason 当结论直接显示）。
        // "已注销"这句由 LogoutSucceeded 通知带出去，不塞进状态里当第 4 种 Idle 语义。
        Assert.Null(c.Current.Reason);
        Assert.All(seen.Where(s => s.Phase == AppPhase.Idle), s => Assert.Null(s.Reason));
    }

    [Fact]
    public async Task 注销失败发LogoutFailed通知且状态不变()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1/2.htm?ACLogOut=2") }, "");
        var notes = new List<(NoticeKind, string?)>();
        var c = Build(h, notes);
        var before = c.Current.Phase;
        await c.RequestLogoutAsync(default);
        Assert.Equal(NoticeKind.LogoutFailed, notes[^1].Item1);
        Assert.Equal(before, c.Current.Phase);
    }

    [Fact]
    public async Task 切到其他SSID后不发任何注销请求且状态回Idle()
    {
        var h = new FakeHttpHandler();
        QueueUnauthThenSuccess(h);
        var notes = new List<(NoticeKind, string?)>();
        var wifi = new FakeWifiSource();
        var c = Build(h, notes, wifi);
        await Changed(c, wifi, Ap);
        await Changed(c, wifi, new AccessPoint("home", "192.168.1.2", "112233445566"));
        Assert.Equal(AppPhase.Idle, c.Current.Phase);
        Assert.DoesNotContain(h.Requests, r => r.Uri.Query.Contains("a=Logout"));
    }

    [Fact]
    public async Task 定时刷新只更新时长不触发登录也不发通知()
    {
        var h = new FakeHttpHandler();
        QueueLoginOnly(h);
        var notes = new List<(NoticeKind, string?)>();
        var wifi = new FakeWifiSource();
        wifi.Raise(Ap);
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"), wifi,
            () => new Credential("id", "pw", "@cmcc"), new Settings(), new FakeClock(),
            new FakeProbe(true), null, (k, d) => notes.Add((k, d)));
        await c.HandleWifiChanged(Ap, default);
        var before = h.Requests.Count;
        notes.Clear();
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        await c.RefreshAsync(default);
        Assert.Equal(4211, c.Current.OnlineSeconds);
        Assert.Equal(before + 2, h.Requests.Count);
        Assert.Empty(notes);
        Assert.DoesNotContain(h.Requests.Skip(before), r => r.Uri.Query.Contains("a=Login"));
    }

    [Fact]
    public async Task 刷新发现认证失效转Failed并发一次通知()
    {
        var h = new FakeHttpHandler();
        QueueLoginOnly(h);
        var notes = new List<(NoticeKind, string?)>();
        var wifi = new FakeWifiSource();
        wifi.Raise(Ap);
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"), wifi,
            () => new Credential("id", "pw", "@cmcc"), new Settings(), new FakeClock(),
            new FakeProbe(true), null, (k, d) => notes.Add((k, d)));
        await c.HandleWifiChanged(Ap, default);
        notes.Clear();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        await c.RefreshAsync(default);
        Assert.Equal(AppPhase.Failed, c.Current.Phase);
        Assert.Equal("认证已失效", c.Current.Reason);
        Assert.Single(notes);
        Assert.Equal(NoticeKind.AuthExpired, notes[0].Item1);
        // 整场测试里登录包只有 setup 那一个：刷新这一步一个都没多打（"刷新永不提交登录"）。
        // 只看刷新窗口的话还得靠 Skip，所以这里直接钉死总数，比负向断言更难被绕过去。
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.Equal(4, h.Requests.Count);                       // 3 setup + 1 探测；失效分支不取时长
    }

    static void QueueUnauthThenSuccess(FakeHttpHandler h)
    {
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
    }

    [Fact]
    public void 一点五秒内同一接入点的重复事件被合并为一次()
    {
        var h = new FakeHttpHandler();
        QueueUnauthThenSuccess(h);
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        var clock = new FakeClock();
        var wifi = new FakeWifiSource { Current = Ap };
        var notes = new List<(NoticeKind, string?)>();
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"), wifi,
            () => new Credential("id", "pw", "@cmcc"), new Settings(), clock,
            new FakeProbe(true), null, (k, d) => notes.Add((k, d)));
        c.Start();

        wifi.Raise(Ap);
        wifi.Raise(Ap);                       // 抖第二次，应被丢掉
        Assert.Equal(3, h.Requests.Count);
        Assert.Single(notes);

        clock.AdvanceSeconds(2);
        wifi.Raise(Ap);                       // 过窗口后允许再走一次（此时已在线，只刷新）
        // brief 这行是 Assert.True(Count > 3)，太松：4 也算过。放行只该多 Probe + 取时长两个包，
        // 登录包总数仍是 1 —— "过了去抖窗口"绝不等于"可以再登一次"。
        Assert.Equal(5, h.Requests.Count);
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.Single(notes);
    }

    [Fact]
    public void 换了接入点时立即放行不受去抖影响()
    {
        var h = new FakeHttpHandler();
        QueueUnauthThenSuccess(h);
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        var clock = new FakeClock();
        var wifi = new FakeWifiSource { Current = Ap };
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"), wifi,
            () => new Credential("id", "pw", "@cmcc"), new Settings(), clock,
            new FakeProbe(true), null, (k, d) => { });
        c.Start();
        wifi.Raise(Ap);
        wifi.Raise(new AccessPoint("zut-stu", "10.133.99.9", "02a1b2c3d4e5"));   // 换了 IP
        // brief 这行写的是 Assert.Equal(3, ...)，但那是反的：3 恰恰是"被去抖吞掉"的样子。
        // 新 IP 必须放行 → 再走 Probe + 取时长（队列里下一份就是 online_9002，探测即已认证分支）
        // = 3 + 2；断言改成放行后的真实数量，并确认界面已经跟着换成新地址。
        Assert.Equal(5, h.Requests.Count);
        Assert.Equal("10.133.99.9", c.Current.Ip);
        Assert.Equal(AppPhase.Online, c.Current.Phase);
    }

    /// 保守策略的最后一道闸：退避等待期间用户切走了 WiFi，这一轮就此打住。
    /// 事件通路的令牌恒为 None（HandleWifiChanged 由 Start 派发），取消救不了这条路径，
    /// 所以"不为已经离开的接入点继续提交登录包"只能由协调器自己认出来。
    [Fact]
    public async Task 退避等待期间切走WiFi后不再为旧接入点提交登录()
    {
        var h = new FakeHttpHandler();
        QueueRejectedWithBackoff(h, "login_reject_pwerr.txt", retries: 3);   // 只用到第一轮就该停
        var notes = new List<(NoticeKind, string?)>();
        var seen = new List<AppStatus>();
        var wifi = new FakeWifiSource { Current = Ap };
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"), wifi,
            () => new Credential("id", "pw", "@cmcc"), new Settings { MaxRetries = 3 },
            new SwitchWifiOnFirstDelayClock(wifi, new AccessPoint("home", "192.168.1.2", "112233445566")),
            new FakeProbe(true), seen.Add, (k, d) => notes.Add((k, d)));

        await c.HandleWifiChanged(Ap, default);

        Assert.Equal(3, h.Requests.Count);                                  // 首扣之后一次都没再提交
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.DoesNotContain(AppPhase.GiveUp, seen.Select(s => s.Phase));   // 不替离开的会话下结论
        Assert.DoesNotContain(AppPhase.Online, seen.Select(s => s.Phase));
        Assert.Empty(notes);                                                // 也不报失败：那条通知是给这一台机器的用户的
        // 收手之后界面必须还有出路。原来这里断言的是"停在被中止前的最后一次发布（LoggingIn）"，
        // 可 LoggingIn 在 StatusPresenter 下把登录/重新检测全禁用，而会话键已记 ⇒ 同键事件只走刷新、
        // 刷新此时什么都不做：这就是那条"点不到的登录按钮"死路。
        Assert.Equal(AppPhase.Failed, c.Current.Phase);
        Assert.Equal("登录已中止", c.Current.Reason);
        Assert.True(StatusPresenter.Of(c.Current, ssidWhitelisted: true).LoginEnabled);
    }

    /// 评审第 2 项：SucceedAsync 与退避循环里的那几道闸门只看 ct，而事件通路的令牌恒为 None。
    /// 于是"登录包交出去之后、连通性探针在途的那几秒里用户换了接入点"这一整段没有任何闸门生效：
    /// 旧 SSID 照样亮绿，还给一条"登录成功 · 耗时 x 秒"——同学看到的是"我在 zut-stu 上去了"，
    /// 人其实已经连到 zut-lib。判据：Online 与成功通知都不许出现。
    [Fact]
    public async Task 连通性探针在途时换了接入点不再发布旧接入点的在线态与成功通知()
    {
        var apB = new AccessPoint("zut-lib", "10.133.99.9", "02a1b2c3d4e5");
        var h = new FakeHttpHandler();
        QueueLoginOnly(h);                                        // 探测 + 取 IP + 登录（门户放行）
        var wifi = new FakeWifiSource { Current = Ap };
        var seen = new List<AppStatus>();
        var notes = new List<(NoticeKind, string?)>();
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"), wifi,
            () => new Credential("id", "pw", "@cmcc"),
            new Settings { MaxRetries = 3, SsidWhitelist = ["zut-stu", "zut-lib"] }, new FakeClock(),
            new SwitchOnProbe(wifi, apB), seen.Add, (k, d) => notes.Add((k, d)));

        await c.HandleWifiChanged(Ap, default);                   // 事件通路：ct 恒为 None，救不了这条路径

        Assert.DoesNotContain(seen, s => s.Ssid == "zut-stu"
            && s.Phase is AppPhase.Online or AppPhase.Degraded);  // 没替离开的接入点亮绿
        Assert.DoesNotContain(notes, n => n.Item1 == NoticeKind.LoginSucceeded);
        Assert.DoesNotContain(notes, n => n.Item1 == NoticeKind.Degraded);
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));   // 也没多交一个包
        // 中止的一轮同样要落到可操作终态（与 Item 1 同一份契约），而不是禁着按钮的 Verifying。
        Assert.Equal(AppPhase.Failed, c.Current.Phase);
        Assert.Equal("登录已中止", c.Current.Reason);
        Assert.True(StatusPresenter.Of(c.Current, ssidWhitelisted: true).ReprobeEnabled);
    }

    /// 探针在途时把 wifi.Current 换到另一个接入点：真机上这一刻就是"用户在验证的几秒里连了别的网络"。
    /// 探针本身返回 true，好让"旧 SSID 亮绿"这条假路径在旧实现下真的走到底（红得干净）。
    sealed class SwitchOnProbe(FakeWifiSource wifi, AccessPoint to) : IConnectivityProbe
    {
        public Task<bool> IsOnlineAsync(CancellationToken ct)
        {
            wifi.Current = to;
            return Task.FromResult(true);
        }
    }

    /// 第一次等待到点时把 WiFi 换成另一个 SSID，模拟"退避的 2 秒里用户回了家"。
    sealed class SwitchWifiOnFirstDelayClock(FakeWifiSource wifi, AccessPoint to) : IClock
    {
        private readonly FakeClock _inner = new();
        private bool _fired;
        public DateTimeOffset UtcNow => _inner.UtcNow;

        public Task Delay(TimeSpan by, CancellationToken ct)
        {
            if (!_fired) { _fired = true; wifi.Current = to; }
            _inner.Advance(by);
            return Task.CompletedTask;
        }
    }

    /// 这一轮要服务的接入点已经不是当前连接了（慢处理期间用户换了网络）。
    /// 事件通路的取消令牌恒为 None（Start 派发的那条路），所以"过期就收手"不能指望 ct，
    /// 只能由协调器拿 wifi.Current 自己认——而且要在拿到锁之后立刻认一次：
    /// 排在队列里的那几秒，正是新接入点的事件已经跑完的那几秒。
    [Fact]
    public async Task 慢处理期间换了接入点就不再为旧接入点提交登录()
    {
        var apB = new AccessPoint("zut-lib", "10.133.99.9", "02a1b2c3d4e5");
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));         // ① AP-A 的探测（挂住 → 放行 → 未认证）
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));         // ② AP-B 的探测
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));  // ③ AP-B 的取 IP
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));        // ④ AP-B 的登录
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wifi = new FakeWifiSource { Current = Ap };
        var seen = new List<AppStatus>();
        var notes = new List<(NoticeKind, string?)>();
        var c = new LoginCoordinator(
            new PortalGateway(new HttpClient(new HoldFirstRequest(h, gate.Task)), "1.1.1.1"), wifi,
            () => new Credential("id", "pw", "@cmcc"), new Settings { MaxRetries = 3, SsidWhitelist = ["zut-stu", "zut-lib"] },
            new FakeClock(), new FakeProbe(true), seen.Add, (k, d) => notes.Add((k, d)));

        var slow = c.HandleWifiChanged(Ap, default);             // A：卡在探测上，锁在它手里
        var queued = c.HandleWifiChanged(Ap, default);           // A 的抖动：排在锁后面
        wifi.Current = apB;                                      // 处理期间用户切了网络
        var fresh = c.HandleWifiChanged(apB, default);           // B 的事件也来排队
        // 放行之前 A 的慢轮只发布过自己的 Probing。此后任何一条状态都不许再打 zut-stu 的牌子：
        // 排队那一轮如果在拿到锁之后不看一眼当前接入点，就会先替旧接入点往界面上闪一下。
        var publishedBeforeRelease = seen.Count;
        gate.SetResult();
        await Task.WhenAll(slow, queued, fresh).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, h.Requests.Count);                       // A 只留下那一次探测；排队那一轮 0 个请求
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.DoesNotContain(seen.Skip(publishedBeforeRelease), s => s.Ssid == "zut-stu");
        Assert.Equal("zut-lib", c.Current.Ssid);                 // 界面跟着新接入点走，没被旧的一轮盖回去
        Assert.Equal(AppPhase.Online, c.Current.Phase);
        Assert.Single(notes);
    }

    /// 凭据拿不到（DPAPI 解不开、账户被删）时，事件通路原来是 `_ = HandleWifiChanged(...)`：
    /// 异常掉进一条没人观察的任务，托盘既不提示也不更新，看上去就是"卡住了"。
    /// 现在这一轮不再无声消失，具体做到的是三件事：
    /// ① 落一行 stage=Coordinator 的记录（哪个接入点 + 异常原文），诊断包里查得出来；
    /// ② 界面落到自己的终态 `Failed` + "密码读取失败，请重新填写"（不是冒充"登录已中止"，
    ///    也不再停在把两个按钮全禁用的 LoggingIn）—— 见下面那条用例的完整断言；
    /// ③ 崩溃不外溢，托盘还活着。
    /// 取凭据那一处的完整契约（零登录包、按钮放行、只记一行）在
    /// `密码读取失败落自己的终态而不是冒充登录中止` 与
    /// `退避重试时密码读不出来也是同一格终态` 里，这条只管事件通路的外溢。
    [Fact]
    public async Task 事件处理抛异常时写一行Coordinator日志而不是静默吞掉()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwcoord" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new TransactionLog(new FakeClock(), dir);
            var h = new FakeHttpHandler();
            h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
            h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
            var notes = new List<(NoticeKind, string?)>();
            var wifi = new FakeWifiSource();
            var c = Build(h, notes, wifi,
                credentials: () => throw new IOException("DPAPI 解不开密码：账户不匹配"), log: log);
            c.Start();
            wifi.Raise(Ap);                       // 异常只能由事件订阅这一层接住

            Assert.True(WaitUntil(() => log.RecentLines().Any(l => l.Contains("Coordinator")), 5000),
                "协调器异常没有落盘：托盘静默冻结，同学发回来的诊断包里也看不到原因");
            var line = log.RecentLines().First(l => l.Contains("Coordinator"));
            Assert.Contains("IOException", line);
            Assert.Contains("DPAPI 解不开密码", line);
            Assert.Contains("zut-stu", line);                    // 出错的是哪个接入点要能看出来
            Assert.True(WaitUntil(() => c.Current.Phase == AppPhase.Failed, 5000),
                "读密码失败这一轮连一个终态都没有：界面停在按钮全禁用的那一格里");
            await Task.Delay(20);                                 // 崩溃只允许以"这一轮没做完"的形式出现
            Assert.Equal(0, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
            Assert.Empty(notes);                                  // 界面上那句话就是给这一台机器的，不再弹提示
        }
        finally
        {
            // 断言失败先抛，这里不能再抛第二次把真正的失败原因盖掉
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// 密码读不出来不是"登录已中止"。原来的形状：`credentials()` 是 `portal.LoginAsync(...)` 的实参，
    /// 抛出来的一路逃到外面那层 catch，于是
    /// ① 界面停在"正在登录…"——LoggingIn 在 StatusPresenter 下把登录与重新检测全禁用，
    ///    而这一轮的会话键什么都没记，同键事件只走刷新、刷新又有相位门：只能等人换网络（那条死路的第三份）；
    /// ② 或者更坏，被谁兜成了一句"登录已中止"—— 门户一个包都没收到，这句话是假的，
    ///    而按钮亮着，人按下去只会得到同一个假结论（评审原话：文案在骗人）。
    /// 现在取凭据这一处有自己的终态与自己的记录：非空原因的 Failed（按钮放行）、一行 Coordinator 记录、
    /// 零登录包。真正的取消通路（Aborted / AbortedBeforeSubmit）一字未动。
    [Fact]
    public async Task 密码读取失败落自己的终态而不是冒充登录中止()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwcred" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new TransactionLog(new FakeClock(), dir);
            var h = new FakeHttpHandler();
            h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));                                 // 探测：未认证
            h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));         // 取 IP
            var notes = new List<(NoticeKind, string?)>();
            var seen = new List<AppStatus>();
            var wifi = new FakeWifiSource { Current = Ap };
            var c = Build(h, notes, wifi, seen: seen, log: log,
                credentials: () => throw new IOException("secret.bin 被别的进程独占"));

            await c.RequestLoginAsync(default);              // 用户点的那一下

            Assert.Equal(AppPhase.Failed, c.Current.Phase);
            Assert.Equal("密码读取失败，请重新填写", c.Current.Reason);        // 非空 ⇒ presenter 放行按钮
            Assert.Equal(0, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));  // 包没交出去
            Assert.Equal(2, h.Requests.Count);                                       // 也只敲了探测与取 IP
            // 恰好一行：再多一行就是同一个故障说了两遍，少一行就是诊断包里查无此事
            var line = Assert.Single(log.RecentLines(), l => l.Contains("Coordinator"));
            Assert.Contains("IOException", line);
            Assert.Contains("secret.bin 被别的进程独占", line);
            Assert.Contains("zut-stu", line);
            var p = StatusPresenter.Of(c.Current, ssidWhitelisted: true);
            Assert.True(p.LoginEnabled);
            Assert.True(p.ReprobeEnabled);
            Assert.Empty(notes);                             // 中止不通知，这一格也不通知：话已经在界面上
            Assert.Contains(seen, s => s.Phase == AppPhase.LoggingIn);   // 确实走到过"正在登录"那一格
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// 取凭据的地方有两处：首扣与退避里的每一次重试。只修第一处，同学看到的还是同一句假话，
    /// 只是这一次发生在退避之后 —— 界面停在"正在登录…（已失败 1 次，2 秒后重试）"。
    /// 同一个终态、同一行记录，而且那一次重试的登录包绝不带着坏凭据交出去。
    [Fact]
    public async Task 退避重试时密码读不出来也是同一格终态()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwcred2" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new TransactionLog(new FakeClock(), dir);
            var h = new FakeHttpHandler();
            h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));                                 // 探测
            h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));         // 取 IP
            h.EnqueueRaw(Fixtures.Read("login_reject_pwerr.txt"));                             // 首扣被拒 → 退避
            h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));         // 重试前再取一次 IP
            var notes = new List<(NoticeKind, string?)>();
            var calls = 0;
            var c = Build(h, notes, log: log, credentials: () =>
            {
                calls++;
                if (calls == 1) return new Credential("id", "pw", "@cmcc");     // 首扣拿得到
                throw new IOException("DPAPI 解不开了");                          // 退避之后就解不开
            });

            await c.HandleWifiChanged(Ap, default);

            Assert.Equal(2, calls);                                            // 重试那一次确实去取过凭据
            Assert.Equal(AppPhase.Failed, c.Current.Phase);
            Assert.Equal("密码读取失败，请重新填写", c.Current.Reason);
            Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));   // 只有首扣那一个包
            Assert.Contains(log.RecentLines(),
                l => l.Contains("Coordinator") && l.Contains("DPAPI 解不开了"));
            Assert.DoesNotContain(NoticeKind.LoginFailed, notes.Select(n => n.Item1));  // 也没被判成 GiveUp
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    static bool WaitUntil(Func<bool> condition, int timeoutMs) => SpinWait.SpinUntil(condition, timeoutMs);

    /// 与 CoordinatorHappyPathTests 同一个约定：真机上事件与 Current 同源，喂事件就要一起喂。
    static Task Changed(LoginCoordinator c, FakeWifiSource wifi, AccessPoint? ap)
    {
        wifi.Current = ap;
        return c.HandleWifiChanged(ap, default);
    }

    /// 第一个请求（旧接入点的探测）挂在这里，等测试把网络换掉之后再放行。
    sealed class HoldFirstRequest(HttpMessageHandler inner, Task gate) : DelegatingHandler(inner)
    {
        private int _seen;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _seen) == 1) await gate;
            return await base.SendAsync(request, ct);
        }
    }
}
