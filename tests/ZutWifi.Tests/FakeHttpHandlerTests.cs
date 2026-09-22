using System.Text.RegularExpressions;
using ZutWifi.Tests.Support;
namespace ZutWifi.Tests;
public class FakeHttpHandlerTests
{
    [Fact]
    public async Task 按入队顺序返回并记录请求()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(302, new[] { ("Location", "http://1.1.1.1/3.htm") }, "");
        var resp = await new HttpClient(h).PostAsync("http://x/y", new StringContent("a=1"));
        Assert.Equal(302, (int)resp.StatusCode);
        Assert.Equal("http://1.1.1.1/3.htm", resp.Headers.Location!.ToString());
        Assert.Single(h.Requests);
        Assert.Equal("a=1", h.Requests[0].Body);
        Assert.Null(h.Requests[0].CookieHeader);
    }

    /// <summary>
    /// 正向证明 Cookie 捕获通道真的工作：Task 6 的「登录请求不带任何 Cookie」只断言 null，
    /// 捕获坏掉时那条负向断言会真空通过，所以这里必须有一条"带了就一定记下来"的断言。
    /// </summary>
    [Fact]
    public async Task 带Cookie头的请求会被原样记录()
    {
        var h = new FakeHttpHandler();
        h.Enqueue(200, Array.Empty<(string, string)>(), "");
        h.Enqueue(200, Array.Empty<(string, string)>(), "");
        using var http = new HttpClient(h);

        using var single = new HttpRequestMessage(HttpMethod.Post, "http://x/login") { Content = new StringContent("a=1") };
        single.Headers.Add("Cookie", "a=b");
        await http.SendAsync(single);
        Assert.Equal("a=b", h.Requests[0].CookieHeader);

        using var multi = new HttpRequestMessage(HttpMethod.Post, "http://x/login") { Content = new StringContent("a=2") };
        multi.Headers.Add("Cookie", "JSESSIONID=abc");
        multi.Headers.Add("Cookie", "ZUT=1");
        await http.SendAsync(multi);
        Assert.Equal("JSESSIONID=abc; ZUT=1", h.Requests[1].CookieHeader);
    }

    [Fact]
    public async Task 可注入抛出异常的行为()
    {
        var h = new FakeHttpHandler();
        h.EnqueueThrow(_ => throw new HttpRequestException("unreachable"));
        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpClient(h).GetAsync("http://x"));
    }

    [Fact]
    public void fixture里出现3htm成功页()
        => Assert.Contains("3.htm", Fixtures.Read("login_success.txt"));

    /// <summary>
    /// 每个 fixture 都真跑一遍 EnqueueRaw 往返：解析出的状态码与 Location 必须逐字等于 fixture 文本里写的值。
    /// Location 用 Uri.OriginalString 比较，才能钉住 logout_success.txt 的畸形空端口 http://1.1.1.1:/2.htm
    /// （Uri.ToString() 会把它规范成 http://1.1.1.1/2.htm，掩盖 Task 7 真正要吃的原文）。
    /// 用例来自枚举输出目录，新增 fixture 自动多出一条用例，不靠手写清单。
    /// </summary>
    [Theory]
    [MemberData(nameof(全部fixture))]
    public async Task 每个fixture经EnqueueRaw逐字回放(string name)
    {
        var text = Fixtures.Read(name);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var statusLine = StatusLine.Match(lines[0]);

        if (!statusLine.Success)
        {
            // a70.htm 是"页面 body"型 fixture：没有状态行，EnqueueRaw 按契约吃不下（见 report 第 5 节口径），
            // 它只能整段作为 Enqueue 的 body 被消费。这里把这条契约也钉进仓库，而不是留在报告里。
            var h = new FakeHttpHandler();
            Assert.Throws<FormatException>(() => h.EnqueueRaw(text));
            h.Enqueue(200, Array.Empty<(string, string)>(), text);
            using var http = new HttpClient(h);
            using var resp = await http.GetAsync("http://1.1.1.1/a70.htm");
            Assert.Equal(200, (int)resp.StatusCode);
            Assert.Equal(text, await resp.Content.ReadAsStringAsync());
            return;
        }

        var expectedStatus = int.Parse(statusLine.Groups[1].Value);
        var (expectedLocation, expectedBody) = ReadHeadersAndBody(lines);

        var handler = new FakeHttpHandler();
        handler.EnqueueRaw(text);
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("http://1.1.1.1/probe");

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
        Assert.Single(handler.Requests);
    }

    private static readonly Regex StatusLine = new(@"^HTTP/\S+\s+(\d{3})(\s|$)", RegexOptions.Compiled);

    /// 独立于 EnqueueRaw 的逐行读法：取 Location 原文与空行之后的 body。
    private static (string? location, string body) ReadHeadersAndBody(string[] lines)
    {
        string? location = null;
        var i = 1;
        for (; i < lines.Length && lines[i].Trim().Length > 0; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon < 0) continue;
            if (lines[i].AsSpan(0, colon).Trim().Equals("Location".AsSpan(), StringComparison.OrdinalIgnoreCase))
                location = lines[i][(colon + 1)..].Trim();
        }
        return (location, i < lines.Length ? string.Join('\n', lines.Skip(i + 1)) : "");
    }

    /// 覆盖下限（不是用例清单）：这 7 个 fixture 少一个就说明漏拷/改名/误删了，直接失败；
    /// 多出来的文件会作为新用例自动被上面的 Theory 覆盖。
    private static readonly string[] FixtureCoverageFloor =
    {
        "login_success.txt", "login_reject_pwerr.txt", "login_ip_online.txt", "logout_success.txt",
        "offline_9002.txt", "online_9002.txt", "a70.htm",
    };

    public static TheoryData<string> 全部fixture()
    {
        var names = Directory.EnumerateFiles(Path.Combine(System.AppContext.BaseDirectory, "Fixtures"))
                             .Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();
        foreach (var must in FixtureCoverageFloor) Assert.Contains(must, names);
        return new TheoryData<string>(names);
    }
}
