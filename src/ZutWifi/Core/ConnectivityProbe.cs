namespace ZutWifi.Core;

public interface IConnectivityProbe { Task<bool> IsOnlineAsync(CancellationToken ct); }

/// 门户说认证成功不等于互联网通了。这一步是需求里的"登录后自动测试网络连通性"。
/// 两个互相独立的判据：HTTP 纯文本探针 + HTTPS 站点，避免单一目标被墙造成误判。
///
/// `budget` 是**整轮**的硬预算，不是每次请求的超时：真机上出现过"门户已经回 3.htm，
/// 这一步 16 分钟不返回"，而那 16 分钟里 `CommandGate` 落不下来 —— 定时刷新、四个按钮、
/// 通知全是死的，日志里连一行新的都没有（它卡在唯一不写日志的那一步上）。
/// 客户端自己的 5 秒超时管不住卡住的那一段，所以这里到点就先回答"没通"，
/// 晚到的那一次结果没人要 —— 但异常必须接下来，不然变成一次没人观察的失败任务。
public sealed class HttpConnectivityProbe(HttpClient http, int attempts = 3, TimeSpan? budget = null)
    : IConnectivityProbe
{
    /// 12 秒：三次两轮里最坏的一次都不该让同学觉得程序没了。真值由用例盯着（默认值必须是有限数）。
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(12);

    private readonly TimeSpan _budget = budget ?? DefaultBudget;

    private static readonly Uri[] Targets =
    [
        new("http://www.msftconnecttest.com/connecttest.txt"),
        new("https://www.bing.com/"),
    ];

    public async Task<bool> IsOnlineAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_budget);                       // 能让正常那次请求自己停下来
        var rounds = RoundsAsync(limit.Token);
        var winner = await Task.WhenAny(rounds, Task.Delay(_budget, CancellationToken.None));   // 停不下来也照样答
        if (winner != rounds) ObserveLate(rounds);
        try
        {
            return winner == rounds && await rounds;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;      // 是**我们**到点了，不是调用方要中止：那种情况取消照旧往上抛，由协调器收尾
        }
    }

    private async Task<bool> RoundsAsync(CancellationToken ct)
    {
        for (var round = 0; round < attempts; round++)
        {
            foreach (var target in Targets)
            {
                try
                {
                    using var resp = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            }
            if (round < attempts - 1) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }

    /// 卡住那一次将来不管是完成还是抛异常，都没人再看了；这里替它把异常读掉。
    private static void ObserveLate(Task orphan) => _ = orphan.ContinueWith(
        static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
