using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using ZutWifi.Wifi;
namespace ZutWifi.Tests;

/// 空口 SSID 必须是"连上了哪个网络"的唯一依据：读错字段就等于在用户没授权的网络上提交账号。
/// 布局常量全部钉在 2026-09-19 真机（Win11 + MT7920）opcode 7 的 604 字节缓冲区上。
public class WifiSentinelTests
{
    static readonly Guid Iface = Guid.Parse("306f4d22-b66f-4176-8cb3-9a4450b68d4d");

    /// 造一个 WLAN_CONNECTION_ATTRIBUTES 形状的缓冲区：偏移量就是被测对象，写错偏移测试立刻变红。
    static byte[] Buffer(int state, string profileName, byte[] ssidBytes, uint? declaredLength = null)
    {
        var buf = new byte[604];
        BitConverter.TryWriteBytes(buf.AsSpan(0), state);
        BitConverter.TryWriteBytes(buf.AsSpan(4), 0);                              // connectMode = profile
        Encoding.Unicode.GetBytes(profileName).AsSpan().CopyTo(buf.AsSpan(8));     // strProfileName：UTF-16LE
        BitConverter.TryWriteBytes(buf.AsSpan(520), declaredLength ?? (uint)ssidBytes.Length);
        ssidBytes.CopyTo(buf.AsSpan(524));                                         // ucSSID：原始空口字节
        return buf;
    }

