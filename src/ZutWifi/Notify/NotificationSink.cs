using ZutWifi.Core;

namespace ZutWifi.Notify;

/// 操作系统通知面的**唯一**出口：这套程序能让同学桌面上"有感觉"的三件事，全收在这一个参数里。
///
/// ① `OpenToastChannel` —— 通知中心那张脸（真那一份里才有 `CreateToastNotifier` / `Show` / 三个 WinRT 事件）。
/// ② `ShowBalloon`      —— 托盘气泡那张脸（真那一份里才有 `NotifyIcon.ShowBalloonTip`）。
/// ③ `EnsureAumid`      —— 开始菜单里那一次快捷方式写入，以及写完对 Explorer 喊的那一嗓子 `SHChangeNotify`。
///
/// 为什么它必须是 `AppContext.Build` 的一个**没有默认值**的参数：
/// 上一版这三条全靠"装配的可选参数记得传替身"，而 `Build` 的参数清一色可选 ——
/// 只要有一条用例忘了换（或者有人把哪个默认值改回真的），每一次失败登录都会在同学桌面上真的弹一条
/// "校园网登录失败"。这不是假设，是已经发生的事故：本机的开始菜单里没有那个快捷方式，
/// `Ensure()` 因此返回一句原因 ⇒ `BalloonOnly` ⇒ 每一条通知都直接落到真气泡上，
/// 而 `AppContextTests` / `MainFormWiringTests` 里有几十条用例走的就是这条通路。
/// 现在"忘记传出口"是**编译错误**，而不是一次安静的桌面事故。
///
/// 全仓只有 `Program.Main` 那一行用 `Production()`。单测递进来的永远是调用方自己写的记账器
/// （`tests/ZutWifi.Tests/Support/RecordingSink.cs`），里面没有 WinRT、没有 NotifyIcon、也没有 shell32。
internal sealed record NotificationSink(
    Func<string, string, NoticeKind, IToastChannel> OpenToastChannel,
    Action<string, string?> ShowBalloon,
    Func<string?> EnsureAumid)
{
    /// 真气泡落点（托盘那一个 `NotifyIcon`）的接线口：**只有 `Production()` 带它**。
    ///
    /// 为什么需要一个接线口而不是直接把气泡写死在这里：气泡必须挂在"正在托盘里的那个图标"上才看得见，
    /// 而那个图标归 `TrayApp` 所有 —— 于是装配的顺序是"先建托盘，再把图标交回给这份 sink"
    /// （见 `AppContext.Build` 里那一句 `BindShellBalloon`）。
    /// 记账器这一格是 null：它因此**从结构上**拿不到托盘图标，也就没有任何一条路径
    /// 能让一次单测把气泡弹到同学桌面上 —— 不是"大家注意别这么写"，是写不出来。
    public Action<Action<string, string?>>? BindShellBalloon { get; init; }

    /// 生产用的那一份：真 WinRT 投递 + 真托盘气泡 + 真开始菜单注册。
    /// 绑上托盘图标之前 `ShowBalloon` 什么都不做 —— 那一段（`Build` 里 new 托盘到绑定之间的一瞬）
    /// 系统里还没有任何一个事件源开着，所以既不会丢通知，也不可能弹在测试桌面上。
    public static NotificationSink Production()
    {
        Action<string, string?>? icon = null;
        return new NotificationSink(
            OpenToastChannel: (title, body, kind) =>
                ToastDelivery.OpenChannel(AumidRegistrar.Aumid, title, body, kind),
            ShowBalloon: (title, body) => icon?.Invoke(title, body),
            EnsureAumid: AumidRegistrar.Ensure)
        { BindShellBalloon = presenter => icon = presenter };
    }
}
