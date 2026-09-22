# ZutWifi — 校园网自动登录程序设计

日期：2026-09-19　状态：待评审　目标平台：Windows 10/11 x64

## 1. 背景与目标

用户连接校园网 WiFi（SSID `zut-stu`）后，必须打开浏览器、进入门户、填用户名/密码/运营商、点登录才能上网。本程序把这个过程搬到后台：检测到连接 `zut-stu` 就自动完成认证，验证结果，并用 Windows 通知报告。程序要发给同校同学使用。

成功标准：连上 `zut-stu` 后无需任何手动操作即可上网；连接其他 WiFi 时程序完全不介入；登录失败时用户能从通知和主窗口看到门户给出的中文原因。

## 2. 已锁定的需求决策

| 决策 | 取值 |
|---|---|
| 触发条件 | 仅当已连接 SSID ∈ SSID 白名单（默认 `zut-stu`） |
| 重试策略 | 保守：一次连接只登录一次，失败按 2s/5s/15s 重试共 3 次后停手通知，不无限重连 |
| 注销 | 仅手动触发；切换 SSID / 休眠 / 掉线都不自动注销 |
| 界面形态 | 托盘常驻 + 完整状态与日志主窗口 |
| 技术栈 | .NET 10 + WinForms，自包含单文件 exe（`net10.0-windows10.0.19041.0`） |
| 分发 | 发给同校同学，各自填自己账号；需首次配置向导 |
| 范围外 | 流量展示、多账号、定时规则、跨学校通用化 |
| 名称 | `ZutWifi` |
| 在线时长 | 显示（来自门户 `9002/0`） |
| IP 已在线 | 提供一键[注销并重登]按钮，仅用户点击才发注销 |

## 3. 已验证的门户接口契约

门户为 Dr.COM eportal（`Server: DrcomServer1.0` / `OMPXY/1.4.7`，Portal 协议 `loginMethod=1`）。以下四项均在 2026-09-19 真机验证，证据见 `spike/logs/spike-20260919-140553.log`。

| 动作 | 请求 | 判据 |
|---|---|---|
| 探测认证状态 | `GET http://1.1.1.1:9002/0`（禁止自动重定向） | `302` 且 `Location: http://1.1.1.1` = **未认证**；`200` 且响应体含 `<title>Logout</title>` = **已认证**。不依赖互联网 |
| 取当前 IP | `GET http://1.1.1.1/a70.htm` | 正则 `ss5="\s*(IPv4)\s*"` 取门户侧记录的本机地址。已认证/未认证两种状态下均可访问（实测均 200，3746 字节）。用于跟随 DHCP 租约变化 |
| 登录 | `POST http://1.1.1.1:801/eportal/?c=ACSetting&a=Login&protocol=http:&hostname=1.1.1.1&iTermType=1&wlanuserip={ip}&wlanacip=null&wlanacname=null&mac=00-00-00-00-00-00&ip={ip}&enAdvert=0&queryACIP=0&loginMethod=1`　表单体：`DDDDD={enc(",0,"+学号+运营商后缀)}&upass={enc(","+密码)}&R1=0&R2=0&R3=0&R6=0&para=00&0MKKey=123456&buttonClicked=&redirect_url=&err_flag=&username=&password=&user=&cmd=&Login=` | `302` 且 `Location` 含 `3.htm` = **成功**；含 `2.htm` = 失败，原因在 `ErrorMsg`（URL 解码 → base64 → GBK）。**不需要任何 Cookie**（零 Cookie 实测成功） |
| 注销 | `POST http://1.1.1.1:801/eportal/?c=ACSetting&a=Logout&wlanuserip=null&wlanacip=null&wlanacname=null&port=&hostname=1.1.1.1&iTermType=1&session=null&queryACIP=0&mac={mac}`（空表单体，`{mac}` 为本机 WLAN MAC 去分隔符小写） | `Location` 含 `ACLogOut=1` = 成功；`=2` = 失败 |

由契约推出的两条不变量：

- **密码明文提交**，`,0,` 前缀与密码的 `,` 前缀是 DrCOM 的字段编码约定，不是哈希。因此无需逆向 JS，也无需 WebView2 兜底方案。
- **登录后不需要程序保活**。门户页面上的 5 秒轮询（`checkScanIP` / `9002`）是"等待手机扫码认证"的前端行为，与会话维持无关。

已认证时的 `9002/0` 响应体形如 `<title>Logout</title> … s1=020;sec=0;uf=0;df=0; …`，其中 `sec` 是本次在线秒数（`GetOnlineSecondsAsync` 的取值来源），`uf/df` 是上下行流量——本期只解析 `sec`，不展示流量。

## 4. 架构

