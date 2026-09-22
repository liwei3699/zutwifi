using System.Runtime.InteropServices;

namespace ZutWifi.Notify;

/// 未打包的 WinForms 应用要进通知中心，必须有一个写了 AppUserModelID 的开始菜单快捷方式，
/// 否则 Toast 会被 shell 静默丢弃（不报错、不弹）。这一步就是造那个 .lnk。
public static class AumidRegistrar
{
    public const string Aumid = "ZutWifi.CampusLogin";

    /// 返回 null 表示注册成功；返回非空 = 失败原因，只能走气泡回退。
    /// 快捷方式写失败最多是"通知降级成气泡"，绝不能连累启动，所以这里不抛异常，只把原因带回去。
    public static string? Ensure() =>
        EnsureIn(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                              "Programs", "ZutWifi"));

    /// 目录抽出来是为了能拿临时目录做验证，不去动用户的开始菜单。
    /// 写完用 ReadAumid 自查一遍：通知中心最坏的失效方式是"不报错也不弹"，
    /// 所以对不上就必须报失败，让上层退到看得见的气球上。
    internal static string? EnsureIn(string dir)
    {
        var value = default(PropVariant);
        try
        {
            Directory.CreateDirectory(dir);
            var lnkPath = Path.Combine(dir, "ZutWifi.lnk");

            var link = (IShellLinkW)(object)new ShellLink();
            // 单文件发布里 Assembly.Location 恒为空字符串（IL3000）：拿它兜底会写出一条
            // 目标为空的快捷方式，表现正是我们最难查的那种失效——Ensure() 报成功、Toast 永不弹。
            var target = Environment.ProcessPath
                ?? Path.Combine(System.AppContext.BaseDirectory, "ZutWifi.exe");
            link.SetPath(target);
            link.SetDescription("ZutWifi 校园网自动登录");
            // 图标跟着 exe 走（0 = 第一个图标资源）：通知中心与开始菜单里那一项显示的就是它。
            // 不设这一格，通知上会挂一个通用窗口图标 —— 同学对着校徽才认得出是这门课的那个工具。
            link.SetIconLocation(target, 0);

            var key = PKeyAppUserModelId;
            value = PropVariant.FromString(Aumid);            // VT_LPWSTR，内存由我们持有，用完释放
            var props = (IPropertyStore)link;                 // 微软那篇"用 AppUserModelID 启用桌面 Toast"的样例走的就是这条 QI
            Check(props.SetValue(ref key, ref value), "写入 AppUserModelID");
            Check(props.Commit(), "提交快捷方式属性");
            Check(((IPersistFile)link).Save(lnkPath, true), "保存快捷方式");
            TellShellTheLinkChanged(lnkPath);                 // 不通知一下就要等下次登录，见方法注释

            var back = ReadAumid(lnkPath);
            return back == Aumid ? null
                : $"AUMID 写入后从磁盘读回的是 {(back is null ? "空" : $"“{back}”")}，通知中心不会认这个快捷方式";
        }
        catch (Exception ex) { return ex.Message; }
        finally { value.Free(); }
    }

    /// 写完 .lnk 后提醒 Explorer 刷新那一项：通知中心是从开始菜单里找这个 AUMID 的，
    /// 不通知的话新快捷方式往往要到下次登录（或手动刷新开始菜单）才生效——
    /// 表现就是"Ensure() 成功、Toast 却还是不弹"，比写错键更难查。
    /// 它只是"喊一嗓子"，成功与否都不改变落盘结果，所以绝不能让它把注册判成失败。
    private static void TellShellTheLinkChanged(string lnkPath)
    {
        try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, lnkPath, IntPtr.Zero); }
        catch { /* 最多是 Explorer 晚一点才认这个快捷方式，不值得为此走气泡 */ }
    }

    private const int SHCNE_UPDATEITEM = 0x00002000;   // "这一项内容变了"
    private const uint SHCNF_PATHW = 0x0005;           // 参数一是宽字符路径
    private const uint SHCNF_FLUSH = 0x1000;           // 等 shell 处理完再返回，省掉"写完立刻读还是旧的"

    /// 重新 Load 一个实例再读属性，所以读到的是文件里的内容，不是刚写进内存的那份。
    /// 单测用它做往返验证；真机排障（通知没进通知中心）时也该先看一眼这里。
    internal static string? ReadAumid(string lnkPath)
    {
        var key = PKeyAppUserModelId;
        var value = default(PropVariant);
        try
        {
            var link = (IShellLinkW)(object)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);
            if (((IPropertyStore)link).GetValue(ref key, out value) != 0) return null;
            return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.pointerValue) : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return null;
        }
        finally { value.Clear(); }
    }

    private static void Check(int hr, string doing)
    {
        if (hr != 0) throw new COMException($"{doing}失败 0x{hr:x8}", hr);
    }

    /// 属性键的两个分量单独暴露，只为让单测能拿它跟 propkey.h 的字面值做比对
    /// （见 NotifierTextTests.AppUserModelID属性键与文档逐字一致）。
    /// 为什么必须这么钉：写入端（SetValue）和自查端（ReadAumid）用的是同一个常量，
    /// 键错了 shell 只是不认识这个属性，SetValue/Commit 照样 S_OK、读回照样读到自己写的那一份 ——
    /// 上一版就是这么"往返全绿"地把每一条 Toast 静默丢掉的，往返那条用例结构上抓不到它。
    internal static Guid AppUserModelIdFmtid => PKeyAppUserModelId.fmtid;
    internal static int AppUserModelIdPid => PKeyAppUserModelId.pid;

    /// PKEY_AppUserModel_ID（propkey.h）：fmtid 9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3、pid 5。
    /// 这是全仓唯一一份，别再抄第二处副本 —— 计划文档抄错过一次（9F4C2855-9F7D-4B59-A873-894D85773B1A，
    /// 那个 GUID 在 shell 里不存在，属性写进去没人认，Toast 被静默丢弃）。
    private static PropertyKey PKeyAppUserModelId =>
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);   // PKEY_AppUserModel_ID

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid fmtid, int pid)
    {
        public Guid fmtid = fmtid;
        public int pid = pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
        [FieldOffset(8)] private long _payloadSize;   // 与 pointerValue 重叠，只为把结构顶到 PROPVARIANT 的 16 字节

        public static PropVariant FromString(string s) =>
            new() { vt = 31 /* VT_LPWSTR */, pointerValue = Marshal.StringToCoTaskMemUni(s) };

        /// 自己 alloc 的宽字符串自己释放；shell 还回来的那一半交给 PropVariantClear。
        public void Free()
        {
            if (vt != 31 || pointerValue == IntPtr.Zero) return;
            Marshal.FreeCoTaskMem(pointerValue);
            pointerValue = IntPtr.Zero;
            vt = 0;
        }

        public void Clear()
        {
            if (pointerValue == IntPtr.Zero) return;
            var local = this;
            PropVariantClear(ref local);
            this = local;
        }
    }

    /// 占位方法一个都不能省：vtable 按 Shobjidl.h 里 IShellLink 的声明顺序排，少一个槽位，
    /// SetPath 就会打到隔壁的方法上。IID 必须是 IShellLinkW 的 000214F9-0000-0000-C000-000000000046；
    /// 计划文档里写的 0214a1c0-8ed0-11ce-9ff6-00aa00688b10 谁也不是 —— 它既不是 IShellLinkW 的 IID，
    /// 也不是 IPersistFile 的 IID（那个是下面用的 0000010b-0000-0000-C000-000000000046），
    /// 照抄过去 QI 直接 E_NOINTERFACE（这条已被单测撞出来过）。
    /// 字符串参数一律显式 LPWStr：这里声明的是 W 接口，默认封装会按 ANSI 传，路径就成了乱码。
    [ComImport, Guid("000214f9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void _GetPath(); void _GetIDList(); void _SetIDList(); void _GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void _GetWorkingDirectory(); void _SetWorkingDirectory(); void _GetArguments(); void _SetArguments();
        void _GetHotkey(); void _SetHotkey(); void _GetShowCmd(); void _SetShowCmd();
        void _GetIconLocation();
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszFile, int iIcon);
        void _SetRelativePath(); void _Resolve();
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf91"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
                               [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string pszFileName);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    /// shell 还回来的 PROPVARIANT 由它的任务分配器分配，只能交给 PropVariantClear。
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// 告诉 Explorer "这个路径上的项变了"（SHCNF_PATHW 时参数一是宽字符路径，参数二保留为 0）。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);
}
