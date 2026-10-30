using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.SpugCaptcha.ViewModels;

/// <summary>
/// Spug 验证码测试工具的 ViewModel：左右两栏各自的发送流程 + 底部本地验证 + 信息日志。
///
/// <para>流程：校验输入 → 决定本次验证码（填了自定义用自定义，留空随机生成 6 位）→ 调 Spug 接口（短信 GET / 邮箱 POST JSON）→
/// 按响应 JSON 的 <c>code == 200</c> 判断是否受理；受理后把验证码记在内存里，
/// 用户收到后到底部输入 → 本地比对 → 显示成功 / 失败。</para>
///
/// <para>验证码是敏感信息：只存在内存里（<see cref="PendingCode"/>），<b>不写配置</b>、也不显示在日志里。
/// 界面状态更新全部走基类的日志 / 属性（后台线程自动切回 UI 线程）。</para>
/// </summary>
public sealed class SpugCaptchaViewModel : ToolViewModelBase
{
    private readonly SpugSender _sender = new();

    private PendingCode? _pendingSms;
    private PendingCode? _pendingMail;

    private string _smsCodeInput = "";
    private string _mailCodeInput = "";

    private string _smsStatusText = "尚未发送";
    private string _mailStatusText = "尚未发送";
    private string _pendingHintText = "还没有成功发送的验证码。";
    private string _verifyCode = "";
    private string _verifyResultText = "";
    private bool _lastVerifySucceeded;
    private bool _lastVerifyFailed;

    public SpugCaptchaViewModel() : base("spug-captcha")
    {
        Cfg = LoadConfig<SpugCaptchaConfig>();

        SendSmsCommand = new AsyncRelayCommand(_ => SendSmsAsync());
        SendMailCommand = new AsyncRelayCommand(_ => SendMailAsync());
        VerifyCommand = new RelayCommand(_ => Verify());
        ClearLogCommand = new RelayCommand(_ =>
        {
            ClearLog();
            Log($"[{DateTime.Now:HH:mm:ss}] 日志已清空（待验证记录不受影响）");
        });

        Log("===== Spug 验证码测试工具 =====");
        Log("用法：粘贴 Spug 控制台复制的模板 URL → 填手机号 / 邮箱 → 发送验证码 → 收到后填到底部「验证码验证」并点「验证」。");
        Log("验证码：两栏的「验证码（可选）」留空就每次随机生成 6 位；填 4~6 位数字/字母则用你指定的值（不写进配置）。");
        Log("说明：Spug 没有校验验证码的接口，本工具只在本机比对刚发送的验证码；模板里的 ${code} / ${number} 是 Spug 服务端占位符。");
        Log($"配置：{ConfigPathText}（推送 URL 是调用凭证，会明文保存在本机，请勿外传）");

        if (Environment.GetEnvironmentVariable("YLT_SPUG_DEMO") == "1") SeedDemo();
    }

    // ---------- 配置 ----------

    /// <summary>强类型配置：两栏 URL / 收件人 / 有效分钟数，改一下自动落盘。</summary>
    public SpugCaptchaConfig Cfg { get; }

    // ---------- 命令 ----------

    /// <summary>发送短信验证码。</summary>
    public AsyncRelayCommand SendSmsCommand { get; }

    /// <summary>发送邮箱验证码。</summary>
    public AsyncRelayCommand SendMailCommand { get; }

    /// <summary>本地校验底部输入的验证码。</summary>
    public RelayCommand VerifyCommand { get; }

    /// <summary>清空信息日志（不影响待验证记录）。</summary>
    public RelayCommand ClearLogCommand { get; }

    // ---------- 界面状态 ----------

    /// <summary>左栏最近一次短信发送的结果（含 Spug 的 msg / request_id 或失败原因）。</summary>
    public string SmsStatusText
    {
        get => _smsStatusText;
        private set => Set(ref _smsStatusText, value);
    }

    /// <summary>右栏最近一次邮箱发送的结果。</summary>
    public string MailStatusText
    {
        get => _mailStatusText;
        private set => Set(ref _mailStatusText, value);
    }

    /// <summary>底部「验证码验证」下方的提示：当前有哪些通道的验证码在等待校验。</summary>
    public string PendingHintText
    {
        get => _pendingHintText;
        private set => Set(ref _pendingHintText, value);
    }

    /// <summary>底部输入框里的验证码（双向绑定）。</summary>
    public string VerifyCode
    {
        get => _verifyCode;
        set => Set(ref _verifyCode, value);
    }

    /// <summary>短信栏「验证码（可选）」：留空则随机生成；填了就用自定义值（只在本轮运行里有效，不写配置）。</summary>
    public string SmsCodeInput
    {
        get => _smsCodeInput;
        set => Set(ref _smsCodeInput, value);
    }

