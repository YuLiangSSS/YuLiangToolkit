using System.Text.Json;
using MimeKit;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Mail;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「邮箱代收」工具的自检：工具元数据 / 服务商预设 / 地址与 HTML 处理 / 配置默认值与 JSON 往返 /
/// 授权码 DPAPI 加解密 / 网络代理解析 / 错误翻译。全部是纯逻辑，**不联网**（真实 Gmail / QQ 登录由人工冒烟）。
/// </summary>
internal static class MailChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckPresets(assert);
        CheckTextHelpers(assert);
        CheckConfig(assert);
        CheckSecureStore(assert);
        CheckProxy(assert);
        CheckErrorMessages(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new MailTool();
        check("[邮箱代收] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "mail-collector" && tool.Name == "邮箱代收"
            && tool.Category == "网络" && tool.Order == 26
            && tool.Tags.Contains("邮箱") && tool.Tags.Contains("IMAP"),
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[邮箱代收] 描述写明「多邮箱 / IMAP / 授权码加密」",
            tool.Description.Contains("Gmail") && tool.Description.Contains("IMAP")
            && tool.Description.Contains("DPAPI"),
            tool.Description);
    }

    // ---------------------------------------------------------------- 服务商预设

    private static void CheckPresets(CheckFn check)
    {
        var gmail = MailPresets.ByKey("gmail");
        var qq = MailPresets.ByKey("qq");
        check("[邮箱代收] Gmail / QQ 预设的服务器与端口正确",
            gmail.Host == "imap.gmail.com" && gmail.Port == 993 && gmail.UseSsl
            && qq.Host == "imap.qq.com" && qq.Port == 993 && qq.UseSsl,
            $"{gmail.Host}:{gmail.Port} / {qq.Host}:{qq.Port}");

        check("[邮箱代收] 预设带授权码申请提示与帮助页",
            gmail.Hint.Contains("应用专用密码") && gmail.HelpUrl?.Contains("apppasswords") == true
            && qq.Hint.Contains("授权码"),
            gmail.Hint);

        check("[邮箱代收] 按服务器 + 端口 + SSL 反查预设",
            MailPresets.Match("imap.gmail.com", 993, true).Key == "gmail"
            && MailPresets.Match("IMAP.QQ.COM", 993, true).Key == "qq"
            && MailPresets.Match("imap.gmail.com", 143, false).Key == "custom"
            && MailPresets.Match("", 993, true).Key == "custom");

        check("[邮箱代收] 按邮箱域名猜服务商",
            MailPresets.DetectFromEmail("a@gmail.com").Key == "gmail"
            && MailPresets.DetectFromEmail("a@qq.com").Key == "qq"
            && MailPresets.DetectFromEmail("a@foxmail.com").Key == "qq"
            && MailPresets.DetectFromEmail("a@163.com").Key == "163"
            && MailPresets.DetectFromEmail("a@outlook.com").Key == "outlook"
            && MailPresets.DetectFromEmail("a@example.com").Key == "custom"
            && MailPresets.DetectFromEmail("not-an-email").Key == "custom");

        check("[邮箱代收] GetDomain 取最后一段 @ 后的域名（小写）",
            MailPresets.GetDomain("Name@Sub.Example.CN") == "sub.example.cn"
            && MailPresets.GetDomain("a@b.com@c.com") == "c.com"
            && MailPresets.GetDomain("") == ""
            && MailPresets.GetDomain("a@") == "");

        var account = new MailAccountConfig { Host = "old.example.com", Port = 143, UseSsl = false };
        gmail.ApplyTo(account);
        check("[邮箱代收] 套用预设会填服务器 / 端口 / SSL，不动其它字段",
            account.Host == "imap.gmail.com" && account.Port == 993 && account.UseSsl
            && account.Email == "" && account.Enabled,
            $"{account.Host}:{account.Port}");

        var custom = MailPresets.Custom;
        custom.ApplyTo(account);
        check("[邮箱代收] 「自定义」预设不覆盖手填的服务器",
            account.Host == "imap.gmail.com" && account.Port == 993 && custom.IsCustom);
    }

    // ---------------------------------------------------------------- 文本处理

    private static void CheckTextHelpers(CheckFn check)
    {
        check("[邮箱代收] 邮箱打码：保留首尾字符与域名",
            MailText.MaskEmail("fantasyuliang@qq.com") == "f***g@qq.com"
            && MailText.MaskEmail("yuliang114514@gmail.com") == "y***4@gmail.com"
            && MailText.MaskEmail("ab@x.com") == "a*@x.com"
            && MailText.MaskEmail("") == "（空）",
            MailText.MaskEmail("fantasyuliang@qq.com"));

        var html = """
            <html><head><style>p{color:red}</style></head>
            <body><p>Hello&nbsp;<b>world</b></p><br/><p>第二行 &amp; more</p>
            <script>alert('x')</script><img alt="签名图" src="x.png" /></body></html>
            """;
        var plain = MailText.HtmlToPlainText(html);
        check("[邮箱代收] HTML 转纯文本：保留文字 / 解码实体 / 去掉脚本样式",
            plain.Contains("Hello world") && plain.Contains("第二行 & more")
            && plain.Contains("签名图")
            && !plain.Contains("alert") && !plain.Contains("color:red")
            && !plain.Contains("<"),
            plain);

        check("[邮箱代收] 附件文件名清洗：去路径 / 非法字符 / 保留扩展名",
            MailText.SanitizeFileName("../../evil:name?.txt") == "evil_name_.txt"
            && MailText.SanitizeFileName(@"C:\temp\报告.pdf") == "报告.pdf"
            && MailText.SanitizeFileName("   ") == "附件"
            && MailText.SanitizeFileName(new string('a', 300) + ".pdf").Length <= 124,
            MailText.SanitizeFileName("../../evil:name?.txt"));

        check("[邮箱代收] 大小格式化",
            MailText.FormatSize(0) == "0 B"
            && MailText.FormatSize(1024) == "1 KB"
            && MailText.FormatSize(1024 * 1024) == "1 MB",
            $"{MailText.FormatSize(0)} / {MailText.FormatSize(1024)}");

        var addresses = new InternetAddressList
        {
            new MailboxAddress("凉雨", "a@b.com"),
            new MailboxAddress("", "c@d.com"),
        };
        check("[邮箱代收] 地址列表格式化：有名字带名字、没名字只有地址",
            MailText.FormatAddressList(addresses) == "凉雨 <a@b.com>、c@d.com"
            && MailText.FormatFirstAddress(addresses) == "凉雨 <a@b.com>"
            && MailText.FormatAddressList(null) == "（未知）",
            MailText.FormatAddressList(addresses));

        check("[邮箱代收] 日期格式化：未知日期显示 —",
            MailText.FormatListDate(DateTimeOffset.MinValue) == "—"
            && MailText.FormatFullDate(DateTimeOffset.MinValue) == "—"
            && MailText.Snippet("  a \n b  ", 10) == "a b");
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new MailCollectorConfig();
        check("[邮箱代收] 配置默认值：30 封 / 打开自动刷新 / 账号为空",
            config.FetchCount == 30 && config.RefreshOnOpen && config.Accounts.Count == 0
            && config.EffectiveFetchCount == 30,
            $"fetch={config.FetchCount}");

        check("[邮箱代收] 代理默认跟随 Windows 系统代理（auto）",
            config.Proxy is { Mode: "auto" } && config.Proxy.Port == 7890,
            $"proxy={config.Proxy.Mode}:{config.Proxy.Host}:{config.Proxy.Port}");

        config.FetchCount = 1;
        var low = config.EffectiveFetchCount;
        config.FetchCount = 500;
        var high = config.EffectiveFetchCount;
        check("[邮箱代收] 拉取数量夹取在 5 ~ 200",
            low == 5 && high == 200, $"low={low} high={high}");

        var account = new MailAccountConfig();
        check("[邮箱代收] 账号默认值：993 / SSL / 启用 / 未配置",
            account.Id.Length > 0 && account.Port == 993 && account.UseSsl && account.Enabled
            && !account.IsConfigured && account.DisplayTitle == "未命名账号",
            $"{account.Id} / {account.Port}");

        account.DisplayName = "主力 Gmail";
        account.Email = "test@gmail.com";
        account.Host = "imap.gmail.com";
        account.Password = "abcd efgh ijkl mnop";
        check("[邮箱代收] 填全邮箱 / 服务器 / 授权码后 IsConfigured = true",
            account.IsConfigured && account.DisplayTitle == "主力 Gmail"
            && account.DisplaySubtitle.Contains("test@gmail.com"),
            account.DisplaySubtitle);

        var json = JsonFileStore.Serialize(account);
        check("[邮箱代收] 账号 JSON 只落盘密文，不出现明文授权码",
            json.Contains("passwordCipher", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("abcd efgh", StringComparison.Ordinal)
            && !json.Contains("Password\"", StringComparison.Ordinal)
            && SecureStore.IsProtected(account.PasswordCipher),
            json.Split('\n').FirstOrDefault(line => line.Contains("passwordCipher"))?.Trim() ?? json);

        config.Accounts.Add(account);
        var roundTripJson = JsonFileStore.Serialize(config);
        var restored = JsonSerializer.Deserialize<MailCollectorConfig>(roundTripJson, JsonFileStore.Options);
        check("[邮箱代收] 配置 JSON 往返：账号 / 密文 / 设置都保留，密码能解开",
            restored is not null && restored.Accounts.Count == 1
            && restored.Accounts[0].Email == "test@gmail.com"
            && restored.Accounts[0].Password == "abcd efgh ijkl mnop"
            && restored.FetchCount == 500 && restored.RefreshOnOpen,
            restored is null ? "反序列化为 null" : $"{restored.Accounts.Count} 个账号");
    }

    // ---------------------------------------------------------------- DPAPI

    private static void CheckSecureStore(CheckFn check)
    {
        const string secret = "qq-authorization-code-123";
        var cipher = SecureStore.Protect(secret);
        check("[邮箱代收] DPAPI：加密结果带 dpapi: 前缀且不含明文",
            SecureStore.IsProtected(cipher) && !cipher.Contains(secret, StringComparison.Ordinal),
            cipher[..Math.Min(28, cipher.Length)] + "…");

        check("[邮箱代收] DPAPI：同一用户能解回原文",
            SecureStore.Unprotect(cipher) == secret);

        check("[邮箱代收] DPAPI：空值 / 历史明文兼容",
            SecureStore.Protect("") == ""
            && SecureStore.Unprotect("") == ""
            && SecureStore.Unprotect("plain-text-password") == "plain-text-password");
    }

    // ---------------------------------------------------------------- 网络代理

    private static void CheckProxy(CheckFn check)
    {
        var plain = MailProxySettings.ParseSystemProxy("127.0.0.1:7897");
        var socks = MailProxySettings.ParseSystemProxy("socks=127.0.0.1:1080");
        var keyed = MailProxySettings.ParseSystemProxy("http=127.0.0.1:7897;https=127.0.0.1:7897");
        check("[邮箱代收] 系统代理解析：纯 host:port / socks= / http=+https=",
            plain is { Host: "127.0.0.1", Port: 7897, Type: "http" }
            && socks is { Host: "127.0.0.1", Port: 1080, Type: "socks5" }
            && keyed is { Host: "127.0.0.1", Port: 7897, Type: "http" }
            && MailProxySettings.ParseSystemProxy("") is null
            && MailProxySettings.ParseSystemProxy("no-port") is null,
            $"{plain} / {socks} / {keyed}");

        check("[邮箱代收] 系统代理例外列表：*.qq.com / <local> / 192.168.*",
            MailProxySettings.IsBypassed("imap.qq.com", "*.qq.com;<local>")
            && !MailProxySettings.IsBypassed("imap.gmail.com", "*.qq.com;<local>")
            && MailProxySettings.IsBypassed("localhost", "*.qq.com;<local>")
            && MailProxySettings.IsBypassed("192.168.1.5", "192.168.*")
            && !MailProxySettings.IsBypassed("imap.gmail.com", "*.qq.com"));

        var system = new SystemProxyInfo(true, "127.0.0.1:7897", "*.qq.com");
        var autoGmail = new MailProxySettings { Mode = "auto" }.Resolve("imap.gmail.com", system);
        var autoQq = new MailProxySettings { Mode = "auto" }.Resolve("imap.qq.com", system);
        check("[邮箱代收] 自动模式：Gmail 走系统代理，QQ 命中例外则直连",
            autoGmail.IsProxy && autoGmail.Client is not null && autoGmail.Description.Contains("7897")
            && !autoQq.IsProxy && autoQq.Description.Contains("例外"),
            $"{autoGmail.Description} / {autoQq.Description}");

        var direct = new MailProxySettings { Mode = "direct" }.Resolve("imap.gmail.com", system);
        var custom = new MailProxySettings
        {
            Mode = "custom",
            Host = "10.0.0.2",
            Port = 1080,
            Type = "socks5",
        }.Resolve("imap.gmail.com", system);
        var customBad = new MailProxySettings { Mode = "custom", Host = "", Port = 0 }
            .Resolve("imap.gmail.com", system);
        check("[邮箱代收] 直连 / 自定义（含类型） / 自定义未填完整",
            !direct.IsProxy
            && custom.IsProxy && custom.Description.Contains("10.0.0.2:1080") && custom.Description.Contains("SOCKS5")
            && !customBad.IsProxy && customBad.Description.Contains("没填完整"),
            $"{direct.Description} / {custom.Description} / {customBad.Description}");

        var disabled = new MailProxySettings { Mode = "auto" }
            .Resolve("imap.gmail.com", new SystemProxyInfo(false, "", ""));
        check("[邮箱代收] 自动模式：系统没开代理就直连",
            !disabled.IsProxy && disabled.Description.Contains("未启用"),
            disabled.Description);

        check("[邮箱代收] 代理类型归一化 / 中文标签",
            MailProxySettings.NormalizeType("SOCKS5") == "socks5"
            && MailProxySettings.NormalizeType("http") == "http"
            && MailProxySettings.TypeLabel("socks5") == "SOCKS5"
            && MailProxySettings.TypeLabel("http") == "HTTP");

        var proxyFailure = new IOException(
            "代理连接失败（系统代理 127.0.0.1:7897（HTTP））：The operation has timed out。",
            new TimeoutException("The operation has timed out."));
        check("[邮箱代收] 代理失败的提示优先于泛泛的「连接超时」",
            ImapMailService.DescribeError(proxyFailure).Contains("代理连接失败"),
            ImapMailService.DescribeError(proxyFailure));
    }

    // ---------------------------------------------------------------- 错误提示

    private static void CheckErrorMessages(CheckFn check)
    {
        check("[邮箱代收] 超时错误翻译成中文提示",
            ImapMailService.DescribeError(new TimeoutException()).Contains("超时"),
            ImapMailService.DescribeError(new TimeoutException()));

        check("[邮箱代收] 邮箱 / 授权码为空时给出可执行的提示",
            ImapMailService.DescribeError(new InvalidOperationException(
                "授权码 / 应用专用密码为空：QQ 邮箱填「授权码」，Gmail 填「应用专用密码」")).Contains("授权码"));

        var authLike = new FakeAuthenticationException("Invalid credentials");
        check("[邮箱代收] 认证类异常（按类型名识别）翻译成「认证失败」",
            ImapMailService.DescribeError(new Exception("outer", authLike)).Contains("认证失败"),
            ImapMailService.DescribeError(authLike));

        check("[邮箱代收] 其它异常的原文保留（方便排查）",
            ImapMailService.DescribeError(new InvalidOperationException("端口不合法")).Contains("端口不合法"));
    }

    /// <summary>模拟一个类型名里带 Authentication 的异常（不依赖 MailKit 内部类型名，版本升级也稳）。</summary>
    private sealed class FakeAuthenticationException(string message) : Exception(message);
}
