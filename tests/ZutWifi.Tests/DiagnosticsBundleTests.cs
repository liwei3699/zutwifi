using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Diagnostics;
using ZutWifi.Shell;
using ZutWifi.Tests.Support;

namespace ZutWifi.Tests;

/// Task 18：诊断包。全部用例都写在临时目录里 —— 不碰 %APPDATA%、不出网、不 spawn 真命令
/// （快照一律走注入的委托）。密码这件事只钉一条：明文不许出现在包里的任何一个字节，
/// 因为这个 zip 是要被同学用 QQ/微信发出去的。
public class DiagnosticsBundleTests
{
    static string Tmp() => Path.Combine(Path.GetTempPath(), "zwbd" + Guid.NewGuid().ToString("N"));

    static string Dir(string? secret = "SuperSecret")
    {
        var d = Tmp();
        Directory.CreateDirectory(d);
        var store = new SettingsStore(d);
        var s = store.Load();
        s.StudentId = "202500000001";
        s.FirstRunCompleted = true;
        store.Save(s);
        if (secret is not null) new SecretStore(d).Set(secret);
        return d;
    }

    static string Read(ZipArchive zip, string suffix)
    {
        var entry = zip.Entries.First(e => e.FullName.EndsWith(suffix, StringComparison.Ordinal));
        using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
        return sr.ReadToEnd();
    }

    static string AllText(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return string.Join('\n', zip.Entries.Select(e => Read(zip, e.FullName)));
    }

    /// 目录里现存的每一项（**含子目录**，只取名字）。
    static SortedSet<string> Present(string dir) =>
        new(Directory.GetFileSystemEntries(dir).Select(p => Path.GetFileName(p)!), StringComparer.Ordinal);