    /// <summary>邮箱栏「验证码（可选）」：留空则随机生成；填了就用自定义值（只在本轮运行里有效，不写配置）。</summary>
    public string MailCodeInput
    {
        get => _mailCodeInput;
        set => Set(ref _mailCodeInput, value);
    }

    /// <summary>验证结果显示文字（成功 / 失败原因）。</summary>
    public string VerifyResultText
    {
        get => _verifyResultText;
        private set => Set(ref _verifyResultText, value);
    }

    /// <summary>上一次验证是否成功（XAML 用它把结果文字染成绿色）。</summary>
    public bool LastVerifySucceeded
    {
        get => _lastVerifySucceeded;
        private set => Set(ref _lastVerifySucceeded, value);
    }

    /// <summary>上一次验证是否失败（XAML 用它把结果文字染成红色）。</summary>
    public bool LastVerifyFailed
    {
        get => _lastVerifyFailed;
        private set => Set(ref _lastVerifyFailed, value);
    }

    // ---------- 短信 ----------

    private async Task SendSmsAsync()
    {
        SmsStatusText = "发送中…";
        var error = ValidateSmsInputs(out var minutes);
        if (error is not null)
        {
            SetSmsFailure(error);
            return;
        }

        var phone = Cfg.Phone.Trim();
        var customCode = (SmsCodeInput ?? "").Trim();
        if (!SpugCaptchaVerifier.TryResolveCode(customCode, out var code, out var codeError))
        {
            SetSmsFailure(codeError);
            return;
        }

        var codeSource = customCode.Length == 0 ? "随机生成（不显示）" : "自定义";
        Log($"[{DateTime.Now:HH:mm:ss}] 短信 → {SpugApi.MaskPhone(phone)}：GET {SpugApi.MaskUrl(Cfg.SmsUrl)}"
            + $"（参数 to / code{(minutes is null ? "" : " / number")}；验证码：{codeSource}）");

        var result = await _sender.SendSmsAsync(Cfg.SmsUrl, phone, code, minutes);
        if (!result.Success)
        {
            SetSmsFailure($"发送失败（code={result.Code}）：{result.Message}");
            return;
        }

        _pendingSms = new PendingCode(SpugChannel.Sms, code, phone, DateTimeOffset.Now);
        SmsStatusText = $"✅ 已受理（{DateTime.Now:HH:mm:ss}）· request_id：{IdOrNone(result.RequestId)}";
        Log($"[{DateTime.Now:HH:mm:ss}] 短信发送成功：{result.Message} · request_id：{IdOrNone(result.RequestId)}"
            + "（验证码已在本机记录，收到后到下方校验）");
        RefreshPendingHint();
    }

    private string? ValidateSmsInputs(out string? minutes)
    {
        minutes = null;
        if (!SpugApi.IsHttpUrl(Cfg.SmsUrl)) return "请先粘贴短信推送 URL（必须以 http:// 或 https:// 开头）";
        if (!SpugApi.IsSmsTemplateUrl(Cfg.SmsUrl))
            return "这个地址不像 Spug 短信模板：路径里应包含 /sms/（请从控制台复制完整调用地址）";
        if (!SpugApi.IsValidPhone(Cfg.Phone)) return "手机号格式不对：应为 11 位、以 1 开头的手机号";

        var minutesText = (Cfg.SmsMinutes ?? "").Trim();
        if (minutesText.Length == 0) return null;
        if (!SpugApi.IsValidSmsMinutes(minutesText))
            return "有效分钟数应为 1~120 的整数（模板只含 ${code} 时留空即可）";
        minutes = minutesText;
        return null;
    }

    private void SetSmsFailure(string message)
    {
        SmsStatusText = "❌ " + message;
        Log($"[{DateTime.Now:HH:mm:ss}] 短信未成功发送：{message}");
    }

    // ---------- 邮箱 ----------

    private async Task SendMailAsync()
    {
        MailStatusText = "发送中…";
        var error = ValidateMailInputs(out var scene, out var minute);
        if (error is not null)
        {
            SetMailFailure(error);
            return;
        }

        var email = Cfg.Email.Trim();
        var customCode = (MailCodeInput ?? "").Trim();
        if (!SpugCaptchaVerifier.TryResolveCode(customCode, out var code, out var codeError))
        {
            SetMailFailure(codeError);
            return;
        }

        var codeSource = customCode.Length == 0 ? "随机生成（不显示）" : "自定义";
        Log($"[{DateTime.Now:HH:mm:ss}] 邮箱 → {SpugApi.MaskEmail(email)}：POST {SpugApi.MaskUrl(Cfg.MailUrl)}"
            + $"（JSON 参数 to / scene / code / minute，场景「{scene}」；验证码：{codeSource}）");

        var result = await _sender.SendMailAsync(Cfg.MailUrl, email, scene, code, minute);
        if (!result.Success)
        {
            SetMailFailure($"发送失败（code={result.Code}）：{result.Message}");
            return;
        }

        _pendingMail = new PendingCode(SpugChannel.Mail, code, email, DateTimeOffset.Now);
        MailStatusText = $"✅ 已受理（{DateTime.Now:HH:mm:ss}）· request_id：{IdOrNone(result.RequestId)}";
        Log($"[{DateTime.Now:HH:mm:ss}] 邮箱发送成功：{result.Message} · request_id：{IdOrNone(result.RequestId)}"
            + "（验证码已在本机记录，收到后到下方校验）");
        RefreshPendingHint();
    }

