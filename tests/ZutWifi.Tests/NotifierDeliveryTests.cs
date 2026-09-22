using System.Runtime.InteropServices;
using Windows.UI.Notifications;
using ZutWifi.Core;
using ZutWifi.Notify;

namespace ZutWifi.Tests;

/// 投递那一步的**判断逻辑**在离线被真跑一遍。
///
/// 为什么还要再开一个接缝：`NotifierTextTests` 那批用例换掉的是整个 `Deliver`，
/// 于是真投递里真正有价值的那几句判断——"这个通知器的 Setting 是什么"、"把三个回调挂上"——
/// 在单测里一次都没执行过。上一轮 `toast.Failed +=` 那一整句被删掉以后，全仓测试照样全绿，
/// 就是这个粒度错的直接后果（评审原话：the real `ToastDelivery.Show()`/`Setting` path is never
/// executed in any test）。
///
/// 这里只把"操作系统那张脸"（`IToastChannel`）换掉：`Notifier.DefaultDeliver` 与
/// `ToastDelivery.Show` 里那四步判断照原样执行，所以
/// "Setting 非 Enabled ⇒ 气泡在运行时真的响了"、"Failed 回调 ⇒ 气泡响了"、
/// "交出去之后通道改了口 ⇒ 生产自己判出该补一发"是**跑出来的**，不是推出来的。
/// `Deliver` 那批用例一条没改、断言一个没松（评审要求：extend, don't weaken）。
///
/// 修复轮 3 立的规矩：**假通道只递事实，不递判断**。它能改的只有"被问 Setting 时答什么"和
/// "事件什么时候到"；"要不要补气泡"这个决定由生产那一句 `WhyFallbackAfterPush` 做
/// （`交出之后的判定住在生产里而且排在交出去之后` 还对着源码钉了一遍，防的就是"以后搬回替身"）。
public class NotifierDeliveryTests
{
    /// 假的 WinRT 触点：把"问设置 / 挂回调 / 推出去"每一步按顺序记下来。
    /// `Steps` 是这条文件里最值钱的断言对象——它证的是**顺序**：回调没挂好就 Push，
    /// 通知中心在 Show 返回前就丢掉的这条通知就永远没人接。
    ///
    /// 修复轮 3 又加了两个旋钮（`SettingAfterPush` / `DuringPush`），它们的存在理由是一样的：
    /// **假通道只递事实，不递判断**。"推出去之后到底要不要补气泡"这个决定由生产那一句判定做，
    /// 这里能改的只有"生产去问的时候通道答什么"、"事件在什么时候到"。
    /// 上一轮这条决策只是"生产里压根没写"留下的默认结果，所以它当时住在假通道的身体里。
    private sealed class FakeChannel(NotificationSetting setting) : IToastChannel
    {
        private int _settingReads;

        public readonly List<string> Steps = [];
        public Action<NoticeKind>? OnActivated { get; private set; }
        public Action<string>? OnDismissed { get; private set; }
        public Action<string>? OnFailed { get; private set; }

        /// 第二次（含以后）被问 Setting 时回的值；null = 一直和第一次一样。
        /// 生产在 `Push()` 正常返回之后会**再问一次**，那一格就是它唯一的"反证"来源。
        public NotificationSetting? SettingAfterPush { get; set; }

        /// 在 `Push()` 返回之前点火：真机上 Failed / Activated 都可能落在"已经交出去、Push 还没返回"这一段。
        public Action<FakeChannel>? DuringPush { get; set; }

        public NotificationSetting Setting
        {
            get
            {
                Steps.Add("setting");
                return _settingReads++ > 0 && SettingAfterPush is { } later ? later : setting;
            }
        }

        public void Attach(Action<NoticeKind> onActivated, Action<string> onDismissed, Action<string> onFailed)
        {
            Steps.Add("attach");
            // 签名是"三个都必须给"，运行时要真给：少给一个 = 那一种失效方式永远没人接。
            ArgumentNullException.ThrowIfNull(onActivated);
            ArgumentNullException.ThrowIfNull(onDismissed);
            ArgumentNullException.ThrowIfNull(onFailed);
            OnActivated = onActivated;
            OnDismissed = onDismissed;
            OnFailed = onFailed;
        }

