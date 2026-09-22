using Windows.UI.Notifications;
using ZutWifi.Core;
using ZutWifi.Notify;

namespace ZutWifi.Tests.Support;

/// 操作系统通知面的**记账器**：装配用例该递给 `AppContext.Build` 的那一份 sink 就是它。
///
/// 三个成员与 `NotificationSink` 的三个出口一一对上，每个只干一件事 ——
/// 把"本来会打到操作系统上的那一下"记进一个列表：
/// ① 通知中心要投递的那条 Toast（`ToastAttempts`，真推出去的记在 `PushedToasts`）；
/// ② 托盘该弹的那条气泡（`Balloons`）；
/// ③ 开始菜单里那次 AUMID 注册（`AumidCalls` + `AumidFailure` 决定装配判不判死通知中心）。
///
/// 为什么这套测试非它不可：`AppContext.Build` 现在**必须**要一份 sink（没有默认值），
/// 所以"新写一条装配用例而忘记换掉操作系统出口"= 编译不过。以前那种"忘了就忘了吧"的代价是
/// 同学桌面上真多出几条"校园网登录失败"（本机就是这么被刷满的：没有开始菜单快捷方式
/// ⇒ `Ensure()` 返回原因 ⇒ `BalloonOnly` ⇒ 每条失败通知都直接落到 `NotifyIcon.ShowBalloonTip`）。
///
/// 默认是"一切正常"那一套：注册成功、通知器可用、气泡与 Toast 各记各的。
/// 要演别的场景就改 `AumidFailure` / `ToastSetting` / `ToastChannelFault`。
internal sealed class RecordingSink
{
    public NotificationSink Sink { get; }

    /// 装配让我们投递 Toast 的每一条（标题、正文、类型）。走到这一步就说明"通知中心会收到一条"。
    public List<(string Title, string Body, NoticeKind Kind)> ToastAttempts { get; } = [];

    /// 其中真的被 `ToastDelivery.Show` 推出去的那几条（假通道的 Push 记的）。
    /// 与 ToastAttempts 的差就是"Setting 判死、连推都不推"的那几种。
    public List<NoticeKind> PushedToasts { get; } = [];

    /// 托盘气泡该弹的每一条 —— 桌面上那一句"校园网登录失败"就在这里。
    public List<(string Title, string? Body)> Balloons { get; } = [];

    /// `EnsureAumid` 被问了几次（装配只问一次；真那一份会写开始菜单并 SHChangeNotify）。
    public int AumidCalls { get; private set; }

    /// 注册失败时回给装配的那一句原因；null = 注册成功。
    public string? AumidFailure { get; set; }

    /// 假通知器的"系统状态"。默认 Enabled ⇒ 投递只是记账，不会被判死又去冒气泡。
    public NotificationSetting ToastSetting { get; set; } = NotificationSetting.Enabled;

    /// 非 null ⇒ 连通道都打不开（本机 `CreateToastNotifier` 就是这么挂的），异常照原样抛给
    /// `Notifier.Notify` 的 catch，于是那条通知落到气泡上。
    public Exception? ToastChannelFault { get; set; }

    /// 一共试了几次"往操作系统上打东西"。守门用例用它自证现场确实是 armed 的。
    public int AttemptedNotifications => ToastAttempts.Count + Balloons.Count;

    public RecordingSink() => Sink = new NotificationSink(OpenToastChannel, ShowBalloon, EnsureAumid);

    public IToastChannel OpenToastChannel(string title, string body, NoticeKind kind)
    {
        if (ToastChannelFault is { } boom) throw boom;
        lock (ToastAttempts) ToastAttempts.Add((title, body, kind));
        return new FakeChannel(this, kind);
    }

    public void ShowBalloon(string title, string? body)
    {
        lock (Balloons) Balloons.Add((title, body));
    }

    public string? EnsureAumid()
    {
        lock (this) AumidCalls++;
        return AumidFailure;
    }

    /// 假的那张脸：`Setting` 照本宣科地回一个值，`Attach` 把三个回调留下（有用例要手工点火），
    /// `Push` 只记账。这里没有任何一处碰 WinRT。
    internal sealed class FakeChannel(RecordingSink owner, NoticeKind kind) : IToastChannel
    {
        public Action<NoticeKind>? OnActivated { get; private set; }
        public Action<string>? OnFailed { get; private set; }

        public NotificationSetting Setting => owner.ToastSetting;

        public void Attach(Action<NoticeKind> onActivated, Action<string> onDismissed, Action<string> onFailed)
        {
            OnActivated = onActivated;
            OnFailed = onFailed;
        }

        public void Push()
        {
            lock (owner.PushedToasts) owner.PushedToasts.Add(kind);
        }
    }
}
