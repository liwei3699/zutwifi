using System.Globalization;
using System.Text;
using ZutWifi.Core;

namespace ZutWifi.Diagnostics;

/// 失效时要能只靠磁盘文件复盘：门户不通 / 被门户拒绝（带中文原因）/ 认证成功但外网不通，
/// 三种情况在这一行里就能分开。密码永远只以长度形式出现。
public sealed class TransactionLog(IClock clock, string? directory = null)
{
    private const int KeepFiles = 5;
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly object _gate = new();
    private int _writeFailures;
    private string? _firstWriteFailure;
    private string Dir { get; } = directory
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                  "ZutWifi", "logs");

    /// 写盘失败的次数与第一次的原因。日志本身写坏时读不到任何行，这两个属性是唯一还能拿到"日志坏了"
    /// 这件事的通道（Task 17 的状态区 / Task 18 的诊断包用得上）。计数只增不减：进程存活期间
    /// "曾经写坏过"必须一直可见，否则 60 秒一次探测里恰好恢复的那一次会把故障抹掉。
    public int WriteFailures => Volatile.Read(ref _writeFailures);
    public string? FirstWriteFailure => Volatile.Read(ref _firstWriteFailure);

    public string LineFor(TransactionRecord r) => OneLine(
        $"[{clock.UtcNow.ToLocalTime():HH:mm:ss.fff}] {r.Stage} {r.Method} {r.Url} " +
        $"status={r.Status?.ToString() ?? "-"} location={r.Location ?? "-"} " +
        $"result={r.Summary ?? "-"} error={r.Failure ?? "-"}");

    /// 整个方法体（含 Prune）都在守卫之内：这一行是 60 秒定时器里的附属品，
    /// 磁盘故障只能让"这一行没记上"，不能让一个还能工作的门户调用变成一次抛错。
    public void Write(TransactionRecord r)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FileForToday(), LineFor(r) + Environment.NewLine, Encoding.UTF8);
                Prune();
            }
        }
        catch (Exception ex) when (IsDiskFault(ex))
        {
            Interlocked.Increment(ref _writeFailures);
            // 只记第一次的原因：后面每次都是同一个磁盘故障，换行刷屏反而盖住根因。
            if (Volatile.Read(ref _firstWriteFailure) is null)
                Volatile.Write(ref _firstWriteFailure, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// 只吞磁盘层面的故障：盘满/无权限（IOException、UnauthorizedAccessException）、
    /// 目录被删或路径不合法（ArgumentException、NotSupportedException）。
    /// 别的异常（空引用之类）继续抛——把它们一起静默掉，就等于把实现 bug 也一起吞了。
    private static bool IsDiskFault(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    public IReadOnlyList<string> RecentLines(int take = 300)
    {
        lock (_gate)
        {
            if (!Directory.Exists(Dir)) return [];
            var newest = new DirectoryInfo(Dir).GetFiles("app-*.log")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
            return newest is null ? [] : File.ReadLines(newest.FullName).TakeLast(take).ToList();
        }
    }

    public string LogDirectory => Dir;

    public static string RedactForm(string body) => string.Join("&", body.Split('&').Select(pair =>
    {
        var i = pair.IndexOf('=');
        if (i < 0) return pair;
        var (name, value) = (pair[..i], pair[(i + 1)..]);
        return name.Equals("upass", StringComparison.OrdinalIgnoreCase) ? $"{name}=***(len={value.Length})" : pair;
    }));

    /// Url/Location/原因/异常消息都有外来成分：门户回一个带 CR 或 LF 的 Location 就能把一次调用劈成两行，
    /// 而 RecentLines 与复盘都按"一次调用一行"来数——劈开的行会被读成两次调用，比缺行更难查。
    private static string OneLine(string s) =>
        s.IndexOfAny(['\r', '\n']) < 0 ? s : s.Replace('\r', ' ').Replace('\n', ' ');

    /// yyyyMMdd 走 InvariantCulture：默认文化若是佛历/回历（th-TH、ar-SA…）会给出六位数年份，
    /// 而 Prune/RecentLines 都靠文件名的定宽十进制序做 Ordinal 比较，年份一变形就同时错两个地方。
    private string FileForToday() =>
        System.IO.Path.Combine(Dir, "app-" + clock.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    private void Prune()
    {
        var files = new DirectoryInfo(Dir).GetFiles("app-*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        foreach (var stale in files.Skip(KeepFiles)) stale.Delete();
        if (files.Count > 0 && files[0].Length > MaxBytes) files[0].Delete();
    }
}
