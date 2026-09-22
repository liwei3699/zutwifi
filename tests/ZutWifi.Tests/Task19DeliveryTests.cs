using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Diagnostics;
using ZutWifi.Shell;
using ZutWifi.Tests.Support;

namespace ZutWifi.Tests;

/// Task 19：发布产物与交付物。这组用例是"说明与代码不许各说各话"的那道闸。
/// build.ps1、dist/使用说明.txt、dist/自检.bat 都不是可执行代码 —— 编译器管不到、写错了也炸不了，
/// 但它们是同学拿到手的唯一一份"这个程序会做什么"。一条过度承诺的说明比少说一句更坏：
/// 照着说明去找一个根本不存在的按钮，找不到的人是他，不是写说明的人。
///
/// 三条与套件里别处不同的地方：
/// ① 全部离线：只读仓库里的文本和临时目录，不出网、不动 %APPDATA%、**不跑** --selftest
///    （那一步会真向门户提交一次登录，测试进程不该替同学做这件事）。
/// ② 会去读 src/ 下的源码文本 —— 与 SelfTestTests 里"不许有第二个 HttpClient 构造点"同一套办法。
///    "六个按钮""五种颜色""自检那十步测到哪""包里到底装了什么"这些说法的真值全在代码里：
///    两边各读一遍再对得上才叫核对，只读说明自己跟自己点头不叫核对。
/// ③ dist/ZutWifi.zip 是跑过 build.ps1 才有的产物（而且被 .gitignore 挡着）：
///    没有它时去核对 build.ps1 里那份点名名单（这条永远有牙），有它时逐条核对真实条目。
public class Task19DeliveryTests
{
    static readonly string[] PayloadPaths = ["dist/ZutWifi.exe", "dist/使用说明.txt", "dist/自检.bat"];
    static readonly string[] ZipEntries = ["ZutWifi.exe", "使用说明.txt", "自检.bat"];

    static string PathOf(string relative) => Path.Combine(Path.GetFullPath(RepoRoot()), relative);

    /// 带 BOM 也认（BOM 检测是 StreamReader 做的），但字节不是 UTF-8 就直接抛：
    /// 有人用 GBK 另存一份说明，这里必须算失败 —— .bat 里那几行 echo 是要在 chcp 65001 下显示的。
    static string ReadText(string relative) => File.ReadAllText(PathOf(relative), new UTF8Encoding(false, true));

