using ZutWifi.Config;
using ZutWifi.Diagnostics;
using ZutWifi.Portal;
using ZutWifi.Shell;
using ZutWifi.Tests.Support;

namespace ZutWifi.Tests;

/// 设置页只验三件事：值落在哪儿（学号进 json、密码进 DPAPI，两边互不串门）、
/// "测试配置"打出去的是哪几个请求、以及自启意图交给的是我注入的那条通路。
/// StartupRegistry.Ensure 会真改 HKCU\...\Run，AumidRegistrar.Ensure 会真往开始菜单写快捷方式，
/// 测试里一次都不许碰 —— 所以两条都是构造参数注入的接缝。
/// 目录走 TempSpace：这一整个类以前一个都没删过（红不红都不删），是 %TEMP% 里那两千多个 zw* 的大头。
public class SettingsPageTests : IDisposable
{
    private readonly TempSpace _tmp = new();

    string Tmp() => _tmp.NewDir("zwui");

    public void Dispose() => _tmp.Dispose();

    /// 自启写入的替身：记下写了什么，但绝不碰注册表。
    private sealed class AutoStartSpy
    {
        public List<bool> Writes { get; } = [];
        public string? Error { get; set; }
        public string? Ensure(bool enabled) { Writes.Add(enabled); return Error; }
    }

    private static SettingsPage NewPage(string dir, AutoStartSpy? spy = null,
        TransactionLog? log = null, Func<PortalGateway>? gateway = null)
    {
        Func<bool, string?>? apply = null;
        if (spy is not null) apply = spy.Ensure;
        return new SettingsPage(new SettingsStore(dir), new SecretStore(dir), log, apply, gateway);
    }

    /// 门户回放的替身：探测→未认证、取 IP→真机页面、登录→3.htm。
    private static FakeHttpHandler ReplayPortal()
    {
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_success.txt"));
        return h;
    }

    private static Func<PortalGateway> ReplayGateway(FakeHttpHandler h, TransactionLog? log = null) =>
        () => new PortalGateway(new HttpClient(h), "1.1.1.1", log);

    // ---------- 值落在哪儿 ----------

    [Fact]
    public void 保存时密码进DPAPI账户进json且互不串门()
    {
        var dir = Tmp();
        var store = new SettingsStore(dir);
        var secrets = new SecretStore(dir);
        var page = NewPage(dir, new AutoStartSpy());
        page.SetStudentId("202500000001");
        page.SetIsp("@ctcc");
        page.SetPassword("NewPass1!");
        page.SimulateSave();
        Assert.Equal("202500000001", new SettingsStore(dir).Load().StudentId);
        Assert.Equal("@ctcc", new SettingsStore(dir).Load().IspSuffix);
        Assert.Equal("NewPass1!", new SecretStore(dir).Get());
        Assert.DoesNotContain("NewPass1!", File.ReadAllText(new SettingsStore(dir).FilePath));
    }

    [Fact]
    public void 密码框不回填明文只显示已设置()
    {
        var dir = Tmp();
        new SecretStore(dir).Set("abc");
        var page = NewPage(dir);
        Assert.Equal("已保存（留空表示不修改）", page.PasswordPlaceholderText);
        Assert.Equal("", page.PasswordText);                     // 框里始终是空的，不是打出来的圆点
    }

    [Fact]
    public void 没有密文时密码框提示未设置()
        => Assert.Equal("未设置", NewPage(Tmp()).PasswordPlaceholderText);

    [Fact]
    public void 打开页面时把磁盘上的设置回填进控件()
    {
        var dir = Tmp();
        new SettingsStore(dir).Save(new Settings
        {
            StudentId = "202500000009", IspSuffix = "@unicom", SsidWhitelist = ["zut-stu", "zut-teacher"],
            AutoStart = false, CloseToTray = false,
        });
        var page = NewPage(dir);
        Assert.Equal("202500000009", page.StudentIdText);
        Assert.Equal("@unicom", page.IspText);
        Assert.Equal("zut-stu,zut-teacher", page.SsidsText);
        Assert.False(page.AutoStartChecked);
        Assert.False(page.CloseToTrayChecked);
    }

    [Fact]
    public void 密码留空保存时不覆盖已存的密文()
    {
        var dir = Tmp();
        new SecretStore(dir).Set("OldPass1!");
        var page = NewPage(dir, new AutoStartSpy());
        page.SetStudentId("202500000001");
        page.SimulateSave();
        Assert.Equal("OldPass1!", new SecretStore(dir).Get());
    }

    [Fact]
    public void 自启开关写注册表意图进设置()
    {
        var dir = Tmp();
        var spy = new AutoStartSpy();
        var page = NewPage(dir, spy);
        page.SetAutoStart(false);
        page.SimulateSave();
        Assert.False(new SettingsStore(dir).Load().AutoStart);
        Assert.Equal(new[] { false }, spy.Writes);                // 只经过注入的那条通路，真注册表一次都没碰
    }

