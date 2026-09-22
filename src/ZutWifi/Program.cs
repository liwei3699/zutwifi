using System.Threading;
using ZutWifi.Config;
using ZutWifi.Diagnostics;
using ZutWifi.Notify;
using ZutWifi.Shell;

namespace ZutWifi;

/// 进程入口：单实例 → 兜底处理器 → 首次向导 → 装配 → 消息循环。
///
/// 这里不放任何业务判断（该不该登录是 LoginCoordinator 的事，按钮亮不亮是 StatusPresenter 的事）。
/// 收尾次序是要害：托盘"退出"只发一个退出请求，让消息循环自己结束，下面 finally 里的
/// parts.Dispose() 与互斥量 Dispose 才跑得到 —— 用 Environment.Exit 会整段跳过，
/// 于是 60 秒心跳和单实例 Mutex 都留在原地，下一次启动被判成"已经在运行"。
internal static class Program
{
    /// `Local\` 前缀 = 只看当前登录会话：两个人各登一个 Windows 账户时可以各开一份。
    private const string MutexName = @"Local\ZutWifi.SingleInstance";

    /// 进程持有的那把锁。Dispose 它就是把"我在运行"这件事还回去。
    private static Mutex? _mutex;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
            return SelfTest.RunAsync(args.Contains("--with-logout")).GetAwaiter().GetResult();

        if (!TryAcquireSingleInstance(out var notOwned))
            return SecondInstance(notOwned);          // 这一支也要把句柄放掉，见 SecondInstance
        _mutex = notOwned;

        AppParts? parts = null;
        try
        {
            // 顺序有讲究：未处理异常的策略要赶在任何控件被创建之前定下来。
            InstallGlobalExceptionHandlers();
            ApplicationConfiguration.Initialize();

            var dir = AppContext.DataDir;
            var store = new SettingsStore(dir);
            AppContext.AutoStartIfNeeded(store);        // exe 换过位置也要把 Run 键修回设置里的那个意图

            // 日志在装配之前就拿到：规矩①要的是"全进程一份"，而首次向导排在装配之前。
            var log = AppContext.NewLog(dir);

            // 向导排在装配之前：先建好组合根的话，无线源一启动就会自动登一次，
            // 紧接着用户在向导里点"立即测试"又登一次 —— 同一次连接给门户交两份凭据，
            // 正是这个程序最要避免的那一侧。
            if (AppContext.NeedsFirstRun(dir)) RunFirstRunWizard(store, log);

            parts = AppContext.Build(NotificationSink.Production(), log: log);
            Application.Run(parts.Form);
            return 0;
        }
        catch (Exception ex)
        {
            Fatal(ex);
            MessageBox.Show("启动失败：" + ex.Message + "\n详情见 %APPDATA%\\ZutWifi\\crash.log 与 logs 目录",
                "ZutWifi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            parts?.Dispose();       // 停 60 秒心跳、关 wlanapi 句柄、摘掉托盘图标、放掉两个客户端
            _mutex?.Dispose();      // 单实例互斥量：不还得等进程退出，"下一次启动"会被这次残骸挡住
            _mutex = null;
        }
    }

    /// 单实例：拿不到就说明已经有一份在跑。调用方负责 Dispose 返回的那个 Mutex。
    public static bool TryAcquireSingleInstance(out Mutex mutex)
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return createdNew;
    }

    /// 第二份实例：说一句话就走，退出码 0（它不是错误，只是"已经有一个人了"）。
    ///
    /// 单独成一个方法就是为了这一句 Dispose：拿不到锁那一支以前直接 return 0，
    /// 于是 TryAcquireSingleInstance new 出来的那个内核句柄一直留在原地 —— 进程马上就退，
    /// 看着无害，但"谁 new 谁放"这条规矩一破，下一个把这条分支改成长驻路径的人就踩坑。
    /// inform 是弹框的接缝：单测里不能真弹一个模态框把套件钉在桌面上。
    internal static int SecondInstance(Mutex notOwned, Action<string>? inform = null)
    {
        notOwned.Dispose();
        (inform ?? (m => MessageBox.Show(m, "ZutWifi", MessageBoxButtons.OK, MessageBoxIcon.Information)))
            ("ZutWifi 已经在运行了（看右下角托盘图标）。");
        return 0;
    }

    private static void RunFirstRunWizard(SettingsStore store, TransactionLog log)
    {
        // 日志一定要传进来（评审 I3）：这一次登录是同学这辈子第一次，也是最可能填错的一次，
        // 门户给了什么判定，事后只有这一份日志说得出。
        using var wizard = new FirstRunWizard(store, new SecretStore(AppContext.DataDir), log: log);
        wizard.ShowDialog();
        // 没点"完成"就关掉：不追问、不弹框，下次启动 NeedsFirstRun 仍然是真，向导还会再来。
    }

    /// 兜底处理器。到这一步只剩一条通路：把异常写进磁盘上那份 crash.log，然后让程序继续活着。
    /// 未处理异常在 WinForms 里默认会弹一个"继续/退出"的框 —— 那会把托盘钉在桌面上，
    /// 而这个人可能正在别的网络里等着认证，所以统一走 CatchException。
    private static void InstallGlobalExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Fatal(e.Exception); e.SetObserved(); };
    }

    /// 崩溃日志。它自己绝不能再抛一次 —— 这是最后一道出口，写不进去也只能咽掉。
    internal static void Fatal(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppContext.DataDir);
            File.AppendAllText(System.IO.Path.Combine(AppContext.DataDir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch (Exception) { /* 磁盘都不肯写了，这里没有任何能做的事 */ }
    }
}
