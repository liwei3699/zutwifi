using System.Runtime.InteropServices;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using ZutWifi.Core;

namespace ZutWifi.Notify;

/// 优先走通知中心 Toast；AUMID 未注册或系统策略禁用时回退托盘气泡，保证"关键状态变化一定有提示"。
public sealed class Notifier(string aumid)
{
    /// 装配用的那一个构造：两条出口（通知中心的通道、托盘气泡）**全部**来自注入的 sink，
    /// 一次都不经过下面那个"默认就是真投递"的字段初始化器。
    ///
    /// 为什么要单独有一个构造而不是让 `AppContext.Build` 事后 set 两个属性：
    /// set 之前那几行里，这个对象揣着的是真的 WinRT 通道工厂 —— 装配一旦在中间发一条通知，
    /// 同学桌面上就多一条。组合根走这里之后，"真"的那一份只存在于 `NotificationSink.Production()` 里。
    /// 主构造那一个（裸 aumid，默认装着真投递）留着是给通知层自己的单测用的，组合根不许走。
    internal Notifier(string aumid, NotificationSink sink) : this(aumid)
    {
        ChannelFactory = sink.OpenToastChannel;
        BalloonFallback = sink.ShowBalloon;
    }

    /// 通知中心不可用时由托盘气泡接手（AUMID 注册失败、组策略禁用等）。装配在 Task 17 完成。
    public Action<string, string?>? BalloonFallback { get; set; }

    /// Toast 被点击时回调，用于打开主窗口并定位日志。Task 17 装配。
    /// 注意：Toast 的 Activated 在 WinRT 线程池上回调，装配方要自己切回 UI 线程。
    public Action<NoticeKind>? Activated { get; set; }

    /// 每次降级（走气泡）时把"为什么"送出去一次。Task 17 把它接进 TransactionLog：
    /// 真机上"为什么没走通知中心"只有这一句有价值，而 Show 失败是不报异常的，别处问不出来。
    public Action<string>? Faulted { get; set; }

    /// 组合根在 `AumidRegistrar.Ensure()` 返回原因时把它置 true（同一个判据已经写进
    /// Settings.NotifierFallbackUsed）：此后每条通知直接走气泡，一次都不碰通知中心。
    /// 为什么需要这个开关而不是等异常：通知中心最主要的失效方式根本不是"抛异常"，
    /// 而是"Show 规规矩矩返回、Toast 永远不出现"（AUMID 没注册就是这一种），catch 那条路等不到东西。
    public bool BalloonOnly { get; set; }

    private readonly object _gate = new();
    private Action<string, string, NoticeKind>? _deliver;

    /// 投递那一步的接缝。真机投递要弹窗，单测既不能弹窗也无从观察结果，所以把它抽成可替换委托：
    /// 默认值是真实 WinRT 投递（走 `ChannelFactory`，而组合根那条路上 `ChannelFactory` 由
    /// `NotificationSink` 供给，见上面那个 internal 构造），单测换成必抛的替身来验回退契约。
    /// Notify 里只有这一处出口，生产与测试走的是同一段 try/catch。
    ///
    /// 注意粒度：这一层换掉的是**整个投递步骤**，所以它测得到 `Notify` 的 catch 契约，
    /// 却测不到真投递里那几句判断（问 Setting、挂 Failed 回调）——那些代码在替身底下，一次都没跑过。
    /// 判断逻辑由下面更窄的 `ChannelFactory` 负责，两条接缝各管一头，谁都不许替掉对方。
    internal Action<string, string, NoticeKind> Deliver
    {
        // 读写都在 _gate 下：这个 getter 第一次被调用的地方可能在 WinRT 回调线程上
        // （Toast 的 Activated / Failed 都上线程池），裸 `??=` 会让两个线程各拿到一份委托实例。
        // 不用 Lazy<> 是因为这个值必须还能被测试整个换掉（Lazy 只能初始化一次）。
        get { lock (_gate) return _deliver ??= DefaultDeliver(); }
        set { lock (_gate) _deliver = value; }
    }

