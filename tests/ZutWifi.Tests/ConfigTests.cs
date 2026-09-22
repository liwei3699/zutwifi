using System.Text.Json;
using ZutWifi.Config;
using ZutWifi.Portal;
using ZutWifi.Tests.Support;
using ZutWifi.Wifi;

namespace ZutWifi.Tests;

/// 每个测试都用 Path.GetTempPath() 下的一个全新目录：
/// 单测既不碰 %APPDATA%（真实配置）也不碰 HKCU\...\Run（真实自启）。
/// StartupRegistry.Ensure 会真改注册表，这里只钉它的常量与 CommandValue()，真注册表由 Task 20 手工验收。
/// 目录由 TempSpace 登记：用例**红了也删**（以前是每个方法末尾手写一句 Directory.Delete，
/// 那句只在绿的时候跑得到，同学的 %TEMP% 就是这么攒出两千多个 zw* 的）。
public class ConfigTests : IDisposable
{
    private readonly TempSpace _tmp = new();

    string Tmp() => _tmp.NewDir("zwcfg");

    public void Dispose() => _tmp.Dispose();

    /// 手改的文件按"磁盘上的字节"写，不经过 SettingsStore.Save，否则测的是我们自己的序列化器而不是用户的笔。
    static SettingsStore WriteRaw(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
        return new SettingsStore(dir);
    }

    /// 同上，但由 JsonSerializer 负责转义：值里带制表符/换行的那份要写成合法的 \t \n 转义，
    /// 因为字面控制字符进 JSON 字符串本身就是坏文件，走的是另一条兜底分支（见"整个json改坏了"）。
    static SettingsStore WriteEscaped(string dir, object shape) =>
        WriteRaw(dir, JsonSerializer.Serialize(shape));

    [Fact]
    public void 默认值符合spec()
    {
        var s = new Settings();
        Assert.Equal(new[] { "zut-stu" }, s.SsidWhitelist);
        Assert.Equal("1.1.1.1", s.PortalHost);
        Assert.Equal("@cmcc", s.IspSuffix);
        Assert.Equal(3, s.MaxRetries);
        Assert.True(s.AutoStart);
        Assert.True(s.CloseToTray);
        Assert.False(s.FirstRunCompleted);
    }

