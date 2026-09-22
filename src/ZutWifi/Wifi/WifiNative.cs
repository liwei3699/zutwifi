using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace ZutWifi.Wifi;

/// 结构布局的**成员与次序**取自 wlanapi.h（WLAN_CONNECTION_ATTRIBUTES / WLAN_ASSOCIATION_ATTRIBUTES
/// / WLAN_SECURITY_ATTRIBUTES 的 MSDN 页逐字抄录），**尺寸与偏移**由 2026-09-19 真机
/// （Win11 + MediaTek MT7920）对 opcode 7 返回缓冲区的逐字节抓取钉死：
///   @0    isState = 1                       —— WLAN_INTERFACE_STATE.connected 是 1（简报里的 2 是 associate 档）
///   @4    wlanConnectionMode = 0            —— profile，与 netsh"连接模式: 配置文件"吻合
///   @8    strProfileName[256 WCHAR]         —— UTF-16LE 的**配置文件名**，本机恰好也叫 zut-stu
///   @520  wlanAssociationAttributes 的首成员 DOT11_SSID：uSSIDLength=7
///   @524  ucSSID = 7A 75 74 2D 73 74 75     —— 原始空口字节（ASCII/UTF-8/GBK，取决于 AP 广播什么）
///   @576  wlanSignalQuality = 0..100        —— 关联属性里的信号质量（ULONG）
///   合计 dataSize = 604 = 520 + 68（关联属性）+ 16（安全属性）
///
/// 为什么这里不是 Pack=1：C 侧这些字段按自然对齐（4），DOT11_MAC_ADDRESS 只有 6 字节，
/// 它后面那个 ULONG 因此落在填充之后的 48（相对关联属性）上。整块按 Pack=1 声明会得到 602 字节，
/// 与真机返回的 604 差的那 2 字节正是这个填充 —— 尺寸对不上时按结构去读就从这里开始错位。
/// 所以本文件显式写 Pack = 4，并由 Fits/DeclaredBytes 那条等式与单测一起钉住（见 LayoutProblem）。
///
/// 上一版按 @8 取"SSID"，读到的其实是配置文件名：本机两者同名所以真机检查通过，
/// 但"名为 zut-stu 的配置文件连上 hotel-5G"就会在用户没授权的网络上提交账号。
/// 现在授权判定只看 @520 的 DOT11_SSID；profileName 仅用于诊断输出。
internal static class WifiNative
{
    public const int WlanInterfaceStateConnected = 1;       // 真机实测：已连接时 @0 == 1
    public const int Dot11SsidMaxLength = 32;               // DOT11_SSID_MAX_LENGTH
    public const int WlanMaxNameLength = 256;               // WLAN_MAX_NAME_LENGTH，单位是 WCHAR（→ 512 字节）
    public const int WlanOpcodeCurrentConnection = 0x00000007;   // 真机：opcode 0（autoconnection）返回 rc=50
    public const int WlanNotificationConnectionStart = 0x00000001;
    public const int WlanNotificationConnectionEnd = 0x00000002;

    /// wlanSignalQuality 的文档值域是 0..100，0xFFFF 是"这一项测不出来"的那一档
    /// （驱动复位/刚关联时真机给得出来）。测不出来就不拿这一轮去触发登录。
    public const int QualityInvalid = 0xFFFF;

    /// 真机（opcode 7）返回的整块字节数：声明的结构必须与之逐字节对得上。
    public const int ConnectionAttributesBytes = 604;