    [Fact]
    public void 注册表写失败时保存仍然成功且把原因显示出来()
    {
        var dir = Tmp();
        var spy = new AutoStartSpy { Error = "策略阻止写入 Run 键" };
        var page = NewPage(dir, spy);
        page.SetStudentId("202500000001");
        var err = Record.Exception(page.SimulateSave);
        Assert.Null(err);                                        // 自启写不进去绝不能连累保存
        Assert.Equal("202500000001", new SettingsStore(dir).Load().StudentId);
        Assert.Contains("策略阻止写入 Run 键", page.HintText);
    }

    [Fact]
    public void 清空SSID保存的是空白名单也就是关掉自动登录()
    {
        // SettingsStore.Normalise 明确把空列表当"用户主动关掉自动登录的开关"原样留着；
        // 设置页不许把它偷偷改回 ["zut-stu"]，否则那个开关在界面上就没有出口。
        var dir = Tmp();
        var page = NewPage(dir, new AutoStartSpy());
        page.SetSsids("");
        page.SimulateSave();
        Assert.Empty(new SettingsStore(dir).Load().SsidWhitelist);
        Assert.Contains("不会自动登录", page.HintText);
    }

    [Fact]
    public void 磁盘上的空白名单重开页面不会被复活成默认值()
    {
        var dir = Tmp();
        new SettingsStore(dir).Save(new Settings { SsidWhitelist = [] });
        var page = NewPage(dir, new AutoStartSpy());
        page.SimulateSave();
        Assert.Empty(new SettingsStore(dir).Load().SsidWhitelist);
    }

    [Fact]
    public void 设置文件被占用时保存不抛且把失败显示出来()
    {
        // settings.json 的位置被一个同名目录占住（真机等价物：编辑器开着它 / 网盘正在同步）。
        // 保存不能把界面顶掉，但也不能假装成功 —— 同学据此决定下一步测什么。
        var dir = Tmp();
        Directory.CreateDirectory(Path.Combine(dir, "settings.json"));
        var page = NewPage(dir, new AutoStartSpy());
        page.SetStudentId("202500000001");
        var err = Record.Exception(page.SimulateSave);
        Assert.Null(err);
        Assert.StartsWith("保存失败", page.HintText);
    }

    [Fact]
    public async Task 保存没成功时测试配置说清测的是旧配置()
    {
        var dir = Tmp();
        Directory.CreateDirectory(Path.Combine(dir, "settings.json"));
        new SecretStore(dir).Set("Pass@2024.");
        var page = NewPage(dir, new AutoStartSpy(), null, ReplayGateway(ReplayPortal()));
        page.SetStudentId("改了也白改");

        await page.SimulateTestClick();

        Assert.Contains("磁盘上那份旧配置", page.TestOutputText);
    }

    // ---------- 读-改-写的保护（评审 I4）与那一栏标题（评审次项） ----------

    /// settings.json 正被另一个进程独占（编辑器开着它 / 网盘正在同步）时点保存：
    /// Load() 那时给的是默认值，照它写回去就是把同学的门户地址、重试上限与白名单整份刷成出厂值。
    /// 界面上的要求很窄：什么都不写，说清楚为什么没写，别把界面顶掉。
    [Fact]
    public void 文件被别的进程独占时保存不写回也不把设置刷成出厂值()
    {
        var dir = Tmp();
        var store = new SettingsStore(dir);
        store.Save(new Settings
        {
            StudentId = "202500000001", IspSuffix = "@ctcc", PortalHost = "1.2.3.4", MaxRetries = 1,
            SsidWhitelist = ["zut-stu", "zut-teacher"], AutoStart = false,
        });
        var spy = new AutoStartSpy();
        var page = NewPage(dir, spy);                      // 建页时文件还没被占住，读到的是真设置
        page.SetStudentId("改了也白改");
        page.SetPassword("Pass@2024.");
        var before = File.ReadAllText(store.FilePath);

        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(Record.Exception(page.SimulateSave));

        Assert.Equal(before, File.ReadAllText(store.FilePath));    // 一个字节都没被改写
        Assert.StartsWith("保存失败", page.HintText);
        Assert.Contains("占用", page.HintText);                    // 说的是人话：谁挡住了这次保存
        Assert.Empty(spy.Writes);                                  // 保存没成就不去动注册表
        Assert.Null(new SecretStore(dir).Get());                   // 密码也不单独抢跑一次
        var after = new SettingsStore(dir).Load();
        Assert.Equal("202500000001", after.StudentId);
        Assert.Equal("1.2.3.4", after.PortalHost);
        Assert.Equal(1, after.MaxRetries);
        Assert.Equal(new[] { "zut-stu", "zut-teacher" }, after.SsidWhitelist);
    }