依赖方向单向：`Shell → LoginCoordinator → (PortalGateway, WifiSentinel)`，`SettingsStore` 与 `Diagnostics` 被上层注入。`LoginCoordinator` 之外没有决策逻辑。

| 单元 | 职责 | 对外接口 | 依赖 |
|---|---|---|---|
| `PortalGateway` | 唯一网络出口类，实现第 3 节四个操作；`HttpClient` 配 `AllowAutoRedirect=false`、无 `CookieContainer` | `ProbeAsync() → AuthState`、`GetClientIpAsync() → string`、`LoginAsync(Credential, ip) → PortalResult`、`LogoutAsync(mac) → PortalResult`、`GetOnlineSecondsAsync() → int?` | `HttpMessageHandler` 可注入 |
| `WifiSentinel` | WLAN 状态源。P/Invoke `wlanapi.dll`（`WlanOpenHandle`/`WlanEnumInterfaces`/`WlanQueryInterface`/`WlanRegisterNotification` 订阅连接通知）；初始化失败自动降级为 5 秒轮询 `WlanQueryInterface`。SSID 是否命中白名单的判定抽成纯函数 `SsidMatcher`，便于单测 | `Current → {Ssid, InterfaceDescription, Ipv4}`、`event Changed` | 仅 OS |
| `LoginCoordinator` | 状态机（第 5 节）。唯一决定"何时登录/注销/重试/发通知" | `event StatusChanged(AppStatus)`、`Command(Login/Logout/Reprobe/RecoverRelogin)` | 假时钟可测 |
| `SettingsStore` | 读写 `settings.json` 与 DPAPI 密码文件（第 7 节） | `Load()/Save(Settings)`、`GetPassword() → string`、`SetPassword(string)` | 无 |
| `Notifier` | Windows 通知中心 Toast（WinRT `Windows.UI.Notifications`），AUMID 未注册时回退 `NotifyIcon.BalloonTip` | `Notify(NoticeKind, detail)` | 无 |
| `Shell` | WinForms：`TrayApp`、`MainForm`、`SettingsPage`、`FirstRunWizard` | 消费 `StatusChanged`，调用 `Command` | 无逻辑，只做呈现 |
| `Diagnostics` | HTTP 事务日志、`--selftest` 无界面通道、导出诊断 zip | `LogTransaction(TransactionRecord)`、`ExportBundle(path)` | 无 |

设计约束：所有 HTTP 交互必须且只能在 `PortalGateway` 内发生，否则事务日志和离线单测无法覆盖全部网络行为。

## 5. 状态机

```
Idle ── SSID ∉ 白名单 ─────────────────▶ Idle（零网络动作，不禁用任何已有会话）
Idle ── SSID ∈ 白名单（连接事件 或 用户点[登录]/[重新检测]）──▶ Probe

Probe ── PortalGateway.ProbeAsync()
  ├ 已认证 ─────────────────▶ Online（不发登录包，不发通知）
  └ 未认证 ─▶ GetIp
                ├ 取不到 ip ─▶ 回退本机 WLAN IPv4 ─▶ 仍无 ─▶ Failed(内网未就绪)
                └ 有 ip ─▶ Login
                     ├ Location 含 3.htm ─▶ Verify
                     │      ├ 外网通 ───────────▶ Online        + 通知「校园网已登录」
                     │      └ 外网 3 次×2s 不通 ─▶ Degraded      + 通知「已认证，但暂时上不了网」
                     └ Location 含 2.htm ─▶ Failed(门户中文原因)
                            ├ ErrorMsg=2（IP已在线）─▶ 主窗口显示 [注销并重登]
                            └ 其他原因 ─▶ 退避 2s/5s/15s 重试，第 3 次仍失败 ─▶ GiveUp + 通知「校园网登录失败：<原因>」
```

`重试上限 = 3` 的含义是首次尝试之外的额外重试次数，即一次连接事件最多向门户提交 4 次。

补充规则：

- **去抖**：`WifiSentinel.Changed` 在 1.5 秒内的重复事件合并为一次，避免信号抖动和唤醒时重复触发。
- **同一连接会话不重复登录**：进入 `Online`/`GiveUp` 后，除非收到新的连接事件或用户点按钮，不再发起登录。
- **状态刷新**：`Online` 时每 60 秒只调用 `ProbeAsync` + `GetOnlineSecondsAsync` 刷新显示，**不因此触发登录**。若刷新发现认证状态由 `Online` 变为未认证，转入 `Failed(认证已失效)` 并发一次通知，把是否重登的决定权交给用户。
- **[注销并重登]**：仅由用户点击触发。执行 `LogoutAsync` → 等待 3 秒 → 重新走 `GetIp → Login`。超时未确认注销结果也继续走登录分支（登录幂等，最坏返回 IP 已在线）。
- **切换到非白名单 SSID**：立刻回到 `Idle`，中止进行中的会话（`CancellationToken`），不发注销包。

