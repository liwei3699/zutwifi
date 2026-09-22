using ZutWifi.Tests.Support;

namespace ZutWifi.Tests;

/// `TempSpace` 自己那一格的回归网。它是"这条用例跑完（包括跑砸）就把登记过的目录收走"这件事的
/// 唯一实现，它坏了整个套件的 %TEMP% 就又没人管了 —— 同学的机器已经为这件事付出过两千多个
/// `zw*` 目录的代价（那一轮红用例每留一个现场，没人回收，也没人知道自己留了）。
/// 所以这里钉三件：① 登记过的真的被删（连里面的文件一起）；② 用例抛了照样删；
/// ③ 三道闸缺一不可 —— 尤其 %APPDATA%\ZutWifi（这个程序真身的家）绝不能被误删。
public class TempSpaceTests : IDisposable
{
    private readonly TempSpace _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 登记过的目录在收尾时连里面的文件一起消失()
    {
        var dir = _tmp.NewDir("zwkeep");
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "logs", "app-20260919.log"), "一条记录");

        _tmp.Dispose();                       // 下面那两条演的是"抛了也走这一步"

        Assert.False(Directory.Exists(dir));
    }

    /// "红了也删"不是口头约定，是 xUnit 的契约：拿到测试类实例之后一定 Dispose，断言抛了也一样。
    /// 这里把同一件事演一遍：抛出去 ⇒ 收尾照旧跑 ⇒ 登记的那一批不留在磁盘上。
    /// （以前的写法是把 `Directory.Delete` 手写在各条用例的最后一行，那一行只有全绿才走得到。）
    [Fact]
    public void 用例抛了之后登记的那一批照样被收走()
    {
        var dir = _tmp.NewDir("zwboom");
        Directory.CreateDirectory(Path.Combine(dir, "settings"));

        // 显式写成 Action 局部量：这段块只会从异常离开（没有可到达的结尾），
        // 直接内联进 Record.Exception(...) 会被重载解析挑到 `Func<Task>` 那一个（CS0619）。
        Action redTestCase = () =>
        {
            try { throw new InvalidOperationException("模拟一条红用例：断言在删目录之前就抛了"); }
            finally { _tmp.Dispose(); }            // 装配点之外没人记得补的那一句，xUnit 替所有人补
        };
        var boom = Record.Exception(redTestCase);

        Assert.NotNull(boom);                  // 异常照原样冒出去（收尾不许把红用例咽成绿）
        Assert.False(Directory.Exists(dir));
    }

    /// 二次 Dispose 不抛（xUnit 之外还有别处可能自己调一次），而且清空登记表：
    /// 收尾之后再登记进来的路径要等下一次 Dispose 才收，不会漏在同一个实例上。
    [Fact]
    public void 收尾两次不抛也不把新登记的漏在原地()
    {
        var tmp = new TempSpace();
        var first = tmp.NewDir("zwdouble");
        Directory.CreateDirectory(first);
        tmp.Dispose();
        Assert.False(Directory.Exists(first));

        var second = tmp.Keep(Path.Combine(Path.GetTempPath(), "zwdouble2" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(second);
        tmp.Dispose();
        Assert.False(Directory.Exists(second));
        tmp.Dispose();                         // 空表再收一次：不抛就是这一条要钉的
    }

    /// 三道闸：不在 %TEMP% 下的、名字不对的、临时目录自己 —— 一个都不许删。
    /// 名字这一闸存在的理由是"万一有人把 DataDir 当成临时目录登记进来了"：
    /// 那一条路必须被拦死，而不是靠大家小心。
    [Fact]
    public void 只认TEMP下zw或ZutWifi开头的那一些别的目录碰都不碰()
    {
        var temp = Path.GetTempPath();
        Assert.True(TempSpace.IsOurTempDir(Path.Combine(temp, "zwcfg" + Guid.NewGuid().ToString("N"))));
        Assert.True(TempSpace.IsOurTempDir(Path.Combine(temp, "ZutWifi" + Guid.NewGuid().ToString("N"))),
            "大小写两种前缀都要认（%TEMP% 里两种都有人写）");
        Assert.True(TempSpace.IsOurTempDir(Path.Combine(temp, "zwlog123", "sub")),
            "首段是自己的就行：删的时候是递归删");
        Assert.False(TempSpace.IsOurTempDir(temp), "临时目录自己绝不能删");
        Assert.False(TempSpace.IsOurTempDir(Path.Combine(temp, "nope-" + Guid.NewGuid().ToString("N"))));
        Assert.False(TempSpace.IsOurTempDir(AppContext.DataDir),
            "%APPDATA%\\ZutWifi 是这个程序真身的家，不是临时目录");
        Assert.False(TempSpace.IsOurTempDir(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        Assert.False(TempSpace.IsOurTempDir(Path.Combine(Path.GetPathRoot(temp)!, "zw" + Guid.NewGuid().ToString("N"))),
            "盘根下的 zw* 也不在授权范围内：只管 %TEMP%");

        // 光判不住还要真的不动手：名字不对的那一个，DeleteIfOurTempDir 必须原样留着它。
        var foreign = Path.Combine(temp, "nope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreign);
        try
        {
            TempSpace.DeleteIfOurTempDir(foreign);
            Assert.True(Directory.Exists(foreign));
        }
        finally { Directory.Delete(foreign); }   // 这一个不在登记范围内，自己收
    }
}
