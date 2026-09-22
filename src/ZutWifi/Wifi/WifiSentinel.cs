using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ZutWifi.Wifi;

// 计划全局类型词表中的两个跨任务契约。WifiSentinel 是首个实现者，故随本文件落地；
// 其它任务只引用 ZutWifi.Wifi.AccessPoint / IWifiSource，不得另行定义。
public sealed record AccessPoint(string Ssid, string Ipv4, string MacNoSeparator);

public interface IWifiSource
{
    AccessPoint? Current { get; }
    event Action<AccessPoint?> Changed;
}

/// wlanapi 通路的三种结果：事件驱动可用 / 只能靠轮询读 / 通路不可用。
/// 上一版把"句柄开上了"当成"事件驱动成立"，通知注册失败时既不订阅也不轮询，状态源就此饿死。
internal enum WlanOpenResult
{
    Ready,                          // 句柄 + 接口 + 通知都到位（心跳仍可降到看门狗档）
    PollWithoutNotifications,       // 能查但不能通知，必须留着快速轮询
    Unavailable,                    // wlanapi 彻底不可用 → 快速轮询负责重开 + netsh 降级
}

/// 对 wlanapi 的最小抽象。真机走 WlanApi（P/Invoke），单测走假实现——
/// "拔网卡""服务挂了"这类分支在真机上没法验证（会断掉用户正在用的连接），只能靠注入钉。
///
/// 契约（对实现者是硬要求，不是风格建议）：TryOpen 与 TryRead 一律永不抛出。
/// 任何失败——返回码非零、驱动异常、DllImport 找不到入口点、连 Marshal 都炸——都必须自己吞掉，
/// 分别返回 Unavailable / false。理由：这两个方法的调用点跑在 wlanapi 自己的通知线程和
/// System.Threading.Timer 的线程池线程上，没有 catch-all，抛出去就是进程级未处理异常（托盘直接没了），
/// 而不是"这一轮读不到、下一拍再试"。WifiSentinel 这一侧对 TryOpen 另兜了一道（见 TryOpenGuarded），
/// 但那只保住心跳那一拍：把契约写在这里，才是让下一位实现者不去踩它的地方。
internal interface IWlanApi : IDisposable
{
    /// 可重复调用；失败路径自己保证不留半开的句柄。永不抛出（见接口契约）。
    WlanOpenResult TryOpen(out Guid interfaceId);

    /// false = 通路本身坏了（没句柄 / 查询报错 / 驱动异常），调用方该走 netsh 降级并重开句柄。
    /// 未连接不算失败：那时返回 true 且 info.Ssid 为空串。永不抛出（见接口契约）。
    bool TryRead(Guid interfaceId, out WifiNative.ConnectionInfo info);
}

