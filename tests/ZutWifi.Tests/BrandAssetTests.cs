using System.Text;
using System.Text.RegularExpressions;
using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Shell;

namespace ZutWifi.Tests;

/// 图标这件事编译器一句都不管：ico 少一档、LogicalName 打错一个字母、COM 的 vtable 槽位被重排 ——
/// 三种都照样编译通过，然后在同学机器上分别表现为"白底方块""永远是默认图标""快捷方式目标为空、
/// 通知中心不弹"。所以这一组全部对着**真产物**验：读 .ico 的目录项、读程序集里真嵌着的那两条资源、
/// 真造一个窗口看它拿到图标没有。
public class BrandAssetTests
{
    static string Root()
    {
        for (var d = new DirectoryInfo(System.AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ZutWifi.sln"))) return d.FullName;
        throw new InvalidOperationException("找不到仓库根（ZutWifi.sln）");
    }

    static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative), new UTF8Encoding(false, true));

    [Fact]
    public void ico里六档尺寸一档不缺而且没指到文件外()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "assets/app.ico"));
        using var r = new BinaryReader(new MemoryStream(bytes));
        Assert.Equal((ushort)0, r.ReadUInt16());        // ICONDIR 的 reserved
        Assert.Equal((ushort)1, r.ReadUInt16());        // 类型 1 = 图标（不是组）
        var count = r.ReadUInt16();

        var entries = new List<(int side, int len, int off)>();
        for (var i = 0; i < count; i++)
        {
            var w = r.ReadByte();
            var h = r.ReadByte();
            r.ReadByte(); r.ReadByte();                 // 颜色数 / 保留
            r.ReadUInt16(); r.ReadUInt16();             // planes / bitCount
            var len = r.ReadInt32();
            var off = r.ReadInt32();
            Assert.Equal(w, h);                         // 方形；非方形会被 Windows 拉扁
            entries.Add((w == 0 ? 256 : w, len, off));  // 0 在 ICO 里就是 256
        }

        Assert.Equal([16, 24, 32, 48, 64, 256], entries.Select(e => e.side).Order().ToArray());
        Assert.All(entries, e => Assert.True(e.off + e.len <= bytes.Length, $"条目 {e.side}px 指到了文件外"));
        // 256 那一档内嵌的是 PNG（Vista 起 .ico 允许）：改成位图写法，这个文件会胖出好几倍。
        Assert.Equal(0x89, bytes[entries.Single(e => e.side == 256).off]);   // PNG 签名头 \x89
    }

    [Fact]
    public void 程序集里真嵌着那两条资源而且解得开()
    {
        // 这一条是"资源名对不对"的唯一证人：LogicalName 少写一个字母，运行期只是安静地拿到 null，
        // 界面上退回默认图标，谁都不会报一句错。
        Assert.NotNull(AppBrand.Icon());
        var logo = AppBrand.Logo();
        Assert.NotNull(logo);
        Assert.True(logo!.Width >= 128, $"关于页那张图只有 {logo.Width}px，缩得太狠了");
    }

    [Fact]
    public void 资源名与csproj里的LogicalName逐字相同()
    {
        var csproj = Read("src/ZutWifi/ZutWifi.csproj");
        Assert.Contains($"LogicalName=\"{AppBrand.IconResourceName}\"", csproj);
        Assert.Contains($"LogicalName=\"{AppBrand.LogoResourceName}\"", csproj);
        Assert.Contains("<ApplicationIcon>..\\..\\assets\\app.ico</ApplicationIcon>", csproj);
        // 来源图（各人自己的徽标，assets/logo.png）是生成素材，不是交付内容：它进 exe 只是让同学多下载一截。
        Assert.DoesNotContain("EmbeddedResource Include=\"..\\..\\assets\\logo.png\"", csproj);
        Assert.Contains("assets\\app.ico", csproj);
    }

    [Fact]
    public void 仓库里那份默认图标是中性的品牌绿而不是任何机构的标志()
    {
        // 公开仓库不带校徽：各人要什么图标，往 assets/logo.png（.gitignore 里）放一张再重跑脚本。
        // 所以"仓库里那一份"必须是内置画出来的那个中性标记 —— 用颜色认它，比认文件名可靠。
        var logo = AppBrand.Logo();
        Assert.NotNull(logo);
        var green = 0;
        var opaque = 0;
        for (var y = 0; y < logo!.Height; y += 4)
            for (var x = 0; x < logo.Width; x += 4)
            {
                var p = logo.GetPixel(x, y);
                if (p.A < 128) continue;
                opaque++;
                if (Math.Abs(p.R - 22) <= 12 && Math.Abs(p.G - 128) <= 14 && Math.Abs(p.B - 61) <= 12) green++;
            }
        Assert.True(opaque > 0);
        Assert.True(green * 2 >= opaque, $"默认图标里品牌绿只占 {green}/{opaque}：那不再是内置那个中性标记");
    }

    [Fact]
    public void 图标来源图是本地私有的而且被gitignore挡着()
    {
        var tool = Read("tools/make-icons.py");
        Assert.Contains("draw_default_mark", tool);         // 没有来源图时，脚本自己画一个中性的
        Assert.Contains("assets/logo.png", tool.Replace('\\', '/'));
        Assert.Contains("assets/logo.png", Read(".gitignore"));
        // 校徽属于学校：它不许出现在仓库里（连历史一起算，公开那份是干净首发提交）。
        Assert.DoesNotContain("EmbeddedResource Include=\"..\\..\\assets\\logo.png\"", Read("src/ZutWifi/ZutWifi.csproj"));
    }

    [Fact]
    public void 生成脚本与两份产物都在仓库里()
    {
        // 二进制资产没有生成脚本，就等于"下次只能手工用画图点一遍"。
        var tool = Read("tools/make-icons.py");
        Assert.Contains("app.ico", tool);
        Assert.True(File.Exists(Path.Combine(Root(), "assets/logo-256.png")));
    }

    [Fact]
    public void 托盘仍然用状态色而校徽没有把它顶掉()
    {
        // 绿/黄/灰/红/橙 是"扫一眼就知道状态"的唯一载体。把校徽放进托盘 = 删掉这条功能，
        // 而 16 像素上也看不清校徽 —— 所以这一格不许换成 AppBrand。
        var tray = Read("src/ZutWifi/Shell/TrayApp.cs");
        Assert.Contains("_icon.Icon = IconFactory.For(\"gray\")", tray);
        Assert.Contains("_icon.Icon = IconFactory.For(p.ColorKey)", tray);
        Assert.DoesNotContain("AppBrand", tray);
    }

    [Fact]
    public void 快捷方式图标槽位与shobjidl的顺序对得上()
    {
        // COM 是按 vtable 位置调用的：SetIconLocation 插错一位，它就去调别人的槽位，
        // 而 C# 这边照样编译通过、照样"没报错"。占位方法必须留在原位，只把要用的那一个换成真签名。
        var src = Read("src/ZutWifi/Notify/AumidRegistrar.cs");
        var at = src.IndexOf("interface IShellLinkW", StringComparison.Ordinal);
        Assert.True(at > 0);
        var body = src[at..src.IndexOf('}', at)];
        var slots = Regex.Matches(body, @"void (_?\w+)\(").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(18, slots.Count);                  // IShellLinkW 在 SetPath 之前一共 18 个方法
        Assert.Equal("SetDescription", slots[4]);       // 第 5 槽
        Assert.Equal("SetIconLocation", slots[14]);     // 第 15 槽
        Assert.Equal("SetPath", slots[17]);             // 第 18 槽（shobjidl.h 里它排在最后）
    }

    [Fact]
    public void 关于页既有校徽也写着不是官方软件()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zwbrand" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var form = new MainForm(new NoopCommands(), new SettingsStore(dir), new SecretStore(dir), null);
            Assert.True(form.AboutShowsLogo, "关于页没拿到校徽（资源没打进来或名字不对）");
            Assert.Contains("学生自制", form.AboutTextShown);
            Assert.Contains(AppBrand.UnofficialNotice, form.AboutTextShown);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private sealed class NoopCommands : ILoginCommands
    {
        public Task LoginAsync(CancellationToken ct) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ReprobeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RecoverReloginAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
