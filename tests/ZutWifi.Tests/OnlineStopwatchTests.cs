using ZutWifi.Config;
using ZutWifi.Core;
using ZutWifi.Shell;

namespace ZutWifi.Tests;

/// 在线时长是"门户给的那一份 + 本地插值"，界面上每秒自己往前走。
/// 之前它只显示门户当场答的那个数，而门户只在每次刷新时答一次 —— 于是登录后那一格里
/// 挂着一个 00:00:00，看着就像程序坏了（真机上就是这么被发现的）。
public class OnlineStopwatchTests
{
    private const long Anchor = 10_000;

    [Theory]
    [InlineData(Anchor, 42)]                      // 刚读到：就是门户那份
    [InlineData(Anchor + 5_000, 47)]              // 五秒后：本地补五秒
    [InlineData(Anchor + 65_000, 107)]            // 跨过一分钟照样连续，不等下一次刷新
    [InlineData(Anchor - 3_000, 42)]              // 尺子倒着走也不许把数字改小
    public void 秒数等于门户那份加上本地走过的(long now, int expected)
    {
        var s = new AppStatus(AppPhase.Online, "zut-stu", "10.133.93.194", null, 42, OnlineAtTickMs: Anchor);
        Assert.Equal(expected, s.SecondsAt(now));
    }

    [Fact]
    public void 没有锚点时照原样给而不猜一个数()
    {
        // 别的相位（失败/空闲）根本没有"什么时候读到的"这件事，硬凑锚点会显示一个假在走的时长。
        Assert.Equal(7, new AppStatus(AppPhase.Online, null, null, null, 7).SecondsAt(Anchor + 9_000));
        Assert.Null(new AppStatus(AppPhase.Idle).SecondsAt(Anchor + 9_000));
    }

    [Fact]
    public void 界面上那一行每秒往前走()
    {
        var now = Anchor;
        using var form = Build(() => now);
        form.Apply(Online(42), ssidWhitelisted: true);
        Assert.Contains("在线 00:00:42", form.DetailText);

        now += 5_000;
        form.SimulateSecondTick();                       // 计时器那一拍做的就只是重画
        Assert.Contains("在线 00:00:47", form.DetailText);
        Assert.Equal(47, form.OnlineSecondsShown);
    }

    [Fact]
    public void 秒表那一拍不碰任何一条命令()
    {
        // 这一条是边界：走秒表绝不许变成"每秒去敲一次门户"。
        var cmd = new Counting();
        var now = Anchor;
        using var form = Build(() => now, cmd);
        form.Apply(Online(1), ssidWhitelisted: true);
        for (var i = 0; i < 5; i++) { now += 1_000; form.SimulateSecondTick(); }
        Assert.Contains("在线 00:00:06", form.DetailText);
        Assert.Equal(0, cmd.Calls);
    }

    [Theory]
    [InlineData(AppPhase.Online, true)]
    [InlineData(AppPhase.Degraded, true)]
    [InlineData(AppPhase.Failed, false)]
    [InlineData(AppPhase.Idle, false)]
    [InlineData(AppPhase.LoggingIn, false)]
    public void 只有确实在线的两格里秒表才走(AppPhase phase, bool expected)
    {
        using var form = Build(() => Anchor);
        var ticking = phase is AppPhase.Online or AppPhase.Degraded;
        // 那两格带非空 Reason（StatusPresenter 的硬契约），其余一律 null。
        var reason = phase switch
        {
            AppPhase.Degraded => "门户返回成功，外网未通，可点重新检测",
            AppPhase.Failed => "门户拒绝",
            _ => null,
        };
        form.Apply(new AppStatus(phase, "zut-stu", "10.133.93.194", reason,
            ticking ? 1 : null, OnlineAtTickMs: ticking ? Anchor : null), ssidWhitelisted: true);
        Assert.Equal(expected, form.TickerEnabled);
    }

    private readonly Support.TempSpace _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private MainForm Build(Func<long> monotonic, ILoginCommands? cmd = null)
    {
        var dir = _tmp.NewDir("zwsw");
        return new MainForm(cmd ?? new Counting(), new SettingsStore(dir), new SecretStore(dir), null,
            monotonic: monotonic);
    }

    static AppStatus Online(int seconds) =>
        new(AppPhase.Online, "zut-stu", "10.133.93.194", null, seconds, OnlineAtTickMs: Anchor);

    private sealed class Counting : ILoginCommands
    {
        public int Calls;
        public Task LoginAsync(CancellationToken ct) { Calls++; return Task.CompletedTask; }
        public Task LogoutAsync(CancellationToken ct) { Calls++; return Task.CompletedTask; }
        public Task ReprobeAsync(CancellationToken ct) { Calls++; return Task.CompletedTask; }
        public Task RecoverReloginAsync(CancellationToken ct) { Calls++; return Task.CompletedTask; }
    }
}
