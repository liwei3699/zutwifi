using ZutWifi.Portal;
namespace ZutWifi.Tests;
public class PortalErrorCodesTests
{
    [Theory]
    [InlineData(2, "认证IP已在线")]
    [InlineData(3, "系统忙，请稍后重试")]
    [InlineData(7, "Radius 认证失败（账号或密码错误）")]
    [InlineData(9, "Radius 注销失败")]
    [InlineData(998, "Portal 参数不足")]
    [InlineData(512, "账号状态待确认，请检查是否欠费或到期")]
    [InlineData(1, "账号状态待确认，请检查是否欠费或到期")]
    public void 已知码按spec文案返回(int code, string expected)
        => Assert.Equal(expected, PortalErrorCodes.Describe(code, "x"));

    [Fact]
    public void 未知码原样透传门户文字()
        => Assert.Equal("门户返回：欠费停机", PortalErrorCodes.Describe(-1, "欠费停机"));

    [Fact]
    public void 未知码且无文字时给通用提示()
        => Assert.Equal("门户返回未知错误", PortalErrorCodes.Describe(-1, ""));
}
