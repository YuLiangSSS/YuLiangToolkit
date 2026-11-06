namespace YuLiangToolkit.Tools.Mail;

/// <summary>
/// 一个邮箱服务商的 IMAP 预设（服务器 / 端口 / SSL / 授权码申请提示）。
/// 只负责「把常用服务商填好」，用户随时可以手改服务器和端口。
/// </summary>
public sealed class MailPreset
{
    public MailPreset(
        string key,
        string name,
        string host,
        int port,
        bool useSsl,
        string hint,
        string? helpUrl = null,
        string? helpLabel = null)
    {
        Key = key;
        Name = name;
        Host = host;
        Port = port;
        UseSsl = useSsl;
        Hint = hint;
        HelpUrl = helpUrl;
        HelpLabel = helpLabel;
    }

    /// <summary>稳定键（不显示在界面上）。</summary>
    public string Key { get; }

    /// <summary>下拉框里显示的名字。</summary>
    public string Name { get; }

    /// <summary>IMAP 服务器；空字符串 = 自定义（不覆盖用户手填的值）。</summary>
    public string Host { get; }

    /// <summary>IMAP 端口。</summary>
    public int Port { get; }

    /// <summary>是否 SSL（993 = true）。</summary>
    public bool UseSsl { get; }

    /// <summary>授权码 / 应用专用密码怎么来的（显示在表单下方）。</summary>
    public string Hint { get; }

    /// <summary>帮助页地址（例如 Gmail 的应用专用密码页）；null = 没有可打开的页面。</summary>
    public string? HelpUrl { get; }

    /// <summary>帮助按钮文字。</summary>
    public string? HelpLabel { get; }

    /// <summary>「自定义」没有服务器值，套用时只把下拉框定位过来，不动用户填的内容。</summary>
    public bool IsCustom => string.IsNullOrWhiteSpace(Host);

    /// <summary>把预设的服务器 / 端口 / SSL 套到一个账号上（显示名称、邮箱、授权码不动）。</summary>
    public void ApplyTo(MailAccountConfig account)
    {
        if (IsCustom) return;
        account.Host = Host;
        account.Port = Port;
        account.UseSsl = UseSsl;
    }

    public override string ToString() => Name;
}

/// <summary>
/// 常用邮箱的 IMAP 预设 + 按邮箱域名猜服务商。
///
/// <para>预设只是「省一次手填」，不是白名单：任何支持 IMAP 的邮箱都可以选「自定义」填服务器。</para>
/// </summary>
public static class MailPresets
{
    /// <summary>「自定义」项（始终在列表最后）。</summary>
    public static MailPreset Custom { get; } = new(
        "custom",
        "自定义",
        "",
        993,
        true,
        "任何支持 IMAP 的邮箱都可以用：填服务器、端口、SSL，再填邮箱 + 授权码 / 应用专用密码。");

    /// <summary>所有预设（顺序 = 下拉框顺序）。</summary>
    public static IReadOnlyList<MailPreset> All { get; } = new[]
    {
        new MailPreset(
            "gmail",
            "Gmail",
            "imap.gmail.com",
            993,
            true,
            "Gmail 不能用登录密码：先在 Google 账号里开启两步验证，再生成 16 位「应用专用密码」，填到授权码框（去掉空格）。"
            + "如果组织管理员禁用了应用专用密码，需要改用 OAuth2（本版暂未支持）。",
            "https://myaccount.google.com/apppasswords",
            "打开应用专用密码页"),
        new MailPreset(
            "qq",
            "QQ 邮箱",
            "imap.qq.com",
            993,
            true,
            "QQ 邮箱 → 设置 → 账号 → 开启「IMAP/SMTP 服务」→ 生成「授权码」，用授权码当密码（不是 QQ 密码）。",
            "https://mail.qq.com/",
            "打开 QQ 邮箱"),
        new MailPreset(
            "163",
            "网易 163 邮箱",
            "imap.163.com",
            993,
            true,
            "网易邮箱 → 设置 → POP3/SMTP/IMAP → 开启 IMAP 服务 → 生成「授权码」，用授权码当密码（不是登录密码）。",
            "https://mail.163.com/",
            "打开 163 邮箱"),
        new MailPreset(
            "126",
            "网易 126 邮箱",
            "imap.126.com",
            993,
            true,
            "网易邮箱 → 设置 → POP3/SMTP/IMAP → 开启 IMAP 服务 → 生成「授权码」，用授权码当密码（不是登录密码）。",
            "https://mail.126.com/",
            "打开 126 邮箱"),
        new MailPreset(
            "outlook",
            "Outlook / Office 365",
            "outlook.office365.com",
            993,
            true,
            "Outlook / Microsoft 365 现在基本强制 OAuth2，基础认证可能被服务器拒绝；"
            + "本版只支持授权码 / 应用密码方式，个人账号可先尝试开启两步验证后的应用密码。"),
        Custom,
    };

    /// <summary>按服务器 + 端口反查预设（找不到返回 <see cref="Custom"/>）。</summary>
    public static MailPreset Match(string? host, int port, bool useSsl)
    {
        var normalized = (host ?? "").Trim();
        foreach (var preset in All)
        {
            if (preset.IsCustom) continue;
            if (string.Equals(preset.Host, normalized, StringComparison.OrdinalIgnoreCase)
                && preset.Port == port
                && preset.UseSsl == useSsl)
            {
                return preset;
            }
        }

        return Custom;
    }

    /// <summary>
    /// 按邮箱域名猜服务商（新增账号时给个合适的默认值）：gmail.com → Gmail，qq.com / foxmail.com → QQ，
    /// 163.com → 163，126.com → 126，outlook.com / hotmail.com / live.com → Outlook，其它 → 自定义。
    /// </summary>
    public static MailPreset DetectFromEmail(string? email)
    {
        var domain = GetDomain(email);
        return domain switch
        {
            "gmail.com" or "googlemail.com" => ByKey("gmail"),
            "qq.com" or "foxmail.com" => ByKey("qq"),
            "163.com" => ByKey("163"),
            "126.com" => ByKey("126"),
            "outlook.com" or "hotmail.com" or "live.com" or "msn.com" => ByKey("outlook"),
            _ => Custom,
        };
    }

    /// <summary>取邮箱域名（小写；没有 @ 返回空字符串）。</summary>
    public static string GetDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "";
        var text = email.Trim();
        var index = text.LastIndexOf('@');
        return index < 0 || index == text.Length - 1 ? "" : text[(index + 1)..].Trim().ToLowerInvariant();
    }

    /// <summary>按 Key 取预设（不存在时抛异常，属于代码写错）。</summary>
    public static MailPreset ByKey(string key)
        => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentOutOfRangeException(nameof(key), key, "未知的邮箱预设");
}
