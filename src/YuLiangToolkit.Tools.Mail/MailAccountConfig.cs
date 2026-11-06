using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Mail;

/// <summary>
/// 一个邮箱账号的配置（存进 <c>Config\mail-collector\config.json</c> 的 <c>accounts</c> 数组）。
///
/// <para>字段分两类：</para>
/// <list type="bullet">
///   <item>可读写属性 = 会落盘（显示名称 / 邮箱 / IMAP 服务器 / 端口 / SSL / 是否启用）；</item>
///   <item><c>[JsonIgnore]</c> 属性 = 不落盘：<see cref="Password"/> 是解密后的明文视图，
///         落盘的是它写入的 <see cref="PasswordCipher"/>（DPAPI 密文）；<see cref="StatusText"/> 等是运行状态。</item>
/// </list>
///
/// <para>属性用 <c>Set(ref ...)</c> 写是为了界面联动：改显示名称时账号列表、筛选下拉框能立刻刷新。
/// 配置的自动保存不依赖通知（见 <c>ToolConfigStore</c> 的内容比对）。</para>
/// </summary>
public sealed class MailAccountConfig : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _displayName = "";
    private string _email = "";
    private string _passwordCipher = "";
    private string _host = "";
    private int _port = 993;
    private bool _useSsl = true;
    private bool _enabled = true;
    private string _statusText = "未连接";

    /// <summary>稳定 Id（新增账号时生成；筛选 / 保存布局用）。</summary>
    public string Id
    {
        get => _id;
        set => Set(ref _id, value ?? "");
    }

    /// <summary>显示名称（只在界面 / 日志里用，不参与登录），留空时显示邮箱地址。</summary>
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (Set(ref _displayName, value ?? ""))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }

    /// <summary>邮箱地址（同时作为 IMAP 登录用户名；QQ / Gmail / 163 都是完整邮箱地址）。</summary>
    public string Email
    {
        get => _email;
        set
        {
            if (Set(ref _email, (value ?? "").Trim()))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(DisplaySubtitle));
                OnPropertyChanged(nameof(IsConfigured));
            }
        }
    }

    /// <summary>DPAPI 加密后的授权码 / 应用专用密码（真正落盘的字段）。</summary>
    public string PasswordCipher
    {
        get => _passwordCipher;
        set
        {
            if (Set(ref _passwordCipher, value ?? "")) OnPropertyChanged(nameof(IsConfigured));
        }
    }

    /// <summary>
    /// 明文授权码 / 应用专用密码（<b>不落盘</b>）：读 = 解密 <see cref="PasswordCipher"/>，
    /// 写 = 加密后存进 <see cref="PasswordCipher"/>。XAML 直接绑定这个属性，配置文件里不会出现明文。
    ///
    /// <para>注意：这是带 setter 的计算属性，必须标 [JsonIgnore]（项目硬性规则）。</para>
    /// </summary>
    [JsonIgnore]
    public string Password
    {
        get => SecureStore.Unprotect(PasswordCipher);
        set
        {
            var cipher = SecureStore.Protect(value);
            if (cipher == PasswordCipher) return;
            PasswordCipher = cipher;
            OnPropertyChanged();
        }
    }

    /// <summary>IMAP 服务器地址（例如 imap.gmail.com）。</summary>
    public string Host
    {
        get => _host;
        set
        {
            if (!Set(ref _host, (value ?? "").Trim())) return;
            OnPropertyChanged(nameof(DisplaySubtitle));
            OnPropertyChanged(nameof(IsConfigured));
        }
    }

    /// <summary>IMAP 端口（SSL 一般是 993）。</summary>
    public int Port
    {
        get => _port;
        set
        {
            if (!Set(ref _port, value)) return;
            OnPropertyChanged(nameof(DisplaySubtitle));
        }
    }

    /// <summary>是否用 SSL（993 选是；143 + STARTTLS 选否）。</summary>
    public bool UseSsl
    {
        get => _useSsl;
        set => Set(ref _useSsl, value);
    }

    /// <summary>是否参与「代收」（取消勾选就跳过这个账号，配置保留）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>运行状态（只显示在账号列表里，不落盘：✅ 成功 / ❌ 失败 / 连接中…）。</summary>
    [JsonIgnore]
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value ?? "");
    }

    /// <summary>账号标题：优先显示名称，其次邮箱，最后「未命名账号」。</summary>
    [JsonIgnore]
    public string DisplayTitle
        => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
            : !string.IsNullOrWhiteSpace(Email) ? Email
            : "未命名账号";

    /// <summary>账号副标题：邮箱 · 服务器:端口。</summary>
    [JsonIgnore]
    public string DisplaySubtitle
    {
        get
        {
            var mail = string.IsNullOrWhiteSpace(Email) ? "未填邮箱" : Email;
            var server = string.IsNullOrWhiteSpace(Host) ? "未填服务器" : $"{Host}:{Port}";
            return $"{mail} · {server}";
        }
    }

    /// <summary>必填项（邮箱 / 服务器 / 授权码）是否都填了。</summary>
    [JsonIgnore]
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(Email)
           && !string.IsNullOrWhiteSpace(Host)
           && !string.IsNullOrWhiteSpace(Password);
}