        public void Push()
        {
            Assert.Contains("attach", Steps);
            Steps.Add("push");
            DuringPush?.Invoke(this);
        }

        /// 复现"通知中心接了单却没弹出来"：真机时序是线程池上异步到，对 Notifier 而言是同一条出口。
        public void RaiseFailed(string reason)
        {
            Assert.Contains("push", Steps);   // 只能是"推出去之后才发现没弹"，不是提前判死
            OnFailed!.Invoke(reason);
        }

        public void RaiseDismissed() => OnDismissed!.Invoke("用户把通知划走了");
    }

    private sealed class Recorder
    {
        public List<(string Title, string? Body)> Balloons { get; } = [];
        public List<NoticeKind> Activated { get; } = [];
        public List<string> Faults { get; } = [];

        /// 注意：**没有**动 `Deliver`——走的是生产那条 `DefaultDeliver → ToastDelivery.Show` 真路。
        public Notifier Wire(Func<string, string, NoticeKind, IToastChannel> open)
            => new(AumidRegistrar.Aumid)
            {
                ChannelFactory = open,
                BalloonFallback = (t, b) => Balloons.Add((t, b)),
                Activated = k => Activated.Add(k),
                Faulted = r => Faults.Add(r),
            };
    }

    // ---------- Setting != Enabled：运行时真的落到气泡 ----------

