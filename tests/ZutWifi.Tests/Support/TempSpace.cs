namespace ZutWifi.Tests.Support;

/// 临时目录的"这一条用例跑完就删"登记处。
///
/// 用法三条：测试类加 `: IDisposable`、类里加 `private readonly TempSpace _tmp = new();`、
/// 建目录改走 `_tmp.NewDir("zwcfg")`，然后 `public void Dispose() => _tmp.Dispose();`。
/// 完事什么都不用管：xUnit 在每条用例结束之后都会 Dispose 测试类实例，**断言抛了也照样 Dispose**，
/// 所以登记过的那一批目录一定被收走。
///
/// 为什么要有它：以前的写法是每个测试方法末尾手写一句 `Directory.Delete(dir, true)` ——
/// 那句只在"用例全绿"时才跑得到，于是一条红用例留一个目录，同学的 %TEMP% 就这么攒出了两千多个
/// `zw*`（`zwcfg…`/`zwui…`/`zwctx…`/`zw…`）。红用例恰恰是最需要留现场的时候？不是：
/// 现场在断言消息里，不在磁盘上；要留的人在环境变量里自己挑。
public sealed class TempSpace : IDisposable
{
    private readonly List<string> _registered = [];

    /// 给一个 %TEMP% 下、带指定前缀的新目录路径，并登记下来。
    /// 这里**不** CreateDirectory：好几条用例的前提正是"目录还不存在"或"目录里什么都没有"，
    /// 建不建由被测代码自己决定（要建的地方它们本来就建）。
    public string NewDir(string prefix = "zw")
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        lock (_registered) _registered.Add(dir);
        return dir;
    }

    /// 登记一个别处已经算出来的路径（子目录、某个 zip 名），让 Dispose 一并收走。
    public string Keep(string path)
    {
        lock (_registered) _registered.Add(path);
        return path;
    }

    public void Dispose()
    {
        List<string> snapshot;
        lock (_registered) { snapshot = [.. _registered]; _registered.Clear(); }
        foreach (var path in snapshot) DeleteIfOurTempDir(path);
    }

    /// 只删"我们自己在 %TEMP% 下建的、带 zw* 或 ZutWifi* 前缀的那一个路径"，三道闸缺一不可。
    /// 尤其不碰 %APPDATA%\ZutWifi：那是这个程序真身未来的家，测试永远不该拿它当临时目录，
    /// 万一有人登记错了，这里就是最后一道拦得住的闸。删不掉（被在途任务占着）就算了，绝不重试。
    internal static void DeleteIfOurTempDir(string path)
    {
        if (!IsOurTempDir(path)) return;
        try
        {
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true);
            else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool IsOurTempDir(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (!full.StartsWith(root.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString())
                ? root : root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;
        // 临时目录本身以及它下面任何一层都不能是"我们的家"以外的那种名字
        var relative = System.IO.Path.GetRelativePath(root, full);
        if (relative.Length == 0 || relative == ".") return false;
        var first = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)[0];
        return first.StartsWith("zw", StringComparison.OrdinalIgnoreCase)
               || first.StartsWith("zutwifi", StringComparison.OrdinalIgnoreCase);
    }
}
