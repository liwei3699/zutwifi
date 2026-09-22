using System.Diagnostics;
using System.Text;
using ZutWifi.Wifi;

namespace ZutWifi.Diagnostics;

/// 跑一条**只读**的命令行工具（netsh / ipconfig / route）并把输出拿回来，用于诊断包里的快照。
/// 类名不叫 Shell：项目里已经有 `ZutWifi.Shell` 这个命名空间，同名类型在两处都在场时点不开。
///
/// 两条硬规矩，都是"在别人机器上诊断"这一用途要求的：
/// ① 有上限：默认 5 秒到点就 Kill。一条卡住的 netsh（WLAN 自动配置服务挂了的时候真会卡）
///    不许把整个导出吊在那里。
/// ② 永不抛出：起不动、超时、输出看不懂，全都换成人能看的一句话。快照缺一项不该让包生不出来，
///    但"为什么缺"必须写在包里 —— 一份静默少了一项的诊断包，比不打包更难查。
///
/// WifiSentinel 里已有一个形状相近的内部 Run：它只回答"读到了没有"（失败一律 null），
/// 因为它的调用方只关心降级；这里要多给一句原因，所以另开一个，不把那边的契约改掉。
internal static class CommandCapture
{
    public const int DefaultTimeoutMs = 5000;

    /// 成功 = 子进程在期限内自己退出了（退出码非零也算成功：那正是"这台机器上 netsh 说了什么"）。
    public static (bool Ok, string Text, string? Reason) TryCapture(
        string file, string arguments, int timeoutMs = DefaultTimeoutMs)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(file, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!proc.Start()) return (false, "", "子进程没起来：" + file);

            // 输出先在后台读干字节：等到进程退出再去 ReadToEnd 会在"输出满了管道"时互相等死。
            var pumping = Task.Run(() =>
            {
                try
                {
                    using var ms = new MemoryStream();
                    proc.StandardOutput.BaseStream.CopyTo(ms);
                    return ms.ToArray();
                }
                catch { return Array.Empty<byte>(); }
            });
            var errPumping = Task.Run(() =>
            {
                try { return proc.StandardError.ReadToEnd(); } catch { return ""; }
            });

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }
                return (false, "", $"超时（>{timeoutMs}ms）已终止：{file} {arguments}");
            }
            var exit = proc.ExitCode;
            if (!pumping.Wait(timeoutMs)) return (false, "", $"读输出超时：{file}");
            var text = Decode(pumping.Result);
            var err = errPumping.Wait(200) ? errPumping.Result : "";
            if (text.Length == 0 && err.Length > 0) text = err;
            if (exit != 0) text += Environment.NewLine + $"(退出码 {exit})";
            return (true, text.Length == 0 ? "(命令没有任何输出)" : text, null);
        }
        catch (Exception ex)
        {
            // Win32Exception（根本没有这个 exe）、InvalidOperationException（已经在跑的过程对象）
            // 都只能说明"这项快照拿不到"，不该往上抛。
            return (false, "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static string Capture(string file, string arguments, int timeoutMs = DefaultTimeoutMs)
    {
        var (ok, text, reason) = TryCapture(file, arguments, timeoutMs);
        return ok ? text : "采集失败：" + reason;
    }

    /// 控制台输出跟着系统码页（中文机器是 GBK），先按 UTF-8 试、见到替换字符再回落 GBK：
    /// 与 WifiSentinel 读 netsh 时同一套解法，SSID 里的中文才不会变成一串问号。
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        return WifiNative.DecodeText(bytes) ?? Encoding.UTF8.GetString(bytes);
    }
}
