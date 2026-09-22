using Microsoft.Win32;

namespace ZutWifi.Config;

/// 开机自启：只写当前用户的 Run 键，值为带引号的 exe 绝对路径。
/// 这里的方法一个都不进单测（Ensure 会真改注册表）——单测只钉 ValueName / RunKeyPath / CommandValue，
/// 真注册表读写由 Task 20 的验收清单手工确认。
public static class StartupRegistry
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ZutWifi";

    public static string CommandValue() => Environment.ProcessPath!;

    /// 只写当前用户的 Run 键，不安装 Windows 服务——这样卸载=删文件+删这一个值。
    /// 返回 null 表示成功，否则是给用户看的一行错误文字（权限被策略挡掉时程序不该崩）。
    public static string? Ensure(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled) key.SetValue(ValueName, "\"" + CommandValue() + "\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }
}
