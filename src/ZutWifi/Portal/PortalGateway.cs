using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using ZutWifi.Diagnostics;

namespace ZutWifi.Portal;

/// 唯一允许访问门户 HTTP 接口的类。判定只看 Location 响应头，所以传入的 HttpClient 必须禁自动重定向。
/// 本类刻意不使用 CookieContainer —— 真机验证零 Cookie 即可认证成功。
/// log 可选：不传时行为与之前完全一致，传了则每个门户调用落一行（表单只落脱敏后的摘要）。
public sealed class PortalGateway(HttpClient http, string portalHost, TransactionLog? log = null)
{
    private static readonly Regex IpField = new("ss5=\"\\s*(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\\s*\"",
        RegexOptions.Compiled);

    /// 门户页面与 ErrorMsg 都是 GBK（码页 936）。全类只解析这一次：
    /// 静态字段初始化器由 CLR 保证在任一成员被触碰前执行且仅执行一次、且线程安全，
    /// 所以注册与取编码的先后是写死的，与调用顺序、测试并行度、进程冷启动都无关。
    /// CodePages 提供程序不注册时 GetEncoding(936) 抛 NotSupportedException。
    private static readonly Encoding Gbk = ResolveGbk();

    private static Encoding ResolveGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }

    public async Task<AuthState> ProbeAsync(CancellationToken ct)
    {
        var url = PortalMessages.ProbeUrl(portalHost);
        int? status = null;
        string? location = null;
        try
        {
            using var resp = await http.GetAsync(url, ct);
            status = (int)resp.StatusCode;
            location = resp.Headers.Location?.ToString();
            AuthState state;
            if (IsRedirect(resp) && resp.Headers.Location?.Host == portalHost) state = AuthState.Unauthenticated;
            // 只有 2xx 的响应体可信：跳向外站的 302 与 4xx/5xx 错误页里出现 "Logout" 也不算已认证。
            else if (!resp.IsSuccessStatusCode) state = AuthState.Unknown;
            else
            {
                var body = await ReadBodyAsync(resp, ct);
                state = body.Contains("Logout", StringComparison.Ordinal) ? AuthState.Authenticated : AuthState.Unknown;
            }
            Record("Probe", "GET", url, status, location, state.ToString());
            return state;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Record("Probe", "GET", url, status, location, AuthState.Unknown.ToString(), failure: ex.Message);
            return AuthState.Unknown;
        }
    }

    public async Task<string?> GetClientIpAsync(CancellationToken ct)
    {
        var url = PortalMessages.LoginPageUrl(portalHost);
        int? status = null;
        string? location = null;
        try
        {
            using var resp = await http.GetAsync(url, ct);
            status = (int)resp.StatusCode;
            location = resp.Headers.Location?.ToString();
            var m = IpField.Match(await ReadBodyAsync(resp, ct));
            var ip = m.Success ? m.Groups[1].Value : null;
            Record("GetIp", "GET", url, status, location, ip is null ? "页面无ss5字段" : "ip=" + ip);
            return ip;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Record("GetIp", "GET", url, status, location, null, failure: ex.Message);
            return null;
        }
    }

    public async Task<PortalResult> LoginAsync(Credential cred, string ip, CancellationToken ct)
    {
        var url = PortalMessages.LoginUrl(portalHost, ip);
        var form = PortalMessages.LoginForm(cred);
        var body = string.Join("&", form.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))
            + PortalMessages.FormSuffix;
        int? status = null;
        string? location = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = FormBody(body) };
            using var resp = await http.SendAsync(req, ct);
            status = (int)resp.StatusCode;
            location = resp.Headers.Location?.ToString() ?? "";
            var result = LoginVerdict(resp, location);
            // formBody 必须经 RedactForm 才能进日志：这一行是会被同学整份发回来的东西。
            Record("Login", "POST", url, status, location,
                result.Reason ?? result.Outcome.ToString(), formBody: body);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Record("Login", "POST", url, status, location, PortalOutcome.TransportError.ToString(),
                failure: ex.Message, formBody: body);
            return PortalResult.Transport("门户不可达：" + ex.Message);
        }
    }

    /// 登录判据：只认 Location 路径段结尾的 3.htm / 2.htm，别的结果一律是"未知"而不是"失败"。
    private static PortalResult LoginVerdict(HttpResponseMessage resp, string loc)
    {
        if (!IsRedirect(resp))
            return PortalResult.Transport($"门户响应 {(int)resp.StatusCode} {resp.ReasonPhrase}");
        // 认证成功/失败只由这两个页面标记证明：跳到别处（门户改版、被网关拦截、会话过期）结果未知，
        // 不能伪造一个 ErrorCode=-1 的"门户拒绝"让上层按失败去退避重登。
        // 比对只用 ? 之前的路径段并要求以 /3.htm 结尾：Contains("3.htm") 会同时把 13.htm、
        // 以及 ?next=/3.htm 这种"跳转参数里提到的页面"读成认证成功——那是本判据里最危险的假阳性
        // （停止重登、托盘亮成已连接，而账号其实没上去）。
        var path = LocationPath(loc);
        if (path.EndsWith("/3.htm", StringComparison.Ordinal)) return PortalResult.Success(loc);
        if (!path.EndsWith("/2.htm", StringComparison.Ordinal))
            return PortalResult.Transport($"门户跳转位置无认证判据：{loc}");
        var (code, text) = DecodePortalError(loc);
        var known = code ?? -1;
        return PortalResult.Rejected(loc, known, PortalErrorCodes.Describe(known, text));
    }

    /// Location 的路径部分：切掉 ? 之后的一切，认证标记只在这一段上比对。
    /// ErrorMsg 仍在完整 loc 上解（它本来就住在查询段里）。
    /// 只切 ? 不切 #：Location 头不会带片段（浏览器发的片段不会进请求），
    /// 真出现带 # 的一律落到"无判据"那一侧——假阴性比假成功好。
    private static string LocationPath(string location)
    {
        var i = location.IndexOf('?');
        return i < 0 ? location : location[..i];
    }

    /// 抓包的表单体是裸 application/x-www-form-urlencoded、不带 charset；
    /// StringContent 的三参构造会自动追加 "; charset=us-ascii"，所以取回媒体类型覆写一次。
    private static StringContent FormBody(string body)
    {
        // body 已由 Uri.EscapeDataString 逐字段百分号编码，只剩 ASCII，用哪个编码写字节都不影响结果。
        var content = new StringContent(body, Encoding.ASCII);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return content;
    }

    public async Task<int?> GetOnlineSecondsAsync(CancellationToken ct)
    {
        var url = PortalMessages.ProbeUrl(portalHost);
        int? status = null;
        string? location = null;
        try
        {
            using var resp = await http.GetAsync(url, ct);
            status = (int)resp.StatusCode;
            location = resp.Headers.Location?.ToString();
            int? seconds = null;
            if (!IsRedirect(resp) && resp.IsSuccessStatusCode)
            {
                var m = Regex.Match(await ReadBodyAsync(resp, ct), @"sec\s*=\s*(\d+)");
                // \d 在 .NET 里是 Unicode 十进制数字，非 ASCII 数字能匹配上却解不出数；门户给的秒数也不保证只有 int 的位数。
                // 解不出来就当作取不到，异常不许冒到 60 秒刷新循环里（int.Parse 在这里会抛 OverflowException）。
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var parsed))
                    seconds = parsed;
            }
            Record("OnlineSeconds", "GET", url, status, location,
                seconds is null ? "无sec" : "seconds=" + seconds);
            return seconds;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Record("OnlineSeconds", "GET", url, status, location, null, failure: ex.Message);
            return null;
        }
    }

    public async Task<PortalResult> LogoutAsync(string macNoSeparator, CancellationToken ct)
    {
        var url = PortalMessages.LogoutUrl(portalHost, macNoSeparator);
        int? status = null;
        string? location = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new FormUrlEncodedContent([]) }; // 浏览器发的是空表单体：Content-Type 为 x-www-form-urlencoded、长度 0，不能省成 null
            using var resp = await http.SendAsync(req, ct);
            status = (int)resp.StatusCode;
            location = resp.Headers.Location?.ToString() ?? "";
            var result = LogoutVerdict(resp, location);
            // 注销的表单体恒为空（凭据在 URL 的 MAC 上），没有需要脱敏的字段，所以不传 formBody。
            Record("Logout", "POST", url, status, location,
                result.Reason ?? result.Outcome.ToString());
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Record("Logout", "POST", url, status, location, PortalOutcome.TransportError.ToString(),
                failure: ex.Message);
            return PortalResult.Transport("门户不可达：" + ex.Message);
        }
    }

    /// 注销判据：只有 3xx 的 Location 里 ACLogOut=1/2 才算数。
    private static PortalResult LogoutVerdict(HttpResponseMessage resp, string loc)
    {
        if (!IsRedirect(resp))
            return PortalResult.Transport($"注销响应不是跳转，状态码 {(int)resp.StatusCode}");
        if (loc.Length == 0)
            return PortalResult.Transport($"注销跳转响应无 Location（状态码 {(int)resp.StatusCode}）");
        switch (LogoutFlag(loc))
        {
            case "1": return PortalResult.Success(loc);
            case "2": return PortalResult.Rejected(loc, 9, PortalErrorCodes.Describe(9, ""));
            default: return PortalResult.Transport($"注销响应的 Location 没有注销判据：{loc}");
        }
    }

    /// 落一行门户调用。formBody 只在 POST 时传，且必须走 RedactForm：
    /// 这一行的去处是一个会被发回来的磁盘文件，任何情况都不许让明文密码经过它。
    /// 这里不包 try：TransactionLog.Write 自己已经把磁盘故障吞成计数，
    /// 日志写不下去时门户调用照常返回判定（成功/被拒/传输异常一个都不受影响）。
    private void Record(string stage, string method, string url, int? status, string? location,
        string? summary = null, string? failure = null, string? formBody = null)
    {
        if (log is null) return;
        var text = formBody is null ? summary : $"{summary} form={TransactionLog.RedactForm(formBody)}";
        log.Write(new TransactionRecord(stage, method, url, status, location, text, failure));
    }

    /// ACLogOut 的门户取值只有 0–5，登录拒绝也复用同名参数，所以按查询参数整值比对：
    /// Contains("ACLogOut=1") 会同时吃掉 ACLogOut=10、ACLogOut=12，把未知结果读成注销成功。
    private static string? LogoutFlag(string location)
    {
        var m = Regex.Match(location, @"[?&]ACLogOut=([^&]*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// ErrorMsg 由门户 URL 编码后 base64，再按 GBK 存中文；部分固件直接给十进制码。
    internal static (int? code, string text) DecodePortalError(string location)
    {
        var m = Regex.Match(location, "ErrorMsg=([^&]*)", RegexOptions.IgnoreCase);
        if (!m.Success) return (null, "");
        var raw = Uri.UnescapeDataString(m.Groups[1].Value);
        string text;
        try { text = Gbk.GetString(Convert.FromBase64String(raw)).Trim(); }
        catch (FormatException) { text = raw; }
        return (PortalErrorCodes.TryParse(text), text);
    }

    internal static bool IsRedirect(HttpResponseMessage resp) =>
        resp.StatusCode is HttpStatusCode.Ambiguous or HttpStatusCode.Moved or HttpStatusCode.Redirect
            or HttpStatusCode.Found or HttpStatusCode.RedirectKeepVerb;

    /// 门户页面是 GBK，见 Gbk 字段：注册与取编码都在静态初始化里做完。
    internal static async Task<string> ReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return Gbk.GetString(bytes);
    }
}
