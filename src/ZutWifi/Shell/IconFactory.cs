using System.Collections.Concurrent;

namespace ZutWifi.Shell;

/// 托盘图标按状态着色。用 GDI 现画，省掉在仓库里放 5 个 .ico 二进制文件。
/// 配色从 StatusPresenter 的 ColorKey 过来，这里只负责"这个颜色长什么样"，不参与判断。
public static class IconFactory
{
    private const int Side = 16;
    private static readonly ConcurrentDictionary<string, Icon> Cache = new();

    public static Color ColorOf(string key) => key switch
    {
        "green" => Color.FromArgb(22, 128, 61),
        "yellow" => Color.FromArgb(202, 158, 12),
        "orange" => Color.FromArgb(194, 106, 24),
        "red" => Color.FromArgb(185, 28, 28),
        _ => Color.FromArgb(120, 120, 120),      // 认不出来的配色一律落回灰色，绝不抛出
    };

    /// 同一个 key 永远返回同一个 Icon 实例：托盘每 60 秒就要换一次图标，
    /// 每次现画会把 GDI 句柄漏到桌面上（一屏 10000 个句柄就见底）。实例由本类持有，调用方不 Dispose。
    public static Icon For(string key) => Cache.GetOrAdd(key, k =>
    {
        using var bmp = new Bitmap(Side, Side);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(ColorOf(k));
            g.FillEllipse(brush, 1, 1, Side - 3, Side - 3);
        }
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();          // Clone 自己 CreateIcon：新句柄独立于 hIcon，生命周期归本类
        }
        finally
        {
            // FromHandle 拿到的 Icon 不拥有句柄，必须自己还：
            // .NET 没公开 Icon 内部那个 DestroyIcon，只能自己 P/Invoke（.NET 文档给的正是这个写法）。
            DestroyIcon(hIcon);
        }
    });

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