/// WLAN 状态源。主路径：订阅 wlanapi 连接通知（真实时、零轮询开销）+ 一条 60 秒看门狗心跳。
/// 降级链：wlanapi 不可用时 5 秒轮询重试打开句柄；仍不可用则解析 `netsh wlan show interfaces`。
/// 三条通路共用同一个心跳本体 TickOnce()——先重开句柄、再读、读不到才落 netsh。
public sealed class WifiSentinel : IWifiSource, IDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromSeconds(60);

    /// 在途心跳最多等多久。netsh 那一跑自己有 5 秒上限，这里留点余量；
    /// 真到点了还没完（例如从回调线程里 Dispose 自己）就放弃等待——退出流程不能被一个进程挂死。
    private static readonly TimeSpan TickDrainTimeout = TimeSpan.FromSeconds(6);

    /// 读侧上闸最多等多久。这是个上限而不是期望值：句柄交换本身是几毫秒的 Win32 调用，
    /// 真等不到就说明这一拍撞上了换句柄/释放，放弃比排队对。
    private static readonly TimeSpan ReadEntryWait = TimeSpan.FromMilliseconds(50);

    /// 过闸那一次读在命令门上留下的名字（诊断包里就是 click://无线读取）。
    private const string ReadGateName = "无线读取";

    private readonly object _gate = new();               // 只管状态：_state/_iface/_current/_poll/_disposed
    private readonly object _openGate = new();           // 只串 Win32 的开/关句柄，见 Reopen 的注释
    private readonly IWlanApi _api;
    private readonly Func<(string? Ssid, Guid? InterfaceId)> _netshSsid;
    private readonly Func<Guid?, (string? Ipv4, string? Mac)> _addresses;
    private readonly TimeSpan _pollInterval;             // 快速轮询档（wlanapi 不可用时）
    private readonly TimeSpan _watchdogInterval;         // 看门狗档（事件驱动成立时）

    /// 装配那把共享命令门（界面四个按钮 + 托盘三条菜单 + 60 秒心跳用的是同一把）。
    /// null = 没有人给门（离线自检那一条通路），此时读只受下面的读写锁保护。
    private readonly CommandGate? _commandGate;

    /// 交给 WlanApi 的那个通知回调本体。测试驱动回调只能驱动它（RunNotificationCallback），
    /// 与 _tick 之于定时器同理 —— 不许再有"装的方法与测的方法是两个人"那种死路。
    private readonly Action _onNativeChanged;

    /// 读写锁：读 = 一次 wlanapi 读取，写 = 开/关句柄（心跳重开、Dispose）。
    /// 为什么在命令门之外还要这一把：门只能回答"有没有人在跑"，它帮不了 Dispose 等在途读收尾；
    /// 而 TryEnter*Lock 一律带超时、绝不无限等 —— 通知线程等在一把写锁上，
    /// 配上"有些驱动的 Close 会等在途回调收尾"就是一次死锁（见 Reopen 的注释）。
    /// 锁次序全类只有一种：_handleGate → _openGate → _gate。谁都不许反着拿。
    private readonly ReaderWriterLockSlim _handleGate = new(LockRecursionPolicy.NoRecursion);

    private System.Threading.Timer? _poll;
    private Guid _iface = Guid.Empty;
    private WlanOpenResult _state = WlanOpenResult.Unavailable;   // Unavailable = 手里没有可复用的句柄
    private bool _disposed;
    private AccessPoint? _current;
    private int _gateSkips;

    /// 装进 Timer 的那个委托本体。测试要驱动心跳就只能驱动它（RunTick），
    /// 不允许再另找一条"看起来等价"的手工通路——上一版的降级链路之所以是死的，
    /// 正是因为回调装的方法和测试调的方法是两个不同的方法。
    private readonly System.Threading.TimerCallback _tick;
    private TimeSpan _tickPeriod;

    public event Action<AccessPoint?> Changed = _ => { };
    public AccessPoint? Current { get { lock (_gate) return _current; } }

    public WifiSentinel(CommandGate? gate = null) : this(null, null, null, null, null, gate) { }

    /// 单测入口：把三个外部动作（wlanapi、netsh、网卡枚举）换成假实现，外加两个心跳档位
    /// 与装配那把共享命令门。生产代码走上面那个带 gate 的构造。
    internal WifiSentinel(IWlanApi? api, Func<(string? Ssid, Guid? InterfaceId)>? netshSsid,
        Func<Guid?, (string? Ipv4, string? Mac)>? addresses,
        TimeSpan? pollInterval = null, TimeSpan? watchdogInterval = null, CommandGate? gate = null)
    {
        // 通知回调只负责"立刻读一次"，重开句柄归心跳：这样回调永远不需要在别的线程手里
        // 等着调 Win32，Close 与回调不会互相咬住。句柄真死了，最迟一个看门狗周期就恢复。
        _onNativeChanged = RefreshOnceSafely;
        _api = api ?? new WlanApi(_onNativeChanged);
        _netshSsid = netshSsid ?? ReadSsidViaNetsh;
        _addresses = addresses ?? (id => WlanIPv4AndMac(id));
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _watchdogInterval = watchdogInterval ?? DefaultWatchdogInterval;
        _commandGate = gate;
        _tick = _ => TickOnce();
    }

    // 只给单测的几个快照：句柄与定时器的状态机在真机上无法制造故障。
    internal bool PollArmed { get { lock (_gate) return _poll is not null; } }
    internal bool NativeLive { get { lock (_gate) return _state != WlanOpenResult.Unavailable && !_disposed; } }
    internal TimeSpan TickPeriod { get { lock (_gate) return _tickPeriod; } }

    /// 跑一拍生产心跳：Invoke 的是定时器持有的那个委托，所以"回调装错方法"这种故障测得出来。
    internal void RunTick() => _tick(null);

    /// 跑一次生产通知回调：Invoke 的正是交给 WlanApi 注册进去的那个委托。
    internal void RunNotificationCallback() => _onNativeChanged();

    /// 被闸门挡回去的轮次：读那一拍（命令门占着 / 句柄正在换）与换句柄那一拍都算。
    /// 拒了这件事必须留痕：诊断包里"这一轮什么都没读到"和"读被拒了"是两种故障。
    internal int GateSkips => Volatile.Read(ref _gateSkips);

    /// 档位选择：事件驱动成立时是慢看门狗，其余情况（能查不能通知、彻底不可用）是快速轮询。
    private TimeSpan IntervalFor(WlanOpenResult result) =>
        result == WlanOpenResult.Ready ? _watchdogInterval : _pollInterval;

    public void Start()
    {
        ReopenIfDead();           // 不持 _gate 调 Win32：见 Reopen 的注释
        lock (_gate)
        {
            if (_disposed) return;
            // 心跳在三种模式下都必须在，只是档位不同：通知只在"连接状态变化"的那一刻投递，
            // 适配器被重置、wlanapi 服务被重启之后不会再有任何通知，也没有任何 tick 可跑——
            // 事件驱动成立时不留定时器就等于托盘永久失明。所以 Ready 档也留一条 60 秒看门狗，
            // 它跑的是与降级轮询完全相同的 TickOnce（重开句柄 → 读 → 必要时落 netsh）。
            ArmLocked(IntervalFor(_state));
        }
        RefreshOnceSafely();     // 两条路径都立刻刷一次：native 给当前值，降级给 netsh 的
    }

    /// 装心跳。回调必须是 _tick；档位没变就不碰定时器（Change 会重置到期时间，白省一轮）。
    /// 只在 _gate 内调用。释放之后绝不重装——Dispose 先把 _disposed 立起来，这里的 _disposed 门就是它的护身符。
    private void ArmLocked(TimeSpan interval)
    {
        if (_poll is null)
        {
            _poll = new System.Threading.Timer(_tick, null, interval, interval);
            _tickPeriod = interval;
            return;
        }
        if (_tickPeriod == interval) return;
        _poll.Change(interval, interval);
        _tickPeriod = interval;
    }

    /// 生产心跳本体：定时器与看门狗装的都是它，全类只有这一个心跳入口。
    /// ① 手里没有可复用的句柄就先重开——这条降级链路存在的全部意义就是"WlanOpenHandle 失败一次
    ///    之后每一拍都再试一次"，否则一次失败就永远只靠 netsh 活着；
    /// ② 还是打不开，紧随其后的 Read() 才落到 netsh；
    /// ③ 读完按最新状态调整档位。
    private void TickOnce()
    {
        ReopenIfDead();
        RefreshOnceSafely();
        lock (_gate)
        {
            if (_disposed) return;
            ArmLocked(IntervalFor(_state));              // 掉了就加速，回来了就降频
        }
    }

    /// 句柄还活着就什么都不做；死了才重开一次。返回重开结果只为方便调用方判断，状态已写回 _state。
    private WlanOpenResult ReopenIfDead()
    {
        lock (_gate)
        {
            if (_disposed) return WlanOpenResult.Unavailable;
            if (_state != WlanOpenResult.Unavailable) return _state;      // 已就绪就别再开第二个句柄
        }
        return Reopen();
    }

    /// 重开 wlanapi 句柄。_gate 绝不在手里：TryOpen 头一件事是 WlanCloseHandle（丢掉上一次半开的句柄），
    /// 而通知回调跑在 wlanapi 自己的线程上、要拿 _gate 才能读状态——有些驱动的 Close 会等在途回调收尾，
    /// 两个锁套在一起就是一次死锁。开句柄这件事由 _openGate 单独串行，保证不会漏掉第二把句柄。
    ///
    /// 换句柄 = 读写锁的**写侧**：Close 落在一次 TryRead 中间就是一次 use-after-free（句柄是驱动给的）。
    /// 这里的等待有上限：等不到就这一拍放弃（读侧还在跑，或另一次交换正在进行），下一拍再来一遍。
    /// 读侧用的是 TryEnterReadLock(50ms)，所以"Close 等在途回调 + 回调等这把写锁"那种互等最多持续
    /// 50 毫秒就会自己解开 —— 回调放弃这一轮，而不是排在写锁上死等。
    private WlanOpenResult Reopen()
    {
        if (!TryEnterWrite(TickDrainTimeout))
        {
            Skip();
            lock (_gate) return _disposed ? WlanOpenResult.Unavailable : _state;
        }
        try
        {
            lock (_openGate)
            {
                lock (_gate) { if (_disposed) return WlanOpenResult.Unavailable; }
                var result = TryOpenGuarded(out var iface);
                lock (_gate)
                {
                    if (_disposed)
                    {
                        // 就在这几毫秒里 Dispose 落了地：刚开出来的句柄当场还回去，不给它留漏网的。
                        if (result != WlanOpenResult.Unavailable) _api.Dispose();
                        _state = WlanOpenResult.Unavailable;
                        _iface = Guid.Empty;
                        return WlanOpenResult.Unavailable;
                    }
                    _state = result;
                    _iface = result == WlanOpenResult.Unavailable ? Guid.Empty : iface;
                    return result;
                }
            }
        }
        finally { ExitWrite(); }
    }

    // ── 读写锁的两个入口：一律带超时、一律不许往外抛 ──
    // 释放路径上 _handleGate 可能已经被 Dispose（Dispose 的最后一步），那时 TryEnter 抛
    // ObjectDisposedException —— 那和"等不到"是同一件事：这一轮不做。
    private bool TryEnterRead(TimeSpan wait)
    {
        try { return _handleGate.TryEnterReadLock(wait); }
        catch (Exception) { return false; }        // ObjectDisposed / LockRecursion / SynchronizationLock
    }

    private bool TryEnterWrite(TimeSpan wait)
    {
        try { return _handleGate.TryEnterWriteLock(wait); }
        catch (Exception) { return false; }
    }

    /// 放锁也要不许抛：Dispose 的最后一步会把这把锁本身收掉，那一路正在读的这一侧
    /// Exit 时就可能拿到 ObjectDisposed / SynchronizationLock —— 那一轮的结果本来就已经不算了，
    /// 为它在通知线程或定时器线程上抛一次会把进程带崩。
    private void ExitRead()
    {
        try { _handleGate.ExitReadLock(); } catch (Exception) { /* 锁已随 Dispose 走掉 */ }
    }

    private void ExitWrite()
    {
        try { _handleGate.ExitWriteLock(); } catch (Exception) { /* 同上 */ }
    }

    /// 被闸门挡回去的轮次（读那一拍、换句柄那一拍都算）。
    private void Skip() => Interlocked.Increment(ref _gateSkips);

    /// IWlanApi.TryOpen 的契约是"永不抛出"（见接口注释）。这里仍然兜一道，因为违约的代价根本不对等：
    /// 调用点只有 Start() 和心跳 TickOnce() 两处，都在没人接异常的流程里 ——
    /// 通知线程与 System.Threading.Timer 的线程池线程上抛出来就是进程级未处理异常，
    /// 而"这一拍什么也没读到"本来只是最多一秒钟的空白。当成通路不可用 ⇒ 照样落 netsh、下一拍再试，
    /// 顺手把可能留下的半开句柄还回去（Dispose 自己也是幂等的，且 _openGate 还在手里）。
    private WlanOpenResult TryOpenGuarded(out Guid interfaceId)
    {
        Guid iface;
        WlanOpenResult result;
        try
        {
            result = _api.TryOpen(out iface);
        }
        catch (Exception)
        {
            try { _api.Dispose(); } catch { /* 违约到 Dispose 也抛的实现：能做的只剩下当它不可用 */ }
            iface = Guid.Empty;
            result = WlanOpenResult.Unavailable;
        }
        interfaceId = iface;
        return result;
    }

    /// 心跳与通知回调共用的"读一次"。两道闸，缺一不可：
    ///
    /// ① 装配那把共享命令门（界面四个按钮、托盘三条菜单、60 秒 ticker 打的都是它）：
    ///    有命令在途就当场放弃这一轮，**绝不排队**。派单点名的就是这一格——一次落在登录中途的读
    ///    如果排进协调器那把非重入锁后面，就是最多 22 秒之后才真的读，读到的还是"已经不是刚才那个"
    ///    的网络，紧接着又把一次登录接上。拒了这件事由门自己写进诊断包（click://无线读取）。
    /// ② 一把读写锁（_handleGate）：读 = 这一次 wlanapi 读取，写 = 开/关句柄。
    ///    门做不到的那一半由它补上：Dispose 要能在关句柄之前等在途读收尾，
    ///    而 try-acquire 的门只回答"有没有人在跑"，不会等。
    ///
    /// 次序是要害：闸门只圈住"读"，对外发事件（Publish）留在两道闸**之外**。
    /// 订阅者里可能同步跑一整轮登录（协调器那个 async void 在无竞争锁上会一路跑到第一次 HTTP），
    /// 把 Publish 关在闸里 = 让一次后台读取把界面点死 22 秒，正是要避免的那一侧。
    /// 异常不能逃出去——它跑在定时器线程和原生回调线程上，没人接；读挂了就报"未连接"，等下一拍。
    private void RefreshOnceSafely()
    {
        try
        {
            if (_commandGate is not { } gate)
            {
                if (ReadLocked(out var plain)) Publish(plain);
                return;
            }
            AccessPoint? ap = null;
            var got = false;
            var running = gate.RunAsync(ReadGateName, _ =>
            {
                got = ReadLocked(out ap);
                return Task.CompletedTask;
            });
            // 命令体是同步的，正常一定已经完成；没完成/被拒/抛了就都算"这一轮没读到"。
            if (!running.IsCompleted) { Skip(); return; }
            if (running.IsFaulted) { _ = running.Exception; return; }      // 观察掉，不留未观察异常
            if (!running.Result) { Skip(); return; }                      // 门被占着：拒（门已经把它写进日志了）
            if (got) Publish(ap);
        }
        catch { /* 状态源宁可短暂报 null，也不能把托盘进程带崩 */ }
    }

    /// 读写锁之内的那一次读。等不到锁（句柄正在交换或已释放）就放弃这一轮，不排队：
    /// 无限等的读侧与"会等在途回调收尾"的 WlanCloseHandle 套在一起就是死锁。
    private bool ReadLocked(out AccessPoint? ap)
    {
        ap = null;
        if (!TryEnterRead(ReadEntryWait)) { Skip(); return false; }
        try
        {
            ap = Read();
        }
        catch { return false; }
        finally { ExitRead(); }
        return true;
    }

    /// 只读一次当前状态，SSID / IP / MAC 真变了才对外发事件。生产通路请走心跳 TickOnce（它先重开句柄）。
    ///
    /// 这个公开出口过读写锁（句柄安全），但**不**过命令门：人主动要的那一次（诊断、手动补刷新）
    /// 不该被后台一条在途命令悄悄拒掉。界面与托盘上的按钮走的仍然是同一把门（见 MainForm/TrayApp）。
    public void RefreshOnce()
    {
        try { if (ReadLocked(out var ap)) Publish(ap); }
        catch { /* 同上：这一轮不报，下一拍再来 */ }
    }

    /// "发一次"：状态真变了才对外发事件。永远在两道闸之外调用（理由见 RefreshOnceSafely）。
    private void Publish(AccessPoint? ap)
    {
        bool changed;
        lock (_gate)
        {
            changed = _current?.Ssid != ap?.Ssid || _current?.Ipv4 != ap?.Ipv4
                || _current?.MacNoSeparator != ap?.MacNoSeparator;
            _current = ap;
        }
        if (changed) Changed.Invoke(ap);
    }

    /// 只读诊断：native 通路看到的空口 SSID 与**配置文件名**分列返回（自检包、日志用），
    /// 外加关联属性里的信号质量——派单要的那一项就摆在这儿，读得到就报，读不到报 null。
    /// 授权判定只用 Ssid —— ProfileName 出现在这里恰恰是为了让人核对它是另一个字段。
    public (string Ssid, string ProfileName, Guid InterfaceId, int? Quality)? ReadNativeDetail()
    {
        Guid iface;
        bool native;
        lock (_gate)
        {
            if (_disposed) return null;
            iface = _iface;
            native = _state != WlanOpenResult.Unavailable;
        }
        if (!native) return null;
        // 诊断这一路同样不能与关句柄并行：它调的是同一个 TryRead。等不到锁就报"读不到"。
        if (!TryEnterRead(ReadEntryWait)) { Skip(); return null; }
        try
        {
            if (!_api.TryRead(iface, out var info)) return null;
            return (info.Ssid, info.ProfileName, iface, info.Quality);
        }
        finally { ExitRead(); }
    }

    private AccessPoint? Read()
    {
        Guid iface;
        bool native;
        lock (_gate)
        {
            if (_disposed) return null;                 // 释放之后不再碰任何外部通路
            iface = _iface;
            native = _state != WlanOpenResult.Unavailable;
        }

        string? ssid = null;
        Guid? bound = null;
        if (native)
        {
            if (_api.TryRead(iface, out var info))
            {
                ssid = info.Ssid;                       // 未连接时就是空串，不降级：native 的状态是权威结论
                bound = iface;                          // 地址必须归属到这张接口上
            }
            else
            {
                // 通路中途坏了：这一轮降级，下一拍的心跳重开句柄。
                lock (_gate) _state = WlanOpenResult.Unavailable;
            }
        }
        if (ssid is null)
        {
            var (netshSsid, netshIface) = NetshSsidSafe();  // 只在 wlanapi 真的不可用时才 spawn 进程
            ssid = netshSsid;
            bound = netshIface;
        }

        var (ip, mac) = AddressesSafe(bound);
        return BuildAccessPoint(ssid ?? "", ip, mac);
    }

    private (string? Ssid, Guid? InterfaceId) NetshSsidSafe()
    {
        try { return _netshSsid(); }
        catch { return (null, null); }                  // 降级链路的每一环都只是"读不到"，绝不是异常
    }

    private (string? Ipv4, string? Mac) AddressesSafe(Guid? interfaceId)
    {
        try { return _addresses(interfaceId); }
        catch { return (null, null); }
    }

    // ── 纯逻辑：地址归属 ──

    public readonly record struct WirelessNic(string InterfaceId, string? Ipv4, string? Mac);

    /// 只认"无线 + Up"的网卡，并把地址绑到 wlanapi 给的那个接口 GUID 上。
    public static (string? Ipv4, string? Mac) WlanIPv4AndMac(Guid? interfaceId = null)
    {
        var nics = new List<WirelessNic>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            nics.Add(new WirelessNic(nic.Id, Ipv4Of(nic), nic.GetPhysicalAddress().ToString()));
        }
        return PickAddress(nics, interfaceId);
    }

    /// GUID 命中即定，命中不了才退回"第一张 Up 的无线网卡"。
    /// 命中但那张卡没有 IPv4 时返回 null：借邻居（Wi-Fi Direct / 热点）的地址去登录，
    /// 等于把一个从没授权过的网络当成校园网提交账号。
    internal static (string? Ipv4, string? Mac) PickAddress(IReadOnlyList<WirelessNic> nics, Guid? interfaceId)
    {
        WirelessNic? firstUp = null;
        foreach (var nic in nics)
        {
            if (interfaceId is { } id && InterfaceIdMatches(nic.InterfaceId, id)) return (nic.Ipv4, nic.Mac);
            firstUp ??= nic;
        }
        return firstUp is { } fallback ? (fallback.Ipv4, fallback.Mac) : (null, null);
    }

    /// NetworkInterface.Id 在 Windows 上是 "{大写 GUID}"（带花括号），wlanapi 给的是裸 GUID。
    internal static bool InterfaceIdMatches(string? nicId, Guid interfaceId)
    {
        if (string.IsNullOrWhiteSpace(nicId) || interfaceId == Guid.Empty) return false;
        return Guid.TryParse(nicId.Trim().TrimStart('{').TrimEnd('}'), out var parsed) && parsed == interfaceId;
    }

    private static string? Ipv4Of(NetworkInterface nic)
        => nic.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.Any.Equals(a.Address) && !IPAddress.Loopback.Equals(a.Address))
            .Select(a => a.Address.ToString()).FirstOrDefault();

    // ── 纯逻辑：MAC 与三元组 ──

    /// 门户 logout URL 要的是"小写、无分隔符"形式（抓包已验证），三种常见写法统一折算。
    public static string NormalizeMac(string mac)
        => mac.Replace("-", "").Replace(":", "").ToLowerInvariant();

    public static AccessPoint? BuildAccessPoint(string? ssid, string? ipv4, string? mac)
    {
        if (string.IsNullOrWhiteSpace(ssid) || string.IsNullOrWhiteSpace(ipv4) || string.IsNullOrWhiteSpace(mac))
            return null;
        return new AccessPoint(ssid!.Trim(), ipv4, NormalizeMac(mac));
    }

    // ── 降级链路：netsh wlan show interfaces ──

    /// wlanapi 彻底不可用时才用。任何失败（命令不存在、超时、输出看不懂）都只是"读不到"。
    internal static (string? Ssid, Guid? InterfaceId) ReadSsidViaNetsh()
    {
        var output = Run("netsh", "wlan show interfaces");
        return output is null ? (null, null) : ParseNetshInterfaces(output);
    }

    /// netsh 里 GUID 行在 SSID 行之前，所以按"最近看到的 GUID"配对即可（每个接口块都会打自己的 GUID）。
    /// 标签必须整段等于 SSID —— "AP BSSID"、"广播的 SSID"、"配置文件/Profile"（那是配置文件名）都不算。
    internal static (string? Ssid, Guid? InterfaceId) ParseNetshInterfaces(string output)
    {
        Guid? guid = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("GUID", StringComparison.OrdinalIgnoreCase))
            {
                if (Guid.TryParse(value, out var parsed)) guid = parsed;    // 下一张接口的 GUID 会覆盖它
                continue;
            }
            if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase) && value.Length > 0) return (value, guid);
        }
        return (null, null);
    }

    /// 跑一个只读命令行工具并拿回解码后的输出；失败一律 null，绝不抛出。
    /// 不预设 StandardOutputEncoding：netsh 的输出码页跟着控制台走（中文机器可能是 GBK），
    /// 所以取原始字节再按 UTF-8 → GBK 双路解，否则中文 SSID 会变成一串问号。
    internal static string? Run(string fileName, string arguments, int timeoutMs = 5000)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!proc.Start()) return null;
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
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }
                return null;
            }
            return !pumping.Wait(timeoutMs) ? null : WifiNative.DecodeText(pumping.Result);
        }
        catch (Exception)
        {
            return null;      // Win32Exception（没有 netsh.exe）/ InvalidOperationException 都算"读不到"
        }
    }

    // ── 生命周期 ──

    public void Dispose()
    {
        System.Threading.Timer? poll;
        lock (_gate)
        {
            if (_disposed) return;                      // 幂等
            _disposed = true;
            poll = _poll;
            _poll = null;
            _state = WlanOpenResult.Unavailable;
            _iface = Guid.Empty;
            _current = null;                            // 释放之后不给下游留一个假的"已连接"
        }
        // Timer.Dispose() 只是"提交取消请求"，不等待在途回调：那一拍完全可以在对象死了之后
        // 才把 netsh 进程 spawn 出来。用 Dispose(WaitHandle) 等它真跑完（有上限），
        // 等完再关句柄——回调还在 TryRead 里就 WlanCloseHandle，等于把一次 use-after-free 留给驱动。
        if (poll is not null)
        {
            using var drained = new ManualResetEvent(false);
            poll.Dispose(drained);
            drained.WaitOne(TickDrainTimeout);          // 等不到也只能继续：释放路径不许挂死
        }
        // 等完定时器那一拍还不够：通知回调那一拍跑在 wlanapi 自己的线程上，定时器管不着它。
        // 所以关句柄要拿读写锁的**写侧**——它与 ReadLocked 里那一次 TryRead 互斥，
        // 于是"Dispose 关句柄 ∥ 在途读"这一格从结构上就不可能出现（有界等待，等不到也只能关）。
        var entered = TryEnterWrite(TickDrainTimeout);
        if (!entered) Skip();                            // 只能带着这一笔继续：退出流程不许挂死
        try { lock (_openGate) _api.Dispose(); }          // 关句柄即注销通知；与心跳的重开互斥
        finally
        {
            if (entered) ExitWrite();
            try { _handleGate.Dispose(); } catch (Exception) { /* 已经在释放了 */ }
        }
    }
}

