using ZutWifi.Portal;
namespace ZutWifi.Tests;
public class PortalMessagesTests
{
    // 下面三条期望串逐字取自真机抓包原文（登录/登录过程.txt 第 25、42 行，注销/注销过程.txt 第 4 行），
    // 不是从模板反推出来的。因此断言用整串相等而不是 Contains/StartsWith：
    // 模板里任何单字符漂移——漏一个 &、参数换序、少一位百分号编码——都必须让构建失败。
    //
    // 抓包那行里的 `upass=%2C...` 曾被当成"DrCOM 要求密码前补一个逗号"，于是模板真的去补了 —— **那是误读**：
    // `%2C` 是那位用户的密码本身的第一个字符。门户前端只做 `f0.upass.value = f1.upass.value`（a41.js:228），
    // 全程不碰密码；只有账号那一侧才拼 `accountPrefix`（a41.js:199）。
    // 拿真账号验证的代价就是一次 `ErrorMsg=userid error2`：多出来的逗号把门户的字段切歪，
    // 它报的是 userid 错，不是"密码错"，所以看提示根本想不到是密码多了个字符。
    // 所以下面这几条钉的是两件事：**抓包原文一字不改**，以及**密码原样透传**（前缀只此一处 = 用户自己打的）。

    [Fact]
    public void 登录URL逐字等于抓包原文()
    {
        Assert.Equal(
            "http://1.1.1.1:801/eportal/?c=ACSetting&a=Login&protocol=http:&hostname=1.1.1.1"
            + "&iTermType=1&wlanuserip=10.133.126.113&wlanacip=null&wlanacname=null"
            + "&mac=00-00-00-00-00-00&ip=10.133.126.113&enAdvert=0&queryACIP=0&loginMethod=1",
            PortalMessages.LoginUrl("1.1.1.1", "10.133.126.113"));
    }

    [Fact]
    public void 注销URL逐字等于抓包原文()
    {
        Assert.Equal(
            "http://1.1.1.1:801/eportal/?c=ACSetting&a=Logout&wlanuserip=null&wlanacip=null"
            + "&wlanacname=null&port=&hostname=1.1.1.1&iTermType=1&session=null&queryACIP=0"
            + "&mac=02a1b2c3d4e5",
            PortalMessages.LogoutUrl("1.1.1.1", "02a1b2c3d4e5"));
    }

    [Fact]
    public void 账号字段带DrCOM前缀与运营商后缀()
    {
        var form = PortalMessages.LoginForm(new Credential("202500000001", "pw", "@cmcc"));
        Assert.Equal(",0,202500000001@cmcc", form["DDDDD"]);
        Assert.Equal("pw", form["upass"]);        // 密码不加任何前缀
    }

    [Fact]
    public void 密码以逗号开头时也只发一份逗号()
    {
        // 真机那位用户的密码就是 `,XXX@XXXX.` 这种形状（开头带逗号）。若模板再补一个，
        // 门户按逗号切字段时账号就被切坏了 —— 报出来的却是 userid error2。
        var form = PortalMessages.LoginForm(new Credential("202500000001", ",Pass@2024.", "@cmcc"));
        Assert.Equal(",Pass@2024.", form["upass"]);   // 上面那一条相等就是"只有一份逗号"，不再数一遍
    }

    [Fact]
    public void 登录body按Task6的拼法逐字等于抓包原文()
    {
        // 与 Task 6 的提交体同一表达式：LoginForm 字段按字典顺序 EscapeDataString 后用 & 连接，
        // 再接自带前导 & 的 FormSuffix。这一条同时钉住了编码（, -> %2C、@ -> %40）、字段顺序与整段后缀。
        // 密码写成带前导逗号的形状：抓包原文里那个 `%2C` 就是它来的，不是模板补的。
        var cred = new Credential("202500000001", ",Pass@2024.", "@cmcc");
        var body = string.Join("&", PortalMessages.LoginForm(cred).Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))
            + PortalMessages.FormSuffix;
        Assert.Equal(
            "DDDDD=%2C0%2C202500000001%40cmcc&upass=%2CPass%402024."
            + "&R1=0&R2=0&R3=0&R6=0&para=00&0MKKey=123456&buttonClicked=&redirect_url="
            + "&err_flag=&username=&password=&user=&cmd=&Login=",
            body);
    }
}
