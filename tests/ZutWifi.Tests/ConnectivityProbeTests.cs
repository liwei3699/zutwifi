using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ZutWifi.Core;

namespace ZutWifi.Tests;

/// 这个类之前**一条直接用例都没有** —— 协调器那几十条全用 `FakeProbe`（瞬间返回）。
/// 结果就是真机上出过一次"门户已经回 3.htm，外网这一步 16 分钟不返回"，
/// 而那 16 分钟里 `CommandGate` 落不下来：定时刷新、四个按钮、通知全是死的，日志里还一行新的都没有。
/// 所以这里钉的不是"通没通"，而是**它必须在有限时间内给个答案**。
public class ConnectivityProbeTests
{
    /// 永远不完成的请求。真机那次卡在哪一段（解析？连接？）没抓到证据，
    /// 所以修法不许依赖"某个阶段一定会响应取消" —— 这条测的就是"什么都不回"。
    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        private readonly TaskCompletionSource<HttpResponseMessage> Never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            ct.UnsafeRegister(static s => ((TaskCompletionSource<HttpResponseMessage>)s!).TrySetCanceled(), Never);
            return Never.Task;
        }
    }

    private sealed class ScriptedHandler(params Func<Task<HttpResponseMessage>>[] replies) : HttpMessageHandler
    {
        private int _i;
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            var next = _i < replies.Length ? replies[_i++] : replies[^1];
            return next();
        }
    }

    private static HttpResponseMessage Text(int status, string body = "Microsoft NCSI") =>
        new((HttpStatusCode)status) { Content = new StringContent(body) };

    [Fact]
    public async Task 第一个目标回200就判通并且不再敲第二个()
    {
        var h = new ScriptedHandler(() => Task.FromResult(Text(200)));
        var online = await new HttpConnectivityProbe(new HttpClient(h)).IsOnlineAsync(default);
        Assert.True(online);
        Assert.Single(h.Urls);
    }

    [Fact]
    public async Task 两个目标都跳到别处时判不通而不是当成成功()
    {
        // 探针客户端不跟跳转（跟了会把门户的劫持页读成 200 = 已上网），所以 302 在这里就是"没通"。
        var h = new ScriptedHandler(() => Task.FromResult(Text(302)));
        Assert.False(await new HttpConnectivityProbe(new HttpClient(h), attempts: 1).IsOnlineAsync(default));
        Assert.Equal(2, h.Urls.Count);      // 一轮把两个目标都敲一遍
    }

    [Fact]
    public async Task 请求永不返回时在预算内给出答案而不是把调用方钉死()
    {
        var h = new HangingHandler();
        var probe = new HttpConnectivityProbe(new HttpClient(h), attempts: 3, budget: TimeSpan.FromMilliseconds(250));
        var sw = Stopwatch.StartNew();
        var online = await probe.IsOnlineAsync(default);
        sw.Stop();

        Assert.False(online);                                   // 到点先答"没通"：宁可让人点一下重新检测
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"预算 250ms，实际等了 {sw.ElapsedMilliseconds}ms");
        // 到点时取消会让当前那次请求落下，循环于是又敲了第二个目标，然后立刻被预算拦下 —— 所以是 1~2，
        // 但绝不是 6（三轮全敲完）。这一格要钉的是"有尽头"，不是"一次都不许多"。
        Assert.InRange(h.Requests, 1, 2);
    }

    [Fact]
    public async Task 调用方取消时取消照旧传下去而不是被预算吞掉()
    {
        var h = new HangingHandler();
        using var cts = new CancellationTokenSource();
        var probe = new HttpConnectivityProbe(new HttpClient(h), budget: TimeSpan.FromSeconds(30));
        var task = probe.IsOnlineAsync(cts.Token);
        await cts.CancelAsync();
        // 取消是"这一轮不该再说话了"，由协调器自己接；探针不许把它变成一句"外网不通"。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void 默认预算是个有限的数而不是无限等()
    {
        // 交付出去的那一份用的是默认值。上面几条都自带小预算，所以它们绿着也证明不了默认值没被人
        // 改成 InfiniteTimeSpan —— 那就等于这道闸根本没装。不真等 12 秒：直接读那个数。
        var src = File.ReadAllText(SourcePath("src/ZutWifi/Core/ConnectivityProbe.cs"), new UTF8Encoding(false, true));
        var m = Regex.Match(src, @"DefaultBudget\s*=\s*TimeSpan\.FromSeconds\((\d+)\)");
        Assert.True(m.Success, "没找到 DefaultBudget = TimeSpan.FromSeconds(N)：默认预算换了写法，这道闸就没人盯着了");
        var seconds = int.Parse(m.Groups[1].Value);
        Assert.InRange(seconds, 1, 30);
    }

    static string SourcePath(string relative)
    {
        for (var d = new DirectoryInfo(System.AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ZutWifi.sln"))) return Path.Combine(d.FullName, relative);
        throw new InvalidOperationException("找不到仓库根（ZutWifi.sln）");
    }
}