## 6. 界面

```
┌ ZutWifi ────────────────────────────────────────────────────┐
│  ● 已认证 · 网络正常             SSID zut-stu   IP 10.133.x.x│
│  在线 00:42:11 · 上次登录 14:05:54 · 本次耗时 1.8s           │
│                                                            │
│  [ 登录 ]      [ 注销 ]      [ 重新检测 ]                   │
│  ─────────────────────────────────────────────────────────  │
│  14:05:53  Probe     未认证                                 │
│  14:05:54  Login     Location → 3.htm（成功）               │
│  14:05:55  Verify    NCSI 200 Microsoft Connect Test         │
│  ─────────────────────────────────────────────────────────  │
│  状态 | 设置 | 关于                          [ 导出诊断包 ]  │
└────────────────────────────────────────────────────────────┘
```

- 状态枚举与配色：`Idle` 灰「未连接校园网」/ `Probing`、`LoggingIn` 黄「正在登录」/ `Online` 绿「已认证 · 网络正常」/ `Degraded` 橙「已认证 · 外网不通」/ `Failed`、`GiveUp` 红「失败：<原因>」（含 `认证已失效`）
- 按钮禁用规则：`SSID ∉ 白名单` 时[登录][注销][重新检测]全部禁用；`Online` 时[登录]禁用、[注销]可用；`Failed` 时[登录]文案变为[重试]
- 关闭行为：点 `X` 最小化到托盘（首次弹一次提示，可在设置里改为直接退出）
- 托盘：图标跟随状态色；右键菜单 = 打开主界面 / 登录 / 注销 / 重新检测 / 开机自启（勾选）/ 退出
- 单实例：命名 `Mutex`；二次启动时唤起已有实例主窗口后退出

## 7. 配置与存储

