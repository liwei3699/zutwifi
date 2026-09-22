namespace ZutWifi.Wifi;

/// SSID 由 AC 广播，大小写与首尾空白不可信，故规范化后要求整串相等——
/// "zut-stu-5G" 这类相似 SSID 必须不触发登录。
public static class SsidMatcher
{
    public static bool IsCampus(string? ssid, IEnumerable<string> whitelist)
    {
        if (string.IsNullOrWhiteSpace(ssid)) return false;
        var target = ssid.Trim();
        return whitelist.Any(w => !string.IsNullOrWhiteSpace(w)
            && string.Equals(w.Trim(), target, StringComparison.OrdinalIgnoreCase));
    }
}