    /// 同一件事在手改坏的 JSON 上也要成立：读不出来就不写。
    [Fact]
    public void 磁盘上是半截json时保存不写回()
    {
        var dir = Tmp();
        var store = new SettingsStore(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(store.FilePath, "{ \"StudentId\": \"202500000001\", 这不是JSON");
        var before = File.ReadAllText(store.FilePath);
        var page = NewPage(dir, new AutoStartSpy());
        page.SetStudentId("202500000009");

        Assert.Null(Record.Exception(page.SimulateSave));

        Assert.Equal(before, File.ReadAllText(store.FilePath));
        Assert.Contains("JSON", page.HintText);
    }

    /// 那一栏的标题以前写"密码只会以长度形式出现"，可这个框里从来只印门户判定、内网地址与登录结果
    /// —— 长度只进事务日志。标题承诺一件界面不做的事，照着它排查的人会去找一个不存在的东西。
    [Fact]
    public async Task 测试配置的输出里既不出现密码也不出现它的长度()
    {
        var dir = Tmp();
        var page = NewPage(dir, new AutoStartSpy(), null, ReplayGateway(ReplayPortal()));
        page.SetStudentId("202500000001");
        page.SetPassword("Pass@2024.");

        await page.SimulateTestClick();

        Assert.DoesNotContain("Pass@2024.", page.TestOutputText);
        Assert.DoesNotContain("len=", page.TestOutputText);
        Assert.DoesNotContain("长度", page.TestOutputCaption);          // 标题不再许诺界面里没有的东西
        Assert.Contains("不会出现密码", page.TestOutputCaption);
    }

    // ---------- 测试配置 ----------

    [Fact]
    public async Task 测试配置只发探测取IP登录三个请求不带Cookie也不注销()
    {
        var dir = Tmp();
        var h = ReplayPortal();
        var log = new TransactionLog(new FakeClock(), Path.Combine(dir, "logs"));
        var page = NewPage(dir, new AutoStartSpy(), log, ReplayGateway(h, log));
        page.SetStudentId("202500000001");
        page.SetPassword("Pass@2024.");
        page.SetIsp("@ctcc");

        await page.SimulateTestClick();

        var output = page.TestOutputText;
        Assert.Contains("Unauthenticated", output);              // ① 探测判定
        Assert.Contains("10.133.126.113", output);               // ② 内网地址
        Assert.Contains("成功", output);                          // ③ 登录判据
        Assert.Equal(3, h.Requests.Count);
        Assert.All(h.Requests, r => Assert.Null(r.CookieHeader)); // 零 Cookie 是真机验证过的契约
        Assert.DoesNotContain(h.Requests, r => r.Uri.Query.Contains("a=Logout"));
        Assert.DoesNotContain(h.Requests, r => (r.Body ?? "").Contains("ACLogOut"));
        // 点测试之前先把设置落了盘：否则测的是旧配置，改完密码看不到新结果
        Assert.Equal("202500000001", new SettingsStore(dir).Load().StudentId);
        Assert.Equal("Pass@2024.", new SecretStore(dir).Get());
    }

    [Fact]
    public async Task 测试配置写的日志只有脱敏后的密码()
    {
        var dir = Tmp();
        var h = ReplayPortal();
        var log = new TransactionLog(new FakeClock(), Path.Combine(dir, "logs"));
        var page = NewPage(dir, new AutoStartSpy(), log, ReplayGateway(h, log));
        page.SetStudentId("202500000001");
        page.SetPassword("Pass@2024.");

        await page.SimulateTestClick();

        var written = string.Join('\n', log.RecentLines());
        Assert.Contains("Login", written);
        Assert.DoesNotContain("Pass@2024.", written);
        Assert.Contains("upass=***(len=", written);
    }

    [Fact]
    public async Task 没填密码时点测试配置一个请求都不发()
    {
        // 空密码去提交就是白送一次 RADIUS 失败计数，账号会被打进锁定 —— 界面上必须先拦住。
        var dir = Tmp();
        var h = new FakeHttpHandler();
        var page = NewPage(dir, new AutoStartSpy(), null, ReplayGateway(h));
        page.SetStudentId("202500000001");

        await page.SimulateTestClick();

        Assert.Empty(h.Requests);
        Assert.Contains("密码", page.TestOutputText);
    }

    [Fact]
    public async Task 门户拒绝时测试配置显示中文原因()
    {
        var dir = Tmp();
        var h = new FakeHttpHandler();
        h.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        h.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        h.EnqueueRaw(Fixtures.Read("login_reject_pwerr.txt"));
        var page = NewPage(dir, new AutoStartSpy(), null, ReplayGateway(h));
        page.SetStudentId("202500000001");
        page.SetPassword("WrongPass!");

        await page.SimulateTestClick();

        Assert.Contains("失败", page.TestOutputText);
        Assert.Contains("Radius 认证失败（账号或密码错误）", page.TestOutputText);
    }
}
