using System.Runtime.InteropServices;
using Windows.UI.Notifications;
using ZutWifi.Core;
using ZutWifi.Notify;
using ZutWifi.Tests.Support;

namespace ZutWifi.Tests;

/// spec 第 8 节的文案表 + 「通知中心不可用一定回退气泡」这条契约。
/// 真机投递（快捷方式被 shell 接受、通知进通知中心、点击回调）离线证不了，推迟到 Task 20；
/// 这里只测不弹窗就能确定的部分，其中投递那一步通过 Deliver 接缝替换，测试永不触达真实 Toast。
/// 回退有四条通路：Show 同步抛异常、WinRT 的 Failed 事件（异步）、通知器 Setting 被禁、组合根强制气泡。
/// 本文件负责的是"契约那一头"（`Notify` 的 catch 名单、文案、`Fallback` 这个出口本身）；
/// "Show 本体那三步判断在运行时真的执行了"由 `NotifierDeliveryTests` 用假通道逐条跑过——
/// 第二轮评审点名的正是这条：这里把 Deliver 一换，真 Show 就连同一句 `toast.Failed +=` 一起消失了。
public class NotifierTextTests
{
    // ---------- 标题：spec 第 8 节 ----------

    [Theory]
    [InlineData(NoticeKind.LoginSucceeded, "校园网已登录")]
    [InlineData(NoticeKind.LoginFailed, "校园网登录失败")]
    [InlineData(NoticeKind.Degraded, "已认证，但暂时上不了网")]
    [InlineData(NoticeKind.AlreadyOnlineElsewhere, "账号已在别处在线")]
    [InlineData(NoticeKind.AuthExpired, "校园网认证已失效")]
    [InlineData(NoticeKind.LogoutSucceeded, "校园网已注销")]
    [InlineData(NoticeKind.LogoutFailed, "校园网注销失败")]
    public void 标题与spec第八节一致(NoticeKind kind, string expected)
        => Assert.Equal(expected, Notifier.TextFor(kind, "详情").Title);

    // ---------- 正文 ----------

