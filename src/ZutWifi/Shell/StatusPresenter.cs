using ZutWifi.Core;

namespace ZutWifi.Shell;

public sealed record Presentation(string Text, string ColorKey, bool LoginEnabled,
    bool LogoutEnabled, bool ReprobeEnabled, bool RecoverVisible);

/// 界面文案、配色、按钮可用性只从这里出；MainForm 与 TrayApp 不再各自判断。
public static class StatusPresenter
{
    public static Presentation Of(AppStatus s, bool ssidWhitelisted)
    {
        if (!ssidWhitelisted) return new("未连接校园网", "gray", false, false, false, false);
        return s.Phase switch
        {
            AppPhase.Idle => new(string.IsNullOrWhiteSpace(s.Reason) ? "已连接校园网 · 待检测" : s.Reason!,
                "gray", true, false, true, false),
            AppPhase.Probing => new("检测中…", "yellow", false, false, false, false),
            AppPhase.AcquiringIp => new("获取内网地址…", "yellow", false, false, false, false),
            AppPhase.LoggingIn => new(string.IsNullOrWhiteSpace(s.Reason) ? "正在登录…" : s.Reason!,
                "yellow", false, false, false, false),
            AppPhase.Verifying => new("已认证 · 正在测试连通性", "yellow", false, false, false, false),
            AppPhase.Online => new("已认证 · 网络正常", "green", false, true, true, false),
            AppPhase.Degraded => new("已认证 · 外网不通", "orange", true, true, true, false),
            AppPhase.Failed => new("失败：" + (s.Reason ?? "未知"), "red", true, true, true, s.CanRecoverRelogin),
            AppPhase.GiveUp => new("登录失败：" + (s.Reason ?? "未知"), "red", true, false, true, false),
            _ => new("未知状态", "gray", false, false, false, false),
        };
    }

    public static string LoginText(AppPhase phase) =>
        phase is AppPhase.GiveUp or AppPhase.Failed ? "重试" : "登录";

    public static string FormatDuration(int? seconds)
    {
        var t = TimeSpan.FromSeconds(seconds ?? 0);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