    [Fact]
    public void 设置往返且文件里不含密码字段()
    {
        var dir = Tmp();
        var store = new SettingsStore(dir);
        var s = store.Load();
        s.StudentId = "202500000001";
        s.IspSuffix = "@ctcc";
        store.Save(s);
        Assert.Equal("@ctcc", new SettingsStore(dir).Load().IspSuffix);
        Assert.DoesNotContain("pass", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 密码经DPAPI往返且落盘不是明文()
    {
        var dir = Tmp();
        var secrets = new SecretStore(dir);
        secrets.Set("Pass@2024.");
        Assert.Equal("Pass@2024.", new SecretStore(dir).Get());
        var file = Path.Combine(dir, "secret.bin");
        Assert.DoesNotContain("Pass@2024.", File.ReadAllText(file));
        Assert.NotEqual(0, new FileInfo(file).Length);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 清空密码即删除密文文件()
    {
        var dir = Tmp();
        var secrets = new SecretStore(dir);
        secrets.Set("x");
        secrets.Set(null);
        Assert.Null(new SecretStore(dir).Get());
        Assert.False(File.Exists(Path.Combine(dir, "secret.bin")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 自启目标键与值名固定且用当前exe路径()
    {
        Assert.Equal("ZutWifi", StartupRegistry.ValueName);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistry.RunKeyPath);
        Assert.Equal(Environment.ProcessPath, StartupRegistry.CommandValue());
    }

    // ── Load() 是手改文件的唯一规范化出口 ──

    /// settings.json 由同学用记事本手改，改出来的形状没人保证。这里收口的三处各自对应一种真实故障：
    /// null 白名单让后台线程上的 SsidMatcher 抛 ArgumentNullException；未小写的主机名让探测永不命中；
    /// 无界的 MaxRetries 把"保守重试"这个需求本身抹掉。

    [Fact]
    public void 手改成null的白名单回落默认值而不是把null带给状态机()
    {
        var dir = Tmp();
        var s = WriteRaw(dir, """{"SsidWhitelist":null,"StudentId":"202500000001"}""").Load();
        Assert.Equal(new[] { "zut-stu" }, s.SsidWhitelist);
        Assert.True(SsidMatcher.IsCampus("zut-stu", s.SsidWhitelist));   // 状态机真正用到的那一刀
        Assert.Equal("202500000001", s.StudentId);                       // 同一条记录里的其他字段不受影响
        Directory.Delete(dir, true);
    }

    /// 空列表和 null 不是一回事：[] 是用户主动关掉自动登录的开关，规范化不许顺手把它打开。
    [Fact]
    public void 显式清空白名单保持为空()
    {
        var dir = Tmp();
        var s = WriteRaw(dir, """{"SsidWhitelist":[]}""").Load();
        Assert.Empty(s.SsidWhitelist);
        Assert.False(SsidMatcher.IsCampus("zut-stu", s.SsidWhitelist));
        Directory.Delete(dir, true);
    }

    /// PortalGateway.ProbeAsync 的判定是 `resp.Headers.Location?.Host == portalHost` 的精确比较，
    /// 而 Uri.Host 永远小写、无空白：手改成"1.1.1.1 "或大写就永不命中，
    /// 于是登录成功后仍被判成未认证 → 反复重登，最后把账号打进 RADIUS 锁死。
    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1")]
    [InlineData("  1.1.1.1  ", "1.1.1.1")]
    [InlineData("Portal.Example.COM", "portal.example.com")]
    [InlineData("\t1.1.1.1\r\n", "1.1.1.1")]
    [InlineData("", "1.1.1.1")]         // 空串/缺失/null 都回默认值，别让 http://:9002/0 这种 URL 出去
    [InlineData(null, "1.1.1.1")]
    public void 门户主机读进来时被规范化成与UriHost同形(string? raw, string want)
    {
        var dir = Tmp();
        var s = WriteEscaped(dir, new { PortalHost = raw }).Load();
        Assert.Equal(want, s.PortalHost);
        Assert.Equal(new Uri(PortalMessages.ProbeUrl(s.PortalHost)).Host, s.PortalHost);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 缺失门户主机字段时用默认值()
    {
        var dir = Tmp();
        Assert.Equal("1.1.1.1", WriteRaw(dir, """{"StudentId":"202500000001"}""").Load().PortalHost);
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(20, 3)]      // 不夹住就是 RetryPolicy(20).MaxAttempts = 21 次凭据提交
    [InlineData(9999, 3)]
    public void 重试上限被夹回0到3(int raw, int want)
    {
        var dir = Tmp();
        var s = WriteRaw(dir, $$"""{"MaxRetries":{{raw}}}""").Load();
        Assert.Equal(want, s.MaxRetries);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 整个json改坏了时回落默认值而不是让程序起不来()
    {
        var dir = Tmp();
        var s = WriteRaw(dir, "{ \"MaxRetries\": 这不是数字,,,").Load();
        Assert.Equal(3, s.MaxRetries);
        Assert.Equal("1.1.1.1", s.PortalHost);
        Assert.Equal(new[] { "zut-stu" }, s.SsidWhitelist);
        Directory.Delete(dir, true);
    }

    /// 启动路径上更常见的失败不是"json 写坏了"，而是"文件正被别的进程独占"：
    /// 同学在 VS/记事本里打开 settings.json，或网盘同步客户端正在写它，File.ReadAllText 抛的是
    /// IOException（共享冲突）而不是 JsonException —— 只挡 JsonException 的话程序当场崩。
    [Fact]
    public void 配置文件被别的进程独占时Load回落默认值而不是抛出()
    {
        var dir = Tmp();
        var store = WriteRaw(dir, """{"StudentId":"202500000001","MaxRetries":2}""");
        using (var hold = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var s = store.Load();                                  // 独占句柄没释放，这一刀必须不抛
            Assert.Equal("zut-stu", Assert.Single(s.SsidWhitelist));
            Assert.Equal("1.1.1.1", s.PortalHost);
            Assert.Equal(3, s.MaxRetries);                          // 默认值，不是文件里那个 2
            Assert.Equal("", s.StudentId);                          // 宁可弹首次向导，也不能起不来
        }
        Directory.Delete(dir, true);
    }

    /// 独占之后松开手：文件本身没坏，下一次 Load 必须照旧读出用户配置（回落只在这一次）。
    [Fact]
    public void 占用解除后Load照旧读到真实配置()
    {
        var dir = Tmp();
        var store = WriteRaw(dir, """{"StudentId":"202500000001","MaxRetries":2}""");
        using (var hold = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(3, store.Load().MaxRetries);
        }
        var s = store.Load();
        Assert.Equal(2, s.MaxRetries);
        Assert.Equal("202500000001", s.StudentId);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 没有settings文件时Load给默认值且不凭空造出文件()
    {
        var dir = Tmp();
        var store = new SettingsStore(dir);
        var s = store.Load();
        Assert.Equal("zut-stu", Assert.Single(s.SsidWhitelist));
        Assert.False(File.Exists(store.FilePath));   // 向导完成之前磁盘上不该有 settings.json
        Assert.Null(new SecretStore(dir).Get());
    }

    [Fact]
    public void 上次登录时间与显式关掉的开关都存得住()
    {
        var dir = Tmp();
        var store = new SettingsStore(dir);
        var s = store.Load();
        s.LastLoginAt = new DateTimeOffset(2026, 9, 19, 14, 5, 30, TimeSpan.FromHours(8));
        s.LastLoginMillis = 1834;
        s.AutoStart = false;
        s.CloseToTray = false;
        s.FirstRunCompleted = true;
        s.NotifierFallbackUsed = true;
        store.Save(s);
        var back = new SettingsStore(dir).Load();
        Assert.Equal(s.LastLoginAt, back.LastLoginAt);
        Assert.Equal(1834, back.LastLoginMillis);
        Assert.False(back.AutoStart);         // 默认是 true，存丢了就等于"用户关不掉自启"
        Assert.False(back.CloseToTray);
        Assert.True(back.FirstRunCompleted);
        Assert.True(back.NotifierFallbackUsed);
        Directory.Delete(dir, true);
    }

    /// 上面那条 DoesNotContain("pass") 查的是文件，这条查的是类型：日后谁给 Settings 加一个 Password 字段，
    /// 序列化器会把它原样写进那个"打包发给人看"的 json，而整条脱敏链都会以为它是安全的。
    [Fact]
    public void Settings类型里根本不存在密码属性()
    {
        var names = typeof(Settings).GetProperties().Select(p => p.Name).ToList();
        Assert.NotEmpty(names);
        Assert.All(names, n =>
        {
            Assert.False(n.Contains("pass", StringComparison.OrdinalIgnoreCase), n);
            Assert.False(n.Contains("secret", StringComparison.OrdinalIgnoreCase), n);
            Assert.False(n.Contains("pwd", StringComparison.OrdinalIgnoreCase), n);
        });
    }

    // ── 读-改-写：SettingsStore.TryUpdate（评审 I4）──

    /// 上面那两条测的是"读不出来时 Load 给默认值"（那是对的，程序要起得来）。
    /// 这一条测的是**紧接着的那一刀**：设置页/向导拿着那份默认值改一个字段就写回去，
    /// 于是同学的手改全没了 —— 手改的东西本来就无法从默认值里恢复。
    [Fact]
    public void 文件被独占时TryUpdate拒绝写回而不是把设置刷成出厂值()
    {
        var dir = Tmp();
        var store = WriteRaw(dir,
            """{"StudentId":"202500000001","MaxRetries":2,"PortalHost":"1.2.3.4","SsidWhitelist":["zut-stu","zut-teacher"],"AutoStart":false}""");
        var before = File.ReadAllText(store.FilePath);

        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(store.TryUpdate(s => s.IspSuffix = "@ctcc", out var why));   // 读不出来 ⇒ 不写
            Assert.NotNull(why);
        }

        Assert.Equal(before, File.ReadAllText(store.FilePath));                       // 一个字节都没动
        var after = store.Load();
        Assert.Equal("202500000001", after.StudentId);
        Assert.Equal(2, after.MaxRetries);
        Assert.Equal("1.2.3.4", after.PortalHost);
        Assert.Equal(new[] { "zut-stu", "zut-teacher" }, after.SsidWhitelist);
        Assert.False(after.AutoStart);
        Assert.NotEqual("@ctcc", after.IspSuffix);                                     // 那一下改没落盘
        Directory.Delete(dir, true);
    }

    /// 松手之后同一条通路照旧能写，而且只改那一个字段（TryUpdate 不是"整份重写一遍默认值"）。
    [Fact]
    public void 占用解除后TryUpdate照旧写得上且只改那一个字段()
    {
        var dir = Tmp();
        var store = WriteRaw(dir, """{"StudentId":"202500000001","MaxRetries":2,"PortalHost":"1.2.3.4"}""");
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(store.TryUpdate(s => s.AutoStart = false, out _));

        Assert.True(store.TryUpdate(s => s.AutoStart = false, out var why));
        Assert.Null(why);
        var s = new SettingsStore(dir).Load();
        Assert.False(s.AutoStart);
        Assert.Equal("202500000001", s.StudentId);
        Assert.Equal("1.2.3.4", s.PortalHost);
        Assert.Equal(2, s.MaxRetries);
        Directory.Delete(dir, true);
    }

    /// 手改坏的 JSON 同一种对待：读不出来就不写。这一次覆盖掉的是"他写的那半截"，
    /// 而那半截里可能还夹着能认出学号的字节 —— 让它原地不动，人才有机会自己修好它。
    [Fact]
    public void json改坏时TryUpdate也拒绝写回()
    {
        var dir = Tmp();
        var store = WriteRaw(dir, "{ \"MaxRetries\": 这不是数字,,,");
        var before = File.ReadAllText(store.FilePath);
        Assert.False(store.TryUpdate(s => s.StudentId = "202500000001", out var why));
        Assert.Contains("JSON", why);
        Assert.Equal(before, File.ReadAllText(store.FilePath));
        Directory.Delete(dir, true);
    }

    /// 第一次建档不算覆盖：那时磁盘上没有任何东西可被抹掉（向导的"完成"就走这一支）。
    [Fact]
    public void 还没有文件时TryUpdate就是建档()
    {
        var dir = Tmp();
        var store = new SettingsStore(dir);
        Assert.True(store.TryUpdate(s => { s.StudentId = "202500000001"; s.FirstRunCompleted = true; }, out var why));
        Assert.Null(why);
        Assert.True(File.Exists(store.FilePath));
        Assert.Equal("202500000001", new SettingsStore(dir).Load().StudentId);
        Directory.Delete(dir, true);
    }

    // ── DPAPI 的失败面 ──

    /// 换电脑、重装系统、被同步盘写坏、别的用户拿到这个文件——对程序来说都是同一件事：解不开。
    /// 必须返回 null 让首次向导接管；抛出会让托盘程序在启动路径上崩掉。
    [Theory]
    [InlineData("truncated")]   // 半截文件：断电或同步冲突的典型产物
    [InlineData("foreign")]     // 不属于本用户的 blob：随机字节，DPAPI 校验必不过
    public void 密文解不开时Get返回null而不是抛出(string kind)
    {
        var dir = Tmp();
        new SecretStore(dir).Set("Pass@2024.");
        var file = Path.Combine(dir, "secret.bin");
        var good = File.ReadAllBytes(file);
        if (kind == "truncated") File.WriteAllBytes(file, good[..(good.Length / 2)]);
        else
        {
            var noise = new byte[good.Length];
            new Random(20260919).NextBytes(noise);
            File.WriteAllBytes(file, noise);
        }
        Assert.Null(new SecretStore(dir).Get());
        Directory.Delete(dir, true);
    }

    /// 设置页里清空密码框保存的是 ""：必须和 null 一样删掉密文，
    /// 否则 Get() 会交出空串，状态机拿着 ",0,学号@cmcc" + 空密码去提交一次注定失败的登录。
    [Fact]
    public void 空串与null一样都表示未配置()
    {
        var dir = Tmp();
        var secrets = new SecretStore(dir);
        secrets.Set("Pass@2024.");
        secrets.Set("");
        Assert.Null(new SecretStore(dir).Get());
        Assert.False(File.Exists(Path.Combine(dir, "secret.bin")));
        Directory.Delete(dir, true);
    }

    /// 密码按原文存：门户对 `,` 前缀之外的字符不做规范化，多一个空格就是"密码错误"。
    [Fact]
    public void 含中文换行与空格的密码原样回来()
    {
        var dir = Tmp();
        const string Pw = "网口🙂 pass word\n with  spaces ";
        new SecretStore(dir).Set(Pw);
        Assert.Equal(Pw, new SecretStore(dir).Get());
        Directory.Delete(dir, true);
    }
}
