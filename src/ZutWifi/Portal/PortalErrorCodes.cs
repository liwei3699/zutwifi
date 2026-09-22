namespace ZutWifi.Portal;

/// 码表译自门户前端 errorMsgObj 的分支，保持门户原义，不自己编文案。
public static class PortalErrorCodes
{
    public static string Describe(int code, string raw) => code switch
    {
        0 or 1 or 512 => "账号状态待确认，请检查是否欠费或到期",
        2 => "认证IP已在线",
        3 => "系统忙，请稍后重试",
        4 => "未知错误，请稍后重试",
        5 => "REQ_CHALLENGE 失败，请联系网络中心",
        6 => "REQ_CHALLENGE 超时，请联系网络中心",
        7 => "Radius 认证失败（账号或密码错误）",
        8 => "Radius 认证超时",
        9 => "Radius 注销失败",
        10 => "Radius 注销超时",
        11 => "其他错误，请稍后重试",
        998 => "Portal 参数不足",
        _ => string.IsNullOrWhiteSpace(raw) ? "门户返回未知错误" : "门户返回：" + raw,
    };

    public static int? TryParse(string raw) => int.TryParse(raw, out var v) ? v : null;
}
