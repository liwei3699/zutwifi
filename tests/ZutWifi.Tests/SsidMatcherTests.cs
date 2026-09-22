using ZutWifi.Wifi;
namespace ZutWifi.Tests;
public class SsidMatcherTests
{
    [Theory]
    [InlineData("zut-stu", true)]
    [InlineData("ZUT-STU", true)]
    [InlineData("  zut-stu ", true)]
    [InlineData("zut-stu-5G", false)]
    [InlineData("TP-LINK_ab12", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 只精确匹配白名单内的完整SSID(string? ssid, bool expected)
        => Assert.Equal(expected, SsidMatcher.IsCampus(ssid, new[] { "zut-stu" }));

    [Fact]
    public void 多条白名单任一命中即为真()
        => Assert.True(SsidMatcher.IsCampus("zut-lib", new[] { "zut-stu", "zut-lib" }));

    [Fact]
    public void 空白名单永不命中()
        => Assert.False(SsidMatcher.IsCampus("zut-stu", Array.Empty<string>()));
}
