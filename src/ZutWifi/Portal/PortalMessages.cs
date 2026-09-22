namespace ZutWifi.Portal;

public enum AuthState { Authenticated, Unauthenticated, Unknown }
public enum PortalOutcome { Success, Rejected, TransportError }

public sealed record PortalResult(PortalOutcome Outcome, string? Reason = null,
    string? RawLocation = null, int? ErrorCode = null)
{
    public bool IsSuccess => Outcome == PortalOutcome.Success;
    public static PortalResult Success(string loc) => new(PortalOutcome.Success, RawLocation: loc);
    public static PortalResult Rejected(string loc, int code, string reason)
        => new(PortalOutcome.Rejected, reason, loc, code);
    public static PortalResult Transport(string reason) => new(PortalOutcome.TransportError, reason);
}

public sealed record Credential(string StudentId, string Password, string IspSuffix);

public static class PortalMessages
{
    // 以下常量逐字复刻自真机抓包与门户前端 login_portal 分支。门户改版时最先要核对的就是这里。
    public const string AccountPrefix = ",0,";      // PC 终端类型编码；手机端为 ",1,"
    // 密码**原样透传**，不加任何前缀。抓包里的 `upass=%2C...` 一度被读成"DrCOM 要求前补逗号"，
    // 于是这里真补了一个 —— 那个 `%2C` 其实是那位用户密码自己的第一个字符。
    // 前端从头到尾只做 `f0.upass.value = f1.upass.value`（a41.js:228），补逗号的只有账号那一侧。
    // 多补的那一个逗号会让门户把字段切歪，而它回的是 `ErrorMsg=userid error2`（提的是 userid），
    // 所以症状看起来跟密码毫无关系。
    public const string FormSuffix =
        "&R1=0&R2=0&R3=0&R6=0&para=00&0MKKey=123456&buttonClicked=&redirect_url=" +
        "&err_flag=&username=&password=&user=&cmd=&Login=";

    public static string Eportal(string host) => $"http://{host}:801/eportal/";

    public static string LoginUrl(string host, string ip) => Eportal(host) +
        "?c=ACSetting&a=Login&protocol=http:&hostname=" + host + "&iTermType=1" +
        $"&wlanuserip={ip}&wlanacip=null&wlanacname=null&mac=00-00-00-00-00-00" +
        $"&ip={ip}&enAdvert=0&queryACIP=0&loginMethod=1";

    public static string LogoutUrl(string host, string mac) => Eportal(host) +
        "?c=ACSetting&a=Logout&wlanuserip=null&wlanacip=null&wlanacname=null&port=" +
        $"&hostname={host}&iTermType=1&session=null&queryACIP=0&mac={mac}";

    public static string ProbeUrl(string host) => $"http://{host}:9002/0";
    public static string LoginPageUrl(string host) => $"http://{host}/a70.htm";

    public static Dictionary<string, string> LoginForm(Credential c) => new()
    {
        ["DDDDD"] = AccountPrefix + c.StudentId + c.IspSuffix,
        ["upass"] = c.Password,
    };
}
