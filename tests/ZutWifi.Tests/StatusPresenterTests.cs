using ZutWifi.Core;
using ZutWifi.Shell;
namespace ZutWifi.Tests;
public class StatusPresenterTests
{
    [Fact]
    public void 未连接校园网时文案固定且四个动作全禁用()
    {
        var p = StatusPresenter.Of(new AppStatus(AppPhase.Online, "TP-LINK"), ssidWhitelisted: false);
        Assert.Equal("未连接校园网", p.Text);
        Assert.Equal("gray", p.ColorKey);
        Assert.False(p.LoginEnabled); Assert.False(p.LogoutEnabled);
        Assert.False(p.ReprobeEnabled); Assert.False(p.RecoverVisible);
    }

    [Theory]
    [InlineData(AppPhase.Online, "green")]
    [InlineData(AppPhase.Degraded, "orange")]
    [InlineData(AppPhase.LoggingIn, "yellow")]
    [InlineData(AppPhase.Probing, "yellow")]
    [InlineData(AppPhase.AcquiringIp, "yellow")]
    [InlineData(AppPhase.Verifying, "yellow")]
    [InlineData(AppPhase.GiveUp, "red")]
    [InlineData(AppPhase.Failed, "red")]
    public void 阶段映射配色(AppPhase phase, string color)
        => Assert.Equal(color, StatusPresenter.Of(new AppStatus(phase, "zut-stu"), true).ColorKey);

    [Fact]
    public void Online时登录禁用注销可用()
    {
        var p = StatusPresenter.Of(new AppStatus(AppPhase.Online, "zut-stu"), true);
        Assert.False(p.LoginEnabled);
        Assert.True(p.LogoutEnabled);
        Assert.True(p.ReprobeEnabled);
    }

    [Fact]
    public void Failed且可恢复时才显示一键重登()
    {
        Assert.True(StatusPresenter.Of(
            new AppStatus(AppPhase.Failed, "zut-stu", CanRecoverRelogin: true), true).RecoverVisible);
        Assert.False(StatusPresenter.Of(
            new AppStatus(AppPhase.Failed, "zut-stu", "认证已失效"), true).RecoverVisible);
    }

    [Fact]
    public void GiveUp时登录按钮文案变重试()
        => Assert.Equal("重试", StatusPresenter.LoginText(AppPhase.GiveUp));

    [Fact]
    public void 失败文案带原因()
        => Assert.Equal("登录失败：认证IP已在线",
            StatusPresenter.Of(new AppStatus(AppPhase.GiveUp, "zut-stu", Reason: "认证IP已在线"), true).Text);

    [Theory]
    [InlineData(null, "00:00:00")]
    [InlineData(0, "00:00:00")]
    [InlineData(4211, "01:10:11")]
    [InlineData(86400, "24:00:00")]
    public void 时长格式化(int? seconds, string expected)
        => Assert.Equal(expected, StatusPresenter.FormatDuration(seconds));
}
