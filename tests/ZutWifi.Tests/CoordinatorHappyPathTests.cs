using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Portal;
using ZutWifi.Shell;
using ZutWifi.Tests.Support;
using ZutWifi.Wifi;
namespace ZutWifi.Tests;

/// 状态机主干：探测 → 取 IP → 登录 → 验证 → 在线。全部跑回放替身，零真实网络。
public class CoordinatorHappyPathTests
{
    private static readonly AccessPoint Ap = new("zut-stu", "10.133.126.113", "02a1b2c3d4e5");

    /// 真机上"事件"和"wifi.Current"是同一份读数的两个出口：WifiSentinel 先写 Current 再发事件，
    /// 所以 HandleWifiChanged(ap) 被调用时 Current 必然已经是 ap（或更新的接入点）。
    /// 协调器现在会拿 Current 判"这一轮是不是已经被取代了"，所以替身必须跟着一起动 ——
    /// 手工喂事件却不喂 Current 的写法，测的是一条真机上不存在的状态。
    static Task Changed(LoginCoordinator c, FakeWifiSource wifi, AccessPoint? ap, CancellationToken ct = default)
    {
        wifi.Current = ap;
        return c.HandleWifiChanged(ap, ct);
    }

    static LoginCoordinator New(HttpMessageHandler handler, List<AppStatus> seen,
        List<(NoticeKind, string?)> notes, FakeWifiSource wifi, IConnectivityProbe? probe = null,
        Settings? settings = null)
        => new(new PortalGateway(new HttpClient(handler), "1.1.1.1"),
            wifi, () => new Credential("id", "pw", "@cmcc"), settings ?? new Settings(),
            new FakeClock(), probe ?? new FakeProbe(true), seen.Add, (k, d) => notes.Add((k, d)));

