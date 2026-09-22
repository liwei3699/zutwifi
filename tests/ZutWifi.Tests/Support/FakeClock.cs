using ZutWifi.Core;
namespace ZutWifi.Tests.Support;

public sealed class FakeClock(DateTimeOffset? start = null) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } =
        start ?? new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);
    public List<TimeSpan> Waits { get; } = [];

    public Task Delay(TimeSpan by, CancellationToken ct) { Waits.Add(by); Advance(by); return Task.CompletedTask; }
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    public void AdvanceSeconds(double s) => Advance(TimeSpan.FromSeconds(s));
}
