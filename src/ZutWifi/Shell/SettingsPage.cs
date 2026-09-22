using ZutWifi.Config;
using ZutWifi.Diagnostics;
using ZutWifi.Portal;

namespace ZutWifi.Shell;

/// 设置页：学号 / 密码 / 运营商后缀 / SSID 白名单 / 开机自启 / 关窗收托盘 / 测试配置。
///
/// 四条硬规矩：
/// ① 密码只进 SecretStore（DPAPI），永不回填明文，也永不进 settings.json 与日志；
///    框里留空＝不改。界面上刻意没有"清空密码"这个动作：留下一个能解出空串的密文，
///    等于让状态机拿着空密码去白交一次注定失败的凭据。
/// ② 会动到机器与门户的两条通路都是注入接缝：单测不许真改 HKCU\...\Run，也不许真给 1.1.1.1 发包。
/// ③ 白名单框清空保存的就是空白名单 —— SettingsStore.Normalise 把 [] 认定为"用户主动关掉自动登录
///    的开关"并原样留着，这里偷偷填回 ["zut-stu"] 等于把那个开关在界面上抹掉，还会让下一次保存
///    把磁盘上的 [] 复活。所以只照实写，并在提示里把后果说清楚。
/// ④ 点"测试配置"先落盘再测：否则测的是上一份配置，改完密码看不到新结果。
public sealed class SettingsPage : UserControl
{
    /// 门户请求本身的超时，与装配那边同一个数：这里点了测试就是手动走一遍真流程。
    private const int PortalTimeoutSeconds = 15;

    private readonly SettingsStore _store;
    private readonly SecretStore _secrets;
    private readonly TransactionLog? _log;
    private readonly Func<bool, string?> _applyAutoStart;
    private readonly Func<PortalGateway>? _gatewayFactory;
    private HttpClient? _http;
    private bool _testing;