    /// 只换掉"操作系统那张脸"的接缝：`IToastChannel` 的三个成员就是真投递仅有的三个 WinRT 触点，
    /// 于是 `DefaultDeliver` 与 `ToastDelivery.Show` 里那四步判断
    /// （问 Setting → 挂三个回调 → 推出去 → 交出去之后当场判一次）
    /// 在单测里**逐字执行**，而不是像 `Deliver` 那样整段被替身吃掉。
    ///
    /// 为什么必须这样：上一轮 `toast.Failed +=` 被删掉以后全仓测试仍然全绿——因为唯一的接缝在 Show
    /// 外面，替身一装，Show 里少掉哪一行都没人知道。评审点名的就是这条："The real
    /// ToastDelivery.Show()/Setting path is never executed in any test."
    /// 第三轮点名的是同一件事的另一半：第 ④ 步那句判定当时在生产里压根不存在（Show 在 `Push()` 之后一个字
    /// 都没有），于是"push 后没有回调就不补气泡"实际住在假通道的 `Push()` 身体里，测试证不了生产怎么做。
    /// 现在判据与补发都在 Show 里，假通道只能递事实（`Setting` 第二次答什么、事件什么时候到）。
    /// 默认值在构造时就装好（不是懒初始化），所以读它不必进锁。
    internal Func<string, string, NoticeKind, IToastChannel> ChannelFactory { get; set; } =
        (title, body, kind) => ToastDelivery.OpenChannel(aumid, title, body, kind);

    /// 默认投递实现。降级的那几条通路都并到 Fallback 这一个出口上：同步抛出的异常由 Notify 的
    /// catch 转过去，异步的 Failed 事件、"通知器已被禁用"、以及"交出去之后通道自己改了口"
    /// （`ToastDelivery.Show` 的第 ④ 步，修复轮 3 搬进生产的那一句）由这里传过去。
    /// 只有 `BalloonOnly` 那一格在 Notify 里就地回退，不经过这里。
    private Action<string, string, NoticeKind> DefaultDeliver() => (title, body, kind) =>
        ToastDelivery.Show(ChannelFactory(title, body, kind), kind,
            onActivated: k => Activated?.Invoke(k),
            onFailed: reason => Fallback(title, body, kind, reason));

    public static (string Title, string Body) TextFor(NoticeKind kind, string? detail) => kind switch
    {
        NoticeKind.LoginSucceeded => ("校园网已登录", detail ?? ""),
        NoticeKind.LoginFailed => ("校园网登录失败", (detail ?? "未知原因") + " · 点击查看详情"),
        NoticeKind.Degraded => ("已认证，但暂时上不了网", detail ?? "门户返回成功，外网未通，可点重新检测"),
        NoticeKind.AlreadyOnlineElsewhere => ("账号已在别处在线", detail ?? "可能是旧租约残留，点一下可注销并重登"),
        NoticeKind.AuthExpired => ("校园网认证已失效", detail ?? "需要重新登录 · 点击查看详情"),
        NoticeKind.LogoutSucceeded => ("校园网已注销", detail ?? "已发送下线请求"),
        NoticeKind.LogoutFailed => ("校园网注销失败", (detail ?? "门户拒绝") + " · 点击查看详情"),
        _ => ("ZutWifi", detail ?? ""),
    };

