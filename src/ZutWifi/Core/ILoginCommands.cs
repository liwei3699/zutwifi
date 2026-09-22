namespace ZutWifi.Core;

/// UI 与协调器之间的唯一契约：四个动作，全部由用户点击或托盘菜单触发。
/// 界面不许自己判断"该不该登录"——那是 LoginCoordinator 的事。
/// 实现类上另有语义更清楚的 RequestLoginAsync / RequestLogoutAsync / RequestReprobeAsync /
/// RequestRecoverReloginAsync，这里四个是它们给 Shell 用的别名。
public interface ILoginCommands
{
    Task LoginAsync(CancellationToken ct);
    Task LogoutAsync(CancellationToken ct);
    Task ReprobeAsync(CancellationToken ct);
    Task RecoverReloginAsync(CancellationToken ct);
}
