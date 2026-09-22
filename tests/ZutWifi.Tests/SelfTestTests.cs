using System.Diagnostics;
using System.Text.RegularExpressions;
using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Diagnostics;
using ZutWifi.Portal;
using ZutWifi.Tests.Support;
using ZutWifi.Wifi;

namespace ZutWifi.Tests;

/// Task 18：`--selftest` 通道。这里跑的每一步都是离线的：门户与外网喂真机回放/替身，
/// 数据目录在临时目录里，无线源是假的 —— 不出网、不碰 %APPDATA%、更不碰真机上的注销
/// （那一下会断掉正在用的连接，属于有人盯着看的验收环节）。
public class SelfTestTests
{
    const string Password = "pw-123456-SECRET";

    /// 一次自检所需的全套替身。Emit 就是"控制台"，换成收集列表才看得见每一行写了什么。
    private sealed class Kit(string dir) : IDisposable
    {
        public readonly string Dir = dir;
        public readonly SettingsStore Store = new(dir);
        public readonly FakeClock Clock = new();
        public readonly FakeHttpHandler Portal = new();
        public readonly FakeHttpHandler Probe = new();
        public readonly List<string> Lines = [];
        public IWifiSource? Wifi;
        public HttpMessageHandler? PortalOverride, ProbeOverride;
        public Action<string>? Emit;
        public int BudgetMs = 5000;

        public SelfTest.Harness Harness() => new(Dir, Clock, Wifi,
            PortalOverride ?? Portal, ProbeOverride ?? Probe, Emit ?? Lines.Add, BudgetMs);

