using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ZutWifi.Config;

/// DPAPI CurrentUser：密文只对同一 Windows 用户可解。换电脑或重装系统后必须重填密码，这点要写进使用说明。
/// 直接用 crypt32.dll 的 P/Invoke 而不引 System.Security.Cryptography.ProtectedData 包：
/// 交付物是发给同学的单文件 exe，少一个依赖少一处装不上的可能。
public sealed class SecretStore(string directory)
{
    private const uint UiForbidden = 0x00000001;   // CRYPTPROTECT_UI_FORBIDDEN
    private const string Description = "ZutWifi";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ZutWifi.v1");

    private string FilePath => System.IO.Path.Combine(directory, "secret.bin");

    /// 解不开就等于没配过：换用户、别的机器、文件被写坏，全都返回 null 让首次向导接管。
    /// 这个函数在启动路径上，抛出等于托盘程序起不来。
    /// COMException 是本类 P/Invoke 返回 false 时自己抛的；CryptographicException 一并挡着，
    /// 哪天换成 ProtectedData 或框架内部抛出时，掉到的还是同一个 null。
    public string? Get()
    {
        if (!File.Exists(FilePath)) return null;
        try { return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(FilePath))); }
        catch (Exception ex) when (ex is COMException or CryptographicException) { return null; }
    }

    /// null 与空串都表示"未配置"并删掉密文：设置页清空密码框保存的是 ""，
    /// 留下一个能解出空串的密文会让状态机拿着空密码去提交一次注定失败的登录。
    public void Set(string? plain)
    {
        Directory.CreateDirectory(directory);
        if (string.IsNullOrEmpty(plain))
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }
        File.WriteAllBytes(FilePath, Protect(Encoding.UTF8.GetBytes(plain)));
    }

    private static byte[] Protect(byte[] data) => Run(data, protect: true);
    private static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var entropy = ToBlob(Entropy);
        var input = ToBlob(data);
        var output = default(DATA_BLOB);   // 用 ref 而不是 out：失败时也能确定它是零值，见下面的释放
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, Description, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                // 描述符走 out string?：interop 封送器负责释放它给的这块指针（CoTaskMemFree 与 crypt32 的
                // LocalAlloc 在 Windows 上是同一个堆），所以传 out _ 就够，不需要手工 Free——也不读那段文字。
                : CryptUnprotectData(ref input, out _, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok)
            {
                Free(output);   // crypt32 失败前可能已经分配过；按 .NET 自家 ProtectedData 的做法一并释放
                throw new COMException("DPAPI 调用失败", Marshal.GetLastWin32Error());
            }
            return FromBlob(output);
        }
        finally
        {
            Free(entropy);
            Free(input);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private static DATA_BLOB ToBlob(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return new DATA_BLOB { cbData = bytes.Length, pbData = ptr };
    }

    private static byte[] FromBlob(DATA_BLOB blob)
    {
        try
        {
            var bytes = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
            return bytes;
        }
        finally { Free(blob); }
    }

    /// AllocHGlobal/FreeHGlobal 在 Windows 上就是 LocalAlloc/LocalFree，正是 crypt32 要求的释放方式。
    private static void Free(DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(blob.pbData);
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? szDataDescr,
        ref DATA_BLOB optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, ref DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, out string? szDataDescr,
        ref DATA_BLOB optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, ref DATA_BLOB dataOut);
}
