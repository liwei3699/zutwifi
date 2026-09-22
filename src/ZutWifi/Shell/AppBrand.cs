namespace ZutWifi.Shell;

/// 校徽只有两个出口：窗口/任务栏那个图标，和「关于」页那张图。
///
/// 两个都从**内嵌资源**按名字取，不去读旁边的文件：单文件 exe 运行时的解压目录是随机的、
/// 而且用完就删，`assets/logo.png` 这种路径只在开发机上好看，交付出去必坏
/// （同一个坑见 AumidRegistrar 里"取 exe 路径不许用 Assembly.Location"那条）。
///
/// 取不到一律返回 null，由调用方退回系统默认图标：一个图标不值得让登录程序起不来，
/// 更不值得让它起不来之后还什么都没说。
public static class AppBrand
{
    /// 与 csproj 里那两条 LogicalName 一一对应（有用例盯着，改一边忘另一边就红）。
    public const string IconResourceName = "ZutWifi.app.ico";
    public const string LogoResourceName = "ZutWifi.logo.png";

    /// 缓存着：窗口重建、切主题、测试反复读，不必每次重新解一份 GDI 对象。
    static Icon? _icon;
    static Bitmap? _logo;
    static readonly object Gate = new();

    public static Icon? Icon()
    {
        lock (Gate) return _icon ??= Read(IconResourceName, s => new Icon(s));
    }

    /// Bitmap 从流构造时 GDI+ 会一直攥着那条流，所以这里克隆一份把流脱开再交出去
    /// （否则流一释放，第一次绘制才炸 —— 那种"只在某些机器上崩"的 bug 最难查）。
    public static Bitmap? Logo()
    {
        lock (Gate) return _logo ??= Read(LogoResourceName, s =>
        {
            using var fromStream = new Bitmap(s);
            return new Bitmap(fromStream);
        });
    }

    static T? Read<T>(string name, Func<Stream, T> parse) where T : class
    {
        try
        {
            using var stream = typeof(AppBrand).Assembly.GetManifestResourceStream(name);
            return stream is null ? null : parse(stream);
        }
        catch (Exception) { return null; }        // 资源没打进来 / GDI+ 解不开：退回默认图标
    }

    /// 「关于」页那一句。校徽往 exe 上一放，同学很容易以为是网络中心官方发的 ——
    /// 这一行的作用是让他知道该找谁：出问题找作者本人，不是学校。
    public const string UnofficialNotice = "学生自制工具，不是网络中心官方软件；出了问题请找作者，别报修学校。";
}
