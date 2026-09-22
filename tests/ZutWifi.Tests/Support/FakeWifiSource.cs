using ZutWifi.Wifi;

namespace ZutWifi.Tests.Support;

/// IWifiSource 的测试替身。Raise() 走真机的事件路径（顺带赋值 Current），
/// 也可直接设 Current 后把 AccessPoint 交给 HandleWifiChanged —— 协调器不依赖事件是否被订阅。
public sealed class FakeWifiSource : IWifiSource
{
    public AccessPoint? Current { get; set; }
    public event Action<AccessPoint?> Changed = _ => { };
    public void Raise(AccessPoint? ap) { Current = ap; Changed.Invoke(ap); }
}