    private string? ValidateMailInputs(out string scene, out string minute)
    {
        scene = (Cfg.MailScene ?? "").Trim();
        minute = (Cfg.MailMinutes ?? "").Trim();

        if (!SpugApi.IsHttpUrl(Cfg.MailUrl)) return "请先粘贴邮箱推送 URL（必须以 http:// 或 https:// 开头）";
        if (!SpugApi.IsMailTemplateUrl(Cfg.MailUrl))
            return "这个地址不像 Spug 邮箱模板：路径里应包含 /mail/（请从控制台复制完整调用地址）";
        if (!SpugApi.IsValidEmail(Cfg.Email)) return "邮箱格式不对，请检查收件地址";
        if (scene.Length == 0) return "请填写验证场景（scene，例如：登录验证）";
        if (scene.Length > 12) return "验证场景最多 12 个字符（Spug 的限制）";
        if (minute.Length == 0) return "请填写有效分钟数（minute，例如：5）";
        if (minute.Length > 12) return "有效分钟数太长（Spug 的限制：数字或中文数字，如 5 / 五分钟）";
        return null;
    }

    private void SetMailFailure(string message)
    {
        MailStatusText = "❌ " + message;
        Log($"[{DateTime.Now:HH:mm:ss}] 邮箱未成功发送：{message}");
    }

    // ---------- 本地验证 ----------

    private void Verify()
    {
        var pending = new List<PendingCode>();
        if (_pendingSms is not null) pending.Add(_pendingSms);
        if (_pendingMail is not null) pending.Add(_pendingMail);

        var outcome = SpugCaptchaVerifier.Verify(VerifyCode, pending);
        LastVerifySucceeded = outcome.Success;
        LastVerifyFailed = !outcome.Success;
        VerifyResultText = outcome.Message;
        Log($"[{DateTime.Now:HH:mm:ss}] 验证：{outcome.Message}");
    }

    private void RefreshPendingHint()
    {
        var parts = new List<string>();
        if (_pendingSms is not null)
            parts.Add($"短信 {SpugApi.MaskPhone(_pendingSms.Recipient)}（{_pendingSms.SentAt:HH:mm:ss} 发出）");
        if (_pendingMail is not null)
            parts.Add($"邮箱 {SpugApi.MaskEmail(_pendingMail.Recipient)}（{_pendingMail.SentAt:HH:mm:ss} 发出）");

        PendingHintText = parts.Count == 0
            ? "还没有成功发送的验证码。"
            : "当前待验证：" + string.Join("；", parts) + "。";
    }

    // ---------- 截图演示 ----------

    /// <summary>
    /// <c>YLT_SPUG_DEMO=1</c> 时塞一份假的发送 / 验证状态，给截图冒烟看界面用。
    /// 只改内存状态，不写配置、不联网（不会真发短信 / 邮件）。
    /// </summary>
    private void SeedDemo()
    {
        var now = DateTimeOffset.Now;
        _pendingSms = new PendingCode(SpugChannel.Sms, "666888", "13800138000", now.AddSeconds(-40));
        _pendingMail = new PendingCode(SpugChannel.Mail, "666888", "demo@example.com", now.AddSeconds(-20));
        SmsStatusText = $"✅ 已受理（{now.AddSeconds(-40):HH:mm:ss}）· request_id：demo-sms-request-id";
        MailStatusText = $"✅ 已受理（{now.AddSeconds(-20):HH:mm:ss}）· request_id：demo-mail-request-id";
        VerifyCode = "666888";
        VerifyResultText = "✅ 验证成功（短信 + 邮箱）";
        LastVerifySucceeded = true;
        RefreshPendingHint();
        Log("[演示] 已填入演示状态（YLT_SPUG_DEMO=1，仅用于截图，不会真发短信 / 邮件）");
    }

    private static string IdOrNone(string requestId)
        => string.IsNullOrWhiteSpace(requestId) ? "（无）" : requestId;
}
