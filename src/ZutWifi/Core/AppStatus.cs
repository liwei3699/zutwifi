namespace ZutWifi.Core;

public enum AppPhase { Idle, Probing, AcquiringIp, LoggingIn, Verifying, Online, Degraded, Failed, GiveUp }

/// 界面上那一行字的唯一数据源。Reason 有硬契约（StatusPresenter 直接把它拼进文案、无兜底）：
/// Failed / GiveUp / Degraded 必须带非空原因，Probing / AcquiringIp / Online 必须为 null，
/// Idle 也必须为 null（切走 WiFi 后不能让界面继续挂着上一次的失败原因）。
public sealed record AppStatus(AppPhase Phase, string? Ssid = null, string? Ip = null,
    string? Reason = null, int? OnlineSeconds = null, bool CanRecoverRelogin = false,
    long? OnlineAtTickMs = null)
{
    /// 显示用的秒数 = 门户当场给的那一份 + 从"读到它"那一刻起本地走过的时间。
    ///
    /// 为什么不直接显示门户的数：门户只在每次刷新时答一次（60 秒一拍），照着它显示就是
    /// "每分钟跳一下"，而登录后那一拍我们手上只有 0 —— 同学看到的就是一个停在 00:00:00 的时长，
    /// 长得像坏了。
    ///
    /// 为什么以门户的数打底而不是从"我们登录那一刻"自己数：会话可能是你在浏览器里登的，
    /// 也可能在别的设备上被注销过；本地自己数在那两种情况下都会撒谎。
    /// 差值走单调时钟（`Monotonic`），所以改系统时间、跨夏令时都不会让时长跳一下或倒退。
    public int? SecondsAt(long tickMs) =>
        OnlineSeconds is not { } seconds || OnlineAtTickMs is not { } anchor
            ? OnlineSeconds                                   // 没锚点的状态照原样给，不猜
            : seconds + Math.Max(0, (int)((tickMs - anchor) / 1000));
}

/// 用户该看到的一条系统通知。协调器只给类型 + 一句话细节，
/// 文案与投递方式（通知中心 / 气球提示回退）由 Shell 侧的 Notifier 负责。
public enum NoticeKind
{
    LoginSucceeded, LoginFailed, Degraded, AlreadyOnlineElsewhere,
    AuthExpired, LogoutSucceeded, LogoutFailed
}