/// 真机实现：wlanapi 的句柄、接口选择与通知注册。
/// 契约：两个 Try* 都不许外抛（见 IWlanApi）——TryOpen 的 catch-all 与 TryRead 的 catch (Exception)
/// 就是契约本体，不是防御性冗余。删掉它们，wlanapi 的一次故障就从"这一轮读不到"升级成"进程没了"，
/// 而且是在通知线程/定时器线程上没人的那种。
internal sealed class WlanApi(Action onChanged) : IWlanApi
{
    private readonly object _gate = new();
    private IntPtr _client = IntPtr.Zero;
    private WifiNative.WlanNotificationCallback? _callbackRef;   // 委托必须被持有，否则 GC 之后回调指针就飞了

    public WlanOpenResult TryOpen(out Guid interfaceId)
    {
        interfaceId = Guid.Empty;
        Close();                                              // 幂等：先丢掉上一次可能半开的句柄
        try
        {
            if (WifiNative.WlanOpenHandle(2, IntPtr.Zero, out _, out var client) != 0) return WlanOpenResult.Unavailable;
            lock (_gate) _client = client;

            if (!TryPickInterface(client, out interfaceId))
            {
                Close();
                return WlanOpenResult.Unavailable;
            }

            _callbackRef = (_, _) => onChanged();
            var registered = WifiNative.WlanRegisterNotification(client,
                (uint)(WifiNative.WlanNotificationConnectionStart | WifiNative.WlanNotificationConnectionEnd),
                1, _callbackRef, IntPtr.Zero, IntPtr.Zero, out _) == 0;
            return registered ? WlanOpenResult.Ready : WlanOpenResult.PollWithoutNotifications;
        }
        catch (DllNotFoundException) { Close(); return WlanOpenResult.Unavailable; }
        catch (EntryPointNotFoundException) { Close(); return WlanOpenResult.Unavailable; }
        catch (Exception) { Close(); return WlanOpenResult.Unavailable; }   // 状态源不能把宿主带崩
    }

