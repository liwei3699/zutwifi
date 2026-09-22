using System.Net;
using System.Text;

namespace ZutWifi.Tests.Support;

public sealed record SentRequest(HttpMethod Method, Uri Uri, string? Body, string? CookieHeader);

public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _behaviours = new();
    public List<SentRequest> Requests { get; } = new();

    public void Enqueue(int status, (string name, string value)[] headers, string body)
        => _behaviours.Enqueue(_ =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8) };
            foreach (var (name, value) in headers)
            {
                if (name.Equals("Location", StringComparison.OrdinalIgnoreCase)) resp.Headers.Location = new Uri(value);
                else if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) resp.Content.Headers.TryAddWithoutValidation(name, value);
                else resp.Headers.TryAddWithoutValidation(name, value);
            }
            return resp;
        });

    public void EnqueueThrow(Func<HttpRequestMessage, Exception> thrower)
        => _behaviours.Enqueue(req => throw thrower(req));

    /// 直接吃真机响应头文本，省得每次手写 headers 数组。
    public void EnqueueRaw(string httpResponseText)
    {
        var lines = httpResponseText.Replace("\r\n", "\n").Split('\n');
        var status = int.Parse(lines[0].Split(' ')[1]);
        var headers = lines.Skip(1).TakeWhile(l => l.Trim().Length > 0)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .Select(p => (p[0].Trim(), p[1].Trim())).ToArray();
        var body = string.Join('\n', lines.Skip(1).SkipWhile(l => l.Trim().Length > 0).Skip(1));
        Enqueue(status, headers, body);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var body = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
        Requests.Add(new SentRequest(req.Method, req.RequestUri!, body,
            req.Headers.Contains("Cookie") ? string.Join("; ", req.Headers.GetValues("Cookie")) : null));
        // 队列耗尽时给一个确定的"门户不可达"响应，而不是让 InvalidOperationException 从 handler 里逃逸：
        // 去抖与刷新用例刻意多消耗请求，兜底把"少排了一份 fixture"暴露成断言失败（请求数/状态），
        // 而不是暴露成一个跟被测逻辑无关的异常。PortalGateway 把非 2xx 读成 Unknown/传输错误，链路不炸。
        return _behaviours.Count > 0 ? _behaviours.Dequeue()(req)
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("no fixture queued") };
    }
}

public static class Fixtures
{
    public static string Read(string name) =>
        // System. 前缀是必须的：Task 17 之后 ZutWifi 命名空间里有了自己的 AppContext（组合根），
        // 而本文件在 ZutWifi.Tests 里，裸写的 AppContext 会先在外层命名空间命中那个类。
        File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", name));
}