    static (LoginCoordinator c, List<AppStatus> seen, List<(NoticeKind, string?)> notes, FakeHttpHandler h,
        FakeWifiSource wifi) Build(FakeHttpHandler h, IConnectivityProbe? probe = null)
    {
        var seen = new List<AppStatus>();
        var notes = new List<(NoticeKind, string?)>();
        var wifi = new FakeWifiSource();
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"),
            wifi, () => new Credential("id", "pw", "@cmcc"), new Settings(),
            new FakeClock(), probe ?? new FakeProbe(true), seen.Add, (k, d) => notes.Add((k, d)));
        return (c, seen, notes, h, wifi);
    }

    static void QueueUnauthenticatedLoginSuccess(FakeHttpHandler h)
    {
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
    }

    /// 一次成功登录只真实消耗 3 个请求（探测 / 取 IP / 登录）。
    /// QueueUnauthenticatedLoginSuccess 末尾那份 online_9002 是给"已认证"分支用的样本，
    /// 需要精确对齐队列的用例走这里，免得把下一个响应喂错给下一步。
    static void QueueLoginOnly(FakeHttpHandler h)
    {
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
    }

    [Fact]
    public async Task 连上校园网且未认证时自动登录成功并发通知()
    {
        var h = new FakeHttpHandler();
        QueueUnauthenticatedLoginSuccess(h);
        var (c, seen, notes, _, wifi) = Build(h);
        await Changed(c, wifi, Ap);
        Assert.Equal(AppPhase.Online, c.Current.Phase);
        Assert.Contains(AppPhase.LoggingIn, seen.Select(s => s.Phase));
        Assert.Single(notes);
        Assert.Equal(NoticeKind.LoginSucceeded, notes[0].Item1);
        Assert.Contains("耗时", notes[0].Item2);
    }

    [Fact]
    public async Task 连接非白名单SSID时零网络请求零通知()
    {
        var h = new FakeHttpHandler();
        var (c, _, notes, _, wifi) = Build(h);
        await Changed(c, wifi, new AccessPoint("TP-LINK", "192.168.1.5", "aabbccddeeff"));
        Assert.Equal(AppPhase.Idle, c.Current.Phase);
        Assert.Empty(h.Requests);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task 白名单可配置多值()
    {
        var h = new FakeHttpHandler();
        QueueUnauthenticatedLoginSuccess(h);
        var seen = new List<AppStatus>(); var notes = new List<(NoticeKind, string?)>();
        var zutLib = new AccessPoint("zut-lib", "10.133.1.9", "aabbccddeeff");
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"),
            new FakeWifiSource { Current = zutLib }, () => new Credential("id", "pw", "@cmcc"),
            new Settings { SsidWhitelist = ["zut-stu", "zut-lib"] }, new FakeClock(), new FakeProbe(true),
            seen.Add, (k, d) => notes.Add((k, d)));
        await c.HandleWifiChanged(zutLib, default);
        Assert.Equal(AppPhase.Online, c.Current.Phase);
    }

    [Fact]
    public async Task 探测到已认证时不发登录包也不发通知但记录时长()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        var (c, seen, notes, _, wifi) = Build(h);
        await Changed(c, wifi, Ap);
        Assert.Equal(AppPhase.Online, c.Current.Phase);
        Assert.Equal(4211, c.Current.OnlineSeconds);
        // 光有秒数不够：界面上那个秒表要靠这个锚点才知道"这是哪一刻读到的"。
        Assert.NotNull(c.Current.OnlineAtTickMs);
        Assert.Equal(4216, c.Current.SecondsAt(c.Current.OnlineAtTickMs!.Value + 5_000));
        Assert.DoesNotContain(AppPhase.LoggingIn, seen.Select(s => s.Phase));
        Assert.Empty(notes);
        Assert.DoesNotContain(h.Requests, r => r.Uri.Query.Contains("a=Login"));
    }

    [Fact]
    public async Task 认证成功但外网不通进入Degraded并发警告通知()
    {
        var h = new FakeHttpHandler();
        QueueUnauthenticatedLoginSuccess(h);
        var (c, _, _, _, wifi) = Build(h, new FakeProbe(online: false));
        await Changed(c, wifi, Ap);
        Assert.Equal(AppPhase.Degraded, c.Current.Phase);
        // 橙色那一格会话是真建立起来了，秒表照样该走（不然它停在 00:00:00 上像坏了）。
        Assert.NotNull(c.Current.OnlineAtTickMs);
        Assert.Equal(3, c.Current.SecondsAt(c.Current.OnlineAtTickMs!.Value + 3_000));
    }

    [Fact]
    public async Task 门户IP字段缺失时回退网卡IP继续登录()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), "<html>没有 ss5 字段</html>");
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        var (c, _, _, _, wifi) = Build(h);
        await Changed(c, wifi, Ap);
        Assert.Equal(AppPhase.Online, c.Current.Phase);
        Assert.Contains("wlanuserip=10.133.126.113", h.Requests[2].Uri.Query);
    }

    [Fact]
    public async Task 同一连接会话内重复事件只刷新状态不重登()
    {
        var h = new FakeHttpHandler();
        QueueUnauthenticatedLoginSuccess(h);          // offline_9002, a70, login_success, online_9002
        h.EnqueueRaw(Fixtures.Read("online_9002.txt"));   // 第二次事件的 GetOnlineSeconds
        var (c, _, notes, _, wifi) = Build(h);
        await Changed(c, wifi, Ap);                   // 消耗 3 个请求
        await Changed(c, wifi, Ap);                   // 只 Probe + 取时长，消耗 2 个
        Assert.Single(notes);
        Assert.Equal(5, h.Requests.Count);
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
    }

    /// 会话身份不能只靠"界面现在是不是 Online"。上一版就是这么写的：被取消的那一轮恰好停在
    /// LoggingIn/Verifying，于是同一次连接的下一个事件又完完整整登了一遍。
    /// 门户回包的那一刻取消 = 登录包真的交出去了，这一轮的会话就已经存在，绝不再交第二个包。
    [Fact]
    public async Task 登录包已提交后被取消时同一次连接不再重登()
    {
        var cts = new CancellationTokenSource();
        var queued = new FakeHttpHandler();
        QueueLoginOnly(queued);                                   // 探测 + 取 IP + 登录（真的发出去）
        queued.EnqueueRaw(Fixtures.Read("offline_9002.txt"));      // 下一个事件的探测：仍然"没认证"
        var wifi = new FakeWifiSource { Current = Ap };
        var seen = new List<AppStatus>(); var notes = new List<(NoticeKind, string?)>();
        var c = New(new CancelOnLoginResponse(queued, cts), seen, notes, wifi);

        await c.HandleWifiChanged(Ap, cts.Token);
        Assert.Equal(1, queued.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        // 这一轮不下"在线"结论，但它确实有一个终态：包已交出 ⇒ Failed + "登录已中止"
        // （见下一条用例）。原来这句注释写的是"被取消的一轮不下结论"，从 bd50871 起就不成立了 ——
        // 什么都不发正是那条点不动按钮的死路，评审要的就是把它落成一个可操作的终态。
        Assert.DoesNotContain(AppPhase.Online, seen.Select(s => s.Phase));

        await Changed(c, wifi, Ap);                               // 同一次连接的重复事件（抖动/唤醒补报）

        Assert.Equal(1, queued.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.Empty(notes);                                      // 也没补弹出任何提示
    }

    /// 上一条评论指出的死路：提交 → 取消之后，界面停在"正在登录…"。
    /// StatusPresenter 在 LoggingIn / Verifying 下把"登录"和"重新检测"全禁用，而这一轮的会话键已经记下
    /// ⇒ 同键事件只走刷新，刷新在非 Online/Degraded 时一个请求都不发（RefreshCoreAsync 的相位门），
    /// 60 秒定时器也就救不回来。能救场的只剩"换个接入点"或"断开"。
    /// 判据因此不看"状态好不好看"，只看 StatusPresenter.Of 给出的按钮可用性。
    [Fact]
    public async Task 提交登录后被取消时落到可操作的失败态且同键事件不再交第二个包()
    {
        var cts = new CancellationTokenSource();
        var h = new FakeHttpHandler();
        QueueLoginOnly(h);                                        // 探测 + 取 IP + 登录（包真的交出去了）
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));           // 之后同键事件的探测：依旧"没认证"
        var wifi = new FakeWifiSource { Current = Ap };
        var seen = new List<AppStatus>(); var notes = new List<(NoticeKind, string?)>();
        var c = New(new CancelWhileAwaitingLogin(h, cts), seen, notes, wifi);

        await c.HandleWifiChanged(Ap, cts.Token);                 // 响应在途时被取消：门户把 OCE 吞成传输错误

        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.Equal(AppPhase.Failed, c.Current.Phase);            // 不再是把按钮锁死的 LoggingIn
        Assert.Equal("登录已中止", c.Current.Reason);                // 非空 ⇒ 界面渲染得出这句话
        var p = StatusPresenter.Of(c.Current, ssidWhitelisted: true);
        Assert.True(p.LoginEnabled);                              // 派单说的"用户点登录"这条路真的存在
        Assert.True(p.ReprobeEnabled);
        Assert.DoesNotContain(AppPhase.Online, seen.Select(s => s.Phase));   // 被中止的一轮不下在线结论
        Assert.Empty(notes);                                       // 用户自己按的取消，不再补一条提示

        await Changed(c, wifi, Ap);                               // 同一次连接的重复事件（抖动/唤醒补报）

        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.Empty(notes);
    }

    /// 探针在途时取消：真机上 HttpConnectivityProbe 不是"返回 false"，最后一轮的
    /// `await Task.Delay(2s, ct)` 会把 TaskCanceledException 直接抛给调用方。
    /// 这条异常同样不许把界面留在"已认证 · 正在测试连通性"（那个阶段按钮也全是禁用的）。
    [Fact]
    public async Task 连通性探测抛出取消异常时同样落到可操作的失败态()
    {
        var h = new FakeHttpHandler();
        QueueLoginOnly(h);
        var cts = new CancellationTokenSource();
        var seen = new List<AppStatus>(); var notes = new List<(NoticeKind, string?)>();
        var c = New(h, seen, notes, new FakeWifiSource { Current = Ap }, new CancellingAndThrowingProbe(cts));

        await c.HandleWifiChanged(Ap, cts.Token);

        Assert.Equal(AppPhase.Failed, c.Current.Phase);
        Assert.Equal("登录已中止", c.Current.Reason);
        Assert.True(StatusPresenter.Of(c.Current, ssidWhitelisted: true).LoginEnabled);
        Assert.DoesNotContain(AppPhase.Online, seen.Select(s => s.Phase));
        Assert.Empty(notes);
    }

    /// 评审第 4 项：提交**之前**的那几道闸门（RunLockedAsync 里探测/取 IP 前后）是同一个死路的另一半。
    /// Probing / AcquiringIp / LoggingIn 在 StatusPresenter 下把登录和重新检测全禁用，而这一轮
    /// 一个登录包都没交出去 ⇒ 事件不会再来（去抖 + 会话键都没记）、60 秒定时器的刷新又有相位门，
    /// 界面就只能等人换个网络才能解锁。既然门户那边什么都没收到，Idle（带 SSID）才是诚实的答案，
    /// presenter 在 Idle 下放行 登录/重新检测。
    [Fact]
    public async Task 提交登录前被取消的一轮回到Idle而不是卡在进行中()
    {
        var cts = new CancellationTokenSource();
        var h = new FakeHttpHandler();
        QueueLoginOnly(h);                                    // 探测（取消就落在这条之后）+ 取 IP + 登录
        var seen = new List<AppStatus>(); var notes = new List<(NoticeKind, string?)>();
        var wifi = new FakeWifiSource { Current = Ap };
        var c = New(new CancelAfterFirstResponse(h, cts), seen, notes, wifi);

        await c.HandleWifiChanged(Ap, cts.Token);

        Assert.Single(h.Requests);                            // 探测之后就收手：没取 IP
        Assert.Equal(0, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));   // 一个登录包都没交
        Assert.Contains(AppPhase.Probing, seen.Select(s => s.Phase));               // 确实进过"检测中…"
        Assert.Equal(AppPhase.Idle, c.Current.Phase);         // 不再是把按钮锁死的 Probing
        Assert.Equal("zut-stu", c.Current.Ssid);              // 带 SSID：界面说的是"连上了，还没检测"
        Assert.Null(c.Current.Reason);                        // Idle 的硬契约（非空会被界面当结论显示）
        var p = StatusPresenter.Of(c.Current, ssidWhitelisted: true);
        Assert.True(p.LoginEnabled);
        Assert.True(p.ReprobeEnabled);
        Assert.Empty(notes);                                  // 人自己按的取消，不补提示
    }

    // ── 以下为契约补强用例：Unknown 极性、Reason 非空、Idle 清原因、取消静默中止 ──────────

    /// 状态文案把 Reason 直接拼进 UI（StatusPresenter 无兜底），所以"该有原因的阶段不能没原因"
    /// 是协调器对界面的硬契约，不是风格偏好。Idle 也一样：留着的 Reason 会被当成这一次的结果显示。
    static void AssertReasonContract(IEnumerable<AppStatus> seen)
    {
        foreach (var s in seen)
        {
            switch (s.Phase)
            {
                case AppPhase.Failed or AppPhase.GiveUp or AppPhase.Degraded:
                    Assert.False(string.IsNullOrWhiteSpace(s.Reason),
                        $"阶段 {s.Phase} 必须带非空 Reason，否则界面渲染出 失败： 这种半截文案");
                    break;
                case AppPhase.Probing or AppPhase.AcquiringIp or AppPhase.Online or AppPhase.Idle:
                    Assert.Null(s.Reason);
                    break;
            }
        }
    }

    [Fact]
    public async Task 探测结果未知时不当作已认证仍然走登录()
    {
        // AuthState.Unknown（超时/答非所问）的含义是"没证明已认证"，不是"已认证"。
        // 门户登录幂等，重复登录回 认证IP已在线，由 Task 12 处理，所以这里宁可多登一次。
        var h = new FakeHttpHandler();
        h.Enqueue(200, Array.Empty<(string, string)>(), "<html>门户答非所问</html>");   // Probe → Unknown
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        var (c, seen, _, _, wifi) = Build(h);
        await Changed(c, wifi, Ap);
        Assert.Equal(AppPhase.Online, c.Current.Phase);
        Assert.Contains(AppPhase.AcquiringIp, seen.Select(s => s.Phase));
        Assert.Contains(AppPhase.LoggingIn, seen.Select(s => s.Phase));
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
    }

    [Fact]
    public async Task 降级绝不静默既有警告通知也带原因()
    {
        var h = new FakeHttpHandler();
        QueueUnauthenticatedLoginSuccess(h);
        var (c, _, notes, _, wifi) = Build(h, new FakeProbe(online: false));
        await Changed(c, wifi, Ap);
        Assert.Equal(AppPhase.Degraded, c.Current.Phase);
        Assert.False(string.IsNullOrWhiteSpace(c.Current.Reason));
        var note = Assert.Single(notes);
        Assert.Equal(NoticeKind.Degraded, note.Item1);
        Assert.False(string.IsNullOrWhiteSpace(note.Item2));
    }

    [Fact]
    public async Task 认证失效进入Failed带原因且只通知一次不自动重登()
    {
        var h = new FakeHttpHandler();
        QueueLoginOnly(h);
        var seen = new List<AppStatus>(); var notes = new List<(NoticeKind, string?)>();
        var wifi = new FakeWifiSource { Current = Ap };
        var c = New(h, seen, notes, wifi);
        await c.HandleWifiChanged(Ap, default);
        Assert.Equal(AppPhase.Online, c.Current.Phase);

        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));   // 会话被网关悄悄掐掉
        await c.RefreshAsync(default);

        Assert.Equal(AppPhase.Failed, c.Current.Phase);
        Assert.False(string.IsNullOrWhiteSpace(c.Current.Reason));
        Assert.Single(notes, n => n.Item1 == NoticeKind.AuthExpired);   // 失效只提示这一条
        // 刷新只读不写：RefreshAsync 永不触发登录包，重登交给用户点按钮或 Task 12 的退避。
        // 只看刷新窗口的话还得靠 Skip，所以这里直接钉死总数，比负向断言更难被绕过去。
        Assert.Equal(1, h.Requests.Count(r => r.Uri.Query.Contains("a=Login")));
        Assert.Equal(4, h.Requests.Count);
    }

    [Fact]
    public async Task 每个发布过的状态都满足Reason契约()
    {
        var ok = new FakeHttpHandler(); QueueUnauthenticatedLoginSuccess(ok);
        var (c1, seen1, _, _, wifi1) = Build(ok);
        await Changed(c1, wifi1, Ap);

        var degraded = new FakeHttpHandler(); QueueUnauthenticatedLoginSuccess(degraded);
        var (c2, seen2, _, _, wifi2) = Build(degraded, new FakeProbe(online: false));
        await Changed(c2, wifi2, Ap);

        var expired = new FakeHttpHandler(); QueueLoginOnly(expired);
        expired.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        var seen3 = new List<AppStatus>(); var notes3 = new List<(NoticeKind, string?)>();
        var c3 = New(expired, seen3, notes3, new FakeWifiSource { Current = Ap });
        await c3.HandleWifiChanged(Ap, default);
        await c3.RefreshAsync(default);

        // 断言契约真的被覆盖到，而不是空转通过
        Assert.Contains(seen2, s => s.Phase == AppPhase.Degraded);
        Assert.Contains(seen3, s => s.Phase == AppPhase.Failed);
        Assert.Contains(AppPhase.Probing, seen1.Select(s => s.Phase));
        foreach (var seen in new[] { seen1, seen2, seen3 }) AssertReasonContract(seen);
    }

    [Fact]
    public async Task 切走非校园网进入Idle时清掉上一次的失败原因()
    {
        var h = new FakeHttpHandler();
        QueueUnauthenticatedLoginSuccess(h);
        var (c, seen, _, _, wifi) = Build(h, new FakeProbe(online: false));
        await Changed(c, wifi, Ap);
        Assert.False(string.IsNullOrWhiteSpace(c.Current.Reason));     // Degraded 带着原因

        await Changed(c, wifi, new AccessPoint("TP-LINK", "192.168.1.5", "aabbccddeeff"));
        Assert.Equal(AppPhase.Idle, c.Current.Phase);
        Assert.Null(c.Current.Reason);                                // 界面不能再显示上一次的原因
        AssertReasonContract(seen);
    }

    /// 验证阶段被取消：不发"登录成功"、不下在线结论，但界面必须落到还能按按钮的那一格。
    /// 原来这条断言的是"停在被取消前的最后一步（Verifying）"—— 而 Verifying 与 LoggingIn 一样把
    /// 登录/重新检测全禁用，正是本轮要拆掉的死路（见上面那条失败态用例）。
    [Fact]
    public async Task 连通性验证途中取消时不发在线通知但落到可操作的失败态()
    {
        var h = new FakeHttpHandler();
        QueueUnauthenticatedLoginSuccess(h);
        var cts = new CancellationTokenSource();
        var seen = new List<AppStatus>(); var notes = new List<(NoticeKind, string?)>();
        var c = New(h, seen, notes, new FakeWifiSource { Current = Ap }, new CancellingProbe(cts));
        await c.HandleWifiChanged(Ap, cts.Token);
        Assert.Empty(notes);                                          // 不弹"登录成功"
        Assert.DoesNotContain(AppPhase.Online, seen.Select(s => s.Phase));
        Assert.Equal(AppPhase.Failed, c.Current.Phase);                // 中止的终态，而不是禁按钮的 Verifying
        Assert.Equal("登录已中止", c.Current.Reason);
        Assert.True(StatusPresenter.Of(c.Current, ssidWhitelisted: true).LoginEnabled);
    }

    [Fact]
    public async Task 令牌已取消时零门户请求零状态发布()
    {
        var h = new FakeHttpHandler();
        QueueUnauthenticatedLoginSuccess(h);
        var (c, seen, notes, _, wifi) = Build(h);
        await Changed(c, wifi, Ap, new CancellationToken(canceled: true));
        Assert.Empty(h.Requests);
        Assert.Empty(seen);
        Assert.Empty(notes);
        Assert.Equal(AppPhase.Idle, c.Current.Phase);
    }

    [Fact]
    public async Task Start之后真实WiFi事件即可驱动整条登录链()
    {
        var h = new FakeHttpHandler();
        QueueLoginOnly(h);
        var seen = new List<AppStatus>(); var notes = new List<(NoticeKind, string?)>();
        var wifi = new FakeWifiSource();                       // Current 由 Raise() 赋值，走事件路径
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var c = new LoginCoordinator(new PortalGateway(new HttpClient(h), "1.1.1.1"),
            wifi, () => new Credential("id", "pw", "@cmcc"), new Settings(), new FakeClock(),
            new FakeProbe(true),
            s => { seen.Add(s); if (s.Phase == AppPhase.Online) online.TrySetResult(); },
            (k, d) => notes.Add((k, d)));
        c.Start();
        wifi.Raise(Ap);
        await online.Task.WaitAsync(TimeSpan.FromSeconds(5));   // 超时失败而不是挂死整条流水线
        Assert.Equal(AppPhase.Online, c.Current.Phase);
        Assert.Single(notes);
    }

    /// 登录包已经交给门户、响应也回来了，就在这一刻取消——客户端之后的收尾会抛，
    /// 但账号已经上去了：这一轮的会话键必须已经记下。
    sealed class CancelOnLoginResponse(HttpMessageHandler inner, CancellationTokenSource src)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.Query.Contains("a=Login")) src.Cancel();
            return response;
        }
    }

    /// 模拟"验证跑一半用户切了 WiFi"：探针返回前把令牌取消掉。
    sealed class CancellingProbe(CancellationTokenSource src) : IConnectivityProbe
    {
        public Task<bool> IsOnlineAsync(CancellationToken ct)
        {
            src.Cancel();
            return Task.FromResult(true);
        }
    }

    /// 登录请求已经写进 handler 的请求清单（= 包已经交给门户），就在等响应的时候取消 ⇒ 抛 OCE。
    /// PortalGateway 把 TaskCanceledException 吞成"门户不可达"的传输错误，所以协调器看到的是
    /// 一个失败结果 + 一个已取消的令牌 —— 真机上"提交后被取消"就是这个样子。
    sealed class CancelWhileAwaitingLogin(HttpMessageHandler inner, CancellationTokenSource src)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);      // 先让内层把这条请求记进 Requests
            if (request.RequestUri!.Query.Contains("a=Login"))
            {
                src.Cancel();
                throw new TaskCanceledException("登录响应在途时被取消");
            }
            return response;
        }
    }

    /// 被取消时不返回 false，而是像真探针那样把 TaskCanceledException 抛出去。
    sealed class CancellingAndThrowingProbe(CancellationTokenSource src) : IConnectivityProbe
    {
        public Task<bool> IsOnlineAsync(CancellationToken ct)
        {
            src.Cancel();
            throw new TaskCanceledException("连通性探测被取消");
        }
    }

    /// 第一个响应（探测）回到客户端之后立刻取消令牌：门户那边一切正常，取消恰好落在
    /// "探测已返回、登录包还没交出去"之间那道闸门上 —— 提交前中止就是这个形状。
    /// （在 SendAsync 里取消会让门户把 OCE 吞成传输错误，那就变成提交后的形状了。）
    sealed class CancelAfterFirstResponse(HttpMessageHandler inner, CancellationTokenSource src)
        : DelegatingHandler(inner)
    {
        private int _seen;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (Interlocked.Increment(ref _seen) == 1) src.Cancel();
            return response;
        }
    }
}