    static byte[] Gbk(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936).GetBytes(text);
    }

    // ── 结构布局：偏移 8 是配置文件名，SSID 在 520 ──

    /// 真机（2026-09-19，Win11 + MT7920）opcode 7 返回的缓冲区就是 604 字节：
    /// isState@0 / wlanConnectionMode@4 / strProfileName[256 WCHAR]@8..519 /
    /// wlanAssociationAttributes@520（首成员即 DOT11_SSID：uSSIDLength@520、ucSSID@524）。
    /// 声明的结构必须与之一致，否则按 @8 读到的就是"用了哪个配置文件"而不是"连上了哪个网络"。
    [Fact]
    public void 连接属性结构布局与真机缓冲区同尺寸()
        => Assert.Equal(604, Marshal.SizeOf<WifiNative.WlanConnectionAttributes>());

    [Fact]
    public void SSID字段在配置文件名之后而不是其中()
    {
        var t = typeof(WifiNative.WlanConnectionAttributes);
        Assert.Equal(8, (int)Marshal.OffsetOf(t, "profileName"));                       // WLAN_MAX_NAME_LENGTH=256 WCHAR = 512 字节
        Assert.Equal(520, (int)Marshal.OffsetOf(t, "association"));                     // 520 才是 wlanAssociationAttributes
        Assert.Equal(588, (int)Marshal.OffsetOf(t, "security"));                        // 关联属性 68 字节之后
        Assert.Equal(0, (int)Marshal.OffsetOf(typeof(WifiNative.Dot11Ssid), "length"));  // uSSIDLength
        Assert.Equal(4, (int)Marshal.OffsetOf(typeof(WifiNative.Dot11Ssid), "ssid"));    // ucSSID
        Assert.Equal(36, Marshal.SizeOf<WifiNative.Dot11Ssid>());                        // ULONG + UCHAR[32]

        var a = typeof(WifiNative.WlanAssociationAttributes);
        Assert.Equal(68, Marshal.SizeOf<WifiNative.WlanAssociationAttributes>());        // wlanapi.h 的自然对齐（MAC 之后有 2 字节填充）
        Assert.Equal(40, (int)Marshal.OffsetOf(a, "bssid"));                             // DOT11_MAC_ADDRESS 只有 6 字节
        Assert.Equal(48, (int)Marshal.OffsetOf(a, "phyType"));                           // 所以它后面的 ULONG 落在填充之后
        Assert.Equal(56, (int)Marshal.OffsetOf(a, "signalQuality"));                     // → 整块里的 576
        Assert.Equal(16, Marshal.SizeOf<WifiNative.WlanSecurityAttributes>());
        Assert.True(WifiNative.QualityOffset > WifiNative.SsidBytesOffset);              // 质量在 SSID 之后，不在它里面
    }

    /// 本机配置文件名与 SSID 都叫 zut-stu —— 这正是上一版把 bug 藏起来的原因。
    /// 名字故意让两者不同：一个名为 zut-stu 的配置文件连上 hotel-5G 时，工具绝不能以为还在校园网。
    [Fact]
    public void 配置文件名与空口SSID是两个字段且只有后者参与判定()
    {
        var info = WifiNative.ReadSsidUtf8(Buffer(1, "zut-stu", "hotel-5G"u8.ToArray()));

        Assert.Equal("hotel-5G", info.Ssid);                  // 取的是 @524 的原始字节
        Assert.Equal("zut-stu", info.ProfileName);            // @8 的值只留给诊断输出
        Assert.False(SsidMatcher.IsCampus(info.Ssid, ["zut-stu"]));
        Assert.Equal("hotel-5G", WifiSentinel.BuildAccessPoint(info.Ssid, "10.1.1.1", "AA-BB")!.Ssid);
    }

    /// 真机抓下来的三个 16 字节切片（@0、@16、@520），逐字对照 hexdump。
    [Fact]
    public void 真机抓包字节解出SSID与配置文件名()
    {
        var buf = new byte[604];
        Convert.FromHexString("01000000000000007A00750074002D00").CopyTo(buf.AsSpan(0));
        Convert.FromHexString("73007400750000000000000000000000").CopyTo(buf.AsSpan(16));
        Convert.FromHexString("070000007A75742D7374750000000000").CopyTo(buf.AsSpan(520));

        var info = WifiNative.ReadSsidUtf8(buf);
        Assert.Equal(1, info.State);
        Assert.Equal("zut-stu", info.Ssid);
        Assert.Equal("zut-stu", info.ProfileName);
        Assert.True(info.Connected);
    }

    [Fact]
    public void 未连接时不给SSID但状态照旧可读()
    {
        var info = WifiNative.ReadSsidUtf8(Buffer(0, "zut-stu", "zut-stu"u8.ToArray()));   // 0 = not_ready
        Assert.Equal("", info.Ssid);
        Assert.False(info.Connected);
        Assert.Equal("zut-stu", info.ProfileName);       // 诊断字段不受状态门控，方便排障
    }

    [Fact]
    public void 缓冲区太短就不解析而不是越界读()
    {
        Assert.Equal("", WifiNative.ReadSsidUtf8(new byte[4]).Ssid);      // opcode 给错时 wlanapi 会回 4 字节 DWORD
        Assert.Equal("", WifiNative.ReadSsidUtf8(new byte[523]).Ssid);    // 连 uSSIDLength 都读不全
        Assert.Equal("", WifiNative.ReadSsidUtf8([]).Ssid);
    }

    // ── 真缓冲区必须来自声明结构的 marshalling：手拼偏移等于自证 ──────────────────
    //
    // 上面那几条 Buffer() 是把字节"喂"给解码器的写法：偏移写错了，测试会照着错的偏移拼，
    // 于是永远绿（评审点名的就是这一格：三条"SSID 是 ASCII"里那两条证明不了真解析）。
    // 下面这一组改成：造一个**声明出来的结构实例**，交给 Marshal.StructureToPtr 落进内存，
    // 再把那块内存整块交给解码器 —— 布局由 CLR 的封送器按声明算出来，测试不参与。
    // 而"结构声明得对不对"由三条**独立来源**的字面值钉住：
    //   604 = 2026-09-19 真机 opcode 7 返回的 dataSize；520/524 = 那份 hexdump 里的 DOT11_SSID；
    //   576 = wlanapi.h 里 WLAN_ASSOCIATION_ATTRIBUTES 的 wlanSignalQuality（自然对齐 4）。

    /// 用一个声明好的结构实例产出缓冲区。偏移一律不由测试自己写，交给封送器。
    static byte[] Marshalled(int state, string profileName, byte[] ssidBytes, uint quality,
        WifiNative.WlanSecurityAttributes security = default)
    {
        var payload = new byte[WifiNative.Dot11SsidMaxLength];
        ssidBytes.CopyTo(payload, 0);
        var attrs = new WifiNative.WlanConnectionAttributes
        {
            state = state,
            mode = 0,                                                  // 连接模式 = profile
            profileName = profileName,
            association = new WifiNative.WlanAssociationAttributes
            {
                dot11Ssid = new WifiNative.Dot11Ssid
                {
                    length = (uint)ssidBytes.Length,
                    ssid = payload,
                },
                bssType = 1,                                           // 基础结构网
                bssid = [0x1c, 0xab, 0x34, 0x4d, 0x1c, 0x80],
                phyType = 1,
                phyIndex = 0,
                signalQuality = quality,
                rxRate = 24000,
                txRate = 24000,
            },
            security = security,
        };
        var size = Marshal.SizeOf<WifiNative.WlanConnectionAttributes>();
        var buffer = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(attrs, ptr, fDeleteOld: false);
            Marshal.Copy(ptr, buffer, 0, size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return buffer;
    }

    [Fact]
    public void 声明结构marshal出的真缓冲区解出SSID与信号质量()
    {
        var buf = Marshalled(1, "zut-stu", "hotel-5G"u8.ToArray(), quality: 77);

        // ── 独立证据：真机返回 604 字节，一块不多一块不少 ──
        Assert.Equal(604, buf.Length);
        Assert.Equal(604, Marshal.SizeOf<WifiNative.WlanConnectionAttributes>());

        var info = WifiNative.ReadSsidUtf8(buf);
        Assert.Equal("hotel-5G", info.Ssid);
        Assert.Equal(77, info.Quality);
        Assert.True(info.Connected);
        Assert.False(SsidMatcher.IsCampus(info.Ssid, ["zut-stu"]));

        // ── 三种"旧读法"在同一块缓冲区上必须都得不到答案，否则这条用例是自证的 ──
        // ① 把 @8 的配置文件名当空口 SSID：本机两者同名过，所以真机检查当时通过了，
        //    而这里名字刻意不同 —— 读配置文件名会拿到 zut-stu，判成校园网就是拿账号去敲陌生网络。
        Assert.Equal("zut-stu", WifiNative.ReadProfileName(buf));
        Assert.NotEqual(WifiNative.ReadProfileName(buf), info.Ssid);
        // ② 派单里那句"dwQuality 在 offset 12"：@12 落在 strProfileName[2..3] 里，
        //    读到的是名字字符（'t' 与 '-'），不是 77。真正的质量在 @576（见下面那条布局用例）。
        Assert.NotEqual(77u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12)));
        Assert.Equal(77u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(WifiNative.QualityOffset)));
        // ③ Pack=1 声明整块会少掉 MAC 地址之后那 2 字节自然对齐填充 ⇒ 602，与真机的 604 差一口气。
        //    （评审要的是"布局与 C 头文件逐字节一致"，而 C 侧这里是自然对齐，不是 pack(1)。）
        Assert.NotEqual(602, Marshal.SizeOf<WifiNative.WlanConnectionAttributes>());
    }

    /// 质量为 0xFFFF（驱动报"这一项测不出来"）时整轮判为未连接。
    /// 旧实现只看 @0 的状态位：状态写着已连接就把 ucSSID 当事实发出去，
    /// 于是一个质量都测不出来的关联（真机上多见于驱动刚复位）会照着触发一次自动登录。
    [Fact]
    public void 质量报0xFFFF时判为未连接而不是照读SSID()
    {
        var buf = Marshalled(1, "zut-stu", "zut-stu"u8.ToArray(), WifiNative.QualityInvalid);

        var info = WifiNative.ReadSsidUtf8(buf);
        Assert.Equal(1, info.State);                                  // 状态位照原样留着，诊断要看
        Assert.Equal("", info.Ssid);                                  // 但这一轮不给名字
        Assert.False(info.Connected);                                 // 只判 state 的旧读法在这里会给出 true
        Assert.Null(WifiSentinel.BuildAccessPoint(info.Ssid, "10.1.1.1", "AA-BB"));
    }

    /// 质量有效（0 与 100 都是文档里的合法端点）时不许把它误判成无效。
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(100u)]
    public void 有效质量端点不影响连接判定(uint quality)
    {
        var info = WifiNative.ReadSsidUtf8(Marshalled(1, "zut-stu", "zut-stu"u8.ToArray(), quality));
        Assert.Equal("zut-stu", info.Ssid);
        Assert.Equal((int)quality, info.Quality);
        Assert.True(info.Connected);
    }

    /// 缓冲区没长到质量字段（老驱动只回关联属性的一半）：读得到的名字照给，
    /// 缺的那一项报"不知道"（null），绝不冒充一个 0xFFFF 把连接判没。
    [Fact]
    public void 缓冲区不够长时质量报不知道而不是报无效()
    {
        var buf = Marshalled(1, "zut-stu", "zut-stu"u8.ToArray(), quality: 88);
        var trimmed = buf[..WifiNative.QualityOffset];
        Assert.True(trimmed.Length >= WifiNative.SsidBytesOffset);

        var info = WifiNative.ReadSsidUtf8(trimmed);
        Assert.Equal("zut-stu", info.Ssid);
        Assert.Null(info.Quality);
        Assert.True(info.Connected);
    }

    /// 布局守卫：声明的每一段都要有名有姓，且恰好填满真机那 604 字节。
    /// 上一版那个 48 字节的占位尾巴就是这么把"securityAttributes 装不进真结构"这件事藏住的：
    /// 尺寸对得上，成员对不上，谁也不会再去核对。现在这条等式一旦不成立，
    /// LayoutProblem 就非空，而解码器在布局自检不过时**拒绝解析**（不是照读）。
    [Fact]
    public void 声明布局与真机字节数自洽否则解码器拒绝解析()
    {
        Assert.Null(WifiNative.LayoutProblem);
        Assert.Equal(604, WifiNative.DeclaredBytes);
        Assert.Equal(8, WifiNative.ProfileNameOffset);
        Assert.Equal(520, WifiNative.AssociationOffset);
        Assert.Equal(520, WifiNative.SsidOffset);
        Assert.Equal(524, WifiNative.SsidBytesOffset);
        Assert.Equal(576, WifiNative.QualityOffset);
        Assert.Equal(WifiNative.DeclaredBytes,
            WifiNative.AssociationOffset + WifiNative.AssociationBytes + WifiNative.SecurityBytes);

        // 越界防护照旧：短到连 ucSSID 第一个字节都读不到的一律不解析。
        Assert.False(WifiNative.Fits(WifiNative.SsidBytesOffset - 1));
        Assert.True(WifiNative.Fits(WifiNative.SsidBytesOffset));
        Assert.True(WifiNative.Fits(WifiNative.DeclaredBytes));
        // 真机将来返回更大的结构（新版 SDK 往 security 尾巴上追加字段）也只能读声明过的前缀。
        Assert.True(WifiNative.Fits(608));
    }

    /// 尺寸对上而成员对不上 —— 派单点名的那一格只能在**成员级**上抓到。
    /// 这里在测试里复刻上一版那个"占位尾巴"的形状（Pack=1 + `byte[48]` 凑数）：
    /// 它 marshal 出来同样是 604 字节，所以"声明的结构与真机同尺寸"这一条根本拦不住它。
    /// 拦住它的是成员级的等式：真结构里关联属性 68 字节、安全属性从 588 起 16 字节，
    /// 而那个假形状把 556..604 整段当成一个不透明的 blob —— 质量字段就死在里面，
    /// 于是"质量为 0xFFFF 判未连接"这种要求在旧形状上连写都写不出来。
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    struct OldPlaceholderShape
    {
        public int state;
        public int mode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WifiNative.WlanMaxNameLength)]
        public string profileName;
        public WifiNative.Dot11Ssid associationSsid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] associationAndSecurity;
    }

    [Fact]
    public void 占位尾巴那个假形状尺寸对得上但成员对不上()
    {
        Assert.Equal(604, Marshal.SizeOf<OldPlaceholderShape>());          // 光看总尺寸：抓不到它
        Assert.Equal(556, (int)Marshal.OffsetOf<OldPlaceholderShape>("associationAndSecurity"));
        Assert.NotEqual(556, WifiNative.SecurityOffset);                   // 真结构的安全属性在 588
        Assert.Equal(588, WifiNative.SecurityOffset);
        Assert.Equal(68, WifiNative.AssociationBytes);
        // 假形状里"关联属性"只有 36 字节（只声明到 dot11Ssid），差的 32 字节全被塞进那个 blob。
        Assert.NotEqual(36, WifiNative.AssociationBytes);
        // 短到没长过质量字段的缓冲区（旧 blob 那种 602/604 里前 576 字节的部分）：
        // 解码器报"质量不知道"，而不是从别处猜一个数出来。
        var partial = Marshalled(1, "zut-stu", "zut-stu"u8.ToArray(), quality: 90)[..570];
        var info = WifiNative.ReadSsidUtf8(partial);
        Assert.Null(info.Quality);
        Assert.Equal("zut-stu", info.Ssid);
    }

    /// 结构与封送一致性反查：把真缓冲区读回来必须还能按结构解释，
    /// 且 SSID 那 32 字节的槽位就是 ucSSID —— 这条把"声明的结构与解码偏移各自漂移"钉死。
    [Fact]
    public void 缓冲区按声明结构读回时各字段与解码结果一致()
    {
        var buf = Marshalled(1, "zut-stu", "hotel-5G"u8.ToArray(), quality: 77);
        var ptr = Marshal.AllocHGlobal(buf.Length);
        try
        {
            Marshal.Copy(buf, 0, ptr, buf.Length);
            var back = Marshal.PtrToStructure<WifiNative.WlanConnectionAttributes>(ptr);
            Assert.Equal("zut-stu", back.profileName);
            Assert.Equal(8u, back.association.dot11Ssid.length);
            Assert.Equal("hotel-5G", Encoding.UTF8.GetString(back.association.dot11Ssid.ssid[..8]));
            Assert.Equal(77u, back.association.signalQuality);

            var info = WifiNative.ReadSsidUtf8(buf);
            Assert.Equal(back.association.dot11Ssid.length,
                (uint)info.Ssid.Length);                              // 解码用的长度与结构里那一个同源
            Assert.Equal((int)back.association.signalQuality, info.Quality);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // ── SSID 解码：UTF-8 优先，U+FFFD 再走 GBK，剩下的都算"不是名字" ──

    /// 三条 ASCII 用例都要走一遍"声明的结构 → 封送 → 解码"，不能只把手拼的字节喂给解码器：
    /// 那样偏移写错也照样绿（评审点名的正是这一格）。
    [Theory]
    [InlineData("zut-stu")]
    [InlineData("ZUT-STU-5G")]
    [InlineData("Zut Wifi 3F")]
    public void ASCII空口SSID按UTF8解码(string ssid)
    {
        var bytes = Encoding.UTF8.GetBytes(ssid);
        Assert.Equal(ssid, WifiNative.DecodeSsid(bytes, (uint)bytes.Length));
        Assert.Equal(ssid, WifiNative.ReadSsidUtf8(Marshalled(1, "任意配置文件名", bytes, quality: 50)).Ssid);
        Assert.Equal(ssid, WifiNative.ReadSsidUtf8(Buffer(1, "任意配置文件名", bytes)).Ssid);
    }

    /// 校园网中文 SSID 在信标里是 GBK 原始字节（D0A3D4B0CDF8 = 校园网），UTF-8 解出来必带 U+FFFD。
    [Fact]
    public void GBK中文SSID解成中文而不是问号()
    {
        var bytes = Gbk("校园网");
        Assert.Equal("D0A3D4B0CDF8", Convert.ToHexString(bytes));                // 先钉住输入本身
        var decoded = WifiNative.DecodeSsid(bytes, (uint)bytes.Length);
        Assert.Equal("校园网", decoded);
        Assert.DoesNotContain('?', decoded);
        Assert.True(SsidMatcher.IsCampus(decoded, ["校园网"]));
        Assert.False(SsidMatcher.IsCampus(decoded, ["zut-stu"]));
    }

    /// UTF-8 广播的中文 SSID 走第一路，不该被 GBK 再解一遍解坏。
    [Fact]
    public void UTF8中文SSID原样解码()
    {
        var bytes = Encoding.UTF8.GetBytes("校园网");
        Assert.Equal("E6A0A1E59BADE7BD91", Convert.ToHexString(bytes));
        Assert.Equal("校园网", WifiNative.DecodeSsid(bytes, (uint)bytes.Length));
    }

    [Fact]
    public void 零长度SSID得到空串并让接入点为空()
    {
        Assert.Equal("", WifiNative.DecodeSsid(new byte[32], 0));
        var info = WifiNative.ReadSsidUtf8(Buffer(1, "zut-stu", new byte[8], declaredLength: 0));
        Assert.Equal("", info.Ssid);
        Assert.Null(WifiSentinel.BuildAccessPoint(info.Ssid, "10.1.1.1", "AA-BB"));
    }

    [Fact]
    public void 声明长度超出载荷或超出协议上限都算截断()
    {
        Assert.Equal("", WifiNative.DecodeSsid([0x7A, 0x75, 0x74], 7));         // 说 7 个字节只给了 3 个
        Assert.Equal("", WifiNative.DecodeSsid(new byte[32], 33));               // DOT11_SSID_MAX_LENGTH = 32
        Assert.Equal("", WifiNative.DecodeSsid(new byte[32], uint.MaxValue));    // 垃圾长度字段
        Assert.Null(WifiSentinel.BuildAccessPoint(WifiNative.DecodeSsid([0x7A, 0x75, 0x74], 7), "10.1.1.1", "AA-BB"));
    }

    /// 不是文本的字节序列不能被"洗"成一个看起来像的名字：宁可报未连接，也不去陌生网络提交账号。
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x01, 0x80, 0x81, 0xC0, 0xC1 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 })]
    [InlineData(new byte[] { 0x80, 0x81, 0x82, 0x83 })]
    public void 不是文本的字节序列不能冒充zut_stu(byte[] garbage)
    {
        var decoded = WifiNative.DecodeSsid(garbage, (uint)garbage.Length);
        Assert.NotEqual("zut-stu", decoded);
        Assert.False(SsidMatcher.IsCampus(decoded, ["zut-stu"]));
        Assert.Null(WifiSentinel.BuildAccessPoint(decoded, "10.1.1.1", "AA-BB"));
    }

    [Fact]
    public void 尾部NUL补齐的SSID裁掉后仍能匹配()
    {
        var padded = new byte[32];
        "zut-stu"u8.CopyTo(padded.AsSpan());
        Assert.Equal("zut-stu", WifiNative.DecodeSsid(padded, 32));
    }

    // ── 降级链路：netsh 解析 ──

    [Fact]
    public void 中文netsh输出解析出SSID与接口GUID()
    {
        const string output = """
            系统上有 1 个接口:

                名称                   : WLAN
                说明            : MediaTek Wi-Fi 6 MT7920 Wireless LAN Card
                GUID                   : 306f4d22-b66f-4176-8cb3-9a4450b68d4d
                物理地址       : 02:a1:b2:c3:d4:e5
                状态                  : 已连接
                SSID                   : zut-stu
                AP BSSID               : 1c:ab:34:4d:1c:80
                配置文件               : zut-stu
            """;
        var (ssid, guid) = WifiSentinel.ParseNetshInterfaces(output);
        Assert.Equal("zut-stu", ssid);
        Assert.Equal(Iface, guid);
    }

    [Fact]
    public void 英文netsh输出解析出SSID与接口GUID()
    {
        const string output = """
            There is 1 interface on the system:

                Name                   : Wi-Fi 2
                Description            : Intel(R) Wi-Fi 6E AX211 160MHz
                GUID                   : 306f4d22-b66f-4176-8cb3-9a4450b68d4d
                Physical address       : 02:a1:b2:c3:d4:e5
                State                  : connected
                SSID                   : zut-stu
                AP BSSID               : 1c:ab:34:4d:1c:80
                Profile                  : zut-stu
            """;
        var (ssid, guid) = WifiSentinel.ParseNetshInterfaces(output);
        Assert.Equal("zut-stu", ssid);
        Assert.Equal(Iface, guid);
    }

    /// 断开时 netsh 根本不打 SSID 行；"AP BSSID"、"配置文件"这些行也不能顶替。
    [Theory]
    [InlineData("""

            There is 1 interface on the system:

                Name                   : Wi-Fi
                Description            : MediaTek Wi-Fi 6 MT7920 Wireless LAN Card
                GUID                   : 306f4d22-b66f-4176-8cb3-9a4450b68d4d
                Physical address       : 02:a1:b2:c3:d4:e5
                State                  : disconnected
                Radio disabled         : false
        """)]
    [InlineData("SSID 前什么也没有\nBSSID : aa:bb:cc:dd:ee:ff\n")]
    [InlineData("")]
    public void netsh里没有SSID行就等于没连上(string output)
        => Assert.Null(WifiSentinel.ParseNetshInterfaces(output).Ssid);

    /// SSID 里带冒号时只能按第一个冒号切标签与值，否则名字被截断就永远匹配不上。
    [Fact]
    public void netsh的SSID含冒号时不被截断()
        => Assert.Equal("a:b:c", WifiSentinel.ParseNetshInterfaces("    SSID  : a:b:c\n").Ssid);

    // ── 地址归属：必须挂在查询到的那张接口上 ──

    [Fact]
    public void 地址优先归属到查询到的接口GUID()
    {
        var nics = new[]
        {
            new WifiSentinel.WirelessNic("{010961EF-B1C6-11F1-8654-02A1B2C3D4E6}", "169.254.1.1", "02A1B2C3D4E7"),  // 热点/Wi-Fi Direct，先枚举到
            new WifiSentinel.WirelessNic("{306F4D22-B66F-4176-8CB3-9A4450B68D4D}", "10.133.126.113", "02-A1-B2-C3-D4-E5"),
        };
        Assert.Equal<(string?, string?)>(("10.133.126.113", "02-A1-B2-C3-D4-E5"), WifiSentinel.PickAddress(nics, Iface));
    }

    /// GUID 命中那张卡没地址时，宁可不报，也不把邻居（热点）的地址当成校园网 IP 去登录。
    [Fact]
    public void 命中的接口没有IPv4时不借别人的地址()
    {
        var nics = new[]
        {
            new WifiSentinel.WirelessNic("{010961EF-B1C6-11F1-8654-02A1B2C3D4E6}", "169.254.1.1", "02A1B2C3D4E7"),
            new WifiSentinel.WirelessNic("{306F4D22-B66F-4176-8CB3-9A4450B68D4D}", null, "02A1B2C3D4E5"),
        };
        var (ip, _) = WifiSentinel.PickAddress(nics, Iface);
        Assert.Null(ip);
        Assert.Null(WifiSentinel.BuildAccessPoint("zut-stu", ip, "AA-BB"));
    }

    [Fact]
    public void 没有GUID匹配时才退回第一张up无线网卡()
    {
        var nics = new[]
        {
            new WifiSentinel.WirelessNic("{11111111-1111-1111-1111-111111111111}", "10.1.1.1", "AABB"),
            new WifiSentinel.WirelessNic("{22222222-2222-2222-2222-222222222222}", "10.2.2.2", "CCDD"),
        };
        Assert.Equal<(string?, string?)>(("10.1.1.1", "AABB"), WifiSentinel.PickAddress(nics, Iface));
        Assert.Equal<(string?, string?)>(("10.1.1.1", "AABB"), WifiSentinel.PickAddress(nics, null));   // wlanapi 不可用（netsh 降级）时的老行为
        Assert.Equal<(string?, string?)>((null, null), WifiSentinel.PickAddress([], Iface));
    }

    /// 上一版无条件取枚举的第一项：本机只有 1 张，所以没暴露。地址要绑 GUID，接口本身也得挑对。
    [Fact]
    public void 接口枚举优先挑已连接的那张()
    {
        var direct = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var buf = new byte[8 + 2 * WifiNative.InterfaceInfoBytes];
        BitConverter.TryWriteBytes(buf.AsSpan(0), 2);
        WriteInterface(buf, 0, direct, "Microsoft Wi-Fi Direct Virtual Adapter", 0);       // 没连接，却被枚举在第一
        WriteInterface(buf, 1, Iface, "MediaTek Wi-Fi 6 MT7920 Wireless LAN Card", 1);

        var interfaces = WifiNative.ParseInterfaceList(buf);
        Assert.Equal(2, interfaces.Count);
        Assert.Equal("MediaTek Wi-Fi 6 MT7920 Wireless LAN Card", interfaces[1].Description);
        Assert.Equal(Iface, WifiNative.PickInterface(interfaces));
        Assert.Equal(direct, WifiNative.PickInterface(
            interfaces.Select(f => f with { State = 0 }).ToList()));                        // 都没连接才退回第一张
        Assert.Equal(Guid.Empty, WifiNative.PickInterface([]));
    }

    static void WriteInterface(byte[] buf, int index, Guid id, string description, int state)
    {
        var start = 8 + index * WifiNative.InterfaceInfoBytes;
        id.ToByteArray().CopyTo(buf.AsSpan(start));
        Encoding.Unicode.GetBytes(description).AsSpan().CopyTo(buf.AsSpan(start + 16));
        BitConverter.TryWriteBytes(buf.AsSpan(start + 16 + 512), state);
    }

    /// 枚举清单的步长必须由声明的结构本身算出来：两张卡时第二项起点是 8+532，
    /// 手写常数与声明各改一头就会错位，而真机那台只有一张卡，这一格只能构造出来。
    /// 上面那个 WriteInterface 故意用字面 16 / 512 写：那三条偏移量与步长必须是同一个答案。
    [Fact]
    public void 接口清单步长由声明结构算出且两张卡都解得出来()
    {
        Assert.Equal(532, WifiNative.InterfaceInfoBytes);       // WLAN_INTERFACE_INFO：GUID 的自然对齐只有 4
        Assert.Equal(16, WifiNative.InterfaceDescriptionOffset);
        Assert.Equal(528, WifiNative.InterfaceStateOffset);

        var direct = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var buf = new byte[8 + 2 * WifiNative.InterfaceInfoBytes];
        BitConverter.TryWriteBytes(buf.AsSpan(0), 2);
        WriteInterface(buf, 0, direct, "Microsoft Wi-Fi Direct Virtual Adapter", 0);
        WriteInterface(buf, 1, Iface, "MediaTek Wi-Fi 6 MT7920 Wireless LAN Card", 1);

        var list = WifiNative.ParseInterfaceList(buf);
        Assert.Equal(2, list.Count);
        Assert.Equal(direct, list[0].Id);
        Assert.Equal("Microsoft Wi-Fi Direct Virtual Adapter", list[0].Description);
        Assert.Equal(Iface, list[1].Id);                        // 步长错一点这里就是垃圾
        Assert.Equal("MediaTek Wi-Fi 6 MT7920 Wireless LAN Card", list[1].Description);
        Assert.Equal(1, list[1].State);
    }

    [Theory]
    [InlineData("{306F4D22-B66F-4176-8CB3-9A4450B68D4D}", true)]     // Windows 的 NetworkInterface.Id 形状
    [InlineData("306f4d22-b66f-4176-8cb3-9a4450b68d4d", true)]
    [InlineData("{010961EF-B1C6-11F1-8654-02A1B2C3D4E6}", false)]    // 同机的热点虚拟网卡
    [InlineData("WLAN", false)]
    [InlineData("", false)]
    public void 接口Id比对容忍花括号与大小写(string nicId, bool expected)
        => Assert.Equal(expected, WifiSentinel.InterfaceIdMatches(nicId, Iface));

    // ── 生命周期与降级链路（真机上无法制造 wlanapi 故障，只能钉成单测） ──

    private sealed class FakeApi(int openFailures = 0) : IWlanApi
    {
        private int _open, _read, _dispose, _badReads;

        // 计数器一律 Interlocked：新用例让真定时器在线程池线程上跑心跳，
        // 测试线程同时在读这些值，普通 ++ 会丢数。
        public int OpenCount => Volatile.Read(ref _open);
        public int ReadCount => Volatile.Read(ref _read);
        public int DisposeCount => Volatile.Read(ref _dispose);

        /// 读到了不属于本接口的 GUID。原来这里是 Assert.Equal —— 但断言抛在定时器线程上
        /// 会被 RefreshOnceSafely 的 catch 吞掉，故障反而消失，所以改成计数由测试自己判。
        public int BadReads => Volatile.Read(ref _badReads);

        /// 前 N 次 TryOpen 失败。可在用例中途改：让"定时器自己那一拍"恰好赶上通路复活。
        public int OpenFailures = openFailures;
        public bool ReadAvailable = true;
        public WlanOpenResult OpenResult = WlanOpenResult.Ready;
        public WifiNative.ConnectionInfo Info = new(WifiNative.WlanInterfaceStateConnected, "zut-stu", "zut-stu");

        public WlanOpenResult TryOpen(out Guid interfaceId)
        {
            if (Interlocked.Increment(ref _open) <= OpenFailures)
            {
                interfaceId = Guid.Empty;
                return WlanOpenResult.Unavailable;
            }
            interfaceId = Iface;
            return OpenResult;
        }

        public bool TryRead(Guid id, out WifiNative.ConnectionInfo info)
        {
            if (id != Iface) Interlocked.Increment(ref _badReads);
            Interlocked.Increment(ref _read);
            info = ReadAvailable ? Info : default;
            return ReadAvailable;
        }

        public void Dispose() => Interlocked.Increment(ref _dispose);
    }

    /// 真定时器自己跑，不许任何人手工驱动方法。上一版的降级链路就是这么"绿"的：
    /// 重开句柄只活在 PollOnce() 里，而定时器装的却是只读的那一半。
    static bool WaitUntil(Func<bool> condition, int timeoutMs) => SpinWait.SpinUntil(condition, timeoutMs);

    [Fact]
    public void 重复Start不再开第二个句柄且Dispose后彻底安静()
    {
        var api = new FakeApi();
        var netshCalls = 0;
        var s = new WifiSentinel(api, () => { netshCalls++; return (null, null); }, _ => ("10.1.1.1", "AA-BB"));
        s.Start();
        s.Start();
        s.Start();
        Assert.Equal(1, api.OpenCount);                    // 第二、三刀绝不能再开一个句柄出去
        Assert.Equal("zut-stu", s.Current!.Ssid);
        Assert.Equal(0, netshCalls);                       // native 活着就不该去 spawn 进程

        s.Dispose();
        s.Dispose();                                       // 幂等
        Assert.Equal(1, api.DisposeCount);
        Assert.Null(s.Current);                            // 释放即归零，不给下游留一个假的"已连接"

        var (readsBefore, netshBefore) = (api.ReadCount, netshCalls);
        s.Start();                                         // 已释放实例上再 Start 必须是 no-op
        s.RunTick();                                       // 在途的定时器回调也不许再碰通路
        Assert.Equal(1, api.OpenCount);
        Assert.Equal(readsBefore, api.ReadCount);
        Assert.Equal(netshBefore, netshCalls);
    }

    /// 上一版的死路：WlanOpenHandle 一失败 _client 就永远是 0，Read() 永远 null，轮询形同虚设。
    /// 手工驱动的入口只有一个 RunTick()，它调的就是装进 Timer 的那个委托本体
    /// （真定时器那一拍另有上面的用例负责）。
    [Fact]
    public void 句柄打不开时心跳那一拍继续重试且复活后不再跑netsh()
    {
        var api = new FakeApi(openFailures: 2);
        var netshCalls = 0;
        var s = new WifiSentinel(api,
            () => { netshCalls++; return ("zut-stu", (Guid?)Iface); },
            _ => ("10.133.126.113", "02-A1-B2-C3-D4-E5"));

        s.Start();                                   // 第一刀 TryOpen 失败 ⇒ 必须装轮询器
        Assert.True(s.PollArmed);
        Assert.Equal(1, api.OpenCount);
        Assert.Equal("zut-stu", s.Current!.Ssid);    // netsh 顶上，不是永远 null
        Assert.Equal(1, netshCalls);

        s.RunTick();                                 // 还是打不开 ⇒ 第二刀重试
        Assert.Equal(2, api.OpenCount);
        Assert.Equal(2, netshCalls);

        s.RunTick();                                 // 第三刀成功 ⇒ 回到 wlanapi，netsh 计数停住
        Assert.Equal(3, api.OpenCount);
        Assert.Equal(2, netshCalls);
        Assert.Equal("zut-stu", s.Current!.Ssid);
        Assert.True(api.ReadCount > 0);

        s.RunTick();
        Assert.Equal(3, api.OpenCount);              // 已就绪就别再开
        s.Dispose();
    }

    /// 诊断出口存在的意义就是让人核对：授权用的是空口 SSID，配置文件名只是另写出来的一个字段，
    /// 质量是第三个字段（读不到报 null，不拿 0xFFFF 冒充"未连接"之外的任何东西）。
    [Fact]
    public void 诊断出口把SSID与配置文件名分列()
    {
        var api = new FakeApi();
        api.Info = api.Info with { Ssid = "hotel-5G", ProfileName = "zut-stu", Quality = 42 };
        var s = new WifiSentinel(api, () => (null, null), _ => ("10.1.1.1", "AA-BB"));
        s.Start();
        var detail = Assert.NotNull(s.ReadNativeDetail());
        Assert.Equal("hotel-5G", detail.Ssid);
        Assert.Equal("zut-stu", detail.ProfileName);
        Assert.Equal(Iface, detail.InterfaceId);
        Assert.Equal(42, detail.Quality);
        Assert.Equal("hotel-5G", s.Current!.Ssid);            // Current 用的也是空口 SSID
        s.Dispose();
        Assert.Null(s.ReadNativeDetail());
    }

    [Fact]
    public void 通知没注册上也要留着轮询()
    {
        var api = new FakeApi { OpenResult = WlanOpenResult.PollWithoutNotifications };
        var s = new WifiSentinel(api, () => throw new InvalidOperationException("native 可用时不该跑 netsh"),
            _ => ("10.1.1.1", "AA-BB"));
        s.Start();
        Assert.True(s.PollArmed);                          // 只有事件驱动成功才允许省掉定时器
        Assert.Equal("zut-stu", s.Current!.Ssid);
        s.Dispose();
    }

    [Fact]
    public void 查询中途失败就退回netsh且下一轮重新开句柄()
    {
        var api = new FakeApi { ReadAvailable = false };
        var netshCalls = 0;
        var s = new WifiSentinel(api,
            () => { netshCalls++; return ("hotel-5G", (Guid?)null); },
            id => { Assert.Null(id); return ("10.9.9.9", "11-22-33-44-55-66"); });   // 降级时没有 GUID，只能退回首张
        s.Start();
        Assert.Equal("hotel-5G", s.Current!.Ssid);
        Assert.Equal(1, netshCalls);
        Assert.False(s.NativeLive);                  // 通路标记为坏，下一轮心跳会重开
        s.RunTick();
        Assert.Equal(2, api.OpenCount);
        Assert.Equal(2, netshCalls);
        s.Dispose();
    }

    /// 降级链路的任何一环抛异常都只能得到"读不到"，不能把托盘进程带崩。
    [Fact]
    public void 降级链路抛异常时不向外传播()
    {
        var s = new WifiSentinel(new FakeApi(),
            () => throw new IOException("netsh 被占用"),
            _ => throw new PlatformNotSupportedException("枚举网卡都能炸"));
        var before = s.Current;
        s.RunTick();                                 // 不得抛出
        Assert.Same(before, s.Current);
        Assert.Null(s.Current);
        s.Dispose();
    }

    /// IWlanApi 的"永不抛出"以前只是一句愿望：接口没写，心跳的重开那一半也没兜。
    /// TryOpen 的调用点落在 Start()/TickOnce() 的 try 之外，而那一拍跑在定时器线程上没人接：
    /// 一个违约的实现（WlanApi 将来新增一步 P/Invoke、或换一份实现）就是"托盘从此不再刷新"，
    /// 连一行痕迹都没有。上面那条用例只覆盖了 netsh 与网卡枚举两环，wlanapi 这一环没人试。
    [Fact]
    public void TryOpen违约抛异常时不外抛并照常降级到netsh()
    {
        var api = new ThrowingOpenApi();
        var netshCalls = 0;
        var s = new WifiSentinel(api,
            () => { Interlocked.Increment(ref netshCalls); return ("zut-stu", (Guid?)Iface); },
            _ => ("10.133.126.113", "02-A1-B2-C3-D4-E5"),
            pollInterval: TimeSpan.FromMinutes(5));

        s.Start();                                   // Start 自己就直接调 TryOpen：违约就是从这里抛出来的
        Assert.Equal(1, api.OpenCount);
        Assert.False(s.NativeLive);                  // 只能按"通路不可用"处理
        Assert.Equal("zut-stu", s.Current!.Ssid);    // 同一拍照样落到 netsh，不是读不到
        Assert.Equal(1, Volatile.Read(ref netshCalls));
        Assert.True(s.PollArmed);                    // 留在快速轮询档，下一拍继续重试

        s.RunTick();                                 // 定时器那一拍同样不许把异常带到线程池上
        Assert.Equal(2, api.OpenCount);
        Assert.Equal(2, Volatile.Read(ref netshCalls));
        Assert.Equal(2, api.DisposeCount);           // 违约可能留下半开句柄 ⇒ 每次失败的 TryOpen 当场还回去
        s.Dispose();
        Assert.Equal(3, api.DisposeCount);
    }

    /// 违反"永不抛出"契约的通路实现：TryOpen 直接抛。真机实现 WlanApi 自己有 catch-all，
    /// 这个替身钉的是"就算实现说话不算数，状态源也不许把异常漏给调用方"。
    private sealed class ThrowingOpenApi : IWlanApi
    {
        private int _open, _dispose;
        public int OpenCount => Volatile.Read(ref _open);
        public int DisposeCount => Volatile.Read(ref _dispose);

        public WlanOpenResult TryOpen(out Guid interfaceId)
        {
            Interlocked.Increment(ref _open);
            interfaceId = Guid.Empty;
            throw new InvalidOperationException("wlanapi 句柄开了一半就炸了");
        }

        public bool TryRead(Guid id, out WifiNative.ConnectionInfo info)
        {
            info = WifiNative.ConnectionInfo.None;
            return false;
        }

        public void Dispose() => Interlocked.Increment(ref _dispose);
    }

    // ── 生产心跳的接线：定时器那一拍必须自己重开句柄 ──────────────────────────────

    /// 上一版的死路（评审原话）：重开句柄只活在 PollOnce() 里，而定时器装的是"只读"那一半，
    /// 于是 WlanOpenHandle 失败后生产上永远不再重试，句柄打不开就一辈子停在 netsh 上。
    /// 这一条刻意不手工驱动任何方法：只 Start()，然后等真定时器自己那一拍把句柄重试出来。
    [Fact]
    public void 真定时器自己那一拍会重开句柄并在复活后回到wlanapi()
    {
        var api = new FakeApi { OpenFailures = int.MaxValue };      // wlanapi 一直打不开
        var netshCalls = 0;
        var s = new WifiSentinel(api,
            () => { Interlocked.Increment(ref netshCalls); return ("zut-stu", (Guid?)Iface); },
            _ => ("10.133.126.113", "02-A1-B2-C3-D4-E5"),
            pollInterval: TimeSpan.FromMilliseconds(50));

        s.Start();
        Assert.True(WaitUntil(() => api.OpenCount >= 4, 2000),
            $"定时器那一拍没打算重开句柄（一共只开了 {api.OpenCount} 次）—— 降级链路仍是死的");
        Assert.True(Volatile.Read(ref netshCalls) >= 3);            // 每一拍都有 netsh 顶上
        Assert.Equal("zut-stu", s.Current!.Ssid);
        Assert.Equal(0, api.BadReads);

        api.OpenFailures = api.OpenCount;                           // 从下一拍起 wlanapi 活了
        Assert.True(WaitUntil(() => api.ReadCount > 0, 2000), "复活后同一拍必须改读 wlanapi");
        Thread.Sleep(100);                                          // 让在途的心跳都落定
        var afterRevival = Volatile.Read(ref netshCalls);
        Thread.Sleep(150);                                          // 好几个 50 ms 周期
        Assert.Equal(afterRevival, Volatile.Read(ref netshCalls));   // 复活之后不再 spawn 进程
        Assert.Equal(TimeSpan.FromSeconds(60), s.TickPeriod);       // 心跳降到看门狗档，不再 5 秒一读
        s.Dispose();
    }

    /// 事件驱动模式下上一版根本没有定时器：通知只在连接状态变化时投递，适配器被重置
    /// （或服务被重启）之后不再有通知，程序就永久瞎了。规则：留一条 60 秒看门狗，
    /// 它跑的是同一条"重开 + 读"通路。
    [Fact]
    public void 事件驱动模式下也留着60秒看门狗心跳()
    {
        var api = new FakeApi();                                    // Ready：句柄 + 通知都成功
        var s = new WifiSentinel(api, () => ("hotel-5G", (Guid?)null), _ => ("10.1.1.1", "AA-BB"));
        s.Start();
        Assert.True(s.PollArmed, "通知模式下没有任何心跳 ⇒ 适配器一重置就永久失明");
        Assert.Equal(TimeSpan.FromSeconds(60), s.TickPeriod);
        Assert.Equal(1, api.OpenCount);                             // 看门狗只是装着，不额外开句柄
        Assert.Equal("zut-stu", s.Current!.Ssid);                   // native 活着就没 netsh 的事

        // 通知还在、但查询中途坏了（等价于适配器被重置）：下一拍必须重开句柄并回到 wlanapi。
        api.ReadAvailable = false;
        s.RunTick();                                                // 读之前状态还是好的 ⇒ 这一拍只降级不重开
        Assert.Equal(1, api.OpenCount);
        api.ReadAvailable = true;
        s.RunTick();
        Assert.Equal(2, api.OpenCount);                             // 重开句柄
        Assert.Equal(0, api.BadReads);
        s.Dispose();
        Assert.Equal(1, api.DisposeCount);                          // 看门狗跟着 Dispose 一起收掉
    }

    /// 心跳间隔跟着通路状态走：wlanapi 活着用看门狗档，掉了立刻回到快速轮询档。
    [Fact]
    public void 心跳间隔在降级与恢复之间切换()
    {
        var api = new FakeApi { OpenFailures = 1 };                 // Start 失败 → 装快速轮询档
        var s = new WifiSentinel(api, () => ("zut-stu", (Guid?)null),
            _ => ("10.1.1.1", "AA-BB"),
            pollInterval: TimeSpan.FromMinutes(1), watchdogInterval: TimeSpan.FromMinutes(3));
        s.Start();
        Assert.Equal(TimeSpan.FromMinutes(1), s.TickPeriod);         // 降级档

        s.RunTick();                                                // 这一拍重开成功（Ready）
        Assert.Equal(2, api.OpenCount);
        Assert.Equal(TimeSpan.FromMinutes(3), s.TickPeriod);         // 立刻降到看门狗档
        s.RunTick();
        Assert.Equal(2, api.OpenCount);                             // 已就绪就别再开第二个句柄

        api.ReadAvailable = false;                                  // 通路中途坏了
        s.RunTick();
        Assert.Equal(TimeSpan.FromMinutes(1), s.TickPeriod);         // 又回到快速档
        s.Dispose();
    }

    /// 评审的 minor，但它是一个真的进程：Timer.Dispose() 只提交取消请求、不等待在途回调，
    /// 于是那一拍可以在对象释放之后才把 netsh 进程开出来。Dispose 必须在锁外等它跑完。
    [Fact]
    public void Dispose等完在途心跳才返回()
    {
        var tickInsideNetsh = new ManualResetEventSlim(false);
        var letTickFinish = new ManualResetEventSlim(false);
        var netshDone = 0;
        var netshCalls = 0;
        var api = new FakeApi { OpenFailures = int.MaxValue };
        var s = new WifiSentinel(api,
            () =>
            {
                if (Interlocked.Increment(ref netshCalls) == 1) return ("zut-stu", (Guid?)Iface);
                tickInsideNetsh.Set();                              // 第 2 次起：卡在"进程还没起来"这一步
                letTickFinish.Wait(TimeSpan.FromSeconds(5));
                Interlocked.Increment(ref netshDone);
                return ("zut-stu", (Guid?)Iface);
            },
            _ => ("10.1.1.1", "AA-BB"), pollInterval: TimeSpan.FromMilliseconds(50));

        s.Start();                                                  // 第 1 次 netsh 来自 Start 的同步刷
        Assert.True(tickInsideNetsh.Wait(TimeSpan.FromSeconds(2)), "真定时器没能跑出一拍在途心跳");
        using var releaser = new System.Threading.Timer(_ => letTickFinish.Set(), null, 80, Timeout.Infinite);

        s.Dispose();
        Assert.True(Volatile.Read(ref netshDone) >= 1,
            "Dispose 返回时那一拍还在跑 ⇒ 它会在对象死后才 spawn netsh 进程");

        var settled = Volatile.Read(ref netshCalls);
        Thread.Sleep(150);                                          // 远大于 50 ms 周期
        Assert.Equal(settled, Volatile.Read(ref netshCalls));        // 释放之后再没有第二拍
        Assert.Equal(1, api.DisposeCount);
        s.Dispose();                                                 // 幂等
        Assert.Equal(1, api.DisposeCount);
    }

    /// 心跳的重开句柄不能占着状态锁：真机上那一步调的是 WlanCloseHandle，而通知回调正排在
    /// 这把锁后面等读状态——有的驱动的 Close 会等在途回调收尾，两个锁套在一起就是一次死锁。
    /// 判据：TryOpen 还没返回的时候，从另一条线程读 Current 读得到吗。
    [Fact]
    public void 重开句柄期间状态锁不在手里()
    {
        var api = new OneShotOpenApi();
        var s = new WifiSentinel(api, () => ("zut-stu", (Guid?)Iface), _ => ("10.1.1.1", "AA-BB"));
        api.Sentinel = s;

        s.RunTick();                                    // 手里没句柄 ⇒ 这一拍必须重开
        Assert.Equal(1, api.OpenCount);
        Assert.True(api.ReadDuringOpen,
            "TryOpen 期间读不到 Current ⇒ 重开句柄占着状态锁（通知回调与 WlanCloseHandle 互等 = 死锁）");
        Assert.Null(api.ReadValue);                     // 还没读过，本来就是 null
        s.Dispose();
    }

    /// 只在 TryOpen 里回读一次状态的假通路。
    private sealed class OneShotOpenApi : IWlanApi
    {
        private int _open;
        public int OpenCount => Volatile.Read(ref _open);
        public WifiSentinel? Sentinel { get; set; }
        public bool ReadDuringOpen { get; private set; }
        public AccessPoint? ReadValue { get; private set; }

        public WlanOpenResult TryOpen(out Guid interfaceId)
        {
            Interlocked.Increment(ref _open);
            // 锁若被重开句柄这一路占着，Wait 就是超时返回 false —— 判的是 Wait 的结果本身，
            // 不能判"后来有没有读到"：等到 TryOpen 返回、锁一松，那个任务照样会跑完。
            var read = Task.Run(() => Sentinel!.Current);
            ReadDuringOpen = read.Wait(TimeSpan.FromMilliseconds(250));
            ReadValue = ReadDuringOpen ? read.Result : null;
            interfaceId = Iface;
            return WlanOpenResult.Ready;
        }

        public bool TryRead(Guid id, out WifiNative.ConnectionInfo info)
        {
            info = new WifiNative.ConnectionInfo(WifiNative.WlanInterfaceStateConnected, "zut-stu", "zut-stu");
            return true;
        }

        public void Dispose() { }
    }

    // ── 读那一次要过的闸：与界面/托盘同一把命令门，忙则拒、绝不排队 ────────────────
    //
    // 派单点名的毛病：60 秒那一拍（以及 wlanapi 通知回调那一拍）从来没上过任何闸，
    // 一次落在登录中途的读于是排进了协调器那把非重入锁后面 —— 最多 22 秒之后才真的读，
    // 读到的还是"已经变了的那个网络"，紧接着又把一次登录接上。
    // 现在的规矩：读和[登录][注销][重新检测]抢同一把 CommandGate，抢不到就当场放弃这一轮
    // （心跳照旧装着，下一拍再读），并且读与"关句柄"用一把读写锁隔开。

    [Fact]
    public async Task 心跳那一拍撞上在途命令时直接拒而不排队()
    {
        var gate = new CommandGate();
        var api = new FakeApi();
        var s = new WifiSentinel(api, () => (null, null), _ => ("10.1.1.1", "AA-BB"),
            pollInterval: TimeSpan.FromMinutes(5), gate: gate);
        s.Start();                                          // 门空着 ⇒ 这一读过得去
        var reads = api.ReadCount;
        Assert.True(reads > 0, "Start 之后一次都没读过：后面的断言就什么都没测");

        var tcs = new TaskCompletionSource();
        var hold = gate.RunAsync("测试·登录", _ => tcs.Task);      // 一条命令正在途（真机上是一次带退避的登录）
        Assert.True(gate.IsBusy);

        s.RunTick();                                        // 60 秒那一拍
        s.RunNotificationCallback();                        // wlanapi 通知回调那一拍
        Assert.Equal(reads, api.ReadCount);                 // 一次都没读 = 拒了，而不是排在登录后面
        Assert.True(s.GateSkips >= 2, $"两拍都没读，但跳过计数只有 {s.GateSkips}：拒的那一步没被记下来");
        Assert.True(s.PollArmed);                           // 拒了也要留着心跳，等门放开再读
        Assert.Equal("zut-stu", s.Current!.Ssid);           // 拒读不改已有的读数（不是"读不到 ⇒ 未连接"）

        tcs.SetResult();
        await hold;
        Assert.False(gate.IsBusy);
        s.RunTick();
        Assert.True(api.ReadCount > reads, "门放开之后下一拍仍然读不到：这一轮拒绝变成永久失明");
        s.Dispose();
    }

    /// 反方向的同一件事：读这一路不许把门留给界面。用户那一下被后台的一拍拒掉，
    /// 就是"我点了没反应"，而派单要的是"落在登录中途的读被拒"，不是反过来。
    [Fact]
    public async Task 一拍读取跑完之后门是开着的界面命令进得来()
    {
        var gate = new CommandGate();
        var api = new FakeApi();
        var s = new WifiSentinel(api, () => (null, null), _ => ("10.1.1.1", "AA-BB"),
            pollInterval: TimeSpan.FromMinutes(5), gate: gate);
        s.Start();

        var login = gate.RunAsync("测试·登录", _ => Task.CompletedTask);
        Assert.True(await login);
        Assert.False(gate.IsBusy, "读完之后把门留在占着的状态：界面从此点不动");

        var reprobe = gate.RunAsync("测试·重新检测", _ => Task.CompletedTask);
        Assert.True(await reprobe);                          // 第二条评论照样进得来
        s.Dispose();
    }

    /// 关句柄与一次读取不能同时在跑：WlanCloseHandle 落在 TryRead 中间就是一次 use-after-free。
    /// 现在那道保护只覆盖了定时器那一拍（Dispose 等它跑完），通知回调那一拍没人等。
    [Fact]
    public async Task Dispose关句柄时不许有读取同时在跑()
    {
        var api = new OverlapApi();
        var s = new WifiSentinel(api, () => (null, null), _ => ("10.1.1.1", "AA-BB"),
            pollInterval: TimeSpan.FromMinutes(5));
        s.Start();

        var ticks = Task.Run(() =>
        {
            for (var i = 0; i < 8; i++) s.RunTick();        // 几拍连着跑，其中一定有一拍正卡在 TryRead 里
        });
        Thread.Sleep(40);
        s.Dispose();
        Assert.Same(ticks, await Task.WhenAny(ticks, Task.Delay(TimeSpan.FromSeconds(10))));

        Assert.Equal(0, api.CloseDuringRead);
        Assert.True(api.Reads > 0, "一次都没读到，这条用例就是空的");
        Assert.Equal(1, api.DisposeCount);
    }

    /// 会记录"关句柄时还有没有在跑的读"的假通路：TryRead 故意慢，好让 Dispose 撞上来。
    private sealed class OverlapApi : IWlanApi
    {
        private int _inRead, _closeDuringRead, _reads, _dispose;
        public int CloseDuringRead => Volatile.Read(ref _closeDuringRead);
        public int Reads => Volatile.Read(ref _reads);
        public int DisposeCount => Volatile.Read(ref _dispose);

        public WlanOpenResult TryOpen(out Guid interfaceId)
        {
            interfaceId = Iface;
            return WlanOpenResult.Ready;
        }

        public bool TryRead(Guid id, out WifiNative.ConnectionInfo info)
        {
            Interlocked.Increment(ref _inRead);
            Interlocked.Increment(ref _reads);
            Thread.Sleep(30);
            info = new WifiNative.ConnectionInfo(WifiNative.WlanInterfaceStateConnected, "zut-stu", "zut-stu", 60);
            Interlocked.Decrement(ref _inRead);
            return true;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _dispose);
            if (Volatile.Read(ref _inRead) != 0) Interlocked.Increment(ref _closeDuringRead);
        }
    }

    /// 通知回调那一拍与心跳那一拍必须是同一条通路：上一版的死路就是"装的方法与测的方法是两个"。
    [Fact]
    public async Task 通知回调与心跳走的是同一条读闸门()
    {
        var gate = new CommandGate();
        var api = new FakeApi();
        var s = new WifiSentinel(api, () => (null, null), _ => ("10.1.1.1", "AA-BB"),
            pollInterval: TimeSpan.FromMinutes(5), gate: gate);
        s.Start();
        var reads = api.ReadCount;

        var tcs = new TaskCompletionSource();
        var hold = gate.RunAsync("测试·登录", _ => tcs.Task);
        s.RunNotificationCallback();
        s.RunTick();
        Assert.Equal(reads, api.ReadCount);
        tcs.SetResult();
        await hold;
        Assert.False(gate.IsBusy);
        s.Dispose();
    }

    // ── 简报里的三段纯逻辑 ──

    [Theory]
    [InlineData("02-A1-B2-C3-D4-E5", "02a1b2c3d4e5")]
    [InlineData("02:a1:b2:c3:d4:e5", "02a1b2c3d4e5")]
    [InlineData("02A1B2C3D4E5", "02a1b2c3d4e5")]
    public void MAC去分隔符并转小写(string input, string expected)
        => Assert.Equal(expected, WifiSentinel.NormalizeMac(input));

    [Theory]
    [InlineData("", "10.1.1.1", "02-A1-B2-C3-D4-E5")]        // 没连上
    [InlineData("zut-stu", null, "02-A1-B2-C3-D4-E5")]        // 拿不到 IP
    [InlineData("zut-stu", "10.1.1.1", null)]                 // 拿不到 MAC
    public void 三要素不全时当前接入点为空(string? ssid, string? ip, string? mac)
        => Assert.Null(WifiSentinel.BuildAccessPoint(ssid, ip, mac));

    [Fact]
    public void 已连接时给出三元组()
    {
        var ap = WifiSentinel.BuildAccessPoint("ZUT-STU", "10.133.126.113", "02-A1-B2-C3-D4-E5");
        Assert.Equal("ZUT-STU", ap!.Ssid);
        Assert.Equal("10.133.126.113", ap.Ipv4);
        Assert.Equal("02a1b2c3d4e5", ap.MacNoSeparator);
    }

    [Fact]
    public void SSID两端空白被裁掉()
        => Assert.Equal("zut-stu", WifiSentinel.BuildAccessPoint(" zut-stu ", "10.1.1.1", "AABB")!.Ssid);
}
