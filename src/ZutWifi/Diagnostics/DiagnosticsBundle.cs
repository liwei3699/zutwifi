using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ZutWifi.Config;

namespace ZutWifi.Diagnostics;

/// 同学电脑上的失效只能靠这个包复盘，所以它必须"一行代码就能生成、且绝不含密码"。
///
/// 四条规矩，每条都钉住一种"发回来的包帮不上忙"的真实情况：
/// ① 只在最终路径上出现一个**完整**的包：素材全备齐了才写 .part、再改名。半截包比没有包更坏 ——
///    同学会把它发出去，而缺的正好是关键那一页。任何一步失败都不留 .part。
/// ② secret.bin 与明文密码不进包：本类根本不读那个文件，挑进去的东西全按名字点名；
///    settings.json 里任何"字段名像密码"的值再兜一层换成 ***（今天没这种字段，防的是以后加）。
/// ③ 单项失败换成写在包里的一行字（采集失败 / 读取失败），导出照常完成；
///    只有"数据目录都不存在""目标写不下去"这种整包无意义的情况才返回一句错误文字（不抛出）。
/// ④ 日志本身写不写得进去要写在清单里：一份静默少写的日志，会让这个包看起来"什么都没发生"。
public static class DiagnosticsBundle
{
    /// 导出文件名前缀；清理旧包时也按这个前缀点名，绝不碰数据目录里的别的东西。
    public const string Prefix = "zutwifi-diagnostics-";

    /// 留几份历史：只留一份的话，"昨天那次失败"和"今天这次"就分不开了。
    internal const int KeepZips = 5;

    internal static readonly string[] SnapshotLabels = ["wlan", "ipconfig", "routes"];

    /// 返回 null = 成功；返回一句话 = 失败原因（调用方负责把它说给人听）。任何异常都不往外抛。
    public static string? Build(string zipPath, SettingsStore store, TransactionLog? log,
        Func<string, string>? snapshotProvider = null)
    {
        var dataDir = Path.GetDirectoryName(store.FilePath) ?? "";
        if (!Directory.Exists(dataDir))
            return $"数据目录不存在：{dataDir}（这台机器还没配置过 ZutWifi？先把程序跑完首次向导再导出）";

        var part = zipPath + ".part";
        var added = new List<string>();
        try
        {
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var logs = log?.LogDirectory ?? Path.Combine(dataDir, "logs");
                    var counts = AddLogs(zip, added, logs);
                    Add(zip, added, "settings.json", RedactSettings(SafeRead(store.FilePath)));
                    Add(zip, added, "version.txt", VersionText());
                    foreach (var label in SnapshotLabels)
                        Add(zip, added, label + ".txt", CollectSnapshot(label, snapshotProvider));
                    Add(zip, added, "manifest.txt", Manifest(dataDir, logs, log, added, counts));
                }
                packed = ms.ToArray();
            }