    [Fact]
    public void 失败正文含原因与操作提示()
    {
        var body = Notifier.TextFor(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）").Body;
        Assert.Contains("Radius 认证失败（账号或密码错误）", body);
        Assert.Contains("点击查看详情", body);
    }

    [Fact]
    public void 一键重登提示写在正文里()
        => Assert.Contains("注销并重登", Notifier.TextFor(NoticeKind.AlreadyOnlineElsewhere, null).Body);

    [Fact]
    public void 成功正文带SSID与耗时原文()
        => Assert.Equal("zut-stu · 耗时 1.8s", Notifier.TextFor(NoticeKind.LoginSucceeded, "zut-stu · 耗时 1.8s").Body);

    /// 协调器实际发的那几句细节（LoginCoordinator 里的字面量），拼出来必须正好是 spec 表里的正文。
    /// Task 12 侧的用例从另一头断言同一批字符串，两边对不上就会红。
    [Theory]
    [InlineData(NoticeKind.Degraded, "门户返回成功，外网未通，可点重新检测", "门户返回成功，外网未通，可点重新检测")]
    [InlineData(NoticeKind.AlreadyOnlineElsewhere, "可能是旧租约残留，点一下可注销并重登", "可能是旧租约残留，点一下可注销并重登")]
    [InlineData(NoticeKind.AuthExpired, "需要重新登录", "需要重新登录")]
    [InlineData(NoticeKind.LogoutSucceeded, "zut-stu", "zut-stu")]
    [InlineData(NoticeKind.LoginFailed, "认证IP已在线", "认证IP已在线 · 点击查看详情")]
    [InlineData(NoticeKind.LogoutFailed, "系统忙请稍后重试", "系统忙请稍后重试 · 点击查看详情")]
    public void 协调器实发的细节拼出spec正文(NoticeKind kind, string detail, string expectedBody)
        => Assert.Equal(expectedBody, Notifier.TextFor(kind, detail).Body);

    /// detail 为 null（协调器还没拿到原因）时的兜底句：不能是空串，否则通知里一句人话都没有。
    [Theory]
    [InlineData(NoticeKind.LoginFailed)]
    [InlineData(NoticeKind.Degraded)]
    [InlineData(NoticeKind.AlreadyOnlineElsewhere)]
    [InlineData(NoticeKind.AuthExpired)]
    [InlineData(NoticeKind.LogoutSucceeded)]
    [InlineData(NoticeKind.LogoutFailed)]
    public void 没有细节时正文自带兜底句(NoticeKind kind)
    {
        var (title, body) = Notifier.TextFor(kind, null);
        Assert.NotEmpty(title);
        Assert.NotEmpty(body);
    }

    [Fact]
    public void 未认识的类型退化为程序名而不是崩()
        => Assert.Equal(("ZutWifi", "细节"), Notifier.TextFor((NoticeKind)999, "细节"));

    // ---------- 回退契约 ----------

    private static readonly Exception[] 通知中心会挂的异常 =
    [
        new ArgumentException("AUMID 未注册"),
        new InvalidOperationException("通知被组策略禁用"),
        new NullReferenceException("投影内部炸了"),
        new COMException("0x80070490"),
    ];

    private static Notifier With(
        Action<string, string, NoticeKind> deliver,
        List<(string Title, string? Body)>? balloons = null,
        List<NoticeKind>? activated = null)
        => new(AumidRegistrar.Aumid)
        {
            Deliver = deliver,
            BalloonFallback = balloons is null ? null : (t, b) => balloons.Add((t, b)),
            Activated = activated is null ? null : k => activated.Add(k),
        };

    [Theory]
    [MemberData(nameof(通知中心会挂的异常数据))]
    public void 投递抛异常时气泡接手且activated补一发(Exception ex)
    {
        var balloons = new List<(string, string?)>();
        var activated = new List<NoticeKind>();
        var n = With((_, _, _) => throw ex, balloons, activated);

        n.Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");

        var (title, body) = Notifier.TextFor(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");
        Assert.Equal(new[] { (title, (string?)body) }, balloons);   // 只发一次，文案与 Toast 完全一致
        Assert.Equal(new[] { NoticeKind.LoginFailed }, activated);  // 气泡没有点击回调，至少让窗口知道发生了什么
    }

    public static TheoryData<Exception> 通知中心会挂的异常数据 => new(通知中心会挂的异常);

    /// 回退带上的是哪一条通知的文案：逐个类型核对，避免只在一种类型上碰巧对。
    [Theory]
    [InlineData(NoticeKind.LoginSucceeded)]
    [InlineData(NoticeKind.LoginFailed)]
    [InlineData(NoticeKind.Degraded)]
    [InlineData(NoticeKind.AlreadyOnlineElsewhere)]
    [InlineData(NoticeKind.AuthExpired)]
    [InlineData(NoticeKind.LogoutSucceeded)]
    [InlineData(NoticeKind.LogoutFailed)]
    public void 回退气泡的标题正文与该类型的文案一致(NoticeKind kind)
    {
        var balloons = new List<(string, string?)>();
        var n = With((_, _, _) => throw new InvalidOperationException(), balloons);
        var detail = kind + "-detail";

        n.Notify(kind, detail);

        Assert.Single(balloons);
        Assert.Equal(Notifier.TextFor(kind, detail), (balloons[0].Item1, balloons[0].Item2));
    }

    [Fact]
    public void 投递成功时不再发气泡()
    {
        var balloons = new List<(string, string?)>();
        var activated = new List<NoticeKind>();
        var n = With((_, _, _) => { }, balloons, activated);

        n.Notify(NoticeKind.Degraded, "门户返回成功，外网未通，可点重新检测");

        Assert.Empty(balloons);      // 双通道同时发会让人看到两条重复通知
        Assert.Empty(activated);     // Toast 的 Activated 只在用户点击时触发，不能在投递时就当"已点击"
    }

    [Fact]
    public void 投递拿到的是拼好的标题正文与类型()
    {
        var seen = new List<(string, string, NoticeKind)>();
        var n = With((t, b, k) => seen.Add((t, b, k)));

        n.Notify(NoticeKind.LogoutFailed, "门户拒绝");

        Assert.Equal(new[] { ("校园网注销失败", "门户拒绝 · 点击查看详情", NoticeKind.LogoutFailed) }, seen);
    }

    [Fact]
    public void 没有装配气泡时通知异常也不外抛()
    {
        // Task 17 之前 Notifier 可能没人接 BalloonFallback；通知层不能反过来把状态机打断。
        var n = With((_, _, _) => throw new COMException("通知中心不可用"));
        n.Notify(NoticeKind.AuthExpired, "需要重新登录");
    }

    /// 只放行已知的那几类投递异常：其余（比如接缝自己被误改成 null、文案逻辑里的 bug）必须冒泡，
    /// 否则真实的编程错误会被"回退"伪装成一次正常的气球提示，谁也发现不了。
    [Fact]
    public void 名单外的异常不被当成投递失败()
    {
        var n = With((_, _, _) => throw new NotSupportedException("这不是通知中心的问题"));
        Assert.Throws<NotSupportedException>(() => n.Notify(NoticeKind.LoginSucceeded, "x"));
    }

    [Fact]
    public void 默认接缝非空否则通知会被静默丢弃()
        => Assert.NotNull(new Notifier(AumidRegistrar.Aumid).Deliver);

    /// 组合根走的是**带 sink 的那一个构造**：两条出口（通知中心的通道工厂、托盘气泡）都来自
    /// 递进来的那一份，于是"真投递"在装配这条路上一次都没出现过。
    /// 这一条与 `AppContextTests` 的两条守门用例各管一头：那里数的是"桌面上没弹东西"，
    /// 这里数的是"接进来的就是递进来的那一个"——少挂一条，通知就会静默地走回真出口。
    [Fact]
    public void 装配用的构造把两条出口都接到注入的出口上()
    {
        var os = new RecordingSink();
        var n = new Notifier(AumidRegistrar.Aumid, os.Sink);
        Assert.Equal(os.Sink.OpenToastChannel, n.ChannelFactory);
        Assert.Equal(os.Sink.ShowBalloon, n.BalloonFallback);

        n.Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");

        Assert.Single(os.ToastAttempts);                       // 通知中心那一张脸 = 记账器那一张
        Assert.Single(os.PushedToasts);
        Assert.Empty(os.Balloons);                             // 通道可用 ⇒ 不许回退
    }

    /// 同一个构造在"AUMID 注册失败"那一格也一样：气泡直接落在注入的出口上，
    /// 一条都不碰通知中心 —— 生产里 BalloonOnly 判死之后的那条看得见的路就是它。
    [Fact]
    public void 装配用的构造在强制气泡下也只碰注入的出口()
    {
        var os = new RecordingSink();
        var n = new Notifier(AumidRegistrar.Aumid, os.Sink) { BalloonOnly = true };

        n.Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");

        Assert.Empty(os.ToastAttempts);
        Assert.Equal(new[] { ("校园网登录失败", (string?)"Radius 认证失败（账号或密码错误） · 点击查看详情") },
            os.Balloons);
    }

    /// Deliver 的默认实现是懒-materialize 的，而它可能第一次被读到的地方是 WinRT 的回调线程
    /// （Toast 的 Activated / Failed 都上线程池）。裸 `??=` 在那里会让两个线程各拿到一份委托实例。
    /// 这一条只是回归网（并发命中才有机会红），真正的保证是 Notifier 里那把锁。
    [Fact]
    public void 并发取默认投递拿到的是同一个实例()
    {
        var n = new Notifier(AumidRegistrar.Aumid);
        var seen = new System.Collections.Concurrent.ConcurrentBag<Delegate>();
        Parallel.For(0, 64, _ => seen.Add(n.Deliver));
        Assert.Single(seen.Distinct(ReferenceEqualityComparer.Instance));
    }

    // ---------- 投递失败的三条通路（评审：只有 Show 抛异常才回退，接不住最主要的失效方式） ----------

    /// 异步失败（WinRT 的 Failed 事件：通知中心接了单却没弹出来）必须落到与同步异常同一个出口。
    /// 这一条钉的是 `Fallback` 那一段（三段各自兜住、气泡文案、Activated 补发、Faulted 收原因）：
    /// 它从 `Deliver` 接缝里直接调 `n.Fallback(...)`，所以它是"出口的契约"，
    /// 而不是"Failed 那条路接通了"的证据——后者在 `NotifierDeliveryTests.通知被静默丢弃时Failed回调把气泡叫起来`，
    /// 那条走的是真的 `ToastDelivery.Show` + `Attach`，只有事件本身（`toast.Failed +=` 那一行）留给 Task 20。
    [Fact]
    public void 异步投递失败走的是与异常同一条气泡通路()
    {
        var balloons = new List<(string, string?)>();
        var activated = new List<NoticeKind>();
        var faults = new List<string>();
        var n = new Notifier(AumidRegistrar.Aumid)
        {
            BalloonFallback = (t, b) => balloons.Add((t, b)),
            Activated = k => activated.Add(k),
            Faulted = reason => faults.Add(reason),
        };
        n.Deliver = (title, body, kind) => n.Fallback(title, body, kind, "通知中心没显示这条通知");

        n.Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");

        var (title2, body2) = Notifier.TextFor(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");
        Assert.Equal(new[] { (title2, (string?)body2) }, balloons);      // 与同步异常那一条的观测完全一致
        Assert.Equal(new[] { NoticeKind.LoginFailed }, activated);
        Assert.Equal(new[] { "通知中心没显示这条通知" }, faults);          // 为什么降级要留得下来（Task 17 落诊断包）
    }

    /// 回退通路自己不许把进程带崩：气泡与 Faulted 都是装配方给的委托，跑在线程池回调上，
    /// 那里没人 catch，抛出去就是整个程序挂掉。
    [Fact]
    public void 回退通路里装配方抛异常也不外抛()
    {
        var n = new Notifier(AumidRegistrar.Aumid)
        {
            BalloonFallback = (_, _) => throw new InvalidOperationException("托盘已经 Dispose 了"),
            Faulted = _ => throw new NotSupportedException("日志写不下去"),
        };
        n.Deliver = (title, body, kind) => n.Fallback(title, body, kind, "通知中心没显示这条通知");
        var ex = Record.Exception(() => n.Notify(NoticeKind.AuthExpired, "需要重新登录"));
        Assert.Null(ex);
    }

    /// 评审第 2 项的另一半：`CreateToastNotifier(aumid).Setting` 不是 Enabled 时，
    /// Show 永远不会弹（组策略/用户设置/清单禁用），必须判定为"这个通道不可用"而不是静默。
    /// 本机拿不到真实的通知器，所以判据本身抽成纯函数来钉，不依赖环境。
    [Theory]
    [InlineData(NotificationSetting.Enabled, null)]                       // 只有这一个能用
    [InlineData(NotificationSetting.DisabledForApplication, "非 Enabled")]
    [InlineData(NotificationSetting.DisabledForUser, "非 Enabled")]
    [InlineData(NotificationSetting.DisabledByGroupPolicy, "非 Enabled")]
    [InlineData(NotificationSetting.DisabledByManifest, "非 Enabled")]
    public void 通知器设置不是已启用时报出原因(NotificationSetting setting, string? expectSomething)
    {
        var reason = ToastDelivery.WhyUnavailable(setting);
        Assert.True((expectSomething is null) == (reason is null),
            $"{setting} 应当 {(expectSomething is null ? "可用" : "不可用")}，实际给出：{reason ?? "null"}");
        if (expectSomething is not null) Assert.Contains(setting.ToString(), reason);  // 诊断里要说清是哪一种禁用
    }

    /// 评审第 3 项："add the missing `WhyUnavailable(Enabled)` case so every enum value is pinned"。
    /// 上面那条 Theory 五个值都在（`Enabled` 那行带 `null` 期望，从 64b5fe7 起就在），
    /// 但**列表是手写的**：投影里将来多一个枚举值，或者有人删掉一行，它不会红。
    /// 这一条补的是"每一个"那一半——枚举自己有什么我们就得问什么，且只放行 Enabled。
    /// 顺带钉住判据的失效方向是安全的：范围外的值（`Unknown`/将来新增的禁用种类）一律判"不可用"，
    /// 也就是宁可多发一条气泡，也不许把"说不清的状态"当成"可以用"从而静默丢通知。
    [Fact]
    public void 设置判据把枚举里的每一个值都钉住了()
    {
        var all = Enum.GetValues<NotificationSetting>();
        Assert.Equal(
        [
            NotificationSetting.Enabled,
            NotificationSetting.DisabledForApplication,
            NotificationSetting.DisabledForUser,
            NotificationSetting.DisabledByGroupPolicy,
            NotificationSetting.DisabledByManifest,
        ], all);   // 与 Windows.UI.Notifications.NotificationSetting 的成员逐个对上（多了/少了都要改判据）

        foreach (var setting in all.Append((NotificationSetting)99))   // 99：范围外，模拟将来冒出来的新值
            Assert.True(setting == NotificationSetting.Enabled == (ToastDelivery.WhyUnavailable(setting) is null),
                $"{setting} 应当 {(setting == NotificationSetting.Enabled ? "放行" : "判不可用")}，" +
                $"实际判据给出：{ToastDelivery.WhyUnavailable(setting) ?? "null（可用）"}");
    }

    // ---------- 强制气泡：AUMID 没注册成功时那条最主要的失效方式 ----------

    /// Ensure() 返回一句原因 ⇒ 通知中心这条路是死的，而 Show 不会抛异常（它只是不弹）。
    /// 所以组合根必须能把 Notifier 永久切到气泡，而不是等一个永远不会来的异常。
    [Fact]
    public void 强制气泡时一条通知都不碰通知中心()
    {
        var balloons = new List<(string, string?)>();
        var activated = new List<NoticeKind>();
        var n = new Notifier(AumidRegistrar.Aumid)
        {
            //  seam 一旦被碰到就红：BalloonOnly 下连 CreateToastNotifier 都不该走
            Deliver = (_, _, _) => throw new NotSupportedException("BalloonOnly 下不该再走通知中心"),
            BalloonFallback = (t, b) => balloons.Add((t, b)),
            Activated = k => activated.Add(k),
            BalloonOnly = true,
        };

        n.Notify(NoticeKind.LoginSucceeded, "zut-stu · 耗时 1.8s");
        n.Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");

        Assert.Equal(
        [
            ("校园网已登录", (string?)"zut-stu · 耗时 1.8s"),
            ("校园网登录失败", "Radius 认证失败（账号或密码错误） · 点击查看详情"),
        ],
        balloons);
        Assert.Equal(new[] { NoticeKind.LoginSucceeded, NoticeKind.LoginFailed }, activated);
    }

    /// 没装配气泡又强制了气泡模式：只能安静地什么都不做，绝不能反过来把状态机打断。
    [Fact]
    public void 强制气泡但没装配气泡时不外抛()
    {
        var n = new Notifier(AumidRegistrar.Aumid)
        {
            Deliver = (_, _, _) => throw new NotSupportedException("不该被调用"),
            BalloonOnly = true,
        };
        var ex = Record.Exception(() => n.Notify(NoticeKind.Degraded, null));
        Assert.Null(ex);
    }

    /// 默认是 false：没显式强制时仍然优先走通知中心（回退只是失败时的兜底，不是新默认）。
    [Fact]
    public void 不强制时仍然走投递接缝()
    {
        var delivered = new List<NoticeKind>();
        var n = new Notifier(AumidRegistrar.Aumid) { Deliver = (_, _, k) => delivered.Add(k) };
        n.Notify(NoticeKind.AuthExpired, "需要重新登录");
        Assert.Equal(new[] { NoticeKind.AuthExpired }, delivered);
    }

    // ---------- 真实 WinRT 的 XML 构造（只建文档，不 Show，所以不弹窗） ----------

    [Fact]
    public void Toast的XML把标题正文按顺序写进两个text节点()
    {
        var xml = ToastDelivery.Build("校园网已登录", "zut-stu · 耗时 1.8s").GetXml();

        // GetTemplateContent 若换了模板或节点数不足 2，Build 会抛 InvalidOperationException；
        // 这里核对标题正文确实进了 XML，且顺序是标题在前（ToastText02 的第 1 行是标题）。
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xml, "<text").Count);
        Assert.Contains("ToastText02", xml);
        Assert.True(xml.IndexOf("校园网已登录", StringComparison.Ordinal)
                    < xml.IndexOf("zut-stu · 耗时 1.8s", StringComparison.Ordinal), xml);
    }

    // ---------- AUMID 属性键：只能字面钉，往返自证不了 ----------

    /// PKEY_AppUserModel_ID 在 propkey.h 里是
    /// `DEFINE_PROPERTYKEY(PKEY_AppUserModel_ID, 0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 5)`，
    /// 即 fmtid `9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3`、pid `5`（微软那篇讲桌面 Toast 的
    /// System.AppUserModel.ID 文档用的是同一个）。
    ///
    /// 为什么必须拿常量本身做字面比对，而不是"写完再读回来"：写入端（`SetValue`）和自查端
    /// （`ReadAumid`）取的是同一个 `PKeyAppUserModelId`。键错时 `SetValue` 只是往属性库里塞了一个
    /// shell 不认识的属性，`Commit`/`Save` 一律返回 S_OK，读回也照样读到自己刚写的那一份——
    /// 上一版就是这么"往返全绿 + Ensure() 报成功 + NotifierFallbackUsed=false"地把每一条 Toast
    /// 静默丢给 shell 的。往返对得上只证明"用同一个键读同一个键"，证不了这个键是文档里那一个。
    ///
    /// 也不能靠环境：这台宿主里 propsys 整体不可用（下面那条往返用例因此挂着 Skip），
    /// 唯一还站得住的证据就是常量与文档值的字符串比对。
    [Fact]
    public void AppUserModelID属性键与文档逐字一致()
    {
        const string documented = "9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3";   // propkey.h / 微软文档原文
        Assert.Equal(documented, AumidRegistrar.AppUserModelIdFmtid.ToString("D"), ignoreCase: true);
        Assert.Equal(5, AumidRegistrar.AppUserModelIdPid);
        // 反着再钉一次：计划里那版把这个 GUID 抄错过一次（9F4C2855-9F7D-4B59-A873-894D85773B1A），
        // 光正着比的话，将来改回错值的人只要顺手把上面那句文档值一起改掉，这条照样绿。
        Assert.False(string.Equals("9F4C2855-9F7D-4B59-A873-894D85773B1A",
                AumidRegistrar.AppUserModelIdFmtid.ToString("D"), StringComparison.OrdinalIgnoreCase),
            "又退回那个不存在的属性键了：SetValue/Commit 会照样 S_OK，Toast 却一条都弹不出来");
    }

    // ---------- AUMID 注册：往返验证（当前环境跑不了，见下面的 Skip 理由） ----------

    /// 写进临时目录（不是开始菜单，所以不动用户的电脑），写完再从磁盘把属性读回来。
    /// 这一条过就能证明 IShellLinkW 的 vtable 顺序、宽字符封送、PropVariant 布局都对得上。
    /// 注意它**证不到属性键本身**：写和读用的是同一个常量，键错了两边照样对上
    /// （上一版就是这么全绿地静默丢 Toast 的）—— 那一半由上面
    /// `AppUserModelID属性键与文档逐字一致` 用字面比对钉住，两处都要过。
    ///
    /// 现在挂着 Skip：这台开发机的测试宿主里整个属性系统都拿不到（连普通 .txt 文件的 IPropertyStore
    /// 都是 E_NOINTERFACE，见 task-14 报告），也就是说这条测的是环境而不是代码。真机验证留到 Task 20：
    /// 在同学那类正常桌面上把 Skip 去掉跑一遍，再按 Win+N 看通知进不进通知中心。
    [Fact(Skip = "沙箱测试宿主里 propsys 不可用（.txt 的属性库都取不到），AUMID 写入通路推迟到 Task 20 真机验证")]
    public void 快捷方式落盘后读回的AUMID就是那一个()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zutwifi-aumid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var err = AumidRegistrar.EnsureIn(dir);
            Assert.True(err is null, "注册失败：" + err);
            var lnk = Path.Combine(dir, "ZutWifi.lnk");
            Assert.True(File.Exists(lnk), "快捷方式没写出来");
            Assert.Equal(AumidRegistrar.Aumid, AumidRegistrar.ReadAumid(lnk));
            // 字节级证据：属性是真的进了 .lnk 文件本身，不是停在某个进程内缓存里。
            Assert.Contains(AumidRegistrar.Aumid, System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(lnk)));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// Task 17 的装配是 `settings.NotifierFallbackUsed = AumidRegistrar.Ensure() is not null;`——
    /// 注册失败必须以"返回一句原因"的形式体现，绝不能把启动带崩。
    [Fact]
    public void 目录建不出来时返回原因而不是抛出()
    {
        var occupied = Path.Combine(Path.GetTempPath(), "zutwifi-blocked-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(occupied, "占位：让同名目录建不出来");
            var err = Record.Exception(() => AumidRegistrar.EnsureIn(Path.Combine(occupied, "sub")));
            Assert.Null(err);                                   // 异常没往外跑
            Assert.False(string.IsNullOrWhiteSpace(
                AumidRegistrar.EnsureIn(Path.Combine(occupied, "sub"))), "失败原因得带回去，否则诊断包里查不到");
            Assert.False(Path.Exists(Path.Combine(occupied, "sub", "ZutWifi.lnk")));
        }
        finally { File.Delete(occupied); }
    }
}
