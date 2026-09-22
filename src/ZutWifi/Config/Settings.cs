namespace ZutWifi.Config;

/// 磁盘上的 settings.json 是同学会用手改的东西，所以这个类只负责"形状"，
/// 值能不能进程序由 SettingsStore.Load() 收口（见那里的 Normalise）。
/// 这里刻意没有密码字段：密码走 SecretStore 的 DPAPI 文件，不进 json、不进日志、不进诊断包。
public sealed class Settings
{
    public List<string> SsidWhitelist { get; set; } = ["zut-stu"];
    public string PortalHost { get; set; } = "1.1.1.1";
    public string StudentId { get; set; } = "";
    public string IspSuffix { get; set; } = "@cmcc";
    public int MaxRetries { get; set; } = 3;
    public bool AutoStart { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
    public bool NotifierFallbackUsed { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public int? LastLoginMillis { get; set; }
}