    private bool TryPickInterface(IntPtr client, out Guid interfaceId)
    {
        interfaceId = Guid.Empty;
        if (WifiNative.WlanEnumInterfaces(client, IntPtr.Zero, out var list) != 0) return false;
        try
        {
            interfaceId = WifiNative.PickInterface(WifiNative.ReadInterfaces(list));
            return interfaceId != Guid.Empty;
        }
        finally { WifiNative.WlanFreeMemory(list); }
    }

    public bool TryRead(Guid interfaceId, out WifiNative.ConnectionInfo info)
    {
        info = WifiNative.ConnectionInfo.None;
        if (interfaceId == Guid.Empty) return false;
        var client = Snapshot();
        if (client == IntPtr.Zero) return false;
        try
        {
            if (WifiNative.WlanQueryInterface(client, ref interfaceId, WifiNative.WlanOpcodeCurrentConnection,
                    IntPtr.Zero, out var size, out var data, out _) != 0) return false;
            try
            {
                if (!WifiNative.Fits(size)) return false;
                info = WifiNative.ReadConnection(data, size);
                return true;
            }
            finally { WifiNative.WlanFreeMemory(data); }
        }
        catch (Exception) { return false; }
    }

    private IntPtr Snapshot() { lock (_gate) return _client; }

    public void Dispose() => Close();

    private void Close()
    {
        IntPtr client;
        lock (_gate)
        {
            client = _client;
            _client = IntPtr.Zero;
        }
        if (client == IntPtr.Zero) return;
        try { WifiNative.WlanCloseHandle(client, IntPtr.Zero); }
        catch (Exception) { /* 注销失败也不能抛：进程正在退出或重开 */ }
    }
}
