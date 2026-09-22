using System.Globalization;
using System.Text;
using ZutWifi.Core;

namespace ZutWifi.Diagnostics;

/// SelfTest 的另一半：一次自检的"记账台"与超时闸门。
///
/// 拆出来不是为了好看 —— 这两样东西服务的对象是"这一轮跑不动了怎么办"，
/// 与 SelfTest.cs 里那条"跑通了长什么样"的正常通路正好相反，读代码的人一次只想看一边。
/// 拆完之后两边各自一屏读完（见 Global Constraints 的 250 行信号）。
public static partial class SelfTest
{
    /// 一次网络调用的硬上限。PortalGateway 自己已经把网络故障吞成判定，这一层管的是
    /// "根本不再返回"那一类（防火墙把连接吊住、代理黑洞、驱动卡死）：
    /// 取消信号它可能肯听（那就早退），也可能不听（那就 WhenAny 放弃等待，绝不多等）。
    internal static async Task<T> WithBudgetAsync<T>(string step, Func<CancellationToken, Task<T>> call,
        int budgetMs, int graceMs, T whenLate, CancellationToken overall, Action<string>? onLate = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(overall);
        cts.CancelAfter(Math.Max(50, budgetMs));
        var running = call(cts.Token);
        var grace = Task.Delay(budgetMs + graceMs);
        if (await Task.WhenAny(running, grace) != running)
        {
            onLate?.Invoke($"{step} 超过 {budgetMs}ms 上限，已不等它（这一项按取不到处理）");
            return whenLate;
        }
        try
        {
            return await running;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // 传进去的那个 token 只由这一层掌控，所以取消就等于到点。
            onLate?.Invoke($"{step} 超过 {budgetMs}ms 上限，已放弃（{ex.GetType().Name}）");
            return whenLate;
        }
    }

    /// 一次自检的全部状态：问题清单、落盘计数、上屏计数、两张超时牌。
    /// 独立成类是为了让"没跑完"那条 catch 通路还能把已经看到的问题与落盘状态说完。
    private sealed class Session(Harness h) : IDisposable
    {
        private readonly CancellationTokenSource _overall =
            new(TimeSpan.FromMilliseconds(Math.Max(1_000, h.OverallBudgetMs)));

        public string LogDir { get; } = Path.Combine(h.Dir, "logs");
        public string ReportPath { get; } = Path.Combine(h.Dir, "logs",
            "selftest-" + h.Clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");

        /// 门户调用的事务日志：与自检日志同一个目录，所以诊断包一次把两份都带走。
        public TransactionLog AppLog { get; } = new(h.Clock, Path.Combine(h.Dir, "logs"));

        public List<string> Problems { get; } = [];
        public bool Incomplete;
        public int FileFailures;
        public string? FirstFileFailure;
        public int ConsoleFailures;
        public string? FirstConsoleFailure;
        public bool OverallCancelled => _overall.IsCancellationRequested;

        /// 一行：带时间戳，先写文件再上屏（顺序是有意的 —— 屏幕可能根本没有，磁盘不会）。
        /// 两侧各自记账，任何一侧都不许把自检带崩。
        public void Line(string text)
        {
            var stamped = $"[{h.Clock.UtcNow.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {text}";
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(ReportPath, stamped + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                FileFailures++;
                FirstFileFailure ??= $"{ex.GetType().Name}: {ex.Message}";
            }
            try { (h.Emit ?? ConsoleLine)(stamped); }
            catch (Exception ex)
            {
                ConsoleFailures++;
                FirstConsoleFailure ??= $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        /// 一条问题：既进问题清单（决定退出码），也当场在日志里留一行。
        public void Problem(string text)
        {
            Problems.Add(text);
            Line("   ⚠ " + text);
        }

        public Task<T> Budgeted<T>(string step, Func<CancellationToken, Task<T>> call, T whenLate) =>
            WithBudgetAsync(step, call, h.CallBudgetMs, GraceMs, whenLate, _overall.Token, Line);

        public async Task WaitAsync(TimeSpan by, string why)
        {
            Line($"   {why}");
            try { await h.Clock.Delay(by, _overall.Token); }
            catch (OperationCanceledException) { Line("   等待被打断（整轮超时）"); }
        }

        /// 收尾：⑩ 落盘健康 + 结论 + 退出码。这里也是"文件写不出去时只剩控制台"那条兜底通路。
        public int Finish()
        {
            if (!Incomplete && AppLog.WriteFailures > 0)
                Problem($"门户调用日志写不进去（{AppLog.WriteFailures} 次）：诊断能力已受损 → {AppLog.LogDirectory}" +
                        $"（首次原因：{AppLog.FirstWriteFailure ?? "未知"}）");

            Line($"⑩ 诊断落盘：自检日志={(FileFailures == 0 ? "每行都写成了" : $"有 {FileFailures} 行没写进去")} " +
                 $"事务日志写入失败={AppLog.WriteFailures} 次 上屏失败={ConsoleFailures} 次");

            var code = Incomplete || FileFailures > 0 ? IncompleteExitCode
                : Problems.Count == 0 ? PassExitCode : ProblemsExitCode;
            Line(Incomplete ? "结论：自检没跑完 —— 这个退出码只说明“这一轮没测成”，不代表任何网络结论"
                : FileFailures > 0 ? "结论：跑完了，但这份自检日志不完整，别只凭它下判断"
                : Problems.Count == 0 ? "结论：通过 —— 设置、密码、无线、门户认证与外网全都对上了"
                : $"结论：发现问题 {Problems.Count} 处，第一条：{Problems[0]}");
            Line($"退出码 {code}（0=全通过，{ProblemsExitCode}=跑完但有问题，{IncompleteExitCode}=这一轮没跑成/日志不完整）");
            Line($"自检日志：{ReportPath}（与 logs/app-*.log 一起由“导出诊断包”打进 zip）");

            // 磁盘这条线断了，只剩控制台；这一句必须让人抄得下来。
            if (FileFailures > 0)
                ForceConsole($"注意：自检日志写不进去（{FirstFileFailure}），以上文字只出现在控制台 ——" +
                             " 请整段复制给维护者，并检查这个目录的权限：" + LogDir);
            if (ConsoleFailures > 0)
                Line($"（另有 {ConsoleFailures} 行没能上屏：{FirstConsoleFailure} —— 从资源管理器双击启动时没有控制台，属正常）");
            return code;
        }

        private void ForceConsole(string text)
        {
            try { (h.Emit ?? ConsoleLine)(text); } catch (Exception) { /* 两条路都断了，只剩退出码 */ }
        }

        /// 单文件 exe 从资源管理器双击进来时根本没有控制台，写标准输出这件事本身会抛
        /// （占位版本就认这件事）。这里不吞：由 Line 那一侧统一记账，不然没人知道上屏失败了几行。
        private static void ConsoleLine(string text) => Console.Out.WriteLine(text);

        public void Dispose() => _overall.Dispose();
    }
}