目录 `%APPDATA%\ZutWifi\`：

- `settings.json` — SSID 白名单（默认 `["zut-stu"]`）、门户主机（默认 `1.1.1.1`）、学号、运营商后缀（默认 `@cmcc`）、重试上限（默认 `3`）、开机自启（默认 `true`）、上次登录时间与耗时
- `secret.bin` — 密码，`ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` 后写入。**明文密码不进 json，不进日志，不进诊断包**
- `logs\app-YYYYMMDD.log` — 事务日志，5MB × 5 滚动

`,`前缀编码 `,0,` 与密码的 `,` 前缀、固定表单字段（`para=00`、`0MKKey=123456` 等）写死在 `PortalGateway`，不做成设置项。

开机自启：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值为 exe 绝对路径。不安装 Windows 服务。

## 8. 通知与错误码翻译

| 场景 | 标题 | 正文 |
|---|---|---|
| 登录成功且外网通 | 校园网已登录 | `zut-stu · 耗时 1.8s` |
| 门户拒绝 | 校园网登录失败 | 中文原因 + `点击查看详情` |
| 已认证但外网探测失败 | 已认证，但暂时上不了网 | 门户返回成功，外网未通，可点重新检测 |
| ErrorMsg=2 | 账号已在别处在线 | 可能是旧租约残留，点一下可注销并重登 |
| 认证中途失效 | 校园网认证已失效 | 需要重新登录 |
| 探测即已认证（用户自己登录过） | 不发通知 | 仅更新状态 |

门户错误码 → 中文（源自身逆向的 `errorMsgObj` 分支，按原义翻译）：`2`=认证IP已在线、`3`=系统忙请稍后重试、`4`=未知错误请稍后重试、`5`=REQ_CHALLENGE 失败、`6`=REQ_CHALLENGE 超时、`7`=Radius 认证失败（账号或密码错误）、`8`=Radius 认证超时、`9`=Radius 注销失败、`10`=Radius 注销超时、`11`=其他错误、`998`=Portal 参数不足；`1`/空/`512`=需查询账号状态，提示"账号状态待确认，请检查是否欠费或到期"。注销看 `ACLogOut=1` 成功 / `=2` 失败。未识别的错误码原样显示并保留在日志中。

## 9. 可诊断性

失效时必须能仅凭磁盘文件复盘，不依赖复现环境。

- **事务日志**：每个 HTTP 交互一条记录 —— 阶段名（Probe/GetIp/Login/Logout/Verify）、耗时、展开后的完整 URL、请求头（`Cookie` 值不落盘，只留字段名）、表单字段（密码记为 `upass=***(len=11)`）、响应状态码、**全部响应头（关键是 `Location`）**、响应体前 2KB（GBK 解码后）、程序判定结论。凭这一条即可区分：门户不可达 / 门户明确拒绝（附中文原因）/ 认证成功但外网不通。
- **`ZutWifi.exe --selftest`**：不启动 GUI，依次执行 探测 → 取 IP → 登录 → 外网验证 →（加 `--with-logout` 才做）注销 → 重登，逐行写 `logs\selftest-*.log`。给同学的排障入口是一个 `自检.bat`。
- **导出诊断包**：zip 内含日志、`settings.json`（写入前剔除密码相关字段）、程序版本、`netsh wlan show interfaces` 输出、IP/路由/DNS 快照、系统时间。

无法覆盖的三种情况（预先写进说明文档）：门户改版引入动态签名或滑块（需同学再提供一次浏览器 HAR）、仅在特定机器或特定楼栋复现的问题（需那台机器的诊断包）、需要链路层抓包的问题（先靠上述材料做排除法再决定）。

## 10. 分发

- 构建：`dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true`，产物 `publish\ZutWifi.exe`（约 60MB，目标机无需安装 .NET）
- 交付 zip：`ZutWifi.exe` + `使用说明.txt` + `自检.bat`
- 首次运行向导：填学号/密码/运营商 → **立即测试**（现场执行一次真实登录并显示门户反馈）→ 选择是否开机自启 → 完成
- 说明文档必须写清的摩擦点：SmartScreen「未知发布者」需点"更多信息 → 仍要运行"；DPAPI 绑定 Windows 用户，**换电脑需重填密码**；改校园网密码后需重开设置页更新
- 打包目录排除 `登录/`、`注销/`、`spike/logs/`（含真实账号与明文密码）

## 11. 错误处理（仅针对真实存在的情形）

- `a70.htm` 取不到 `ss5` → 回退本机 WLAN IPv4 → 仍无 → `Failed(内网未就绪)`，按退避重试
- 存在多个网卡/虚拟网卡 → 只取"已连接且 SSID 匹配"接口的 IPv4（`Get-NetIPAddress -InterfaceAlias` 等价逻辑，用 .NET `NetworkInterface` 实现）
- `9002` 探测超时 → 视为未知，直接走 `GetIp → Login`（登录幂等，最坏返回"IP 已在线"）
- 账号密码含 `&`、`=`、`@`、中文 → 一律走 `FormUrlEncodedContent`，不做字符串拼接
- 门户返回非 302（200/500/连接重置）→ 记为 `Failed(门户响应异常 <状态码>)`，保留原始响应体片段
- 程序崩溃或断电后重启 → 由 `Probe` 重建状态，不持久化"已登录"标志
- WinForms 未处理异常 → 记入日志并弹一次通知，不静默退出

## 12. 测试策略

离线单测（xUnit，不联网）：

- `PortalGateway`：用今天两份真机日志做成 fixture 响应，断言 `Location` 成功/失败判定、`ErrorMsg` 的 URL 解码 + base64 + GBK 解码、URL 模板与查询串顺序、表单体编码（含特殊字符密码）、无 Cookie 头
- `LoginCoordinator`：假时钟驱动全部状态转移 —— 一次连接只登一次、2s/5s/15s 退避、`Online` 后 60 秒刷新不触发登录、`IP已在线` 分支不自动注销、1.5 秒去抖、切 SSID 中止
- `SsidMatcher` / `SettingsStore` 的纯逻辑与 DPAPI 往返

真机验证：`--selftest` 跑通完整链路（`spike/spike.ps1` 作为参考实现）。

手工验收清单：关开 WLAN 触发重连、连手机热点（必须零动作）、休眠唤醒、故意填错密码看到"账号或密码错误"、冷启动开机自启、点 `X` 收进托盘、通知点击跳转对应日志、导出诊断包内容核对且不含密码。

诚实边界：界面观感只能在用户实际运行后确认；未经反馈不会声称 UI 已验证。

## 13. 风险

1. **门户改版**是唯一重大风险（`a70.htm`/`9002` 路径、`,0,` 编码、302 目标页变更）。缓解：改版后 `--selftest` 能直接看出是哪一步断的，修复集中在 `PortalGateway` 一个类。
2. **校园网并发/风控限制**未知。缓解：保守重试策略 + 永不自动注销 + 退避上限；已在线状态用 `IP已在线` 显式提示而不是硬撞。
3. **`iTermType`/`protocol` 等固定参数的长期有效性**未经验证超过一天。缓解：写进 `PortalGateway` 常量并附注释说明来源，便于改版时定位。

## 14. 参考产物

- `登录/登录过程.txt`、`注销/注销过程.txt`：浏览器真实登录/注销的完整请求响应
- `登录/a41.js`：门户前端逻辑（`login_portal`、`errorMsgObj`、`getCurrIP` 的取值来源）
- `spike/spike.ps1`、`spike/logs/spike-20260919-140553.log`：纯 HTTP 可行性验证程序与其输出证据