        /// 文件名里的时间戳来自 FakeClock 的起点（2026-09-19 06:00:00Z），所以是可预期的。
        public string ReportPath() => Path.Combine(Dir, "logs", "selftest-20260919-060000.log");
        public string Report() => File.ReadAllText(ReportPath());

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch (IOException) { /* 被在途任务占着就算了 */ }
        }
    }

    static Kit New(string studentId = "202500000001", string? password = Password, bool firstRun = true,
        string ssid = "zut-stu", bool connected = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwst" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(dir);
        var s = store.Load();
        s.StudentId = studentId;
        s.FirstRunCompleted = firstRun;
        store.Save(s);                                       // Save 顺手把目录建出来
        new SecretStore(dir).Set(password);                  // null / "" = 删掉密文 = "没配过密码"
        return new Kit(dir)
        {
            Wifi = new FakeWifiSource
            {
                Current = connected ? WifiSentinel.BuildAccessPoint(ssid, "10.133.126.113", "02-A1-B2-C3-D4-E5") : null,
            },
        };
    }

    /// 一条完整通路上的六次门户调用（④⑥⑦⑧⑧）与一次外网探测，全部用真机抓的响应回放。
    static void QueueHappyPath(Kit k)
    {
        k.Portal.EnqueueRaw(Fixtures.Read("offline_9002.txt"));              // ④ 未认证
        k.Portal.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));   // ⑥ 门户侧 IP
        k.Portal.EnqueueRaw(Fixtures.Read("login_success.txt"));             // ⑦ 3.htm = 成功
        k.Portal.EnqueueRaw(Fixtures.Read("online_9002.txt"));               // ⑧ 复检：已认证
        k.Portal.EnqueueRaw(Fixtures.Read("online_9002.txt"));               // ⑧ 在线秒数
        k.Probe.Enqueue(200, Array.Empty<(string, string)>(), "Microsoft NCSI");   // ⑨ 外网通
    }

    static bool SentLogin(FakeHttpHandler h) => h.Requests.Any(r => r.Uri.Query.Contains("a=Login"));
    static bool SentLogout(FakeHttpHandler h) => h.Requests.Any(r => r.Uri.Query.Contains("a=Logout"));

    // ---------- 通过的那一条路 ----------

    [Fact]
    public async Task 自检逐步落盘并给出通过结论()
    {
        using var k = New();
        QueueHappyPath(k);

        Assert.Equal(SelfTest.PassExitCode, await SelfTest.RunAsync(withLogout: false, k.Harness()));

        var text = k.Report();
        foreach (var step in new[]
                 {
                     "① 设置", "② 密码", "③ 无线接口", "④ 认证状态", "⑤ 注销", "⑥ 门户侧",
                     "⑦ 登录", "⑧ 认证状态复检", "⑨ 互联网旁证", "⑩ 诊断落盘", "结论：通过",
                 })
            Assert.Contains(step, text);
        Assert.Contains("zut-stu", text);
        Assert.Contains("02a1b2c3d4e5", text);                     // MAC 直接给出来，注销那一支要用它对
        Assert.Contains("在线秒数=4211", text);
        Assert.Contains("⑦ 登录：Success", text);
        Assert.Contains("⑨ 互联网旁证：通", text);
    }

    [Fact]
    public async Task 每一行都同时进文件与控制台()
    {
        using var k = New();
        QueueHappyPath(k);
        await SelfTest.RunAsync(withLogout: false, k.Harness());

        var file = File.ReadAllLines(k.ReportPath()).Where(l => l.Length > 0).ToList();
        Assert.Equal(file.Count, k.Lines.Count);                   // 少任何一边都算不合格
        Assert.All(k.Lines, l => Assert.Matches(@"^\[\d\d:\d\d:\d\d\.\d\d\d\] ", l));   // 每行都带时间戳
        Assert.Contains(k.Lines, l => l.Contains("⑦ 登录：Success"));
    }

    [Fact]
    public async Task 自检与诊断包连起来看时明文密码不落进任何一个字节()
    {
        using var k = New();
        QueueHappyPath(k);
        await SelfTest.RunAsync(withLogout: false, k.Harness());

        var logs = Path.Combine(k.Dir, "logs");
        var app = File.ReadAllText(Directory.GetFiles(logs, "app-*.log").Single());
        Assert.Contains("upass=***(len=", app);                    // 表单体只以脱敏摘要落盘
        Assert.DoesNotContain(Password, app);
        Assert.DoesNotContain(Password, k.Report());

        var zip = Path.Combine(k.Dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, k.Store, new TransactionLog(k.Clock, logs),
            snapshotProvider: _ => "SSID : zut-stu"));
        using var a = System.IO.Compression.ZipFile.OpenRead(zip);
        foreach (var e in a.Entries)
        {
            using var sr = new StreamReader(e.Open());
            Assert.DoesNotContain(Password, sr.ReadToEnd());
        }
        Assert.DoesNotContain("secret.bin", a.Entries.Select(e => e.FullName));
    }

    // ---------- 不通过：但一定要跑完 ----------

    [Fact]
    public async Task 门户不通时自检跑完并给出可看的原因()
    {
        using var k = New();
        k.Portal.EnqueueThrow(_ => new HttpRequestException("no route to host"));
        k.Portal.EnqueueThrow(_ => new HttpRequestException("no route to host"));
        k.Portal.EnqueueThrow(_ => new HttpRequestException("no route to host"));
        k.Portal.EnqueueThrow(_ => new HttpRequestException("no route to host"));
        k.Portal.EnqueueThrow(_ => new HttpRequestException("no route to host"));
        k.Probe.EnqueueThrow(_ => new HttpRequestException("no route to host"));

        Assert.Equal(SelfTest.ProblemsExitCode, await SelfTest.RunAsync(false, k.Harness()));
        var text = k.Report();
        Assert.Contains("⑦ 登录", text);
        Assert.Contains("门户不可达", text);
        Assert.Contains("⑨ 互联网旁证：不通", text);
        Assert.Contains("结论：发现问题", text);
        Assert.DoesNotContain("自检没跑完", text);                 // 断网 ≠ 自检失败，这七步照样要有
    }

    [Fact]
    public async Task 凭据不全时绝不提交登录包()
    {
        using var k = New(password: null);                         // secret.bin 不存在 = 没配过密码
        Assert.Equal(SelfTest.ProblemsExitCode, await SelfTest.RunAsync(false, k.Harness()));
        Assert.False(SentLogin(k.Portal));                         // 空密码去敲门户 = 白送一次 RADIUS 失败计数
        var text = k.Report();
        Assert.Contains("② 密码", text);
        Assert.Contains("还没有保存过密码", text);
        Assert.Contains("⑦ 登录：跳过", text);
    }

    [Fact]
    public async Task 首次向导没点完成时这是一条看得见的问题()
    {
        using var k = New(firstRun: false);
        QueueHappyPath(k);
        Assert.Equal(SelfTest.ProblemsExitCode, await SelfTest.RunAsync(false, k.Harness()));
        Assert.Contains("首次向导", k.Report());
    }

    [Fact]
    public async Task 不在白名单网络时不向陌生网络提交账号()
    {
        using var k = New(ssid: "cafe-guest");
        k.Portal.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        k.Portal.EnqueueRaw(Fixtures.Read("offline_9002.txt"));
        k.Portal.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        k.Probe.Enqueue(200, Array.Empty<(string, string)>(), "Microsoft NCSI");

        Assert.Equal(SelfTest.ProblemsExitCode, await SelfTest.RunAsync(false, k.Harness()));
        Assert.False(SentLogin(k.Portal));
        var text = k.Report();
        Assert.Contains("白名单命中=False", text);
        Assert.Contains("陌生网络", text);
    }

    [Fact]
    public async Task 已认证且没要求注销时不重复提交登录()
    {
        using var k = New();
        k.Portal.EnqueueRaw(Fixtures.Read("online_9002.txt"));     // ④ 已认证
        k.Portal.EnqueueRaw(Fixtures.Read("online_9002.txt"));     // ⑧ 复检
        k.Portal.EnqueueRaw(Fixtures.Read("online_9002.txt"));     // ⑧ 在线秒数
        k.Probe.Enqueue(200, Array.Empty<(string, string)>(), "Microsoft NCSI");

        Assert.Equal(SelfTest.PassExitCode, await SelfTest.RunAsync(false, k.Harness()));
        Assert.False(SentLogin(k.Portal));                         // 重复提交正是门户账号保护最爱抓的那一类
        Assert.Contains("⑦ 登录：跳过", k.Report());
    }

    [Fact]
    public async Task 带withLogout时注销排在登录之前()
    {
        using var k = New();
        k.Portal.EnqueueRaw(Fixtures.Read("offline_9002.txt"));    // ④
        k.Portal.EnqueueRaw(Fixtures.Read("logout_success.txt"));  // ⑤ 注销成功
        k.Portal.Enqueue(200, Array.Empty<(string, string)>(), Fixtures.Read("a70.htm"));
        k.Portal.EnqueueRaw(Fixtures.Read("login_success.txt"));   // ⑦
        k.Portal.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        k.Portal.EnqueueRaw(Fixtures.Read("online_9002.txt"));
        k.Probe.Enqueue(200, Array.Empty<(string, string)>(), "Microsoft NCSI");

        Assert.Equal(SelfTest.PassExitCode, await SelfTest.RunAsync(withLogout: true, k.Harness()));
        var order = k.Portal.Requests.Select(r => r.Uri.Query.Contains("a=Logout") ? "L"
            : r.Uri.Query.Contains("a=Login") ? "I" : ".").ToList();
        // 这一串里还有 ④ 的探测与 ⑥ 的取 IP（都不带 a= 参数，所以记成 "."）：
        // 要比的是两个包的下标先后。原来那行取 order[0..1] 把 ④ 当成了 ⑤，
        // 于是"先探测、再注销、再登录"这条完全正确的通路被读成失败。
        var atLogout = order.IndexOf("L");
        var atLogin = order.IndexOf("I");
        Assert.True(atLogout >= 0, $"没发出注销包：{string.Concat(order)}");
        Assert.True(atLogin > atLogout, $"注销必须早于登录：{string.Concat(order)}");
        var text = k.Report();
        Assert.Contains("⑤ 注销：Success", text);
        Assert.Contains("⑦ 登录：Success", text);
        Assert.Contains("注销成功，等 3 秒", text);                    // 中间那一停也要在日志里看得见
        // 注销的是"哪一个会话"整个押在这一个参数上（门户的注销包没有凭据，只有 MAC）：
        // 它错了就是踢掉别人的会话，或者一个谁也踢不掉却照样报成功。
        var logout = Assert.Single(k.Portal.Requests, r => r.Uri.Query.Contains("a=Logout"));
        Assert.Contains("mac=02a1b2c3d4e5", logout.Uri.Query);
        Assert.Equal("02a1b2c3d4e5", LogoutMac(logout.Uri.Query));
        Assert.Contains("MAC=[02a1b2c3d4e5]", text);                    // ③ 那一行印的就是被注销的那一个
    }

    /// 从查询串里把 mac 参数的值取出来（整值比对，而不是 Contains 那种"里面碰巧有这几个字"）。
    static string LogoutMac(string query)
    {
        var pair = query.Split('&').FirstOrDefault(p => p.StartsWith("mac=", StringComparison.Ordinal));
        Assert.NotNull(pair);
        return pair!["mac=".Length..];
    }

    /// 决策 7 的自检这一侧：门户事务日志写不进去（磁盘满、目录被占、盘符变了）是最阴的一种故障 ——
    /// 程序还在正常跑、界面上什么都不缺，只有下一次真出问题时才发现没有记录可看。
    /// 这里只锁住 app-*.log 那一个文件：自检日志照写，所以两条通路各自记账才验得出来。
    [Fact]
    public async Task 门户事务日志写不进去时自检与包都明确说出这一点()
    {
        using var k = New();
        QueueHappyPath(k);
        var logs = Path.Combine(k.Dir, "logs");
        Directory.CreateDirectory(logs);
        using var blocker = new FileStream(Path.Combine(logs, "app-20260919.log"),
            FileMode.Create, FileAccess.Write, FileShare.None);

        var code = await SelfTest.RunAsync(withLogout: false, k.Harness());
        Assert.Equal(SelfTest.ProblemsExitCode, code);            // 不是 78：整轮跑完了；也不是 0：诊断能力坏了
        var text = k.Report();
        Assert.Contains("门户调用日志写不进去", text);
        Assert.Contains("事务日志写入失败=", text);                 // ⑩ 那一行把次数直接给出来
        Assert.Contains("结论：发现问题", text);

        // 同一个故障进包：清单里写明写入失败，被锁住的那份日志本身写"读取失败"而不是静默缺席。
        var appLog = new TransactionLog(k.Clock, logs);
        appLog.Write(new TransactionRecord("Probe", "GET", "http://1.1.1.1:9002/0", null, null, "x", null));
        Assert.True(appLog.WriteFailures >= 1);
        var zip = Path.Combine(k.Dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, k.Store, appLog, snapshotProvider: _ => "SSID : zut-stu"));
        string appCopy, manifest;
        using (var a = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            appCopy = Copy(a, "app-20260919.log");
            manifest = Copy(a, "manifest.txt");
        }
        Assert.Contains("日志写入失败：1", manifest);
        Assert.Contains("读取失败", appCopy);
    }

    static string Copy(System.IO.Compression.ZipArchive a, string suffix)
    {
        var entry = a.Entries.First(e => e.FullName.EndsWith(suffix, StringComparison.Ordinal));
        using var sr = new StreamReader(entry.Open());
        return sr.ReadToEnd();
    }

    // ---------- 端到端：Program.Main 里 --selftest 那一句走的公共入口 ----------
    //
    // 上面每一条都是"自造一个 Harness + 把 Emit 换成收集列表"，也就是从内层往里驱动。
    // 那样有三件事一直没被碰过：
    // ① 生产那一份 harness（`Harness.Production()`）到底接了些什么，没人读过一眼；
    // ② `Session.ConsoleLine` → `Console.Out` 这条**真**上屏通路从没被执行过（Emit 一律被换掉了）；
    // ③ 退出码是从哪几个数推出来的 —— 原来只由"某一行里碰巧出现了那个数字"弱弱兜着，
    //    在通路末端插一句写死的 `return 0` 依旧一片绿。
    // 下面四条按"公共入口 → 真控制台 → 落盘日志 → 退出码"整条跑一遍：只换掉离线必须换的三样。

    /// 生产 harness 的离线版：**只有**数据目录、两个 HTTP 处理器、无线源被换掉，
    /// `Emit` 保持 null ⇒ 这一轮上屏走的就是真机那一条 `Console.Out`。
    static SelfTest.Harness OfflineHarness(Kit k) => SelfTest.Harness.Production() with
    {
        Dir = k.Dir,
        Clock = k.Clock,
        Wifi = k.Wifi,
        PortalHandler = k.PortalOverride ?? k.Portal,
        ProbeHandler = k.ProbeOverride ?? k.Probe,
    };

    /// 在真控制台上跑一整轮：拿回退出码、控制台全文、以及磁盘上那份自检日志。
    /// Console.Out 是进程级的（别的并行用例也可能写它），所以两侧都只按"这一轮自己的行"比，
    /// 绝不下"屏幕上出现的就是这些"那种结论。
    static async Task<(int Code, string Screen, string Log)> RunOnConsole(Kit k, bool withLogout)
    {
        var real = Console.Out;
        var cap = new StringWriter();
        int code;
        Console.SetOut(cap);
        try { code = await SelfTest.RunAsync(withLogout, OfflineHarness(k)); }
        finally { Console.SetOut(real); }
        return (code, cap.ToString(), k.Report());
    }

    static List<string> LinesOf(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

    /// 同一轮的两份出口必须一字不差地等量：文件里的每一行在控制台上出现同样多次，
    /// 控制台上每一行带时间戳的也在文件里出现同样多次（少一行、多一行、某行只落一边都红）。
    /// `atLeast` 是"这一轮至少该有几行"的地板：跑完的一轮二十几行，③ 就炸的那一轮只有九行，
    /// 没有这块地板，一条空日志也能让上面那两个循环一次都不进（那是最坏的假绿）。
    static void AssertBothSides(string log, string screen, int atLeast = 10)
    {
        var file = LinesOf(log);
        var console = LinesOf(screen);
        Assert.True(file.Count >= atLeast, $"这一轮日志只有 {file.Count} 行，这条用例什么都没验：{log}");
        foreach (var line in file.Distinct())
        {
            var (inFile, onScreen) = (file.Count(l => l == line), console.Count(l => l == line));
            Assert.True(inFile == onScreen, $"这一行只落在了一边（文件 {inFile} 次 / 控制台 {onScreen} 次）：{line}");
        }
        foreach (var line in console.Where(l => l.StartsWith('[')).Distinct())
        {
            var (inFile, onScreen) = (file.Count(l => l == line), console.Count(l => l == line));
            Assert.True(inFile == onScreen, $"屏幕上这一行没进日志（文件 {inFile} 次 / 控制台 {onScreen} 次）：{line}");
        }
    }

    /// 印出来的那一句"退出码 N（…）"里的 N 就是交给 Program.Main 的那一个：
    /// 退出码与"人读到的那个数"从此同源，把推导换成写死的数就同时红两处。
    static void AssertPrintedExitCode(string text, int code)
    {
        var printed = Regex.Matches(text, @"退出码 (\d+)（").Cast<Match>()
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(printed);
        Assert.Equal(code, Assert.Single(printed.Distinct()));
    }

    [Fact]
    public void 生产那一份harness接的就是真目录真时钟真控制台与两条命名接缝()
    {
        var p = SelfTest.Harness.Production();
        Assert.Equal(AppContext.DataDir, p.Dir);            // 真机上写 %APPDATA%\ZutWifi\logs
        Assert.IsType<SystemClock>(p.Clock);                // 不是 FakeClock：时间戳来自系统
        Assert.Null(p.Wifi);                                // 真 WifiSentinel（③ 与 wlanapi 明细）
        Assert.Null(p.PortalHandler);                       // 真客户端，而且只能从两条命名接缝拿
        Assert.Null(p.ProbeHandler);
        Assert.Null(p.Emit);                                // 真控制台：Session.ConsoleLine → Console.Out
        Assert.Equal(SelfTest.DefaultCallBudgetMs, p.CallBudgetMs);
        Assert.Equal(SelfTest.DefaultOverallBudgetMs, p.OverallBudgetMs);
    }

    [Fact]
    public async Task 端到端公共入口全通过时退出码0并且日志与控制台一行不差()
    {
        using var k = New();
        QueueHappyPath(k);
        Assert.Null(OfflineHarness(k).Emit);                // 这一轮真的用控制台，而不是收集列表

        var (code, screen, log) = await RunOnConsole(k, withLogout: false);

        Assert.Equal(SelfTest.PassExitCode, code);
        AssertBothSides(log, screen);
        AssertPrintedExitCode(log, code);
        AssertPrintedExitCode(screen, code);
        foreach (var step in new[]
                 {
                     "① 设置", "② 密码", "③ 无线接口", "④ 认证状态", "⑤ 注销", "⑥ 门户侧",
                     "⑦ 登录", "⑧ 认证状态复检", "⑨ 互联网旁证", "⑩ 诊断落盘", "结论：通过",
                 })
        {
            Assert.Contains(step, log);                    // 每一步都在磁盘上
            Assert.Contains(step, screen);                 // 也在真控制台上
        }
        Assert.DoesNotContain("⚠", log);
        Assert.Contains("退出码 0（0=全通过", screen);
    }

    [Fact]
    public async Task 端到端公共入口发现一处问题时退出码1而印出的计数就是它依据的那几个()
    {
        using var k = New(firstRun: false);                 // 就一处问题：首次向导没点"完成"
        QueueHappyPath(k);

        var (code, screen, log) = await RunOnConsole(k, withLogout: false);

        Assert.Equal(SelfTest.ProblemsExitCode, code);
        AssertBothSides(log, screen);
        AssertPrintedExitCode(log, code);
        AssertPrintedExitCode(screen, code);

        // 这一轮退出码唯一的非零依据就是问题数：⚠ 的行数 == 结论里印的那个数 == 1
        Assert.Equal(1, LinesOf(log).Count(l => l.Contains("⚠")));
        Assert.Contains("结论：发现问题 1 处", log);
        Assert.Contains("结论：发现问题 1 处", screen);
        // ⑩ 那一行把另外三个计数当面印出来，它们全是干净的 —— 结论与计数出自同一次收尾
        Assert.Contains("⑩ 诊断落盘：自检日志=每行都写成了 事务日志写入失败=0 次 上屏失败=0 次", log);
        Assert.Contains("⑩ 诊断落盘：自检日志=每行都写成了 事务日志写入失败=0 次 上屏失败=0 次", screen);
        // "有问题"不等于"没跑完"：通过的那几步照样在
        Assert.Contains("⑦ 登录：Success", screen);
        Assert.Contains("⑨ 互联网旁证：通", screen);
    }

    [Fact]
    public async Task 端到端公共入口自检自身抛时退出码78而三个计数全是干净的()
    {
        using var k = New();
        QueueHappyPath(k);
        k.Wifi = new ThrowingWifi();                        // ③ 那一步就炸：整轮没跑完

        var (code, screen, log) = await RunOnConsole(k, withLogout: false);

        Assert.Equal(SelfTest.IncompleteExitCode, code);
        // 这一轮在 ③ 就炸了，只剩表头 + ①② + 异常行 + ⑩ + 结论 + 退出码 + 路径那几行
        AssertBothSides(log, screen, atLeast: 8);
        AssertPrintedExitCode(log, code);
        AssertPrintedExitCode(screen, code);
        Assert.Contains("自检自身异常（这一轮没跑完）：InvalidOperationException", log);
        Assert.Contains("结论：自检没跑完", screen);
        Assert.DoesNotContain("④ 认证状态", log);                          // 门户那几步确实没跑到
        // 78 不是从计数推出来的：那一行三个数全干净，唯一的依据是"没跑完"那面旗，
        // 所以把 Incomplete 这一支漏掉（只判 FileFailures 与问题数）会在这里红。
        Assert.Contains("⑩ 诊断落盘：自检日志=每行都写成了 事务日志写入失败=0 次 上屏失败=0 次", log);
        Assert.DoesNotContain("结论：发现问题", log);
        Assert.DoesNotContain("结论：通过", log);
    }

    /// ③ 那一步就抛的无线源：真机上"句柄没开上却照样往下读属性"就是这一类。
    private sealed class ThrowingWifi : IWifiSource
    {
        public AccessPoint? Current => throw new InvalidOperationException("wlanapi 句柄开不上");
        public event Action<AccessPoint?> Changed { add { } remove { } }
    }

    /// 公共入口那一句必须**只是转发**：交给 Program.Main 的退出码只能由 Session 算出来。
    /// 为什么这一寸只能按源码钉（和约束④ 那条同一套办法）：真跑一次 `SelfTest.RunAsync(false)`
    /// 就是往 %APPDATA%\ZutWifi 写、往门户与外网发包，派单的硬规则里不许；
    /// 于是能运行时验的全在上面几条里验了，剩下的"`=>` 右边写的是什么"用源码钉死：
    /// 把这一句换成 `=> Task.FromResult(0)`，或者在末端 `return` 一个写死的数，当场红。
    [Fact]
    public void 公共入口只是转发退出码只能由这一轮收尾算出来()
    {
        var dir = Path.Combine(RepoRoot(), "src", "ZutWifi", "Diagnostics");
        var self = CodeLines(File.ReadAllText(Path.Combine(dir, "SelfTest.cs")));
        var entry = Assert.Single(self, l => l.StartsWith("public static Task<int> RunAsync(bool withLogout)"));
        Assert.Equal("public static Task<int> RunAsync(bool withLogout) => RunAsync(withLogout, Harness.Production());",
            entry);

        var all = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs")) all.AddRange(CodeLines(File.ReadAllText(file)));
        Assert.DoesNotContain(all, l => Regex.IsMatch(l, @"return\s+(0|1|78)\b"));
        Assert.DoesNotContain(all, l => l.Contains("Task.FromResult"));
        Assert.DoesNotContain(all, l => l.Contains("return PassExitCode") || l.Contains("return ProblemsExitCode"));
        // 算出来的码只从这一处出去；写"测不成"那一档的两处兜底之外没有第三条出口。
        Assert.Equal(1, Count(all, "return s.Finish();"));
        Assert.Equal(1, Count(all, "try { return s.Finish(); } catch (Exception) { return IncompleteExitCode; }"));
        Assert.Equal(1, all.Count(l => l.Contains("var code = Incomplete || FileFailures > 0")));
    }

    // ---------- 诊断自己坏掉的样子 ----------

    [Fact]
    public async Task 自检日志落不下去时退出码是78并当场说出来()
    {
        using var k = New();
        QueueHappyPath(k);
        File.WriteAllText(Path.Combine(k.Dir, "logs"), "占位：让日志目录建不出来");

        var code = await SelfTest.RunAsync(false, k.Harness());
        Assert.Equal(SelfTest.IncompleteExitCode, code);            // 既不是"通过"也不是"测出问题"
        Assert.False(File.Exists(k.ReportPath()));
        Assert.Contains("写不进去", string.Join('\n', k.Lines));     // 只剩控制台这一条路，必须走通
    }

    [Fact]
    public async Task 没有控制台时自检照样跑完并落盘()
    {
        using var k = New();
        QueueHappyPath(k);
        k.Emit = _ => throw new IOException("资源管理器双击进来的，没有控制台");

        Assert.Equal(SelfTest.PassExitCode, await SelfTest.RunAsync(false, k.Harness()));
        Assert.Contains("⑨ 互联网旁证：通", k.Report());             // 文件一份不少
        Assert.True(File.ReadAllLines(k.ReportPath()).Length > 10);   // 每一行都抛过，但没人崩
        Assert.Empty(k.Lines);                                        // 屏幕上确实一个字都没有
    }

    [Fact]
    public async Task 门户调用不回答时自检按时收场而不是挂死()
    {
        using var k = New();
        k.PortalOverride = new HangHandler();
        k.ProbeOverride = new HangHandler();
        k.BudgetMs = 200;
        var started = Stopwatch.StartNew();
        var code = await SelfTest.RunAsync(false, k.Harness());
        started.Stop();

        Assert.Equal(SelfTest.ProblemsExitCode, code);
        Assert.True(started.ElapsedMilliseconds < 8000, $"自检没被超时上限管住：{started.ElapsedMilliseconds}ms");
        var text = k.Report();
        Assert.Contains("超过", text);                              // 超时是被记录的一行，不是一次等待
        Assert.Contains("⑨ 互联网旁证：不通", text);
        Assert.Contains("结论：发现问题", text);
    }

    [Fact]
    public async Task 超时会放弃一个不理会取消信号的任务()
    {
        var late = new List<string>();
        var started = Stopwatch.StartNew();
        // 不接 ct 的任务：光靠取消信号是等不回来的，必须真的"不等它"。
        var value = await SelfTest.WithBudgetAsync("测试", _ => Task.Delay(4000).ContinueWith(_ => 42),
            budgetMs: 100, graceMs: 50, whenLate: 0, overall: default, onLate: m => late.Add(m));
        started.Stop();

        Assert.Equal(0, value);
        Assert.True(started.ElapsedMilliseconds < 2000, $"{started.ElapsedMilliseconds}ms");
        Assert.Contains("超过", Assert.Single(late));
    }

    [Fact]
    public void 自检用的探测客户端与组合根同一条工厂不跟跳转()
    {
        Assert.Equal(5, AppContext.ProbeTimeoutSeconds);            // 探针重试 3 轮 × 2 目标，这里必须短
        var (probe, ownedProbe) = SelfTest.NewProbeClient(substitute: null);
        using (probe) using (ownedProbe)
        {
            Assert.NotNull(ownedProbe);
            Assert.False(ownedProbe!.AllowAutoRedirect);           // 跟下去就把门户劫持页读成"已上网"
            Assert.Equal(TimeSpan.FromSeconds(5), probe.Timeout);
        }
        var (portal, ownedPortal) = SelfTest.NewPortalClient(substitute: null);
        using (portal) using (ownedPortal)
        {
            Assert.False(ownedPortal!.AllowAutoRedirect);
            Assert.Equal(TimeSpan.FromSeconds(15), portal.Timeout);
        }
    }

    /// 约束④ 原来钉在"另一个人另外造一个客户端"上（NewClient 的返回值），而不是钉在这一轮
    /// 真的用了哪一个对象上 —— 把 ⑨ 那一句换成 `new HttpClient()` 依旧一片绿，而假绿正是它给的。
    /// 现在⑨ 那一行印的是**当下这一个**客户端的超时，另有一道现场闸门（ProbeClientHealth）判它：
    /// 换成默认那个（100 秒、会跟跳转）之后，这一行与退出码两处一起红。
    [Fact]
    public async Task 探测客户端就是这一轮用的那一个它的超时印在日志里()
    {
        using var k = New();
        QueueHappyPath(k);
        Assert.Equal(SelfTest.PassExitCode, await SelfTest.RunAsync(withLogout: false, k.Harness()));
        var line = Assert.Single(k.Lines, l => l.Contains("⑨ 互联网旁证"));
        Assert.Contains("单次超时=5s", line);                       // 印的是那个 HttpClient 自己的 Timeout
        Assert.Contains("禁自动跳转=", line);
        Assert.DoesNotContain("探测客户端", string.Join('\n', k.Lines.Where(l => l.Contains("⚠"))));
    }

    /// 上一条钉的是"工厂给的东西是对的"，这一条钉的是"生产路径只能从这个工厂拿"：
    /// 整个 Diagnostics 目录里除了两条命名接缝（SelfTestClients.cs）之外不许出现 `new HttpClient`
    /// （含 `new HttpClientHandler`），而 `AppContext.NewNoRedirect` 在那里恰好被引用两次。
    /// 注释里当然还可以出现这个词，所以逐行跳过注释行。
    /// 为什么用源码来钉：注入替身时那个真 handler 根本不在场（AppContext.NewNoRedirect 对替身返回 null），
    /// 离线跑一遍看不见"生产用的那一个跟不跟跳转"，这一半只能靠构造点唯一来保证 —— 报告里明说了这一点。
    [Fact]
    public void 诊断这一段只有一个HttpClient构造点()
    {
        var dir = Path.Combine(RepoRoot(), "src", "ZutWifi", "Diagnostics");
        Assert.True(Directory.Exists(dir), $"找不到诊断源码目录：{dir}");
        var files = Directory.GetFiles(dir, "*.cs");
        Assert.True(files.Length >= 5, $"诊断源码只有 {files.Length} 个文件？这条守卫什么都没扫到");
        var seamUses = 0;
        foreach (var file in files)
        {
            var code = CodeLines(File.ReadAllText(file));
            // 断言消息里不能出现那个词本身，否则守卫会把自己的注释读成违规（注释行已跳过）。
            Assert.DoesNotContain(code, l => l.Contains("new HttpClient"));
            seamUses += Count(code, "AppContext.NewNoRedirect");
        }
        Assert.Equal(2, seamUses);                       // 门户一条 + 探测一条，别处再出现就是第三个构造点
        var clients = CodeLines(File.ReadAllText(Path.Combine(dir, "SelfTestClients.cs")));
        Assert.Equal(1, Count(clients, "ProbeTimeoutSeconds, substitute"));
        Assert.Equal(1, Count(clients, "PortalTimeoutSeconds, substitute"));
        var steps = CodeLines(File.ReadAllText(Path.Combine(dir, "SelfTest.cs")));
        Assert.Equal(1, Count(steps, "NewPortalClient(h.PortalHandler)"));      // 生产路径确实从接缝拿
        Assert.Equal(1, Count(steps, "NewProbeClient(h.ProbeHandler)"));
    }

    /// 只留代码行：文档注释里出现 `new HttpClient()` 是在说明"别这么写"，不该被这条守卫读成违规。
    static List<string> CodeLines(string source) =>
        source.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("//", StringComparison.Ordinal)).ToList();

    static int Count(IEnumerable<string> lines, string needle) =>
        lines.Count(l => l.Contains(needle, StringComparison.Ordinal));

    /// 从测试输出目录往上找仓库根（源码级断言需要它）。找不到就明说而不是静默通过 ——
    /// 一条会静默通过的守卫用例比没有守卫更糟：它让人以为这件事已经被钉住了。
    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(System.AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ZutWifi.sln"))) return d.FullName;
        throw new FileNotFoundException("找不到仓库根（ZutWifi.sln）：这条源码级守卫无法工作");
    }

    [Fact]
    public void 三档退出码互不相同且没有一个等于通过()
    {
        Assert.Equal(0, SelfTest.PassExitCode);
        Assert.NotEqual(SelfTest.PassExitCode, SelfTest.ProblemsExitCode);
        Assert.NotEqual(SelfTest.PassExitCode, SelfTest.IncompleteExitCode);
        // 78 这一档是占位版本留下的语义："没测成"不许被发布脚本读成"测过了"。
        Assert.Equal(78, SelfTest.IncompleteExitCode);
    }

    /// 永远不回答的请求处理器：真机上"连接被防火墙吊住"就是这个样子。
    private sealed class HangHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
