using ZutWifi.Core;
namespace ZutWifi.Tests.Support;

public sealed class FakeProbe(bool online, int failFirstTimes = 0) : IConnectivityProbe
{
    private int _calls;
    public int Calls => _calls;
    public Task<bool> IsOnlineAsync(CancellationToken ct) => Task.FromResult(_calls++ >= failFirstTimes && online);
}
