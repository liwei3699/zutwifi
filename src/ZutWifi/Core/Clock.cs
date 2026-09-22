namespace ZutWifi.Core;

public interface IClock { DateTimeOffset UtcNow { get; } Task Delay(TimeSpan by, CancellationToken ct); }

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan by, CancellationToken ct) => Task.Delay(by, ct);
}

/// 单调毫秒（从系统启动算起）。在线时长在两次刷新之间往前插值只认它。
/// 为什么不用 `DateTimeOffset.UtcNow` 做差：改系统时间、时区或一次 NTP 回拨，
/// 都会让那个数字倒退或猛跳一下 —— 那是"看起来像程序有 bug"的东西，而它其实只是用了会倒着走的尺子。
public static class Monotonic
{
    public static long TickMs => Environment.TickCount64;
}