    public void Notify(NoticeKind kind, string? detail)
    {
        var (title, body) = TextFor(kind, detail);
        if (BalloonOnly)
        {
            Fallback(title, body, kind, "装配时已判定通知中心不可用（AUMID 注册没成功）");
            return;
        }
        try
        {
            Deliver(title, body, kind);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or NullReferenceException or COMException)
        {
            Fallback(title, body, kind, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// 投递失败的唯一出口。五条通路都汇到这儿（第一轮评审指出的缺口：当时只有第 ① 条）：
    /// ① Show 同步抛异常；② WinRT 的 ToastNotification.Failed 事件 —— 通知中心"接了单却没弹出来"，
    /// 异步到达；③ channel.Setting（= `CreateToastNotifier(aumid).Setting`）不是 Enabled；
    /// ④ 组合根用 BalloonOnly 直接判死；⑤ 交出去之后（`Push()` 正常返回）判定发现通道自己改了口
    /// （第三次评审搬进生产的那一句，判据 = `ToastDelivery.WhyFallbackAfterPush`）。
    /// ②③⑤ 的判断在 `ToastDelivery.Show` 里，第二轮起由 `NotifierDeliveryTests` 拿假通道逐条**执行**过
    /// （第一轮只有纯函数 `WhyUnavailable` 被钉，Show 本体一次都没跑；⑤ 是第三轮才存在的、能被执行的一格）。
    /// 出重登记：①③④ 三条天生互斥（抛了就没进队列，判死了就不再往下走），所以这里不登记；
    /// 但 ② 与 ⑤ 第一次可能命中**同一条**通知（Failed 在 Push 返回前就到、判定随后又看见反证），
    /// 所以那一次的重登记在 `ToastDelivery.Show` 里做（一次性闸门），不许在这里补第二份状态。
    /// 本方法绝不允许往外抛：它可能正跑在 WinRT 的线程池回调上，那里没人 catch，抛出去就是整个程序挂掉。
    internal void Fallback(string title, string body, NoticeKind kind, string? reason)
    {
        Guard(() => BalloonFallback?.Invoke(title, body));   // 气泡是"用户一定看得到"的最后一条路，排最前
        Guard(() => Activated?.Invoke(kind));                 // 气泡没有点击回调，至少让窗口知道发生了什么
        Guard(() => Faulted?.Invoke(reason ?? "通知投递失败"));
    }

    private static void Guard(Action work)
    {
        try { work(); }
        catch { /* 装配方给的委托坏了也不能反噬通知层：回退已经是最底层，再往上就只剩崩 */ }
    }
}

/// 真投递仅有的三个 WinRT 触点，一个都不多。抽成接口的唯一理由是"能被一份假的整体装进单测"：
/// 三个成员分别回答"这个 aumid 的通知器现在是什么状态"、"把三个事件回调挂上"、"交出去"。
/// 挂回调的参数一律非 null —— 少挂一个就等于少接住一种失效方式，
/// 而 `Failed` 接住的正是最主要的那一种："Show() 返回得漂漂亮亮，通知中心里却没有这一条"。
///
/// `Setting` 一次投递会被问**两遍**：推之前那一遍用来就地判死，推之后那一遍是第 ④ 步判定的输入
/// （修复轮 3：那条判定必须住在生产里，所以它只能问通道拿事实，谁都不许把判断写进 `Push()`）。
/// 也就是说这张脸只负责"答"，判与发都由 `ToastDelivery.Show` 做。
internal interface IToastChannel
{
    NotificationSetting Setting { get; }
    void Attach(Action<NoticeKind> onActivated, Action<string> onDismissed, Action<string> onFailed);
    void Push();
}

/// 真实的通知中心投递。拆成 Build 与 Show：前者只造 XML 文档、无副作用，可以在单测里真跑；
/// 后者的判断逻辑（问 Setting → 挂三个回调 → 推 → 推完再判一次）对着假通道跑，真那张脸（WinRtToastChannel）
/// 离线永不调用 —— 否则每次跑测试都在同学机器上弹一条。
internal static class ToastDelivery
{
    /// ToastText02 = 标题 + 正文两行。模板若变了（拿不到两个 text 节点），
    /// 这里抛 InvalidOperationException 让 Notifier 走气泡，而不是在 Item(1) 上默默 NRE。
    public static XmlDocument Build(string title, string body)
    {
        var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
        var texts = xml.GetElementsByTagName("text");
        if (texts.Count < 2)
            throw new InvalidOperationException($"Toast 模板缺 text 节点（实际 {texts.Count} 个），无法写标题与正文");
        texts.Item(0)!.AppendChild(xml.CreateTextNode(title));
        texts.Item(1)!.AppendChild(xml.CreateTextNode(body));
        return xml;
    }

    /// 这个通知器能不能用；能用返回 null，不能用返回一句给人看的诊断。
    /// Enabled 之外的那几种（用户在系统设置里关掉、组策略、清单/应用级禁用）都是同一种失效形状：
    /// Show 调用成功、HRESULT 漂亮、Toast 永远不出现 —— 与 AUMID 没注册一模一样，所以必须在这里就判死。
    /// 抽成纯函数是为了让单测能逐值钉住（本机拿不到真实通知器，判据不能依赖环境）；
    /// 它现在也确实在运行时被 Show 走一遍：`NotifierDeliveryTests` 拿假通道把每一条都跑过。
    internal static string? WhyUnavailable(NotificationSetting setting) => setting switch
    {
        NotificationSetting.Enabled => null,
        NotificationSetting.DisabledForApplication => $"通知中心已为本应用关闭（{setting}）",
        NotificationSetting.DisabledForUser => $"用户在系统设置里关掉了本应用的通知（{setting}）",
        NotificationSetting.DisabledByGroupPolicy => $"组策略禁用了通知（{setting}）",
        NotificationSetting.DisabledByManifest => $"应用清单没声明通知能力（{setting}）",
        // 认不出的一律判"不可用"：说不清的状态宁可多发一条气泡，也不许当成"可以用"从而静默丢通知。
        _ => $"通知中心状态不可用（{setting}）",
    };

    /// 打开真通道：建通知器 + 建 Toast（含建 XML）。这三步任何一步抛出来都由 `Notifier.Notify`
    /// 的 catch 接手 ⇒ 同一个气泡出口。测试永远不调用它（那会碰真的通知中心）。
    internal static IToastChannel OpenChannel(string aumid, string title, string body, NoticeKind kind) =>
        new WinRtToastChannel(aumid, Build(title, body), kind);

    /// 投递的判断逻辑，四步一个都不能少、顺序也不能换：
    /// ① 先问 Setting —— 非 Enabled 时 `Push()` 不会弹也不会抛（通知中心最常见的失效方式就是
    ///    "返回得漂漂亮亮，什么都没发生"），所以在这里就地判死，连推都不推（推了会在通知中心里
    ///    留一条谁也看不见的记录）；
    /// ② 三个回调先挂好，再推 —— Failed 有可能在 Push 返回前就到，先推后挂就漏。三个捕获**同时往下面
    ///    那份账里记一笔**，记的是生产自己的账，不是通道的；
    /// ③ 推出去。返回不代表弹出来了；
    /// ④ 交出去之后**当场判一次**（`WhyFallbackAfterPush`）：这一条到底算不算送出去了。
    /// 四个出口都把原因交给 onFailed，让它去走气泡：① 就地、② 异步（线程池上）、③ 抛出来（由 catch 转）、
    /// ④ 在这里同步补（而且与 ② 共用一次性闸门，同一条通知最多一发）。
    ///
    /// ④ 是第三轮评审搬进来的那一句："push 后没有回调就不补气泡"以前只是一句**约定**——生产代码在
    /// `Push()` 之后一个字都没有，于是那条判断实际住在测试替身的 `Push()` 身体里，测试永远观察不到
    /// 生产在"推出去且什么都没回调"时怎么做（把 `Push()` 改成无条件回调 Failed、或把 `Attach` 的捕获改掉，
    /// 当时的测试集照旧全绿）。现在判据与补发都在下面这几行，假通道只能递事实。
    public static void Show(IToastChannel channel, NoticeKind kind,
        Action<NoticeKind>? onActivated, Action<string>? onFailed)
    {
        if (WhyUnavailable(channel.Setting) is { } why) { onFailed?.Invoke(why); return; }   // ①

        // 生产在这一次投递里记下的三份事实 —— ④ 的判定只认它们，谁都不许拿判据当输入。
        string? failedReason = null;      // ② 的 Failed 给过的反证
        var acked = false;                // ② 的 Activated / Dismissed：已经在屏幕上出现过
        var handedOver = false;           // ③ 是不是真的正常交出去了

        var balloonSent = 0;              // 一次性闸门：② 与 ④ 第一次可能命中同一条通知
        void Balloon(string reason)
        {
            // Failed 跑在 WinRT 线程池上、④ 跑在投递线程上，两者可能同时想补同一发 ⇒
            // 这里没有锁可借用（同一条通知的账只此一份），就用一次性的 Exchange。
            if (Interlocked.Exchange(ref balloonSent, 1) != 0) return;
            onFailed?.Invoke(reason);
        }

        channel.Attach(                                                            // ②
            k => { acked = true; onActivated?.Invoke(k); },
            // 划走（或自己超时消失）说明这条通知**弹出来了**：它不是失败，再补一条气泡就是重复通知。
            _ => acked = true,
            r => { failedReason = r; Balloon(r); });

        channel.Push();                                                            // ③
        handedOver = true;

        if (WhyFallbackAfterPush(handedOver, failedReason, acked, channel.Setting) is { } late)   // ④
            Balloon(late);
    }

    /// ④ 那一句判定，单独抽成纯函数好逐格钉住（与 `WhyUnavailable` 同一套打法）。
    /// 返回 null = 不补气泡；返回一句话 = 现在就补，走的还是 onFailed 那一个出口。
    /// 四份输入全部是"通道记下的事实"，没有一份是替身的判断：
    ///   pushed       —— 没正常交出去就轮不到这里说话（那是 `Notify` 的 catch 的地盘，抢着补就是双发）；
    ///   failedReason —— Failed 已经补过一发 ⇒ 绝不双发；
    ///   acked        —— 用户点过或划走过 ⇒ 它明明白白弹出来过，更不该补；
    ///   settingNow   —— **Push 之后再问一次通道**：交出与 Show 真正生效之间用户把通知关了，
    ///                   这是一种既不留异常、也不发任何事件的丢，唯一不靠事件就能问出来的证据就是它。
    /// 四份都没有反证时按"已送达"处理 —— 这是**刻意的取舍**（`NotifierDeliveryTests.推出去之后再没有回调时按已送达处理不补气泡`
    /// 钉着它）：一条弹出来、没人点也没人划、最后自己过期的 Toast 永远不发事件，
    /// 给它补一发就等于给每一条成功通知都发两份提示（第二轮已为此砍掉过超时看门狗）。
    /// 诚实的边界：剩下的那种最坏情况——"Setting 说可用、Show 正常返回、shell 收了单却不弹、也不发 Failed"——
    /// 这一句判不出来（它没有任何输入），只能由 `AumidRegistrar.Ensure()` 在装配时就判死（`BalloonOnly`），
    /// 并把"真机上 Failed 到底回不回"留给 Task 20。
    internal static string? WhyFallbackAfterPush(bool pushed, string? failedReason, bool acked,
        NotificationSetting settingNow)
    {
        if (!pushed || failedReason is not null || acked) return null;
        return WhyUnavailable(settingNow) is { } late
            ? $"通知交出去之后又被判为不可用：{late}"
            : null;
    }

    /// Failed 事件跑在 WinRT 的线程池上，那里没人 catch：取原因这一步抛出来就是整个程序挂掉，
    /// 所以它必须自己兜住，最坏是"少了一句原因"，而不是丢一次崩溃。
    /// 取原因的动作交给调用方（真机上要跨一层 WinRT 对象），这里只保证**永远回一句能看的话**。
    internal static string WhyFailed(Func<string?>? readReason)
    {
        try { return $"通知中心没显示这条通知（{readReason?.Invoke() ?? "无原因"}）"; }
        catch { return "通知中心没显示这条通知（原因取不出来）"; }
    }

    /// 真的那张脸。整类只有这一处碰 WinRT 事件，所以"三个回调挂上了没有"这一件事
    /// 集中在这三行 `+=` 里，看得见也数得清；Task 20 要对着真机验的就是这三行。
    /// 这张脸**只答不判**：`Setting` 一次投递被问两遍（推前判死 / 推后当判定的输入），
    /// 三个事件只是把 WinRT 那一侧的消息转交给生产记的那份账，判与发都在 `Show` 里。
    sealed class WinRtToastChannel(string aumid, XmlDocument xml, NoticeKind kind) : IToastChannel
    {
        private readonly ToastNotifier _notifier = ToastNotificationManager.CreateToastNotifier(aumid);
        private readonly ToastNotification _toast = new(xml) { Tag = kind.ToString(), Group = "zutwifi" };

        public NotificationSetting Setting => _notifier.Setting;

        public void Attach(Action<NoticeKind> onActivated, Action<string> onDismissed, Action<string> onFailed)
        {
            _toast.Activated += (_, _) => onActivated(kind);
            // 这个投影里 ToastDismissedEventArgs 没暴露划走的原因（编译期就拦住了），所以这里现编一句。
            // 它值钱的不是那句话，而是"这条曾经上过屏"这个事实：判定（Show 的第 ④ 步）拿它当反证。
            _toast.Dismissed += (_, _) => onDismissed("用户把通知划走了，或者它自己超时消失了");
            _toast.Failed += (_, e) => onFailed(WhyFailed(() => e?.ErrorCode?.Message));
        }

        // `Show()` 返回不等于收下了，而"交出与 Show 真正生效之间用户把通知关了"这一种丢既不留异常
        // 也不发任何事件 —— 所以 `Show` 在第 ④ 步会再读一次这里。诚实的边界：
        // **还是读回 Enabled 而 shell 就是不弹**那种（AUMID 没注册的经典形状）这一遍也救不了，
        // 那一格靠装配时的 `AumidRegistrar.Ensure()` ⇒ `BalloonOnly`，不靠这里。
        public void Push() => _notifier.Show(_toast);
    }
}