    private readonly TextBox _student = new() { Width = 260 };
    private readonly TextBox _password = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly ComboBox _isp = new() { Width = 140, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _ssids = new() { Width = 260 };
    private readonly CheckBox _autoStart = new() { Text = "开机自启", AutoSize = true };
    private readonly CheckBox _closeToTray = new() { Text = "关窗口收进托盘", AutoSize = true };
    private readonly Button _save = new() { Text = "保存", Width = 90, Height = 30 };
    private readonly Button _test = new() { Text = "测试配置", Width = 110, Height = 30, Margin = new Padding(0, 0, 10, 0) };
    private readonly Label _hint = new() { AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90), Margin = new Padding(0, 8, 0, 0) };
    private readonly TextBox _output = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9F), BackColor = Color.White,
    };

    /// 输出那一栏的标题。以前它写的是"密码只会以长度形式出现"——那句话在这个框里从来没兑现过：
    /// 这里三行打的只有门户判定、内网地址与登录结果，密码连长度都不出现（长度只进事务日志）。
    /// 标题承诺一件界面不做的事，看界面排查的人就会去找一个不存在的东西。
    private readonly GroupBox _result = new()
    {
        Text = "测试配置的输出（只有门户判定、内网地址与登录结果，不会出现密码）",
        Dock = DockStyle.Fill, Padding = new Padding(12),
    };

    public string StudentIdText => _student.Text;
    public string PasswordText => _password.Text;
    public string IspText => _isp.Text;
    public string SsidsText => _ssids.Text;
    public bool AutoStartChecked => _autoStart.Checked;
    public bool CloseToTrayChecked => _closeToTray.Checked;
    public string HintText => _hint.Text;
    public string TestOutputText => _output.Text;
    public string TestOutputCaption => _result.Text;
    public bool TestButtonEnabled => _test.Enabled;

    /// 宿主（首次向导）那一侧的一句话往哪儿显示：就这一行的提示位。
    /// 向导点"完成"时设置落不下去，总得有个地方说清楚，别再新造一个标签。
    public void ShowHint(string text) => _hint.Text = text;

    /// 密码框当前的状态文字。永远不返回密文本身。
    public string PasswordPlaceholderText =>
        string.IsNullOrEmpty(_password.Text) && _secrets.Get() is not null
            ? "已保存（留空表示不修改）" : "未设置";

    /// log 传的是装配那一份（全进程只有一份，见 AppContext 规矩①）；
    /// applyAutoStart 与 gatewayFactory 是让单测能换掉真注册表与真门户的两条接缝。
    public SettingsPage(SettingsStore store, SecretStore secrets, TransactionLog? log = null,
        Func<bool, string?>? applyAutoStart = null, Func<PortalGateway>? gatewayFactory = null)
    {
        _store = store; _secrets = secrets; _log = log;
        _applyAutoStart = applyAutoStart ?? StartupRegistry.Ensure;
        _gatewayFactory = gatewayFactory;
        _isp.Items.AddRange(["@cmcc", "@telecom", "@unicom", ""]);
        var s = _store.Load();
        _student.Text = s.StudentId;
        _isp.Text = s.IspSuffix;
        _ssids.Text = string.Join(",", s.SsidWhitelist);
        _autoStart.Checked = s.AutoStart;
        _closeToTray.Checked = s.CloseToTray;
        RefreshPasswordPlaceholder();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(18),
        };
        grid.Controls.Add(Row("学号", _student), 0, 0);
        grid.Controls.Add(Row("密码", _password), 0, 1);
        grid.Controls.Add(Row("运营商后缀", _isp), 0, 2);
        grid.Controls.Add(Row("校园网 SSID（逗号分隔）", _ssids), 0, 3);
        var flags = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        flags.Controls.Add(_autoStart);
        flags.Controls.Add(_closeToTray);
        grid.Controls.Add(flags, 0, 4);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_test);
        buttons.Controls.Add(_hint);
        grid.Controls.Add(buttons, 0, 5);

        _result.Controls.Add(_output);

        Controls.Add(_result);
        Controls.Add(grid);

        _save.Click += (_, _) => Save();
        // await 而不是 .Wait()：这条通路要真给门户发包，堵住 UI 线程就是整个界面（含托盘）冻在这里。
        _test.Click += async (_, _) => await RunTestAsync();
    }

    private static Control Row(string label, Control input)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        p.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 12, 0), MinimumSize = new Size(170, 0) });
        input.Margin = new Padding(0);
        p.Controls.Add(input);
        return p;
    }

    public void SetStudentId(string v) => _student.Text = v;
    public void SetPassword(string v) => _password.Text = v;
    public void SetIsp(string v) => _isp.Text = v;
    public void SetSsids(string v) => _ssids.Text = v;
    public void SetAutoStart(bool v) => _autoStart.Checked = v;
    public void SetCloseToTray(bool v) => _closeToTray.Checked = v;
    public void SimulateSave() => Save();
    public Task SimulateTestClick() => RunTestAsync();

    private void RefreshPasswordPlaceholder() =>
        _password.PlaceholderText = _password.TextLength == 0 ? PasswordPlaceholderText : "";

    private void Save() => Write(hint: true);

    /// 落盘。hint=false 时不碰提示行（"测试配置"内部调用它，不能让保存把测试结果盖掉）。
    /// 返回值 = "盘上是不是真的换了"，两种 false（读不出来 / 写不进去）都要说给用户听。
    private bool Write(bool hint)
    {
        Settings saved = new();
        bool wrote;
        string? why;
        try
        {
            // 读-改-写只走 TryUpdate（评审 I4）：盘上那份读不出来时就一个字都不写。
            // 光靠 Load() 分不清"本来就没有"与"有但我没读到"，那一次写回去就是把同学的
            // 学号、门户地址、重试上限与 SSID 白名单整份刷成出厂状态，而且不可挽回。
            wrote = _store.TryUpdate(s =>
            {
                s.StudentId = _student.Text.Trim();
                s.IspSuffix = _isp.Text.Trim();
                s.SsidWhitelist = ParseSsids(_ssids.Text);
                s.AutoStart = _autoStart.Checked;
                s.CloseToTray = _closeToTray.Checked;
                saved = s;
            }, out why);
            if (wrote && _password.Text.Length > 0) _secrets.Set(_password.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // settings.json 的位置被占住、目录只读：说清楚，别让一次保存把界面顶掉。
            wrote = false; why = ex.Message;
        }
        if (!wrote)
        {
            // 保存没成就别去碰注册表，也别让密码框看起来"已经存下了"。
            if (hint) _hint.Text = "保存失败：" + why;
            return false;
        }

        var regErr = _applyAutoStart(saved.AutoStart);   // null = 成功，否则是给用户看的一行原因
        RefreshPasswordPlaceholder();
        if (!hint) return true;
        var text = "已保存";
        if (saved.SsidWhitelist.Count == 0) text += "（白名单为空＝不会自动登录）";
        if (regErr is not null) text += "，但开机自启设置失败：" + regErr;
        _hint.Text = text;
        return true;
    }

    /// 中英文逗号都算分隔：中文输入法下打出来的那个逗号必须是分隔符，不能变成 SSID 的一部分。
    internal static List<string> ParseSsids(string raw) => raw
        .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ---------- 测试配置 ----------

    private async Task RunTestAsync()
    {
        if (_testing) return;                       // 一次点击一个登录包，连点就是给门户重复交凭据
        _testing = true;
        _test.Enabled = false;
        _output.Clear();
        try
        {
            // ④ 先落盘再测。保存失败也要说清楚：那样下面测的是磁盘上那份旧配置，
            // 否则"我明明改了密码"却仍然失败会把人引向完全错误的排查方向。
            if (!Write(hint: false)) Append("⚠ 设置没保存成功，下面测的是磁盘上那份旧配置。");
            await PortalTestRun.ExecuteAsync(NewGateway, _store, _secrets, Append, ReportFault);
        }
        finally
        {
            _test.Enabled = true;
            _testing = false;
        }
    }

    private PortalGateway NewGateway() => _gatewayFactory?.Invoke()
        ?? new PortalGateway(OwnHttp(), _store.Load().PortalHost, _log);

    private HttpClient OwnHttp() => _http ??= AppContext.NewNoRedirectClient(PortalTimeoutSeconds);

    private void Append(string line)
    {
        _output.AppendText(line + Environment.NewLine);
        _output.SelectionStart = _output.TextLength;
        if (_output.IsHandleCreated) _output.ScrollToCaret();
    }

    /// 测试通路自己抛出来的异常（正常不会：PortalGateway 把网络故障都吞成判定）落进日志。
    /// 这一份日志就是装配用的那一份（AppContext 传进来的），所以点一次测试也在诊断包里留下记录。
    private void ReportFault(Exception ex) =>
        _log?.Write(new TransactionRecord("Settings", "TEST", "click://settings-page", null, null,
            "测试配置没跑完", $"{ex.GetType().Name}: {ex.Message}"));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _http?.Dispose();
        base.Dispose(disposing);
    }
}