    /// 导出前后这两份清单的差集（新增的 + 消失的）必须正好等于 expected：
    /// 多出一个 .part / .tmp / 一个改名改了一半的目录，或者顺手弄丢了别人的文件，都在这里红。
    /// 为什么按整个目录比而不是 `GetFiles("*.part")`：后者只盯得住"当前这一版恰好用 .part 收尾"那一种写法，
    /// 换成 .tmp、.zip.new、或者"先建目录再改名"就看不见残留了 —— 而同学发回来的包里最怕的就是这种半成品。
    static void AssertDiffIs(string dir, SortedSet<string> before, params string[] expected)
    {
        var now = Present(dir);
        var diff = now.Except(before).Concat(before.Except(now))
            .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal).ToList(), diff);
    }

    // ---------- 包里的内容 ----------

    [Fact]
    public void 诊断包含日志设置与网络快照()
    {
        var dir = Dir();
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "app-20260919.log"), "Login status=302 result=成功");
        File.WriteAllText(Path.Combine(logs, "selftest-20260919-060000.log"), "[06:00:00.000] ① 设置");

        var zip = Path.Combine(dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, new SettingsStore(dir),
            new TransactionLog(new FakeClock(), logs), snapshotProvider: _ => "SSID : zut-stu"));

        // 这个 using 必须是块而不是"变量式"：包被 Windows 当作打开中的文件时，末尾那句
        // Directory.Delete 会抛 IOException（zip 的句柄要到方法结束才还，而删目录在这之前）。
        using (var a = ZipFile.OpenRead(zip))
        {
            var names = a.Entries.Select(e => e.FullName).ToList();
            Assert.Contains(names, n => n.EndsWith("app-20260919.log"));
            Assert.Contains(names, n => n.EndsWith("selftest-20260919-060000.log"));
            Assert.Contains(names, n => n.EndsWith("settings.json"));
            Assert.Contains(names, n => n.EndsWith("wlan.txt"));
            Assert.Contains(names, n => n.EndsWith("version.txt"));
            Assert.Contains(names, n => n.EndsWith("manifest.txt"));
            // 密文文件按名字排除：它是这个包里最不该出现的东西。
            Assert.All(names, n => Assert.False(n.EndsWith("secret.bin"), n));
            Assert.DoesNotContain("secret.bin", names);
            Assert.Contains("202500000001", Read(a, "settings.json"));
        }
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 明文密码在包里的任何一个字节都不许出现()
    {
        var dir = Dir("Sup3r-S3cret-Pw!");
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        // 连"日志里都写过一次带密码的表单体"这种最坏情况一起验：日志侧有 RedactForm，包侧不许再漏。
        File.WriteAllText(Path.Combine(logs, "app-20260919.log"),
            "Login POST form=DDDDD=,0,202500000001@cmcc&upass=***(len=15)");
        var zip = Path.Combine(dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, new SettingsStore(dir),
            new TransactionLog(new FakeClock(), logs), snapshotProvider: _ => "SSID : zut-stu"));
        Assert.DoesNotContain("Sup3r-S3cret-Pw!", AllText(zip));
        Assert.DoesNotContain("Sup3r-S3cret-Pw!", File.ReadAllText(Path.Combine(dir, "settings.json")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 以后加进设置的密码字段一律只以星号出现()
    {
        var dir = Dir(secret: null);
        // 同学会手改 settings.json，日后也可能加进带密码的字段：脱敏按"字段名像密码"判，不按今天的字段表判。
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """
            {
              "StudentId": "202500000001",
              "PortalPassword": "hunter2",
              "password": "plain-old",
              "ApiToken": "tk-77",
              "SsidWhitelist": ["zut-stu"]
            }
            """);
        var zip = Path.Combine(dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, new SettingsStore(dir), null,
            snapshotProvider: _ => "SSID : zut-stu"));
        using (var a = ZipFile.OpenRead(zip))
        {
            var settings = Read(a, "settings.json");
            Assert.DoesNotContain("hunter2", settings);
            Assert.DoesNotContain("plain-old", settings);
            Assert.DoesNotContain("tk-77", settings);
            Assert.Contains("***", settings);
            Assert.Contains("202500000001", settings);          // 非敏感字段原样留着，不然包就没用了
            Assert.Contains("zut-stu", settings);
        }
        // 未知字段不影响程序读设置（脱敏只动包里那份，磁盘上那份是别人的真配置）
        Assert.Equal("202500000001", new SettingsStore(dir).Load().StudentId);
        Directory.Delete(dir, true);
    }

    // ---------- 失败路径 ----------

    [Fact]
    public void 缺目录时返回错误文字而不是抛出()
    {
        var zip = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".zip");
        var err = DiagnosticsBundle.Build(zip, new SettingsStore(@"D:\不可能存在的路径"), null,
            snapshotProvider: _ => throw new InvalidOperationException("boom"));
        Assert.NotNull(err);
        Assert.Contains("不可能存在的路径", err);
        Assert.False(File.Exists(zip));
    }

    [Fact]
    public void 快照采集失败时把原因写进包里而不是让导出中断()
    {
        var dir = Dir();
        var zip = Path.Combine(dir, "bundle.zip");
        var err = DiagnosticsBundle.Build(zip, new SettingsStore(dir), null,
            _ => throw new InvalidOperationException("wlanapi 句柄开不上"));
        Assert.Null(err);                                                  // 导出照样完成
        using (var a = ZipFile.OpenRead(zip))
        {
            Assert.Contains("采集失败：wlanapi 句柄开不上", Read(a, "wlan.txt"));   // 但原因留在包里
            Assert.Contains("采集失败", Read(a, "routes.txt"));
        }
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 日志写不下去时包里明确写着这件事()
    {
        var dir = Dir();
        // 用一个同名文件把 logs 目录占掉：这是"日志目录只读/被占用"那一类故障的最省事模型。
        File.WriteAllText(Path.Combine(dir, "logs"), "占位：让日志目录建不出来");
        var log = new TransactionLog(new FakeClock(), Path.Combine(dir, "logs"));
        log.Write(new TransactionRecord("Probe", "GET", "http://1.1.1.1:9002/0", null, null, "x", null));
        Assert.True(log.WriteFailures >= 1);

        var zip = Path.Combine(dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, new SettingsStore(dir), log,
            snapshotProvider: _ => "SSID : zut-stu"));
        using (var a = ZipFile.OpenRead(zip))
        {
            var manifest = Read(a, "manifest.txt");
            Assert.Contains("日志写入失败：1", manifest);
            Assert.Contains("日志份数：app=0", manifest);                    // 日志目录没建成，包里没有 logs/ 是事实
        }
        Directory.Delete(dir, true);
    }

    [Fact]
    public void 打包失败时既不留下半截zip也不留下临时文件()
    {
        var dir = Dir();
        var zip = Path.Combine(dir, "bundle.zip");
        Directory.CreateDirectory(zip);                                   // 最后一步改名必然失败
        var before = Present(dir);                                        // 连这个挡路的目录一起算进基线
        var err = DiagnosticsBundle.Build(zip, new SettingsStore(dir), null,
            snapshotProvider: _ => "SSID : zut-stu");
        Assert.NotNull(err);
        Assert.Empty(Directory.GetFiles(dir, "*.part"));                  // 素材早就备齐了，只是落不了地
        AssertDiffIs(dir, before);                                        // 整个目录一比：什么都没多出来
        Assert.False(File.Exists(zip + ".part"));                         // 点名那一个（不靠通配符）
        Assert.False(File.Exists(zip + ".tmp"));
        Assert.True(Directory.Exists(zip));
        Directory.Delete(zip, true);
        Directory.Delete(dir, true);
    }

    /// 另一种失败位置：素材还没落盘就先失败（目标目录建不出来）。上面那一条是".part 已经写了、改名没改成"，
    /// 这一条是"根本轮不到写"。两种收尾都必须一个兄弟都不留，不然同学发回来的目录里会躺着几份没人认领的包。
    [Fact]
    public void 目标目录本身就建不出来时同样什么都不留下()
    {
        var dir = Dir();
        File.WriteAllText(Path.Combine(dir, "logs"), "占位：logs 是个文件，zip 就写不进去");
        var before = Present(dir);
        var zip = Path.Combine(dir, "logs", "bundle.zip");                // 父目录是个文件 → CreateDirectory 抛
        var err = DiagnosticsBundle.Build(zip, new SettingsStore(dir), null, snapshotProvider: l => l);
        Assert.NotNull(err);
        AssertDiffIs(dir, before);
        Assert.False(File.Exists(zip + ".part"));
        Assert.False(File.Exists(zip + ".tmp"));
        DeleteDir(dir);
    }

    // ---------- 成功导出之后同样不许有半成品 ----------

    [Fact]
    public void 成功导出之后目录里只多出那一个包不留任何兄弟()
    {
        var dir = Dir();
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "app-20260919.log"), "一行日志");
        var zip = Path.Combine(dir, "bundle.zip");
        var before = Present(dir);
        string? Export() => DiagnosticsBundle.Build(zip, new SettingsStore(dir),
            new TransactionLog(new FakeClock(), logs), snapshotProvider: l => $"【{l}】那一页");

        Assert.Null(Export());
        Assert.True(File.Exists(zip));
        AssertDiffIs(dir, before, "bundle.zip");                          // 只多这一个，别的都是残留

        Assert.Null(Export());                                            // 目标已存在 → 走"换掉旧的"那一条
        AssertDiffIs(dir, before, "bundle.zip");                          // 这一步最容易把 .part 落在原地

        var target = DiagnosticsBundle.NewTargetPath(dir);                // 界面那个真落点（带时间戳）
        Assert.Null(DiagnosticsBundle.Build(target, new SettingsStore(dir), null, snapshotProvider: l => l));
        AssertDiffIs(dir, before, "bundle.zip", Path.GetFileName(target));
        Assert.True(File.Exists(target));
        // 日志目录只被读、不被写：打包不许在那里留任何东西
        Assert.Equal(new[] { "app-20260919.log" },
            Directory.GetFiles(logs).Select(Path.GetFileName).ToList());
        DeleteDir(dir);
    }

    // ---------- 导出目标 ----------

    [Fact]
    public void 导出目标在数据目录里并且只保留最近几份()
    {
        var dir = Dir();
        var first = DiagnosticsBundle.NewTargetPath(dir);
        Assert.StartsWith(dir, first);
        Assert.EndsWith(".zip", first);
        Assert.Contains("zutwifi-diagnostics-", first);
        File.WriteAllText(first, "旧的一份");
        for (var i = 0; i < 8; i++)
            File.WriteAllText(Path.Combine(dir, $"zutwifi-diagnostics-2020{i:D2}0101-00000{i}.zip"), "x");

        var second = DiagnosticsBundle.NewTargetPath(dir);
        Assert.NotEqual(first, second);
        var left = Directory.GetFiles(dir, "zutwifi-diagnostics-*.zip");
        Assert.True(left.Length <= 5, $"旧包没被清理，剩下 {left.Length} 份");
        Directory.Delete(dir, true);
    }

    // ---------- 快照命令的上限 ----------

    [Fact]
    public void 快照命令跑不完就被掐掉并说清原因()
    {
        // 回环 ping：不碰校园网、不碰外网，只用来制造一个"就是不在期限内退出"的子进程。
        var started = Stopwatch.StartNew();
        var (ok, _, reason) = CommandCapture.TryCapture("ping.exe", "127.0.0.1 -n 30", 700);
        started.Stop();
        Assert.False(ok);
        Assert.Contains("超时", reason);
        Assert.True(started.ElapsedMilliseconds < 4000, $"没有按时掐掉子进程：{started.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void 命令起不动时也是一句人能看的话而不是抛出()
    {
        var text = CommandCapture.Capture("zw-这个命令不存在.exe", "/c");
        Assert.StartsWith("采集失败：", text);
    }

    // ---------- 真实快照通路：同学发回来的那份 zip 就是这两行产的 ----------

    /// snapshotProvider **不传** ⇒ DefaultSnapshot ⇒ 真去跑 netsh / ipconfig / route。
    /// 这一条是注入委托永远替不了的：套件里原来每一条 Build 都传了 snapshotProvider，
    /// 于是把 DefaultSnapshot 换成 `return ""`、或把三条命令里的任意一条改掉，全都一片绿，
    /// 而同学那份包恰恰就是这两行产出来的。
    /// 离线安全：三条都是本机只读命令（不敲门户、不出网），唯一的依赖是子进程 + 5 秒上限。
    [Fact]
    public void 不注入快照委托时包里是三条真命令的输出而不是空页()
    {
        var dir = Dir(secret: null);
        var zip = Path.Combine(dir, "bundle.zip");
        var started = Stopwatch.StartNew();
        var err = DiagnosticsBundle.Build(zip, new SettingsStore(dir), null);      // ← 没有任何接缝
        started.Stop();

        Assert.Null(err);                                                          // 生产那条路不许抛
        Assert.True(started.ElapsedMilliseconds < 40_000,
            $"三条命令各限 5 秒，整份包却不该超过 40 秒：{started.ElapsedMilliseconds}ms");
        using (var a = ZipFile.OpenRead(zip))
        {
            var wlan = Read(a, "wlan.txt");
            var ipconfig = Read(a, "ipconfig.txt");
            var routes = Read(a, "routes.txt");
            var texts = new[] { ("wlan.txt", wlan), ("ipconfig.txt", ipconfig), ("routes.txt", routes) };
            foreach (var (name, text) in texts)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{name} 是空的：真命令的一项都不许静默缺席");
                Assert.True(text.Length < 2_000_000, $"{name} 没有上限：{text.Length} 字节");
                // 注入委托那份假值出现在这里，就说明"生产路径其实走的还是注入那条"
                Assert.DoesNotContain("SSID : zut-stu", text);
                if (text.StartsWith("采集失败："))   // 拿不到只能是一句带原因的话（没 WLAN、命令被禁…）
                    Assert.True(text.Length > "采集失败：".Length + 2, $"{name} 说采集失败却没说为什么：{text}");
            }
            // 三项一起"采集失败"就说明这条用例已经不在验真命令了（这台机器上请手工验一遍再改断言）。
            Assert.True(texts.Count(t => !t.Item2.StartsWith("采集失败：")) >= 1,
                "三条快照全成了采集失败：这条用例什么都没验");

            // ipconfig 少了 /all 就没有物理地址，而 MAC 正是这个包里最有价值的一行
            // （注销的是哪个会话、同学报障核对的都是它）。
            if (!ipconfig.StartsWith("采集失败："))
                Assert.Matches("[0-9A-Fa-f]{2}(-[0-9A-Fa-f]{2}){5}", ipconfig);
            // route print 少了 -4 会把 IPv6 那张表一起塞进包（真机上那一段几十行，全是没用的）
            if (!routes.StartsWith("采集失败："))
                Assert.DoesNotMatch("(?i)ipv6", routes);
        }
        DeleteDir(dir);
    }

    /// 快照项 → 命令行 的那张表是这个包的全部来源，逐字钉住才改不动：
    /// 上面那条真命令用例只能证明"跑出来的东西非空"，证明不了"跑的是这条命令"
    /// （netsh 换成另一条 netsh、route 换成 route print 都还是像样的输出）。
    [Fact]
    public void 快照项到命令行与包里的文件名一一对应()
    {
        Assert.Equal(["wlan", "ipconfig", "routes"], DiagnosticsBundle.SnapshotLabels);
        Assert.Equal(("netsh", "wlan show interfaces"), DiagnosticsBundle.SnapshotCommand("wlan"));
        Assert.Equal(("ipconfig", "/all"), DiagnosticsBundle.SnapshotCommand("ipconfig"));
        Assert.Equal(("route", "print -4"), DiagnosticsBundle.SnapshotCommand("routes"));
        // 名单之外的名字不许悄悄变成某条命令：它得是包里的一句"采集失败"，因为那就意味着少一页
        Assert.Contains("采集失败：未知的快照项", DiagnosticsBundle.CollectSnapshot("secret", null));
        Assert.DoesNotContain("netsh", DiagnosticsBundle.CollectSnapshot("secret", null));
    }

    /// 白名单与"实际采集了什么"必须两边对齐（评审的第 2 条：`route print -4` 在白名单里，
    /// 却没有任何一条用例证明它单独进过包）。原来每一条用例喂给注入委托的都是**同一个常数**
    /// （`_ => "SSID : zut-stu"`），三页拿到同一句假值，于是：
    /// ① 从 Build 的循环里删掉 "routes"、② 把三个标签都写成同一个文件名、③ 传错标签给采集函数
    /// —— 三种改法当时都不会让任何一条用例报警（只有那条真命令用例按名字读到过 routes.txt，
    /// 而它读的是"这一页非空"，看不出这一页到底是谁）。
    /// 现在两边各钉一次：白名单里有什么，Build 就该问什么、包里就该有什么，而且各归各页。
    [Fact]
    public void 白名单里每一项都被单独采集成自己的一页()
    {
        var dir = Dir();
        var asked = new List<string>();
        var zip = Path.Combine(dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, new SettingsStore(dir), null,
            label => { asked.Add(label); return $"【{label}】那一页"; }));

        Assert.Equal(DiagnosticsBundle.SnapshotLabels, asked);            // 每一项、按顺序、不多问也不少问
        using (var a = ZipFile.OpenRead(zip))
        {
            var names = a.Entries.Select(e => e.FullName).ToList();
            // 字面写死（不是从白名单推）：白名单少一项就在这里红，两边才算真的互相制着
            Assert.Contains("wlan.txt", names);
            Assert.Contains("ipconfig.txt", names);
            Assert.Contains("routes.txt", names);                         // ← route print -4 自己的一页
            Assert.Equal("【wlan】那一页", Read(a, "wlan.txt"));
            Assert.Equal("【ipconfig】那一页", Read(a, "ipconfig.txt"));
            Assert.Equal("【routes】那一页", Read(a, "routes.txt"));       // 内容串页/两页并一页都红在这里
            var manifest = Read(a, "manifest.txt");
            foreach (var name in new[] { "wlan.txt", "ipconfig.txt", "routes.txt" })
                Assert.Contains(name, manifest);                          // 清单里也点得到这三个名
        }
        DeleteDir(dir);
    }

    // ---------- 清单：包里到底有什么 ----------

    /// "不含密码"这五个字不够：同一份包里还有 MAC、机器名与 Windows 用户名（ipconfig /all 带出来的），
    /// 同学要知道自己正在发出去的是什么，才谈得上"发给维护者而不是贴到公开场合"。
    [Fact]
    public void 清单写明白包里有什么以及没有密码()
    {
        var dir = Dir();
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        var log = new TransactionLog(new FakeClock(), logs);
        log.Write(new TransactionRecord("Probe", "GET", "http://1.1.1.1:9002/0", null, null, "x", null));
        var zip = Path.Combine(dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, new SettingsStore(dir), log,
            snapshotProvider: _ => "SSID : zut-stu"));
        string manifest;
        using (var a = ZipFile.OpenRead(zip)) manifest = Read(a, "manifest.txt");

        foreach (var what in new[]
                 {
                     "logs/", "settings.json", "wlan.txt", "ipconfig.txt", "routes.txt", "version.txt",
                     "MAC", "机器名", "用户名", "自检日志",
                 })
            Assert.Contains(what, manifest);
        Assert.Contains("不含任何密码", manifest);
        Assert.Contains("secret.bin", manifest);          // 点名排除，而不是"看起来像密码的字段"那种含糊话
        Assert.Contains("***", manifest);
        Directory.Delete(dir, true);
    }

    // ---------- 导出按钮（Task 16 的两条规矩在这儿继续成立） ----------

    [Fact]
    public async Task 导出按钮在后台打包不挡住界面而且同时只跑一条()
    {
        var dir = Dir();
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var uiThread = Environment.CurrentManagedThreadId;
        var shown = new List<(string Body, bool Ok)>();
        string? target = null;

        var form = new MainForm(new NoopCommands(), new SettingsStore(dir), new SecretStore(dir),
            new TransactionLog(new FakeClock(), logs))
        {
            RunExport = t =>
            {
                target = t;
                Interlocked.Increment(ref calls);
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);   // 磁盘与进程活在池上
                gate.Task.Wait(TimeSpan.FromSeconds(5));
                return null;
            },
            ExportPresenter = (body, ok) => { lock (shown) shown.Add((body, ok)); },
        };

        var first = form.ExportBundleAsync();
        Assert.False(first.IsCompleted);                                   // UI 线程没有同步等它
        // 先等第一条真的在池上跑起来再点第二下：Task.Run 排进线程池到进 RunExport 之间有几微秒，
        // 不先把这一头对齐，calls 那次读就是在比谁快，而不是在验那把门。
        PumpUntil(() => Volatile.Read(ref calls) == 1);
        await form.ExportBundleAsync();                                    // 在途时的第二次点击不该开第二条
        Assert.Equal(1, calls);
        gate.SetResult();
        await first;
        PumpUntil(() => shown.Count > 0);
        Assert.Equal(1, calls);
        Assert.NotNull(target);
        Assert.StartsWith(dir, target);
        Assert.Contains("zutwifi-diagnostics-", target);
        Assert.EndsWith(".zip", target);
        var body = Assert.Single(shown);
        Assert.True(body.Ok);
        Assert.Contains(target!, body.Body);
        Assert.Contains("不含密码", body.Body);
        form.Dispose();
        Directory.Delete(dir, true);
    }

    /// Task 16 那把"一次只跑一条界面命令"的门是全窗口一把，不是每个按钮一把：
    /// 导出必须也在这把门里 —— 它打包的正是四个动作在写的那同一份日志，
    /// 半截的事务进了包，同学发回来的就成了最难查的那一种。
    ///
    /// 这条用例是同步的、靠 PumpUntil 推进（与 MainFormWiringTests 同一套）：窗口一 Show 出来，
    /// 续跑就落在 WindowsFormsSynchronizationContext 上，async 的 await 会把这条线程交回 xUnit，
    /// 那时没人再泵消息，用例不是失败而是挂住。PerformClick 也要真句柄才作数。
    [Fact]
    public void 导出与四个动作共用同一把门()
    {
        var dir = Dir();
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        var cmdGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exportGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exportCalls = 0;
        var cmdCalls = 0;

        var form = new MainForm(new GatedCommands(cmdGate.Task, () => Interlocked.Increment(ref cmdCalls)),
            new SettingsStore(dir), new SecretStore(dir), new TransactionLog(new FakeClock(), logs))
        {
            RunExport = _ =>
            {
                Interlocked.Increment(ref exportCalls);
                exportGate.Task.Wait(TimeSpan.FromSeconds(5));
                return null;
            },
            ExportPresenter = (_, _) => { },
        };
        form.Opacity = 0;                                 // 挪出屏幕 + 透明：桌面上看不见，但走的是真控件那条路
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        form.Apply(new AppStatus(AppPhase.Idle, "zut-stu", "10.1.1.1", null, null), ssidWhitelisted: true);
        Assert.True(form.LoginButtonEnabled);
        Assert.True(form.ExportButtonEnabled);

        try
        {
            // ① 一条动作在途 → 导出进不来，也不许排队（排队的导出会在下一次点击时再跑一遍）
            form.SimulateLoginClick();
            Assert.Equal(1, Volatile.Read(ref cmdCalls));
            Assert.False(form.ExportButtonEnabled);
            _ = form.ExportBundleAsync();
            PumpFor(200);                                   // 真等一会儿：被挡下的导出不能只是"还没轮到它"
            Assert.Equal(0, exportCalls);
            cmdGate.SetResult();
            PumpUntil(() => !form.CommandInFlight);
            Assert.Equal(0, exportCalls);
            Assert.True(form.ExportButtonEnabled);

            // ② 导出在途 → 同一把门把四个动作挡在外面，而且界面上看得见是被挡住的
            var running = form.ExportBundleAsync();
            PumpUntil(() => Volatile.Read(ref exportCalls) == 1);
            Assert.True(form.CommandInFlight);
            Assert.False(form.LoginButtonEnabled);
            Assert.False(form.ExportButtonEnabled);
            form.SimulateLoginClick();
            Assert.Equal(1, Volatile.Read(ref cmdCalls));
            exportGate.SetResult();
            // 等的是"在途标记落回去"而不是"那个 Task 完成"：RunAsync 的收尾走 OnUi，
            // 而 BeginInvoke 是发了不等回的 —— 任务先完成、按钮后恢复，这中间必须泵过消息才算数。
            PumpUntil(() => running.IsCompleted && !form.CommandInFlight);
            Assert.Equal(1, exportCalls);
            Assert.True(form.LoginButtonEnabled);
            Assert.True(form.ExportButtonEnabled);
        }
        finally
        {
            form.Dispose();
            DeleteDir(dir);
        }
    }

    /// 目录清理单独一条：窗口/句柄的 finally 里不该因为一个临时文件的读锁没放就把整条用例判失败。
    private static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (IOException) { /* 仍在在途的读句柄：临时目录留给系统清，不算用例失败 */ }
    }

    [Fact]
    public async Task 导出失败时框里给的是原因和日志目录兜底()
    {
        var dir = Dir();
        var logs = Path.Combine(dir, "logs");
        var shown = new List<(string Body, bool Ok)>();
        var form = new MainForm(new NoopCommands(), new SettingsStore(dir), new SecretStore(dir),
            new TransactionLog(new FakeClock(), logs))
        {
            RunExport = _ => "日志目录被占用",
            ExportPresenter = (body, ok) => shown.Add((body, ok)),
        };
        await form.ExportBundleAsync();
        var shownOne = Assert.Single(shown);
        Assert.False(shownOne.Ok);
        Assert.Contains("日志目录被占用", shownOne.Body);
        Assert.Contains(logs, shownOne.Body);                              // 回到 Task 16 那条"把日志目录发我"的兜底
        form.Dispose();
        Directory.Delete(dir, true);
    }

    /// 同学真点下去的那一条通路：RunExport **不设接缝** ⇒ 走界面默认那个委托
    /// `t => DiagnosticsBundle.Build(t, _store, _log)`。它原来在整个套件里是死代码 ——
    /// 三条导出用例都换了 RunExport，于是把 `_log` 从参数里删掉、或把目标换到别处去，没有一条用例会红。
    /// 唯一保留的接缝是"弹框"那一个：不换掉它，用例会在同学桌面上钉一个模态框把整套测试挂住；
    /// 打包这条线（目标路径、Build、传进去的 store/log）一点都没被替换。
    [Fact]
    public async Task 不换成任何导出接缝时真的把共享设置与日志打成了包()
    {
        var dir = Dir(secret: null);
        var logs = Path.Combine(dir, "logs");
        var store = new SettingsStore(dir);
        // 与装配同一份日志（AppContext.NewLog(dir) 用的就是 dir/logs）：这行字只有默认那条委托带得进包。
        var log = new TransactionLog(new FakeClock(), logs);
        log.Write(new TransactionRecord("Login", "POST", "http://1.1.1.1:801/eportal/", null, "/3.htm",
            "只有默认那条委托才会把这行带进包", null));
        var shown = new List<(string Body, bool Ok)>();
        var form = new MainForm(new NoopCommands(), store, new SecretStore(dir), log)
        {
            ExportPresenter = (body, ok) => shown.Add((body, ok)),
        };

        await form.ExportBundleAsync();

        var shownOne = Assert.Single(shown);
        Assert.True(shownOne.Ok, "默认那条委托报错了：" + shownOne.Body);
        var zip = Assert.Single(Directory.GetFiles(dir, "zutwifi-diagnostics-*.zip"));   // 目标就在数据目录里
        Assert.Equal(dir, Path.GetDirectoryName(zip));
        Assert.Contains(Path.GetFileName(zip), shownOne.Body);
        Assert.Contains("机器名", shownOne.Body);                                     // 弹框也要说清包里有什么
        using (var a = ZipFile.OpenRead(zip))
        {
            var names = a.Entries.Select(e => e.FullName).ToList();
            Assert.Contains(names, n => n.EndsWith("settings.json"));
            Assert.Contains(names, n => n.EndsWith("manifest.txt"));
            Assert.Contains(names, n => n.EndsWith("wlan.txt"));
            Assert.Contains(names, n => n.EndsWith("ipconfig.txt"));
            Assert.Contains(names, n => n.EndsWith("routes.txt"));
            Assert.DoesNotContain("secret.bin", names);
            Assert.Contains("202500000001", Read(a, "settings.json"));        // ← 传进去的是 _store 那一份
            Assert.Contains("只有默认那条委托才会把这行带进包", Read(a, "app-20260919.log"));
            var manifest = Read(a, "manifest.txt");
            // 这两行合起来才证明 Build 真的收到了那份共享日志：传 null 时清单会另写一句"没有传入事务日志对象"。
            Assert.Contains("日志份数：app=1", manifest);
            Assert.Contains("日志写入失败：0 次（日志通路正常）", manifest);
            Assert.DoesNotContain("没有传入事务日志对象", manifest);
        }
        form.Dispose();
        DeleteDir(dir);
    }

    private static void PumpUntil(Func<bool> done, int millis = 3000)
    {
        var deadline = Environment.TickCount64 + millis;
        while (!done() && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Assert.True(done(), "等到超时都没等到结果");
    }

    /// 不问条件、只泵这么多次消息：用来证明"这件事确实没发生"（那种断言没有可等的条件）。
    private static void PumpFor(int millis)
    {
        var deadline = Environment.TickCount64 + millis;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private sealed class NoopCommands : ILoginCommands
    {
        public Task LoginAsync(CancellationToken ct) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ReprobeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RecoverReloginAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// 一条卡在 await 上的动作（MainFormWiringTests 里那个 PendingSpy 的同一种形状）：
    /// 用来把"命令在途"这个状态留住，好在它两头各敲一次导出。
    private sealed class GatedCommands(Task gate, Action onStart) : ILoginCommands
    {
        public async Task LoginAsync(CancellationToken ct) { onStart(); await gate; }
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ReprobeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RecoverReloginAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
