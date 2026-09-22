namespace ZutWifi.Diagnostics;

/// <summary>
/// 自检这一段用的两个 HTTP 客户端：**整个 Diagnostics 目录里唯一的构造点**。
///
/// 为什么单独一个文件、而且拆成两条命名接缝而不是一个带 timeout 参数的通用工厂：
/// 约束④（探测客户端必须禁自动跳转、必须短超时）的全部价值在于"生产路径拿的就是这一个对象"，
/// 而通用工厂会把"改掉那个参数"或"干脆自己 new 一个"的余地留在调用方手里 ——
/// 上一版就是这样：用例钉的是 `NewClient` 的返回值，把 ⑨ 那一句换成 `new HttpClient()` 依旧一片绿，
/// 而认证前那张门户劫持页回的就是 200，跟了跳转会被读成"已上网"，那一轮自检从此只会假绿。
/// 现在一处只有一个名字、超时从常量里取、调用点连参数都没有（只接受替身）：
/// ⑨ 那一行印的就是这里造出来**那一个**对象的超时与跳转开关，另有一道现场闸门（ProbeClientHealth）判它。
///
/// 组合根那一份（AppContext.Build）用的是同一个 AppContext.NewNoRedirect，两处不是两套规矩。
/// </summary>
public static partial class SelfTest
{
    /// <summary>门户客户端的唯一构造点（约束④）：跳转让原样留在响应里，④–⑧ 的判据就住在 Location 头上。</summary>
    internal static (HttpClient client, HttpClientHandler? owned) NewPortalClient(
        HttpMessageHandler? substitute) =>
        AppContext.NewNoRedirect(AppContext.PortalTimeoutSeconds, substitute);

    /// <summary>⑨ 那个外网探针的客户端的唯一构造点：短（探针自己重试 3 轮 × 2 目标）+ 不跟跳转。</summary>
    internal static (HttpClient client, HttpClientHandler? owned) NewProbeClient(
        HttpMessageHandler? substitute) =>
        AppContext.NewNoRedirect(AppContext.ProbeTimeoutSeconds, substitute);

    /// <summary>
    /// 约束④ 的现场闸门：这一轮**真的**用了一个会跟跳转、或者不受 5 秒管着的探测客户端，
    /// 就把整轮判成有问题（退出码 1），而不是让那一句"通"白亮在那里。
    /// 判的是手里这一个对象，不是常量，所以绕过 NewProbeClient 换回默认那个（100 秒）会当场留下痕迹。
    /// 注入替身时 owned 为 null —— 替身本来就不可能跟跳转（它不是 SocketsHttpHandler），
    /// 那一种情况下只有超时这一半可判：离线能做到的极限就在这里，另一半天生要靠构造点唯一。
    /// </summary>
    private static void ProbeClientHealth(Session s, HttpClient client, HttpClientHandler? owned)
    {
        if (owned is { AllowAutoRedirect: true })
            s.Problem("⑨ 的探测客户端会跟跳转：认证前那张门户页的 200 会被读成\"已上网\"，" +
                      "这一轮的旁证不可信（约束④ 破了，见 NewProbeClient）");
        if (client.Timeout != TimeSpan.FromSeconds(AppContext.ProbeTimeoutSeconds))
            s.Problem($"⑨ 的探测客户端超时是 {client.Timeout.TotalSeconds:0.##}s 而不是 " +
                      $"{AppContext.ProbeTimeoutSeconds}s：这一路没走 NewProbeClient 那个工厂");
    }

    /// <summary>⑨ 那一行里的"禁自动跳转"：印的是这一个客户端实际用的那个 handler。</summary>
    private static string SwitchText(HttpClientHandler? owned) =>
        owned is null ? "不适用（注入的替身本来就不跟跳转）" : (owned.AllowAutoRedirect ? "否" : "是");
}
