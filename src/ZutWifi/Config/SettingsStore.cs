using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZutWifi.Config;

/// 读写 `%APPDATA%\ZutWifi\settings.json`（目录由 Task 17 的 AppContext.DataDir 决定，本类不认识 %APPDATA%）。
public sealed class SettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// 本程序的重试硬顶。一次连接事件 = 1 次提交 + MaxRetries 次重试（RetryPolicy.MaxAttempts = MaxRetries+1），
    /// 3 就是最多 4 次凭据提交。这个应用的全部要点就是保守：连续错密码会把账号打进 RADIUS 锁定，
    /// 所以"上限"不是可调项，是夹取边界。
    public const int MaxRetriesCeiling = 3;

    /// 规范化时的兜底值唯一来源：Settings 自己的属性初始值。这里再抄一份字面量就会和它漂移。
    private static readonly Settings Fallback = new();

    public string FilePath => System.IO.Path.Combine(directory, "settings.json");

    /// <summary>
    /// 读盘，对外行为与以前一字不变：读不出来就给一份默认值（手改坏了配置就用默认值，别让程序起不来）。
    /// "这一次到底读到了没有"由 <see cref="TryLoad"/> 说出去 —— 要读-改-写的人必须知道。
    /// </summary>
    public Settings Load() => TryLoad(out var s, out _) ? s : new Settings();

    /// <summary>
    /// 读盘 + 如实报告读没读到。三种失败（被别的进程独占、只读/权限、半截 JSON）在真机上都会发生，
    /// 而它们对外的表现是同一种：这一次当没有配置，下次文件空出来照旧读得到。
    /// <para>
    /// 为什么还要多这一个 bool：只拿 <see cref="Load"/> 的调用方分不清"磁盘上本来就没有"与
    /// "有，但我没读到"，于是<strong>读-改-写</strong>那条路会拿着默认值往下写，
    /// 把同学的 PortalHost、MaxRetries、SSID 白名单整份刷成出厂状态（评审 I4）。
    /// 还没有 settings.json 时算读成功：那一刻磁盘上没有任何东西可以被覆盖，那一次写就是建档。
    /// </para>
    /// </summary>
    /// <param name="settings">读到了就是那一份（已规范化）；读不出来是默认值，仅供界面显示。</param>
    /// <param name="reason">读不出来的原因，直接可以贴给用户看；读成功时为 null。</param>
    public bool TryLoad(out Settings settings, out string? reason)
    {
        if (!File.Exists(FilePath)) { settings = new Settings(); reason = null; return true; }
        try
        {
            var read = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Opts) ?? new Settings();
            settings = Normalise(read);
            reason = null;
            return true;
        }
        catch (JsonException ex)
        {
            settings = new Settings();
            reason = "settings.json 里的 JSON 读不出来，先把它改对再保存。原因：" + ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            // 磁盘上真实存在的 settings.json 被别的进程独占（同学在编辑器里打开它、网盘同步客户端正在写）
            // 时 ReadAllText 抛的是 IOException，这是启动路径上更常撞到的那一种。
            settings = new Settings();
            reason = "settings.json 正被别的进程占用（编辑器/网盘同步），先关掉它再保存。原因：" + ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            settings = new Settings();
            reason = "settings.json 读不了（只读或权限被改过）。原因：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读-改-写的唯一安全通路：读没成功是知道的，没读成功就**一个字都不写**。
    /// 界面上那两个"Load → 改几个框 → Save"的写法（设置页的保存、向导的完成）都从这里走。
    /// </summary>
    /// <param name="mutate">只在读成功时被调一次，参数是磁盘上那一份（不是默认值）。</param>
    /// <param name="reason">false 时是"为什么没写"，可以直接贴给用户；true 时为 null。</param>
    /// <returns>false = 磁盘上那份没读出来，因此**没有写**。写本身失败照旧抛出，由调用方说给用户听。</returns>
    public bool TryUpdate(Action<Settings> mutate, out string? reason)
    {
        if (!TryLoad(out var s, out reason)) return false;
        mutate(s);
        Save(s);
        return true;
    }

    public void Save(Settings s)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Opts));
    }

    /// <summary>
    /// 手改文件进程序之前的唯一规范化出口：反序列化只保证"能解析"，不保证"能用"。
    /// 三条不变量各自钉住一种真实故障，取值一律以 Settings 的属性初始值为唯一来源，免得两处字面量漂移。
    /// </summary>
    private static Settings Normalise(Settings s)
    {
        // null 白名单会让后台状态机线程上的 SsidMatcher 直接抛出（实测 ArgumentNullException，
        // 来自 whitelist.Any(...) —— 不是"安静地不匹配"）；
        // 空列表 [] 不一样——它是用户主动关掉自动登录的开关，必须原样留着。
        s.SsidWhitelist ??= [.. Fallback.SsidWhitelist];

        // 探测判定是 `resp.Headers.Location?.Host == portalHost` 的精确比较，而 Uri.Host 恒为小写无空白：
        // 主机名不归一化就是"登录成功但永远判成未认证"，比崩溃更难查。
        s.PortalHost = HostOrDefault(s.PortalHost?.Trim().ToLowerInvariant());

        s.MaxRetries = Math.Clamp(s.MaxRetries, 0, MaxRetriesCeiling);
        return s;
    }

    private static string HostOrDefault(string? trimmedHost) =>
        string.IsNullOrEmpty(trimmedHost) ? Fallback.PortalHost : trimmedHost;
}