            var targetDir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            File.WriteAllBytes(part, packed);
            if (File.Exists(zipPath)) File.Delete(zipPath);   // 上一份还在的话换掉它，不是先删了再赌一把
            File.Move(part, zipPath, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            TryDelete(part);
            return Describe(ex);
        }
    }

    /// 导出去处：数据目录里带时间戳的一份，旧的只留最近 KeepZips 份。
    /// 选数据目录而不是桌面：那里一定有写权限（settings.json 就在旁边），而且路径本来就印在界面上，
    /// 同学不需要在两三个"可能在那里"的文件夹里找。返回的是**目标路径**，不写文件。
    /// 名字里带到毫秒并且撞名就补序号：只留一份历史的意义在于"昨天那次失败"和"今天这次"分得开，
    /// 同一秒里连点两次导出如果把后一份静默盖到前一份上，这个包就变成"只有一份"的假象。
    internal static string NewTargetPath(string dataDir)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        PruneOldZips(dataDir);                                  // 给马上要写的这一份留个位置
        var target = Path.Combine(dataDir, Prefix + stamp + ".zip");
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(dataDir, $"{Prefix}{stamp}-{n}.zip");
        return target;
    }

    private static void PruneOldZips(string dataDir)
    {
        try
        {
            var stale = new DirectoryInfo(dataDir).GetFiles(Prefix + "*.zip")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(KeepZips - 1).ToList();
            foreach (var f in stale) TryDelete(f.FullName);
        }
        catch (Exception) { /* 清理失败不影响导出：最多目录里多几份 zip */ }
    }

    // ---------- 各项内容 ----------

    /// 日志按名字点名（app-*.log / selftest-*.log），整个目录里唯一不属于这两类的就是别的东西 ——
    /// 密码文件本来也不在这个目录里，但"按名字挑"这条规矩要保证以后加文件也不会把它顺走。
    static (int app, int selftest) AddLogs(ZipArchive zip, List<string> added, string logs)
    {
        var app = 0; var selftest = 0;
        DirectoryInfo dir;
        try { dir = new DirectoryInfo(logs); }
        catch (Exception) { return (0, 0); }

        foreach (var (pattern, taken, name) in new[]
                 {
                     // 5 = TransactionLog 自己的保留份数（那边的常量是私有的，这里只是"最多捞这么多份"，
                     // 真漂动了也不会漏文件：目录里有几份就拿几份，上限只是防一个巨大的历史目录）。
                     ("app-*.log", 5, "app"),
                     ("selftest-*.log", 3, "selftest"),
                 })
        {
            var files = Newest(dir, pattern, taken);
            if (name == "app") app = files.Count; else selftest = files.Count;
            foreach (var f in files)
                Add(zip, added, "logs/" + f.Name, SafeRead(f.FullName));
        }
        return (app, selftest);
    }

    static List<FileInfo> Newest(DirectoryInfo dir, string pattern, int take)
    {
        try
        {
            return dir.GetFiles(pattern)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(take).ToList();
        }
        catch (Exception) { return []; }        // 目录不存在 / 没权限：这一项就是 0 份，清单里会说明
    }

    /// 快照：注入的委托优先（单测用它免掉子进程），否则跑对应的只读命令。
    /// 每一项都包在 Safe 里 —— 拿不到只是包里的一行字，不该让导出中断。
    internal static string CollectSnapshot(string label, Func<string, string>? provider)
    {
        try { return (provider ?? DefaultSnapshot)(label); }
        catch (Exception ex) { return "采集失败：" + (ex.Message.Length == 0 ? ex.GetType().Name : ex.Message); }
    }

    static string DefaultSnapshot(string label)
    {
        var (file, args) = SnapshotCommand(label);
        return file is null ? "采集失败：未知的快照项 " + label : CommandCapture.Capture(file, args);
    }

    /// 快照项 → 跑哪条命令。这是"同学发回来的那份包里的三页从哪儿来"的唯一答案，
    /// 所以它单独成一个方法、并被用例逐字钉住：以前这张表住在一个 switch 表达式里，
    /// 换成另一条 netsh、或把 `-4` 去掉、或整段 return "" 都不会让任何用例报警
    /// （套件的每一条 Build 都传了 snapshotProvider，只有生产路径才走到这里）。
    /// 三条全部是只读本机命令：不敲门户、不出网，且各带 CommandCapture 那 5 秒上限。
    internal static (string? File, string Args) SnapshotCommand(string label) => label switch
    {
        "wlan" => ("netsh", "wlan show interfaces"),
        "ipconfig" => ("ipconfig", "/all"),        // /all 才有 MAC 与机器名：清单里承诺的就是这一页
        "routes" => ("route", "print -4"),        // -4：IPv6 那张表对这个包没有意义
        _ => (null, ""),
    };

    static string VersionText() =>
        "ZutWifi " + (typeof(DiagnosticsBundle).Assembly.GetName().Version?.ToString() ?? "?") +
        " / .NET " + Environment.Version +
        " / " + System.Runtime.InteropServices.RuntimeInformation.OSDescription +
        " / " + Environment.OSVersion.VersionString +
        $" / 机器名={Environment.MachineName} 用户={Environment.UserName}" +
        $" / {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}";

    static string Manifest(string dataDir, string logs, TransactionLog? log,
        IReadOnlyList<string> added, (int app, int selftest) counts)
    {
        var failures = log?.WriteFailures ?? 0;
        var sb = new StringBuilder();
        sb.AppendLine("ZutWifi 诊断包清单");
        sb.AppendLine($"生成时间：{DateTime.Now:o}");
        sb.AppendLine($"数据目录：{dataDir}");
        sb.AppendLine($"日志目录：{logs}");
        sb.AppendLine($"日志写入失败：{failures} 次" +
                      (failures == 0 ? "（日志通路正常）" : "（原因：" + (log?.FirstWriteFailure ?? "未知") +
                       "）—— 这个程序的全部排障能力都押在那些日志上，这一行就是它坏掉的证据"));
        sb.AppendLine($"日志份数：app={counts.app} selftest={counts.selftest}");
        if (log is null) sb.AppendLine("（这次导出没有传入事务日志对象，上面那一行只说明目录里现有什么）");
        sb.AppendLine("包含文件：");
        foreach (var name in added) sb.AppendLine("  " + name);
        // "不含密码"这一句不够：同一个包里还有 MAC、机器名与 Windows 用户名（ipconfig /all 与 version.txt
        // 带出来的）。发出去的人必须知道自己正在发的是什么，才谈得上"只发给维护者、别贴到公开场合"。
        sb.AppendLine("说明：这个包里有——最近的运行日志与自检日志（logs/）、脱敏后的设置（settings.json）、");
        sb.AppendLine("     WLAN/IP/路由三份网络快照（wlan.txt、ipconfig.txt、routes.txt）与版本系统信息（version.txt）。");
        sb.AppendLine("     快照与版本页里带着 MAC 地址、网卡名与 IP、本机机器名和当前 Windows 用户名：");
        sb.AppendLine("     这些是排障要用的（注销的是哪个会话、码页与系统版本都对不上时全靠它们），");
        sb.AppendLine("     但它们同样是你这台机器的身份信息，所以这个包只发给维护者，不要贴进群组或公开场合。");
        sb.AppendLine("     这个包不含任何密码：明文密码从来不进包，DPAPI 密文文件 secret.bin 也不在候选名单里；");
        sb.AppendLine("     settings.json 里任何名字像密码的字段值都已换成 ***。");
        sb.AppendLine("     若这份不足以定位问题，请在同样的故障状态下运行 \"ZutWifi.exe --selftest\" 后再导出一次，");
        sb.AppendLine("     自检日志（logs/selftest-*.log）会一起进包。");
        return sb.ToString();
    }

    // ---------- 脱敏 ----------

    /// 键名像密码就打死值，按"名字"判而不是按今天的字段表判：
    /// settings.json 是同学会手改的文件，日后多出一个 PortalPassword 也不该漏出去。
    private static readonly Regex JsonPair = new(
        "\"(?<key>[A-Za-z_][A-Za-z0-9_]*)\"(?<gap>\\s*:\\s*)\"(?<val>[^\"]*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] SuspectKeyParts =
        ["pass", "pwd", "secret", "token", "key", "credential", "auth"];

    internal static string RedactSettings(string json) =>
        JsonPair.Replace(json, m => IsSecretKey(m.Groups["key"].Value)
            ? "\"" + m.Groups["key"].Value + "\"" + m.Groups["gap"].Value + "\"***\""
            : m.Value);

    private static bool IsSecretKey(string key) =>
        SuspectKeyParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    // ---------- 小工具：全部"失败换文字"，不抛 ----------

    static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) { return $"(读取失败：{Describe(ex)} —— 文件在吗？被别的程序占着吗？)"; }
    }

    static void Add(ZipArchive zip, List<string> added, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes);
        added.Add(name + $"（{bytes.Length} 字节）");
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* 删不掉也只能留着 */ }
    }

    static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