    // ── 声明的结构：成员次序 = wlanapi.h，Pack = 4 = C 侧自然对齐 ──

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Dot11Ssid
    {
        public uint length;                                            // ULONG uSSIDLength           @0

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = Dot11SsidMaxLength)]
        public byte[] ssid;                                            // UCHAR ucSSID[32]            @4
    }

    /// WLAN_ASSOCIATION_ATTRIBUTES：68 字节（含 dot11Bssid 之后那 2 字节自然对齐填充）。
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WlanAssociationAttributes
    {
        public Dot11Ssid dot11Ssid;                                    // @0   .. 36
        public int bssType;                                            // @36  DOT11_BSS_TYPE
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] bssid;                                           // @40  DOT11_MAC_ADDRESS（6 字节，之后填充到 48）
        public int phyType;                                            // @48  DOT11_PHY_TYPE
        public int phyIndex;                                           // @52  ULONG uDot11PhyIndex
        public uint signalQuality;                                     // @56  WLAN_SIGNAL_QUALITY wlanSignalQuality
        public int rxRate;                                             // @60  ULONG ulRxRate
        public int txRate;                                             // @64  ULONG ulTxRate
    }

    /// WLAN_SECURITY_ATTRIBUTES：16 字节。授权判定一个都不读，但成员得摆在那儿 ——
    /// 上一版用一句 `byte[48]` 占位凑尺寸，尺寸对上了而成员对不上，"这个尾巴装不装得进真结构"
    /// 就再也没人核对过（派单点名的那条）。
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WlanSecurityAttributes
    {
        public int oneXEnabled;                                        // @0  BOOL
        public int authAlgorithm;                                      // @4  DOT11_AUTH_ALGORITHM
        public int cipherAlgorithm;                                    // @8  DOT11_CIPHER_ALGORITHM
        public int encryptionType;                                     // @12 DOT11_ENCRYPTION_TYPE
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    public struct WlanConnectionAttributes
    {
        public int state;                                              // WLAN_INTERFACE_STATE isState      @0
        public int mode;                                               // WLAN_CONNECTION_MODE              @4

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WlanMaxNameLength)]
        public string profileName;                                     // WCHAR strProfileName[256] @8 —— 不是空口 SSID！

        public WlanAssociationAttributes association;                  // wlanAssociationAttributes @520
        public WlanSecurityAttributes security;                        // wlanSecurityAttributes    @588
    }

    // ── 布局常量：一律由声明本身算出来，没有一个是手抄的字面值 ──

    /// 声明结构自己的尺寸 —— 与真机返回的 dataSize 对照的就是它。
    public static readonly int DeclaredBytes = Marshal.SizeOf<WlanConnectionAttributes>();
    public static readonly int AssociationBytes = Marshal.SizeOf<WlanAssociationAttributes>();
    public static readonly int SecurityBytes = Marshal.SizeOf<WlanSecurityAttributes>();
    public static readonly int SsidBytes = Marshal.SizeOf<Dot11Ssid>();

    public static readonly int ProfileNameOffset =
        (int)Marshal.OffsetOf<WlanConnectionAttributes>(nameof(WlanConnectionAttributes.profileName));   // 8
    public static readonly int AssociationOffset =
        (int)Marshal.OffsetOf<WlanConnectionAttributes>(nameof(WlanConnectionAttributes.association));   // 520
    public static readonly int SecurityOffset = AssociationOffset + AssociationBytes;                      // 588

    /// 空口 SSID：关联属性的首成员，长度字段与原始字节各占一段。
    public static readonly int SsidOffset =
        AssociationOffset + (int)Marshal.OffsetOf<WlanAssociationAttributes>(
            nameof(WlanAssociationAttributes.dot11Ssid));                                                  // 520
    public static readonly int SsidBytesOffset =
        SsidOffset + (int)Marshal.OffsetOf<Dot11Ssid>(nameof(Dot11Ssid.ssid));                             // 524

    /// 关联属性里的信号质量（派单里说的"dwQuality"就是这一项，位置在 @576，不在 @12 ——
    /// @12 落在 strProfileName 里，读它是"把配置文件名当别的字段"那一类毛病）。
    public static readonly int QualityOffset =
        AssociationOffset + (int)Marshal.OffsetOf<WlanAssociationAttributes>(
            nameof(WlanAssociationAttributes.signalQuality));                                              // 576

    /// 布局自检：声明的各段必须首尾相接、恰好填满真机那 604 字节，且质量字段在块内。
    /// 返回非 null = 布局与真机返回的尺寸已经对不上（改结构的人必须当场看见，而不是等真机再翻车）。
    public static string? LayoutProblem => Check();

    private static string? Check()
    {
        if (DeclaredBytes != ConnectionAttributesBytes)
            return $"声明的 WLAN_CONNECTION_ATTRIBUTES 是 {DeclaredBytes} 字节，真机 opcode 7 返回 {ConnectionAttributesBytes} 字节";
        if (ProfileNameOffset != 8 || SsidOffset != ProfileNameOffset + WlanMaxNameLength * 2)
            return $"字段次序与 wlanapi.h 不符：profileName@{ProfileNameOffset} 之后应当紧跟关联属性@{SsidOffset}";
        if (SsidBytes != 4 + Dot11SsidMaxLength)
            return "DOT11_SSID 应当是 ULONG uSSIDLength + UCHAR ucSSID[32] = 36 字节";
        if (SsidBytesOffset + Dot11SsidMaxLength > AssociationOffset + AssociationBytes)
            return "ucSSID 那 32 字节的槽位越过了关联属性的边界";
        if (AssociationOffset + AssociationBytes + SecurityBytes != DeclaredBytes)
            return $"关联属性({AssociationBytes}) + 安全属性({SecurityBytes}) 填不满声明的 {DeclaredBytes} 字节";
        if (SecurityOffset + SecurityBytes > DeclaredBytes)
            return "安全属性这一段装不进声明的结构";
        if (QualityOffset + 4 > DeclaredBytes) return "质量字段落在声明结构之外";
        return null;
    }

    /// 一次查询的全部结论。Ssid 只在 state==connected 且质量有效且字节确实解出一个像样的名字时非空；
    /// ProfileName 与授权判定无关，只是给自检/日志看的。
    /// Quality 为 null = 这次返回的缓冲区没长到那一段（不知道），与"读到了 0xFFFF"（无效）是两件事，
    /// 所以用可空而不是 -1 之类的哨兵：0xFFFFFFFF 那种垃圾值本来就会折算成 -1。
    public readonly record struct ConnectionInfo(int State, string Ssid, string ProfileName,
        int? Quality = null)
    {
        /// 只判状态位是不够的：驱动刚复位时 @0 写着"已连接"，而质量那一栏测不出来。
        public bool Connected => State == WlanInterfaceStateConnected && Quality != QualityInvalid;
        public static ConnectionInfo None => new(-1, "", "", null);
    }

    [DllImport("wlanapi.dll")]
    public static extern int WlanOpenHandle(int clientVersion, IntPtr reserved,
        out int negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    public static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    public static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceInfo);

    [DllImport("wlanapi.dll")]
    public static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceId, int opcode,
        IntPtr reserved, out int dataSize, out IntPtr data, out int negotiation);

    [DllImport("wlanapi.dll")]
    public static extern int WlanRegisterNotification(IntPtr clientHandle, uint notifySource, int flushOld,
        WlanNotificationCallback callback, IntPtr context, IntPtr reserved, out uint previous);

    public delegate void WlanNotificationCallback(IntPtr pData, IntPtr context);

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr memory);

    /// 查询缓冲区至少要装到 ucSSID 第一个字节才允许解析。
    /// opcode 给错时 wlanapi 会回一个 4 字节的 DWORD（如通道号），按 604 字节结构去读就是越界。
    /// 真机将来返回更大的结构也判通过：解码器只会读声明过的那一段前缀。
    public static bool Fits(int dataSize) => dataSize >= SsidBytesOffset;

    /// 把 wlanapi 返回的缓冲区整块复制到托管内存后再解析：
    /// 不直接 PtrToStructure，是因为结构按最大尺寸声明，而 dataSize 由驱动说了算。
    public static ConnectionInfo ReadConnection(IntPtr data, int dataSize)
    {
        if (data == IntPtr.Zero || !Fits(dataSize)) return ConnectionInfo.None;
        var buffer = new byte[dataSize];
        Marshal.Copy(data, buffer, 0, dataSize);
        return ReadSsidUtf8(buffer);
    }

    /// SSID 解码器本体： opcode 7 那块缓冲区进来，(状态, 空口 SSID, 配置文件名, 信号质量) 出去。
    /// 名字里的 Utf8 说的是 ucSSID 那一段按 802.11 的原始字节解（UTF-8 优先、退 GBK），
    /// 而不是 @8 那个 UTF-16LE 的配置文件名 —— 授权判定只用前者。
    ///
    /// 纯函数：喂一个构造好的缓冲区就能覆盖全部判定分支，不需要真机。
    /// 第一道门是布局自检：声明与真机返回的尺寸一旦对不上，这里直接拒绝解析（返回 None），
    /// 而不是照着一个错位的结构往下读。
    public static ConnectionInfo ReadSsidUtf8(ReadOnlySpan<byte> buffer)
    {
        if (LayoutProblem is not null) return ConnectionInfo.None;
        if (buffer.Length < 4) return ConnectionInfo.None;
        var state = BinaryPrimitives.ReadInt32LittleEndian(buffer[..4]);
        var profile = ReadProfileName(buffer);
        var quality = buffer.Length >= QualityOffset + 4
            ? (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(QualityOffset, 4))
            : (int?)null;                                        // null = 这次没读到，不是"读到了 0xFFFF"
        // 未连接时 ucSSID 是上一次的残留或全零，一律不认。
        if (state != WlanInterfaceStateConnected) return new ConnectionInfo(state, "", profile, quality);
        if (buffer.Length < SsidBytesOffset) return new ConnectionInfo(state, "", profile, quality);
        // 质量报"测不出来"就整轮判为未连接：只判 @0 的状态位，会把一次驱动刚复位、
        // 什么都没测出来的关联当成事实发出去，紧接着就是一次自动登录。
        if (quality == QualityInvalid) return new ConnectionInfo(state, "", profile, quality);

        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(SsidOffset, 4));
        return new ConnectionInfo(state, DecodeSsid(buffer.Slice(SsidBytesOffset), length), profile, quality);
    }

    /// 仅诊断用：@8 的 WCHAR[256]。授权判定绝不引用它。
    public static string ReadProfileName(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ProfileNameOffset + WlanMaxNameLength * 2) return "";
        var raw = buffer.Slice(ProfileNameOffset, WlanMaxNameLength * 2);
        var chars = Encoding.Unicode.GetString(raw);
        var end = chars.IndexOf('\0');
        return (end < 0 ? chars : chars[..end]).Trim();
    }

    /// GBK（码页 936）：校园网中文 SSID 在信标里就是 GBK 原始字节。
    /// 严格解码：解不动就抛，交由 DecodeSsid 判为"不是名字"，而不是吐一串问号冒充真名。
    private static readonly Encoding? Gbk = ResolveGbk();

    private static Encoding? ResolveGbk()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception)
        {
            return null;      // 极端环境没有码页提供程序：中文 SSID 只能判为不匹配（fail closed）
        }
    }

    /// ucSSID 的解码规则：UTF-8 优先；出现 U+FFFD 说明多半是 GBK，再用 936 解一次；
    /// 两轮都解不出"像个名字"的文本就返回空串——宁可不认，也不凭空造一个网络名去登录。
    public static string DecodeSsid(ReadOnlySpan<byte> payload, uint length)
    {
        if (length is 0 or > Dot11SsidMaxLength) return "";         // 0 = 没连上；>32 = 长度字段不可信
        if (payload.Length < (int)length) return "";                 // 声明比给的多：截断，不猜
        var bytes = payload.Slice(0, (int)length);

        var text = Encoding.UTF8.GetString(bytes);                   // GetString 不抛：非法字节变 U+FFFD
        if (text.Contains('\uFFFD'))
        {
            if (Gbk is null) return "";
            try { text = Gbk.GetString(bytes); }
            catch (DecoderFallbackException) { return ""; }           // 连 GBK 都不认：不是文本
        }

        var name = text.TrimEnd('\0').Trim();                        // 有的驱动会把 32 字节全填上并 NUL 补齐
        return LooksLikeName(name) ? name : "";
    }

    /// 剩下的 U+FFFD 与控制字符（含内嵌 NUL）都说明这不是一个广播出来的名字。
    private static bool LooksLikeName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
            if (c == '\uFFFD' || char.IsControl(c)) return false;
        return true;
    }

    /// UTF-8 优先、失败退 GBK、再失败返回 null：netsh 的输出码页跟着控制台走，同一个解码策略正好适用。
    public static string? DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        var text = Encoding.UTF8.GetString(bytes);
        if (!text.Contains('\uFFFD')) return text;
        if (Gbk is null) return null;
        try { return Gbk.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    // ── WLAN_INTERFACE_INFO_LIST ──
    // { DWORD 数; DWORD 索引; WLAN_INTERFACE_INFO[] } 而每项是
    // { GUID interfaceGuid; WCHAR strDescription[256]; WLAN_INTERFACE_STATE state; } = 532 字节/项
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    public struct WlanInterfaceInfo
    {
        public Guid interfaceGuid;                                     // @0   .. 16

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WlanMaxNameLength)]
        public string description;                                     // @16  .. 528

        public int state;                                              // @528 .. 532
    }

    /// 数组步长由声明本身给出（GUID 的成员全是 ULONG/USHORT/BYTE[]，自然对齐只有 4 ⇒ 532 而不是 536）。
    /// 上一版这里是手写的 `16 + 512 + 4`：结构改了、步长不改，多台无线网卡（软 AP/虚拟卡）时
    /// 第二项之后全是错位，而这在这台只有一张卡的真机上根本看不出来。
    public static readonly int InterfaceInfoBytes = Marshal.SizeOf<WlanInterfaceInfo>();
    public static readonly int InterfaceDescriptionOffset =
        (int)Marshal.OffsetOf<WlanInterfaceInfo>(nameof(WlanInterfaceInfo.description));
    public static readonly int InterfaceStateOffset =
        (int)Marshal.OffsetOf<WlanInterfaceInfo>(nameof(WlanInterfaceInfo.state));

    public readonly record struct InterfaceInfo(Guid Id, string Description, int State)
    {
        public bool Connected => State == WlanInterfaceStateConnected;
    }

    /// 纯函数：从 WlanEnumInterfaces 的返回缓冲区解出接口清单（越界的项直接丢掉，不猜）。
    public static List<InterfaceInfo> ParseInterfaceList(ReadOnlySpan<byte> buffer)
    {
        var list = new List<InterfaceInfo>();
        if (buffer.Length < 8) return list;
        var count = BinaryPrimitives.ReadInt32LittleEndian(buffer[..4]);
        for (var i = 0; i < count; i++)
        {
            var start = 8 + i * InterfaceInfoBytes;
            if (start + InterfaceInfoBytes > buffer.Length) break;
            var name = Encoding.Unicode.GetString(
                buffer.Slice(start + InterfaceDescriptionOffset, WlanMaxNameLength * 2));
            var end = name.IndexOf('\0');
            list.Add(new InterfaceInfo(
                new Guid(buffer.Slice(start, 16)),
                (end < 0 ? name : name[..end]).Trim(),
                BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(start + InterfaceStateOffset, 4))));
        }
        return list;
    }

    /// 一台机器上可能有多个原生 WLAN 接口（虚拟网卡/软 AP）：已连接的那个才是我们要问状态的接口，
    /// 全都没连接时退回第一张——上一版无条件取第一项，选中的可能是张没连的虚拟卡。
    public static Guid PickInterface(IReadOnlyList<InterfaceInfo> interfaces)
    {
        foreach (var f in interfaces) if (f.Connected && f.Id != Guid.Empty) return f.Id;
        foreach (var f in interfaces) if (f.Id != Guid.Empty) return f.Id;
        return Guid.Empty;
    }

    public static List<InterfaceInfo> ReadInterfaces(IntPtr listPtr)
    {
        if (listPtr == IntPtr.Zero) return [];
        var count = Math.Max(0, Marshal.ReadInt32(listPtr));
        var buffer = new byte[8 + count * InterfaceInfoBytes];
        Marshal.Copy(listPtr, buffer, 0, buffer.Length);
        return ParseInterfaceList(buffer);
    }
}