    static bool HasBom(string relative)
    {
        var bytes = File.ReadAllBytes(PathOf(relative));
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    // ---------- 交付文件本身 ----------

    [Fact]
    public void 三个交付文件都在并且都是UTF8()
    {
        // dist/ZutWifi.exe 不在这里：它是 build.ps1 生成的、被 .gitignore 挡着的产物，
        // 新克隆的仓库里根本没有。它在不在、和 zip 是不是同生同死，由下面那条 zip 用例管。
        foreach (var relative in new[] { "build.ps1", "dist/使用说明.txt", "dist/自检.bat" })
            Assert.True(File.Exists(PathOf(relative)), $"缺交付文件：{relative}");

        // .ps1 必须带 BOM：PowerShell 5.1 在没有 BOM 时按本地 ANSI 码页读脚本文件，
        // $Payload 里那两个中文文件名就变成"文件不存在"，最后打出一个少两页的 zip。
        Assert.True(HasBom("build.ps1"), "build.ps1 要带 UTF-8 BOM：否则 PowerShell 5.1 读到的中文路径是错的");
        // .bat 反过来绝不能带 BOM：cmd 会把那三个字节当成第一条命令的一部分，第一行就不再是 @echo off。
        Assert.False(HasBom("dist/自检.bat"), "自检.bat 不许带 BOM：cmd 会把 BOM 当成命令的一部分");
        // .txt 带 BOM：老版本记事本没有 BOM 就按 ANSI 猜，同学打开看到的是一堆乱码。
        Assert.True(HasBom("dist/使用说明.txt"), "使用说明.txt 带 BOM 更稳：记事本在没有 BOM 时会猜码页");

        var bat = ReadText("dist/自检.bat");
        Assert.StartsWith("@echo off", bat.TrimStart('\r', '\n'));
        Assert.Contains("chcp 65001", bat);
        // 批处理只认 CRLF：混进裸 LF 的那一行会和上一行被当成同一条命令。
        Assert.DoesNotContain("\n", bat.Replace("\r\n", ""));

        Assert.DoesNotContain("\uFFFD", ReadText("build.ps1"));
        Assert.DoesNotContain("\uFFFD", bat);
        Assert.DoesNotContain("\uFFFD", ReadText("dist/使用说明.txt"));
    }

    // ---------- zip 里装什么（评审点名的那条"永远不会红"的断言） ----------

    [Fact]
    public void 打包脚本点名的就是交付物那三样()
    {
        var ps = ReadText("build.ps1");
        var list = Regex.Match(ps, @"\$Payload\s*=\s*@\(([^)]*)\)", RegexOptions.Singleline);
        Assert.True(list.Success, "build.ps1 里找不到 $Payload = @(...) 那份名单：zip 该装什么必须只写在一处");
        var named = Regex.Matches(list.Groups[1].Value, "'([^']+)'").Cast<Match>()
            .Select(m => m.Groups[1].Value.Replace('\\', '/')).ToArray();

        // 少一个、多一个、改个名，全都从这里开始红。
        Assert.Equal(PayloadPaths.OrderBy(x => x, StringComparer.Ordinal),
            named.OrderBy(x => x, StringComparer.Ordinal));

        // 打包那一行用的必须就是这份名单，而不是另抄一遍（两处真值早晚分叉）。
        var compress = Assert.Single(ps.Split('\n'), l => l.TrimStart().StartsWith("Compress-Archive"));
        Assert.Contains("$Payload", compress);
        Assert.Contains("-DestinationPath dist/ZutWifi.zip", compress);
        Assert.Contains("-Force", compress);

        // -Force 意味着"zip 一定生成"，所以"少了一页"只能靠打包之前那一步点名发现。
        Assert.Contains("foreach ($p in $Payload)", ps);
        Assert.Contains("Test-Path -LiteralPath $p", ps);
        foreach (var relative in named.Where(n => n != "dist/ZutWifi.exe"))
            Assert.True(File.Exists(PathOf(relative)), $"名单里的 {relative} 不在磁盘上：zip 会缺这一页");

        // exe 是从 publish 拷过来的，那一段也要有守卫（-Force 掩盖不了"根本没发布出来"）。
        Assert.Contains("dotnet publish", ps);
        var published = Regex.Match(ps, @"'([^']*ZutWifi\.exe)'").Groups[1].Value;
        Assert.True(published.EndsWith("ZutWifi.exe", StringComparison.Ordinal),
            $"发布产物按后缀判，这个名字对不上：{published}");
        Assert.Contains("if (-not (Test-Path -LiteralPath $Published))", ps);
        Assert.Contains("Copy-Item -LiteralPath $Published -Destination dist/ -Force", ps);
    }

    [Fact]
    public void 交付zip里恰好三个条目一个不多一个不少()
    {
        var zipPath = PathOf(Path.Combine("dist", "ZutWifi.zip"));
        if (!File.Exists(zipPath))
        {
            // 还没跑过 build.ps1（zip 在 .gitignore 里）。这时还管用的核对是"别留下半套产物"。
            Assert.False(File.Exists(PathOf(Path.Combine("dist", "ZutWifi.exe"))),
                "dist/ZutWifi.exe 在而 dist/ZutWifi.zip 不在：build.ps1 跑到一半就断了，重跑一次再提交");
            return;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        var names = archive.Entries.Select(e => e.FullName).ToArray();

        // 比的是名单本身（旧写法比的是"zip 文件存在"，而 -Force 让它永远存在 —— 那条断言没有可能失败）。
        Assert.Equal(ZipEntries.OrderBy(x => x, StringComparer.Ordinal),
            names.OrderBy(x => x, StringComparer.Ordinal));
        // 主名按后缀判，不按"包含"判：ZutWifi.exe.bak、MyZutWifi.exe.new 都不算数。
        Assert.Single(names, n => n.EndsWith("ZutWifi.exe", StringComparison.Ordinal));
        Assert.All(names, n => Assert.False(n.Contains('/'), $"条目套了一层目录：{n}"));

        // 多出来的成员一律算失败：发布目录里那些非 exe 的产物、仓库里的参考脚本与日志、凭据文件。
        Assert.DoesNotContain(names, n => n.EndsWith(".pdb", StringComparison.Ordinal) ||
                                          n.EndsWith(".dll", StringComparison.Ordinal) ||
                                          n.EndsWith(".json", StringComparison.Ordinal) ||
                                          n.EndsWith(".log", StringComparison.Ordinal) ||
                                          n.EndsWith(".md", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("secret.bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("spike", StringComparison.OrdinalIgnoreCase));
        // 仓库里那两目录（登录/ 与 注销/）是真机抓包参考，一旦被通配进 -Path 就是往外交东西。
        Assert.DoesNotContain(names, n => n.StartsWith("登录", StringComparison.Ordinal) ||
                                          n.StartsWith("注销", StringComparison.Ordinal));

        // 包里的两份文字要就是磁盘上这一份：zip 里塞一份过期说明，是另一种过度承诺。
        Assert.Equal(Trim(ReadText("dist/使用说明.txt")), Trim(ReadEntry(archive, "使用说明.txt")));
        Assert.Equal(Trim(ReadText("dist/自检.bat")), Trim(ReadEntry(archive, "自检.bat")));
    }

    static string Trim(string text) => text.TrimEnd('\r', '\n').TrimStart('\uFEFF');

    static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ---------- build.ps1 的退出码守卫 ----------

    [Fact]
    public void 每条dotnet命令之后都检查退出码()
    {
        var lines = PsCodeLines(ReadText("build.ps1")).Split('\n');
        var calls = Enumerable.Range(0, lines.Length)
            .Where(i => lines[i].TrimStart().StartsWith("dotnet ")).ToArray();

        Assert.Equal(3, calls.Length);                          // ① 全量测试 ② 单文件发布 ④ 交付一致性核对
        foreach (var i in calls)
            Assert.True(GuardLine(lines, i) is not null,
                $"第 {i + 1} 行那条 dotnet 后面没有 $LASTEXITCODE 检查：dotnet 是外部程序，" +
                "$ErrorActionPreference 管不住它的非零退出码，测试红了照样往下发布");

        // 评审点名的就是发布那一步：它后面那句守卫要能说出"是 publish 失败了"。
        var publish = calls.First(i => lines[i].Contains("publish"));
        var guard = GuardLine(lines, publish);
        Assert.NotNull(guard);
        Assert.Contains("publish", guard);
        Assert.Contains("throw", guard);
        Assert.Contains("$ErrorActionPreference = 'Stop'", ReadText("build.ps1"));
    }

    /// 从某条 dotnet 命令起，跳过反引号续行，再往下看四行：$LASTEXITCODE 必须出现在这一窗里。
    static string? GuardLine(string[] lines, int callIndex)
    {
        var i = callIndex;
        while (i < lines.Length - 1 && lines[i].TrimEnd().EndsWith('`')) i++;
        for (var k = i + 1; k <= Math.Min(lines.Length - 1, i + 4); k++)
            if (lines[k].Contains("$LASTEXITCODE")) return lines[k];
        return null;
    }

    /// 只看真语句：`#` 行注释与 `<# ... #>` 块注释都剔掉。
    /// 剔注释不是为了省事 —— 注释里写 "--selftest" 是在解释**为什么不做**这件事，
    /// 拿它去判"脚本里做了这件事"就会假红（同一套办法见 SelfTestTests 的 CodeLines）。
    static string PsCodeLines(string script)
    {
        var keep = new List<string>();
        var inBlock = false;
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (inBlock)
            {
                if (line.Contains("#>")) inBlock = false;
                continue;
            }
            if (line.TrimStart().StartsWith("<#"))
            {
                if (line.Contains("#>")) continue;
                inBlock = true;
                continue;
            }
            if (line.TrimStart().StartsWith('#')) continue;
            keep.Add(line);
        }
        return string.Join('\n', keep);
    }

    [Fact]
    public void 闸门之前先把上一次的交付产物清掉()
    {
        // ① 那一步会拿真 zip 里的文字和磁盘上这一份比。不清旧产物时，改过说明文字就会出现
        // "闸门因为上一轮的 zip 而红" —— 看着像脚本坏了，实际是产物过期；下一个人多半去放宽断言。
        var code = PsCodeLines(ReadText("build.ps1"));
        var clean = code.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("Remove-Item"), "");
        Assert.NotEmpty(clean);
        Assert.Contains("dist/ZutWifi.zip", clean);
        Assert.Contains("dist/ZutWifi.exe", clean);
        // 第一条 dotnet test 必须排在清理之后，否则清的是这一轮刚打出来的包。
        Assert.True(code.IndexOf("Remove-Item", StringComparison.Ordinal) <
                    code.IndexOf("dotnet test", StringComparison.Ordinal),
                    "清理要排在 ① 全量测试之前");
    }

    [Fact]
    public void 发布流水线里不跑活体自检()
    {
        var ps = ReadText("build.ps1");
        var code = PsCodeLines(ps);
        // --selftest 会真提交一次登录，--with-logout 会真把会话踢下线：两条都不属于打包。
        // （打包机既不配置账号也不在校园网里，跑出来一定是"①② 报问题、⑥⑦ 跳过、退出码 1"，什么也没测到。）
        Assert.DoesNotContain("--selftest", code);
        Assert.DoesNotContain("--with-logout", code);
        Assert.Contains("Compress-Archive", code);
        // 这个理由要留在脚本里，否则下一个人会把它当成"漏了一步"加回来。
        Assert.Contains("不跑", ps);
        Assert.Contains("门户", ps);
    }

    // ---------- 使用说明 ↔ 代码 ----------

    /// 界面上"能点的按钮"就是这些字面量（MainForm 五个 + 设置页两个）。名字是从源码里捞的，
    /// 不是抄进用例的第六份真值：加一个按钮而忘了写进说明，这条要第一个红。
    [Fact]
    public void 说明把主窗口上六个按钮全列出来()
    {
        var doc = ReadText("dist/使用说明.txt");
        var labels = ButtonLabels("src/ZutWifi/Shell/MainForm.cs")
            .Concat(ButtonLabels("src/ZutWifi/Shell/SettingsPage.cs")).ToArray();

        Assert.Equal(["登录", "注销", "重新检测", "注销并重登", "导出诊断包", "保存", "测试配置"], labels);
        foreach (var label in labels)
            Assert.Contains(label, doc, StringComparison.Ordinal);

        // 评审点名的那一句：六个，不是四个。
        Assert.Contains("六个", doc);
        Assert.DoesNotContain("四个按钮", doc);

        // "注销并重登"平时不在界面上：这个说法要对得上 MainForm 里那句 Visible = false。
        Assert.Contains("Visible = false", ReadText("src/ZutWifi/Shell/MainForm.cs"));
        Assert.Contains("隐藏", doc);
    }

    static string[] ButtonLabels(string relative) => Regex.Matches(ReadText(relative),
            @"Button\s+_\w+\s*=\s*new\(\)\s*\{[^}]*Text\s*=\s*""([^""]+)""")
        .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();

    [Fact]
    public void 五种托盘颜色就是呈现规则里那五种()
    {
        var doc = ReadText("dist/使用说明.txt");
        var presenter = ReadText("src/ZutWifi/Shell/StatusPresenter.cs");
        var keys = Regex.Matches(presenter, "\"(green|yellow|orange|red|gray)\"").Cast<Match>()
            .Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(["gray", "green", "orange", "red", "yellow"], keys);
        var words = new Dictionary<string, string>
        {
            ["green"] = "绿", ["yellow"] = "黄", ["gray"] = "灰", ["red"] = "红", ["orange"] = "橙",
        };
        foreach (var key in keys) Assert.Contains(words[key], doc);
        // 说明里不许出现代码里没有的颜色（"蓝=待命中"这种一写出来就永远对不上）
        foreach (var extra in new[] { "蓝", "紫", "粉" }) Assert.DoesNotContain(extra, doc);
        // 颜色得有含义，不能只列五个字
        Assert.Contains("已认证", doc); Assert.Contains("外网不通", doc); Assert.Contains("没连", doc);
    }

    [Fact]
    public void 说明里那几行按钮可用性对得上StatusPresenter()
    {
        var doc = ReadText("dist/使用说明.txt");

        var online = StatusPresenter.Of(new AppStatus(AppPhase.Online, "zut-stu", "10.1.1.1"), true);
        Assert.False(online.LoginEnabled);                      // 在线时不再交第二份凭据
        Assert.True(online.LogoutEnabled); Assert.True(online.ReprobeEnabled);
        var onlineLine = DocLine(doc, "已认证 · 网络正常");
        Assert.Contains("注销", onlineLine); Assert.Contains("重新检测", onlineLine);
        Assert.Contains("登录", onlineLine); Assert.Contains("灰", onlineLine);   // 还得说清"登录为什么不亮"

        var idle = StatusPresenter.Of(new AppStatus(AppPhase.Idle, "zut-stu", "10.1.1.1"), true);
        Assert.True(idle.LoginEnabled); Assert.True(idle.ReprobeEnabled); Assert.False(idle.LogoutEnabled);
        var idleLine = DocLine(doc, "空闲");
        Assert.Contains("登录、重新检测能点", idleLine);
        Assert.DoesNotContain("注销", idleLine);

        var off = StatusPresenter.Of(new AppStatus(AppPhase.Online, "TP-LINK", "192.168.1.2"), false);
        Assert.False(off.LoginEnabled); Assert.False(off.LogoutEnabled); Assert.False(off.ReprobeEnabled);
        var offLine = DocLine(doc, "没连校园网");
        Assert.Contains("全灰", offLine); Assert.Contains("导出诊断包", offLine);

        var giveUp = StatusPresenter.Of(new AppStatus(AppPhase.GiveUp, "zut-stu", "10.1.1.1", "密码错"), true);
        Assert.True(giveUp.LoginEnabled); Assert.False(giveUp.LogoutEnabled); Assert.True(giveUp.ReprobeEnabled);
        var giveUpLine = DocLine(doc, "放弃（");
        Assert.Contains("重试", giveUpLine); Assert.Contains("重新检测", giveUpLine);
        Assert.Contains("注销是灰", giveUpLine);

        var failed = StatusPresenter.Of(
            new AppStatus(AppPhase.Failed, "zut-stu", "10.1.1.1", "账号已在别处在线", CanRecoverRelogin: true), true);
        Assert.True(failed.RecoverVisible); Assert.True(failed.LoginEnabled);
        var failedLine = DocLine(doc, "登录（“重试”）、注销、重新检测都能点");
        Assert.Contains("注销并重登", failedLine);

        // 按钮的字会随阶段换：Failed/GiveUp 那个就是"重试"，说明里两个名字都要有
        Assert.Equal("重试", StatusPresenter.LoginText(AppPhase.GiveUp));
        Assert.Equal("登录", StatusPresenter.LoginText(AppPhase.Online));
        Assert.Contains("重试", doc);
    }

    static string DocLine(string doc, string needle)
    {
        var line = doc.Split('\n').FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal));
        Assert.NotNull(line);                                   // 说明里少了这一档状态：先红在这里
        return line.Trim();
    }

    [Fact]
    public void 首次向导那一段说的是真有的按钮和真打的字()
    {
        var doc = ReadText("dist/使用说明.txt");
        var wizard = ReadText("src/ZutWifi/Shell/FirstRunWizard.cs");

        // 界面上那个动作叫"测试配置"，输出里也没有英文的 Success：这两样以前都写在说明里，都是假的。
        Assert.DoesNotContain("立即测试登录", doc);
        Assert.DoesNotContain("Success", doc);
        Assert.Contains("测试配置", doc);
        Assert.Contains("完成", doc);
        // 向导里那张表单就是设置页那一个控件（同一份表单），所以说明只需要教一次。
        Assert.Contains("new SettingsPage(_store, secrets, _log, applyAutoStart, Gateway)", wizard);
        // 说明里那三步的字面量，必须就是 PortalTestRun 打出来的那三行
        var steps = Regex.Matches(wizard, "line\\(\"([^\"]+)…\"\\)").Cast<Match>()
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(["① 探测认证状态", "② 获取内网地址", "③ 提交登录"], steps);
        foreach (var step in steps) Assert.Contains(step, doc);
    }

    [Fact]
    public void 说明说的网络地址就是代码访问的那些()
    {
        var doc = ReadText("dist/使用说明.txt");
        var messages = ReadText("src/ZutWifi/Portal/PortalMessages.cs");
        var probe = ReadText("src/ZutWifi/Core/ConnectivityProbe.cs");

        Assert.Contains("http://{host}:9002/0", messages);
        Assert.Contains("http://{host}:801/eportal/", messages);
        Assert.Contains("msftconnecttest.com", probe); Assert.Contains("bing.com", probe);
        Assert.Contains("1.1.1.1:9002", doc);
        Assert.Contains("1.1.1.1:801", doc);
        Assert.Contains("msftconnecttest", doc); Assert.Contains("bing", doc);
        // "只访问学校门户"是过度承诺：那两个连通性探针也是出网的，必须一起说。
        Assert.DoesNotContain("只访问学校门户", doc);
    }

    [Fact]
    public void 包里到底装了什么说明要与真打包一致()
    {
        var doc = ReadText("dist/使用说明.txt");

        // 评审点名的那句假话：日志**就是**包的一部分（那是这个程序唯一的排障通道）。
        Assert.DoesNotContain("日志目录不会放进压缩包", doc);
        Assert.DoesNotContain("不含日志", doc);
        Assert.DoesNotContain("不包含日志", doc);

        var (dir, entries, manifest) = BuildBundle();
        try
        {
            foreach (var member in new[]
                     { "settings.json", "version.txt", "wlan.txt", "ipconfig.txt", "routes.txt", "manifest.txt" })
            {
                Assert.Contains(entries, e => e == member);
                Assert.Contains(member, doc);                   // 包里每一样，说明都要点得到名
            }
            Assert.Contains(entries, e => e.StartsWith("logs/app-", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.StartsWith("logs/selftest-", StringComparison.Ordinal));
            Assert.Contains("app-*.log", doc);
            Assert.Contains("selftest-*.log", doc);
            Assert.Contains("日志本来就在包里", doc);
            Assert.DoesNotContain(entries, e => e.Contains("secret.bin", StringComparison.OrdinalIgnoreCase));

            // "不含密码"那一句要跟着清单的原话走：包里还有 MAC、机器名与 Windows 用户名。
            foreach (var warning in new[] { "MAC", "机器名", "用户名" })
            {
                Assert.Contains(warning, manifest);
                Assert.Contains(warning, doc);
            }
            Assert.Contains("不含任何密码", manifest);
            Assert.Contains("绝不含密码", doc);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public void 自检写到哪一步以及日志写在哪说明要照实说()
    {
        var doc = ReadText("dist/使用说明.txt");
        var bat = ReadText("dist/自检.bat");
        var session = ReadText("src/ZutWifi/Diagnostics/SelfTestSession.cs");
        var selfTest = ReadText("src/ZutWifi/Diagnostics/SelfTest.cs");

        // 自检那份日志的真名与真位置：两条求助通路（发文件 / 发 zip）都得说得下去。
        Assert.Contains("\"selftest-\"", session);
        Assert.Contains("Path.Combine(h.Dir, \"logs\"", session);
        Assert.Contains(@"logs\selftest-", doc);
        Assert.Contains("app-<yyyyMMdd>.log", doc);
        Assert.Contains(@"%APPDATA%\ZutWifi\logs", doc);
        Assert.Contains("selftest-", bat); Assert.Contains("app-", bat); Assert.Contains("logs", bat);

        // 退出码的三个真值在 SelfTest.cs 里；说明里那三个数必须跟着它们走（"exit 2" 那种说法代码里就没有）。
        foreach (var (name, value) in new[]
                 { ("PassExitCode", "0"), ("ProblemsExitCode", "1"), ("IncompleteExitCode", "78") })
        {
            Assert.Contains($"public const int {name} = {value};", selfTest);
            Assert.Contains($"{value} = ", doc);                // 说明里那一档退出码说的是同一个数
        }
        Assert.Contains("78", doc); Assert.Contains("78", bat);
        Assert.DoesNotContain("exit 2", doc);
        Assert.DoesNotContain("退出码 2（", doc);

        // 不许把 --selftest 说成"①–⑨ 全跑"：①②③⑩ 在任何机器上都会跑完，④–⑨ 要配置过账号且在校园网里。
        foreach (var text in new[] { doc, bat })
        {
            Assert.DoesNotContain("①–⑨", text);
            Assert.DoesNotContain("①-⑨", text);
            Assert.Contains("跳过", text);
        }
        Assert.Contains("没配置过账号", doc);
        Assert.Contains("保存过密码", bat);
        // 注销那一步默认不跑，而且会真下线：两个文件都得把这条说明白。
        Assert.Contains("--with-logout", doc); Assert.Contains("--with-logout", bat);
        Assert.Contains("踢下线", doc); Assert.Contains("踢下线", bat);
        // 屏幕上看不到字是代码里就承认的通路（上屏失败只记账不崩），所以说明以日志为准。
        Assert.Contains("ConsoleFailures", session);
        Assert.Contains("以日志为准", bat);
    }

    // ---------- 诊断包的份数上限（轮转只有事务日志那一处） ----------

    [Fact]
    public void 包里的日志有上限但那不是第二套轮转()
    {
        // 分工钉在这里：删除旧日志这件事只有 TransactionLog.Prune 做（那边的常量 KeepFiles = 5）。
        // 诊断包这边的 5 / 3 是"读多少份"的上限，它一份都不删。两头都要证：
        // 上限真生效（8 份里只带最新 5 份），而且磁盘上那 8 份一份不少。
        Assert.Contains("private const int KeepFiles = 5;",
            ReadText("src/ZutWifi/Diagnostics/TransactionLog.cs"));
        var doc = ReadText("dist/使用说明.txt");
        Assert.Contains("最多 5 份", doc);
        Assert.Contains("最多 3 份", doc);

        var dir = Tmp();
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        var store = new SettingsStore(dir);
        var s = store.Load(); s.StudentId = "202500000001"; store.Save(s);
        for (var d = 1; d <= 8; d++) File.WriteAllText(Path.Combine(logs, $"app-202609{d:D2}.log"), $"第 {d} 份");
        for (var d = 1; d <= 4; d++)
            File.WriteAllText(Path.Combine(logs, $"selftest-202609{d:D2}-060000.log"), $"自检 {d}");

        var zip = Path.Combine(dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, store, null, snapshotProvider: _ => "SSID : zut-stu"));

        var (entries, manifest) = ReadZip(zip);
        Assert.Equal(5, entries.Count(e => e.StartsWith("logs/app-", StringComparison.Ordinal)));
        Assert.Equal(3, entries.Count(e => e.StartsWith("logs/selftest-", StringComparison.Ordinal)));
        Assert.Contains("logs/app-20260908.log", entries);      // 带的是最新的几份
        Assert.DoesNotContain("logs/app-20260901.log", entries);
        Assert.Contains("日志份数：app=5 selftest=3", manifest);

        Assert.Equal(8, Directory.GetFiles(logs, "app-*.log").Length);      // 一份都没被删
        Assert.Equal(4, Directory.GetFiles(logs, "selftest-*.log").Length);
        DeleteDir(dir);
    }

    // ---------- 仓库设置与单文件陷阱（源码文本级闸） ----------

    [Fact]
    public void 仓库设置确保抓包目录与发布产物不入库()
    {
        // 抓包目录里是**真凭据**。这条看着像 hygiene，实际是防"某人一次 git add -A 就把账号推上网"。
        var ignored = ReadText(".gitignore");
        foreach (var entry in new[] { "登录/", "注销/", "spike/", "publish/", "dist/ZutWifi.exe", "dist/ZutWifi.zip" })
            Assert.Contains(entry, ignored);
    }

    [Fact]
    public void 注册AUMID取程序路径不许用AssemblyLocation()
    {
        // 单文件 exe 里 Assembly.Location 恒为空字符串（IL3000）。真拿它兜底，写出来的开始菜单快捷方式
        // 目标就是空的：Ensure() 报成功、通知中心永远不弹 —— 而这一层只有发布版才有，日常测试跑不到。
        // 只看非注释行：上面那段说明文字里故意写着这个 API 的名字。
        var code = ReadText("src/ZutWifi/Notify/AumidRegistrar.cs")
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//"))
            .Aggregate("", (a, l) => a + l);
        Assert.DoesNotContain("Assembly.Location", code);
        Assert.Contains("Environment.ProcessPath", code);
        Assert.Contains("System.AppContext.BaseDirectory", code);
    }

    [Fact]
    public void 自检脚本把退出码说给人听并且停住不闪窗()
    {
        // 同学是双击跑的：不 echo 退出码、不 pause，窗口一闪而过，什么证据都留不下。
        var bat = ReadText("dist/自检.bat");
        Assert.Contains("%ERRORLEVEL%", bat);
        Assert.Contains("pause", bat.ToLowerInvariant());
    }

    [Fact]
    public void 托盘那一段列出的菜单项和点击行为都对得上代码()
    {
        var doc = ReadText("dist/使用说明.txt");
        var tray = ReadText("src/ZutWifi/Shell/TrayApp.cs");
        foreach (var item in new[] { "打开主界面", "登录", "注销", "重新检测", "开机自启", "退出" })
        {
            Assert.Contains($"\"{item}\"", tray);   // 菜单里真建了这一项
            Assert.Contains(item, doc);             // 说明里也把它列给了同学
        }

        // 点通知目前只做一件事：把主窗口带到前台。说明不许替它多承诺"跳到日志页并选中最后一行"——
        // 那一步根本没接；而"点通知没反应"和"点通知跳错了地方"这两种故障，同学看到的差别只在这句话上。
        Assert.Contains("Notifier.Activated = _ => ShowMainWindow();", ReadText("src/ZutWifi/AppContext.cs"));
        Assert.DoesNotContain("选中最后一行", doc);
        Assert.DoesNotContain("跳到“日志”", doc);
    }

    [Fact]
    public void 说明里那三个运营商后缀就是下拉里那三个()
    {
        // 后缀是纯字符串，填错一位就是一次注定失败的登录（而门户回的还是那种看不懂的错）。
        // 同学只会照说明上的字去对下拉，所以两边必须是同一串，不许一边改了另一边没改。
        var doc = ReadText("dist/使用说明.txt");
        var page = ReadText("src/ZutWifi/Shell/SettingsPage.cs")
            .Split('\n').FirstOrDefault(l => l.Contains("_isp.Items.AddRange"), "");
        Assert.NotEmpty(page);
        foreach (var suffix in new[] { "@cmcc", "@telecom", "@unicom" })
        {
            Assert.Contains($"\"{suffix}\"", page);   // 下拉里真能选到
            Assert.Contains(suffix, doc);             // 说明里写的就是它
        }
        Assert.DoesNotContain("@ctcc", doc);          // 那个没验过的写法别回来
        Assert.DoesNotContain("@ctcc", page);
    }

    // ---------- 小工具 ----------

    static string Tmp() => Path.Combine(Path.GetTempPath(), "zw19" + Guid.NewGuid().ToString("N"));

    /// 一次真打包（临时目录、注入快照委托 ⇒ 不 spawn 子进程、不出网）。
    static (string dir, string[] entries, string manifest) BuildBundle()
    {
        var dir = Tmp();
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        var store = new SettingsStore(dir);
        var s = store.Load(); s.StudentId = "202500000001"; s.FirstRunCompleted = true; store.Save(s);
        new SecretStore(dir).Set("Sup3r-S3cret-Pw!");           // 密码文件在旁边：Build 根本不读它
        File.WriteAllText(Path.Combine(logs, "app-20260911.log"), "运行日志");
        File.WriteAllText(Path.Combine(logs, "selftest-20260911-060000.log"), "自检记录");
        var zip = Path.Combine(dir, "bundle.zip");
        Assert.Null(DiagnosticsBundle.Build(zip, store, null, snapshotProvider: _ => "SSID : zut-stu"));
        var (entries, manifest) = ReadZip(zip);
        Assert.DoesNotContain("Sup3r-S3cret-Pw!", manifest);
        return (dir, entries, manifest);
    }

    static (string[] entries, string manifest) ReadZip(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return (archive.Entries.Select(e => e.FullName).ToArray(), ReadEntry(archive, "manifest.txt"));
    }

    static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (IOException) { /* zip 句柄可能还没放：临时目录留给系统清，不算用例失败 */ }
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(System.AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ZutWifi.sln"))) return d.FullName;
        throw new InvalidOperationException("找不到仓库根（ZutWifi.sln）");
    }
}