    [Theory]
    [InlineData(NotificationSetting.DisabledForUser)]
    [InlineData(NotificationSetting.DisabledByGroupPolicy)]
    [InlineData(NotificationSetting.DisabledForApplication)]
    [InlineData(NotificationSetting.DisabledByManifest)]
    public void 通知器被禁用时运行时落到气泡而不是静默(NotificationSetting setting)
    {
        var fake = new FakeChannel(setting);
        var rec = new Recorder();

        rec.Wire((_, _, _) => fake).Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");

        var (title, body) = Notifier.TextFor(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");
        Assert.Equal(new[] { (title, (string?)body) }, rec.Balloons);       // 气泡真的响了，文案与 Toast 一致
        Assert.Equal(new[] { NoticeKind.LoginFailed }, rec.Activated);      // 窗口也知道了
        Assert.Contains(setting.ToString(), Assert.Single(rec.Faults));     // 原因是哪一种禁用要说得清
        // 明知是死通道还往通知中心塞一条 ⇒ 用户会在通知中心里看到一条自己看不见的"幽灵记录"
        Assert.Equal(new[] { "setting" }, fake.Steps);
    }

    /// Enabled 是唯一放行的一种：真推出去，而且**只**推出去（外加修复轮 3 那一次"交出去之后回头再问一遍"）。
    /// 这条就是评审点名缺的 `WhyUnavailable(Enabled)` 那一端在运行时（而不只是纯函数）的证据；
    /// 第二次 `setting` 是第 ④ 步判定的输入 —— 它由**生产**去问，不是假通道自己顺口补的。
    [Fact]
    public void 通知器可用时只推出去不改口也不双发()
    {
        var fake = new FakeChannel(NotificationSetting.Enabled);
        var rec = new Recorder();

        rec.Wire((_, _, _) => fake).Notify(NoticeKind.LoginSucceeded, "zut-stu · 耗时 1.8s");

        Assert.Equal(new[] { "setting", "attach", "push", "setting" }, fake.Steps);
        Assert.Empty(rec.Balloons);
        Assert.Empty(rec.Activated);   // 推出去 ≠ 用户点了：不能在投递时就当"已点击"
        Assert.Empty(rec.Faults);
    }

    // ---------- Failed：通知被 shell 静默丢弃（最主要的失效方式） ----------

    /// 真机上 `Show()` 规规矩矩返回、通知中心里却一条都没有，唯一还留下来的信号就是 `Failed` 事件。
    /// 评审第 1 项要的就是这一条：被丢掉的通知必须最终出现在托盘气泡里。
    [Fact]
    public void 通知被静默丢弃时Failed回调把气泡叫起来()
    {
        var fake = new FakeChannel(NotificationSetting.Enabled);
        var rec = new Recorder();
        var n = rec.Wire((_, _, _) => fake);

        n.Notify(NoticeKind.AuthExpired, "需要重新登录");
        Assert.Empty(rec.Balloons);            // 推出去的那一刻还没有回退（正常投递不许多冒泡）
        fake.RaiseFailed("通知中心没显示这条通知（Unknown）");

        var (title, body) = Notifier.TextFor(NoticeKind.AuthExpired, "需要重新登录");
        Assert.Equal(new[] { (title, (string?)body) }, rec.Balloons);
        Assert.Equal(new[] { NoticeKind.AuthExpired }, rec.Activated);
        Assert.Equal(new[] { "通知中心没显示这条通知（Unknown）" }, rec.Faults);
    }

    /// 回退不是"熔断"：这一条被 shell 丢掉了，下一条照样得正常投递。
    /// （钉住这件事是因为 Failed 回调是**闭包**里的，写成一个实例字段就会把整个 Notifier 永久切成气泡，
    ///  而那等于"通知中心抖一下 ⇒ 本次会话再也不走通知中心"。）
    [Fact]
    public void 一条被丢弃不影响下一条正常投递()
    {
        var rec = new Recorder();
        var first = new FakeChannel(NotificationSetting.Enabled);
        var second = new FakeChannel(NotificationSetting.Enabled);
        var queue = new Queue<IToastChannel>([first, second]);
        var n = rec.Wire((_, _, _) => queue.Dequeue());

        n.Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");
        first.RaiseFailed("通知中心没显示这条通知（Unknown）");
        n.Notify(NoticeKind.LoginSucceeded, "zut-stu · 耗时 1.8s");

        Assert.Single(rec.Balloons);
        Assert.Equal(new[] { "setting", "attach", "push", "setting" }, second.Steps);
        Assert.Single(rec.Activated);   // 只有回退那一次"让窗口知道"；第二条正常投递不许冒出来
    }

    /// 划走说明通知**弹出来了**：再补一条气泡就是给同一条通知发两份提示。
    /// 这是三个回调里唯一"什么都不往外送"的那一个，故意钉住——它挡住的是"顺手把 Dismissed 也接进气泡"。
    /// （修复轮 3 起它还要往生产那份账里记一笔"这条弹过"，供第 ④ 步判定查 —— 记账不等于发消息。）
    [Fact]
    public void 用户划走通知时什么都不许再发生()
    {
        var fake = new FakeChannel(NotificationSetting.Enabled);
        var rec = new Recorder();
        var n = rec.Wire((_, _, _) => fake);
        n.Notify(NoticeKind.Degraded, null);

        fake.RaiseDismissed();

        Assert.Empty(rec.Balloons);
        Assert.Empty(rec.Faults);
        Assert.Empty(rec.Activated);   // 划走 ≠ 点击，不该把主窗口拽出来
    }

    /// 点击回调要真的接到 `Notifier.Activated` 上（Task 17 靠它打开主窗口）。
    [Fact]
    public void 点击回调接通而回退保持安静()
    {
        var fake = new FakeChannel(NotificationSetting.Enabled);
        var rec = new Recorder();
        var n = rec.Wire((_, _, _) => fake);
        n.Notify(NoticeKind.LoginFailed, "x");
        Assert.Equal(new[] { "setting", "attach", "push", "setting" }, fake.Steps);

        fake.OnActivated!.Invoke(NoticeKind.LoginFailed);

        Assert.Equal(new[] { NoticeKind.LoginFailed }, rec.Activated);
        Assert.Empty(rec.Balloons);
    }

    // ---------- 推出去之后"这条到底算不算送出去了"：判定住在生产里（修复轮 3） ----------

    /// 评审点名的那一格：前两轮里"push 后没有回调就不补气泡"只是**约定**——生产代码在 `Push()` 之后
    /// 一个字都没有，于是这条判断实际上住在测试替身里，测试永远观察不到生产在"推出去且什么都没回调"时怎么做。
    /// 现在 `ToastDelivery.Show` 在 `Push()` 正常返回之后必须再问一次通道、再过一道判定
    /// （`ToastDelivery.WhyFallbackAfterPush`），所以这一条**跑的是生产那两句**：
    /// 假通道只是在第二次被问时答"我这儿已经变成用户关掉了"，补气泡的动作发生在生产里。
    [Fact]
    public void 交出之后通知器变了卦时生产当场补一条气泡()
    {
        var fake = new FakeChannel(NotificationSetting.Enabled)
        {
            SettingAfterPush = NotificationSetting.DisabledForUser,
        };
        var rec = new Recorder();

        rec.Wire((_, _, _) => fake).Notify(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");

        var (title, body) = Notifier.TextFor(NoticeKind.LoginFailed, "Radius 认证失败（账号或密码错误）");
        Assert.Equal(new[] { (title, (string?)body) }, rec.Balloons);      // 气泡真的响了，而不是"约定它不该响"
        Assert.Equal(new[] { NoticeKind.LoginFailed }, rec.Activated);
        Assert.Contains("用户", Assert.Single(rec.Faults));                 // 为什么降级要说清是哪一种
        Assert.Contains("DisabledForUser", Assert.Single(rec.Faults));
        // 这条断言才是"决策搬进生产"的证据：生产在 Push 之后**回头问过通道**（第二次 setting），
        // 而不是假通道在 Push 里自己顺手补了一发。
        Assert.Equal(new[] { "setting", "attach", "push", "setting" }, fake.Steps);
    }

    /// 那条**刻意的取舍**，钉住它别被"顺手加个保险"改掉：一条推出去之后再没有任何回调的 Toast
    /// （弹出来了、用户既没点也没划、让它自己过期）不许补第二条气泡。
    /// 判据里"已交出 + 无回调 + 通道仍然说可用 ⇒ 不补"就是这一格。
    /// 变异"永远补一发"会红在这里（连同 `通知器可用时只推出去不改口也不双发`）。
    [Fact]
    public void 推出去之后再没有回调时按已送达处理不补气泡()
    {
        var fake = new FakeChannel(NotificationSetting.Enabled);
        var rec = new Recorder();

        rec.Wire((_, _, _) => fake).Notify(NoticeKind.LoginSucceeded, "zut-stu · 耗时 1.8s");

        Assert.Equal(new[] { "setting", "attach", "push", "setting" }, fake.Steps);
        Assert.Empty(rec.Balloons);      // 过期 ≠ 失败：补一发就是同一条通知给用户看两份
        Assert.Empty(rec.Faults);        // 也没有任何"降级"被记进诊断包
        Assert.Empty(rec.Activated);
    }

    /// Failed 抢在 `Push()` 返回之前就到（回调与判定为同一件事竞争）：气泡由 Failed 那一条通路发，
    /// 之后的判定看见"已经有反证了"就**不许再补第二发**，原因也得到底是 Failed 给的那一句。
    /// 这条钉的是"同一条通知最多一条气泡"这个新缺口（第五通路和第二条通路第一次可能同时命中）。
    [Fact]
    public void Failed先到之后判定不许再补第二发()
    {
        var fake = new FakeChannel(NotificationSetting.Enabled)
        {
            SettingAfterPush = NotificationSetting.DisabledForUser,     // 判定若鲁莽补发，这里就是第二发
            DuringPush = c => c.RaiseFailed("通知中心没显示这条通知（0x80070490 找不到名称）"),
        };
        var rec = new Recorder();

        rec.Wire((_, _, _) => fake).Notify(NoticeKind.AuthExpired, "需要重新登录");

        Assert.Single(rec.Balloons);
        Assert.Equal(new[] { "通知中心没显示这条通知（0x80070490 找不到名称）" }, rec.Faults);
        Assert.Equal(new[] { "setting", "attach", "push", "setting" }, fake.Steps);
    }

    /// 事件在"交出去之后、Push 返回之前"就到 ⇒ 生产记下的事实里已经写着"这条弹过并被处理了"，
    /// 判定看见就不许补。这一格走的是生产的三个捕获（`onActivated`/`onDismissed`/`onFailed` 都记账），
    /// 所以把 `Attach` 里任何一个捕获改成空体都会红在这里。
    [Fact]
    public void 交出之后已点击时判定不补气泡()
    {
        var fake = new FakeChannel(NotificationSetting.Enabled)
        {
            SettingAfterPush = NotificationSetting.DisabledForUser,
            DuringPush = c => c.OnActivated!.Invoke(NoticeKind.Degraded),
        };
        var rec = new Recorder();

        rec.Wire((_, _, _) => fake).Notify(NoticeKind.Degraded, null);

        Assert.Empty(rec.Balloons);                                     // 用户都点过了，还补什么
        Assert.Empty(rec.Faults);
        Assert.Equal(new[] { NoticeKind.Degraded }, rec.Activated);      // 点击回调本身照旧接通
    }

    /// 已弹出的另一种证据：划走（Dismissed）也算"这条送达过"，之后判定同样不许补。
    [Fact]
    public void 交出之后已划走时判定不补气泡()
    {
        var fake = new FakeChannel(NotificationSetting.Enabled)
        {
            SettingAfterPush = NotificationSetting.DisabledByManifest,
            DuringPush = c => c.OnDismissed!.Invoke("用户把通知划走了"),
        };
        var rec = new Recorder();

        rec.Wire((_, _, _) => fake).Notify(NoticeKind.LogoutSucceeded, "zut-stu");

        Assert.Empty(rec.Balloons);
        Assert.Empty(rec.Faults);
    }

    /// 判定本体是纯函数，所以四种事实的每一格都能逐行钉住（与 `WhyUnavailable` 同一套打法）：
    /// 运行时用例证的是"这一句在生产里被跑到"，这一条证的是"这一句的表就是这个表"。
    /// 参数顺序 = 生产在 Push 返回之后能看到的四份事实：交出去没有 / Failed 给的原因 / 已经弹过并被处理 / 通道此刻的自述。
    [Theory]
    [InlineData(false, null, false, NotificationSetting.Enabled, null)]                                  // 还没交出 ⇒ 轮不到判定说话（那是 catch 的地盘）
    [InlineData(false, null, false, NotificationSetting.DisabledByGroupPolicy, null)]                    // 同上：乱补就是双发
    [InlineData(true, "通知中心没显示这条通知", false, NotificationSetting.DisabledForUser, null)]        // Failed 已经补过 ⇒ 绝不双发
    [InlineData(true, null, true, NotificationSetting.DisabledForUser, null)]                            // 已点击/已划走 ⇒ 更不补
    [InlineData(true, null, false, NotificationSetting.DisabledForApplication, "非 null")]               // 通道自己承认收不下 ⇒ 现在就补
    [InlineData(true, null, false, NotificationSetting.DisabledForUser, "非 null")]
    [InlineData(true, null, false, NotificationSetting.DisabledByGroupPolicy, "非 null")]
    [InlineData(true, null, false, NotificationSetting.DisabledByManifest, "非 null")]
    [InlineData(true, null, false, NotificationSetting.Enabled, null)]                                   // 那条刻意的取舍
    [InlineData(true, null, false, (NotificationSetting)99, "非 null")]                                  // 认不出的状态宁可多发一条
    public void 交出之后的判定只认通道记下的事实(bool pushed, string? failed, bool acked,
        NotificationSetting settingNow, string? expected)
    {
        var reason = ToastDelivery.WhyFallbackAfterPush(pushed, failed, acked, settingNow);
        if (expected is null)
        {
            Assert.Null(reason);   // 不补气泡
            return;
        }
        Assert.NotNull(reason);
        Assert.Contains(settingNow.ToString(), reason);   // 原因是哪一种"收不下"要落进诊断包
    }

    // ---------- 同一个出口：三条通路的气泡文案必须一模一样 ----------

    /// 同步抛异常与异步 Failed 共用 `Notifier.Fallback`：逐类型核对两边文案一致，
    /// 避免"回退是回退了，但退化成另一套话"（Task 12/15 从另一头断言同一批字符串）。
    [Theory]
    [InlineData(NoticeKind.LoginSucceeded)]
    [InlineData(NoticeKind.LoginFailed)]
    [InlineData(NoticeKind.Degraded)]
    [InlineData(NoticeKind.AlreadyOnlineElsewhere)]
    [InlineData(NoticeKind.AuthExpired)]
    [InlineData(NoticeKind.LogoutSucceeded)]
    [InlineData(NoticeKind.LogoutFailed)]
    public void 异常通路与Failed通路的气泡文案逐类型一致(NoticeKind kind)
    {
        var detail = kind + "-detail";

        var dropped = new FakeChannel(NotificationSetting.Enabled);
        var recA = new Recorder();
        recA.Wire((_, _, _) => dropped).Notify(kind, detail);
        dropped.RaiseFailed("同一条原因");

        var pushBoom = new PushBoomChannel();
        var recC = new Recorder();
        recC.Wire((_, _, _) => pushBoom).Notify(kind, detail);

        Assert.Equal(recA.Balloons, recC.Balloons);
        Assert.Equal(Notifier.TextFor(kind, detail), (recC.Balloons[0].Title, recC.Balloons[0].Body));
        Assert.Equal(recA.Activated, recC.Activated);
        Assert.Single(recA.Faults);
    }

    /// 通道还没打开就抛（本机 propsys 对 `CreateToastNotifier` 就是这样）：异常必须被 `Notify`
    /// 的 catch 接住并落到同一个气泡上，而不是从通知层冒到状态机里。
    [Fact]
    public void 打不开通道时仍然落到同一个气泡()
    {
        var rec = new Recorder();
        var n = rec.Wire((_, _, _) => throw new COMException("E_NOINTERFACE"));

        var ex = Record.Exception(() => n.Notify(NoticeKind.LogoutFailed, "门户拒绝"));

        Assert.Null(ex);
        Assert.Single(rec.Balloons);
        Assert.Contains("E_NOINTERFACE", Assert.Single(rec.Faults));
    }

    /// 真投递里的取原因代码：`e` 为 null、或取 `ErrorCode.Message` 自己抛
    /// （WinRT 的异常对象跨线程时就会长这样）都必须回一句能看的话，绝不能反过来抛出。
    /// 那条回调跑在线程池上，抛出去就是整个程序挂掉。
    [Fact]
    public void 取失败原因永远给出一句话而不是抛出()
    {
        Assert.Equal("通知中心没显示这条通知（无原因）", ToastDelivery.WhyFailed(null));
        Assert.Equal("通知中心没显示这条通知（0x80070490 找不到名称）",
            ToastDelivery.WhyFailed(() => "0x80070490 找不到名称"));
        Assert.Equal("通知中心没显示这条通知（原因取不出来）",
            ToastDelivery.WhyFailed(() => throw new InvalidOperationException("跨线程取异常对象炸了")));
    }

    // ---------- 通道工厂自己的边界 ----------

    /// 默认装着真实 WinRT 通道工厂：这一句是 null 的话每条通知都会被静默吞掉，
    /// 而 `默认接缝非空否则通知会被静默丢弃` 只钉住了 Deliver 那一半。
    /// （绝不调用它——一调用就 `CreateToastNotifier`，会在同学机器上碰通知中心。）
    [Fact]
    public void 不注入时通道工厂装着真投递()
        => Assert.NotNull(new Notifier(AumidRegistrar.Aumid).ChannelFactory);

    /// 通道工厂坏在名单外的地方（比如被误改成一个不该出现的桩）必须冒泡，
    /// 不能被"回退"伪装成一次正常气泡——与 `名单外的异常不被当成投递失败` 同一口径，只是换了位置。
    [Fact]
    public void 通道工厂的名单外异常照样冒泡()
    {
        var n = new Notifier(AumidRegistrar.Aumid)
        {
            ChannelFactory = (_, _, _) => throw new NotSupportedException("这不是通知中心的问题"),
        };
        Assert.Throws<NotSupportedException>(() => n.Notify(NoticeKind.LoginSucceeded, "x"));
    }

    /// Push 抛 `InvalidOperationException`（Toast 模板节点不够就是这个形状）：
    /// 落在 catch 名单里 ⇒ 气泡接手，而不是在 `Item(1)!` 上默默 NRE。
    [Fact]
    public void 推送时抛名单内异常也落到气泡()
    {
        var rec = new Recorder();
        var n = rec.Wire((_, _, _) => new PushBoomChannel(
            () => throw new InvalidOperationException("Toast 模板缺 text 节点（实际 1 个），无法写标题与正文")));

        n.Notify(NoticeKind.Degraded, null);

        Assert.Single(rec.Balloons);
        Assert.Equal(new[] { NoticeKind.Degraded }, rec.Activated);
        Assert.Contains("缺 text 节点", Assert.Single(rec.Faults));
    }

    // ---------- 形状守卫：那条判定不许搬回测试替身 ----------

    /// 运行时那几条已经证了"判定被跑到"（生产没在 Push 之后回头问过通道，`setting` 就少一次，用例红），
    /// 这一条补的是"以后有人把整段判定搬回假通道的 `Push()` 里"那一格 —— 与 `AppContextTests`
    /// 数 `_icon.ShowBalloonTip(` 的同一套走法：运行时替身看不见的东西，只能对着源码钉。
    /// 锚点找不到就直接红，绝不静默放过。
    [Fact]
    public void 交出之后的判定住在生产里而且排在交出去之后()
    {
        var src = NotifierCodeLines();

        var push = src.FindIndex(l => l.Contains("channel.Push();", StringComparison.Ordinal));
        var decide = src.FindIndex(l => l.Contains("WhyFallbackAfterPush(handedOver", StringComparison.Ordinal));
        Assert.True(push >= 0, "Show 里那句 `channel.Push();` 不见了：这条守卫扫了个空");
        Assert.True(decide > push,
            "第 ④ 步的判定没排在交出去之后（或者被整段搬走了）：\"push 后没有回调就不补气泡\"又变回一句约定");

        Assert.Equal(1, Count(src, "internal static string? WhyFallbackAfterPush("));  // 判据只此一份
        Assert.Equal(1, Count(src, "WhyFallbackAfterPush(handedOver"));                 // 调用点也只此一处
        // 判定读的四份事实全部由生产的三个捕获与 Push 的返回写下（少一行 = 少一种反证）。
        var capture = SrcSegment(src, "channel.Attach(", "channel.Push();");
        Assert.Equal(1, Count(capture, "acked = true; onActivated?.Invoke(k)"));
        Assert.Equal(1, Count(capture, "r => { failedReason = r; Balloon(r); }"));
        Assert.Equal(2, Count(capture, "acked = true"));   // Activated 与 Dismissed 各记一笔，划走也算"上过屏"
    }

    /// 从测试输出目录往上找仓库根（与 `AppContextTests.RepoRoot` 同一套走法）；找不到就红，不静默通过。
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(System.AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ZutWifi.sln"))) return d.FullName;
        throw new FileNotFoundException("找不到仓库根（ZutWifi.sln）：这条源码级守卫无法工作");
    }

    /// `Notifier.cs` 的代码行（丢掉空行与 `//`、`///` 开头的）：注释里写"别把判定搬回替身"不该被读成违规。
    private static List<string> NotifierCodeLines()
    {
        var path = Path.Combine(RepoRoot(), "src", "ZutWifi", "Notify", "Notifier.cs");
        Assert.True(File.Exists(path), $"找不到源码文件 {path}：这条守卫扫了个空");
        return [.. File.ReadAllLines(path).Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))];
    }

    private static List<string> SrcSegment(List<string> lines, string from, string to)
    {
        var i = lines.FindIndex(l => l.Contains(from, StringComparison.Ordinal));
        Assert.True(i >= 0, $"源码里找不到锚点 `{from}`：形状变了，守卫得跟着改（不能装作扫过了）");
        var j = lines.FindIndex(i + 1, l => l.Contains(to, StringComparison.Ordinal));
        Assert.True(j > i, $"锚点 `{from}` 之后找不到 `{to}`：次序变了，同上");
        return [.. lines[i..j]];
    }

    private static int Count(IEnumerable<string> lines, string needle) =>
        lines.Count(l => l.Contains(needle, StringComparison.Ordinal));

    private sealed class PushBoomChannel(Action? boom = null) : IToastChannel
    {
        public NotificationSetting Setting => NotificationSetting.Enabled;
        public void Attach(Action<NoticeKind> a, Action<string> d, Action<string> f) { }
        public void Push()
        {
            boom?.Invoke();
            throw new COMException("0x80070490");
        }
    }
}
