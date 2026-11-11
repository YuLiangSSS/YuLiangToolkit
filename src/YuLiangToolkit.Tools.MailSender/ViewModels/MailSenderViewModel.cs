using System.Collections.ObjectModel;
using System.Windows.Documents;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.MailSender.ViewModels;

/// <summary>
/// 「邮件推送」的 ViewModel：管配置、附件清单、请求状态与日志；**富文本编辑体本身在界面层**
/// （<see cref="Views.RichTextHtml"/>），ViewModel 只通过 <see cref="GetHtml"/> / <see cref="LoadHtml"/>
/// 这两个委托跟界面交换 HTML，这样它自己仍然可以被无头调用（自检 / 自动化不建窗口）。
///
/// <para>发送流程：界面回调 <see cref="GetHtml"/> 拿到当前正文（行内图片的 <c>src</c> 已换成 <c>cid:</c>）
/// → <see cref="MailComposer"/> 校验并组装 → <see cref="ResendClient"/> 发出去 → 结果写状态行 + 日志。</para>
/// </summary>
public sealed class MailSenderViewModel : ToolViewModelBase
{
    private readonly ResendClient _client;

    private string _apiKey = "";
    private bool _apiKeyVisible;
    private bool _lastSendSucceeded;
    private bool _lastSendFailed;
    private string _lastEmailId = "";
    private string _lastQueryText = "";
    private string _bodySummary = "";
    private MailAttachmentItem? _selectedAttachment;

    public MailSenderViewModel() : base("mail-sender")
    {
        _client = new ResendClient();
        Cfg = LoadConfig<MailSenderConfig>();
        Cfg.Normalize();
        // 配置文件里存的是密文，界面只拿到明文；不打印、不落日志
        _apiKey = ApiKeyStore.Unprotect(Cfg.ApiKeyCipher);

        foreach (var path in Cfg.Attachments) Attachments.Add(new MailAttachmentItem(path));

        SendCommand = new AsyncRelayCommand(_ => SendAsync(), _ => !IsBusy);
        QueryLastCommand = new AsyncRelayCommand(_ => QueryLastAsync(), _ => !IsBusy && LastEmailId.Length > 0);
        AddAttachmentCommand = new RelayCommand(_ => AddAttachmentsRequested?.Invoke());
        RemoveAttachmentCommand = new RelayCommand(_ => RemoveSelectedAttachment(), _ => SelectedAttachment is not null);
        ClearAttachmentsCommand = new RelayCommand(_ => ClearAttachments(), _ => Attachments.Count > 0);
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        PickApiKeyCommand = new RelayCommand(_ => ToggleApiKeyVisible());
        GenerateIdempotencyKeyCommand = new RelayCommand(_ => IdempotencyKey = Guid.NewGuid().ToString("N"));

        Log("===== 邮件推送（Resend POST /emails） =====");
        Log("流程：填发件人 / 收件人 / 主题 → 写正文（富文本或 HTML 源码）→ 加附件 → 点「发送」。");
        Log("API Key 用 Windows DPAPI 加密保存，配置里只有密文；日志只显示打码后的样子。");
        Log($"推送地址：{Cfg.Endpoint}");
        if (!Cfg.HasApiKey) Log("提示：还没填 API Key，点右上角「API Key」输入后再发送。");
    }

    // ---------- 配置 ----------

    /// <summary>强类型配置；属性改一下自动落盘。</summary>
    public MailSenderConfig Cfg { get; }

    // ---------- 与界面交换正文 ----------

    /// <summary>界面提供：取当前正文 HTML（行内图片换成 cid: 之后的版本）。</summary>
    public Func<string>? GetHtml { get; set; }

    /// <summary>界面提供：把 HTML 载入编辑区（打开工具时恢复草稿、切回富文本时用）。</summary>
    public Action<string>? LoadHtml { get; set; }

    /// <summary>界面提供：请求用户选择附件文件（打开文件对话框是界面的事）。</summary>
    public Action? AddAttachmentsRequested { get; set; }

    /// <summary>界面提供：滚动到日志（发送结束后把最新结果露出来）。</summary>
    public Action? ScrollLogToEnd { get; set; }

    // ---------- 命令 ----------

    /// <summary>发送邮件。</summary>
    public AsyncRelayCommand SendCommand { get; }

    /// <summary>按发送返回的 id 查一次投递状态（Resend 的 GET /emails/{id}）。</summary>
    public AsyncRelayCommand QueryLastCommand { get; }

    /// <summary>添加附件（弹出文件选择框）。</summary>
    public RelayCommand AddAttachmentCommand { get; }

    /// <summary>移除选中的附件。</summary>
    public RelayCommand RemoveAttachmentCommand { get; }

    /// <summary>清空附件清单。</summary>
    public RelayCommand ClearAttachmentsCommand { get; }

    /// <summary>清空日志。</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>显示 / 隐藏 API Key 明文。</summary>
    public RelayCommand PickApiKeyCommand { get; }

    /// <summary>生成一个幂等键（同一封邮件重试不会重复投递）。</summary>
    public RelayCommand GenerateIdempotencyKeyCommand { get; }

    // ---------- API Key ----------

    /// <summary>API Key 明文（只在内存里；落盘走 <see cref="MailSenderConfig.ApiKeyCipher"/>）。</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (!Set(ref _apiKey, value ?? "")) return;
            Cfg.ApiKeyCipher = ApiKeyStore.Protect(_apiKey);
            OnPropertyChanged(nameof(ApiKeyMasked));
            OnPropertyChanged(nameof(ApiKeyHint));
        }
    }

    /// <summary>打码后的 Key（日志 / 状态行用）。</summary>
    public string ApiKeyMasked => ApiKeyStore.Mask(_apiKey);

    /// <summary>Key 输入框下方的一行提示。</summary>
    public string ApiKeyHint => Cfg.HasApiKey
        ? $"已保存（DPAPI 密文）：{ApiKeyMasked}"
        : "还没填写：到推送服务控制台创建 API Key 后粘贴到这里";

    /// <summary>Key 是否以明文显示。</summary>
    public bool ApiKeyVisible
    {
        get => _apiKeyVisible;
        set => Set(ref _apiKeyVisible, value);
    }

    private void ToggleApiKeyVisible()
    {
        ApiKeyVisible = !ApiKeyVisible;
        StatusText = ApiKeyVisible ? "API Key 已显示明文（注意别截到屏幕里）" : "API Key 已隐藏";
    }

    /// <summary>可选的幂等键（留空就不带这个头）。</summary>
    public string IdempotencyKey
    {
        get => _idempotencyKey;
        set => Set(ref _idempotencyKey, value ?? "");
    }

    private string _idempotencyKey = "";

    // ---------- 附件 ----------

    /// <summary>附件清单（界面 ListBox 绑定）。</summary>
    public ObservableCollection<MailAttachmentItem> Attachments { get; } = new();

    /// <summary>选中的附件（「移除」按钮作用于它）。</summary>
    public MailAttachmentItem? SelectedAttachment
    {
        get => _selectedAttachment;
        set
        {
            if (Set(ref _selectedAttachment, value)) StatusText = value is null ? "就绪" : $"已选中附件：{value.FileName}";
        }
    }

    /// <summary>附件摘要：<c>2 个 · 共 1.2 MB（base64 后 1.6 MB）</c>。</summary>
    public string AttachmentSummary
    {
        get
        {
            if (Attachments.Count == 0) return "还没有附件";
            var raw = Attachments.Sum(a => Math.Max(a.SizeBytes, 0));
            var encoded = Attachments.Sum(a => a.Base64Bytes);
            return $"{Attachments.Count} 个 · 共 {Core.Storage.FileSize.Format(raw)}" +
                   $"（base64 后 {Core.Storage.FileSize.Format(encoded)}，上限 " +
                   $"{Core.Storage.FileSize.Format(MailSenderDefaults.MaxEmailBytesAfterBase64)}）";
        }
    }

    /// <summary>有没有附件（空状态提示用）。</summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>加一批附件（拖入 / 文件对话框都会走这里）。</summary>
    public void AddAttachmentPaths(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;
        foreach (var path in MailAttachmentItem.Distinct(paths))
        {
            if (Attachments.Any(a => string.Equals(a.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            if (!File.Exists(path))
            {
                Log($"跳过（文件不存在）：{path}");
                skipped++;
                continue;
            }
            var item = new MailAttachmentItem(path);
            Attachments.Add(item);
            Cfg.Attachments.Add(path);
            added++;
        }

        if (added > 0) Log($"已添加 {added} 个附件" + (skipped > 0 ? $"（跳过 {skipped} 个重复 / 不存在的）" : ""));
        else if (skipped > 0) Log($"没有新增附件（{skipped} 个重复 / 不存在的已跳过）");

        RefreshAttachmentSummary();
    }

    private void RemoveSelectedAttachment()
    {
        var item = SelectedAttachment;
        if (item is null) return;
        Attachments.Remove(item);
        Cfg.Attachments.RemoveAll(p => string.Equals(p, item.FilePath, StringComparison.OrdinalIgnoreCase));
        SelectedAttachment = Attachments.FirstOrDefault();
        Log($"已移除附件：{item.FileName}");
        RefreshAttachmentSummary();
    }

    private void ClearAttachments()
    {
        Attachments.Clear();
        Cfg.Attachments.Clear();
        SelectedAttachment = null;
        Log("已清空附件清单");
        RefreshAttachmentSummary();
    }

    private void RefreshAttachmentSummary()
    {
        OnPropertyChanged(nameof(AttachmentSummary));
        OnPropertyChanged(nameof(HasAttachments));
    }

    // ---------- 状态 ----------

    /// <summary>上一次发送成功（XAML 用它把状态行染成绿色）。</summary>
    public bool LastSendSucceeded
    {
        get => _lastSendSucceeded;
        private set => Set(ref _lastSendSucceeded, value);
    }

    /// <summary>上一次发送失败（XAML 用它把状态行染成红色）。</summary>
    public bool LastSendFailed
    {
        get => _lastSendFailed;
        private set => Set(ref _lastSendFailed, value);
    }

    /// <summary>最近一次发送拿到的邮件 id（查询状态 / 找客服时用）。</summary>
    public string LastEmailId
    {
        get => _lastEmailId;
        private set
        {
            if (Set(ref _lastEmailId, value ?? "")) OnPropertyChanged(nameof(HasLastEmailId));
        }
    }

    /// <summary>有没有最近一次的邮件 id（「查询状态」按钮据此变灰）。</summary>
    public bool HasLastEmailId => LastEmailId.Length > 0;

    /// <summary>查询到的投递状态文字。</summary>
    public string LastQueryText
    {
        get => _lastQueryText;
        private set => Set(ref _lastQueryText, value ?? "");
    }

    /// <summary>正文摘要（右侧日志区顶部显示「HTML xx KB / 纯文本 xx KB」）。</summary>
    public string BodySummary
    {
        get => _bodySummary;
        private set => Set(ref _bodySummary, value ?? "");
    }

    /// <summary>刷新正文摘要（界面在编辑时调用）。</summary>
    public void UpdateBodySummary(string html)
    {
        var plain = MailHtml.ToPlainText(html);
        BodySummary = MailHtml.IsEmptyBody(html)
            ? "正文为空"
            : $"HTML {Core.Storage.FileSize.Format(System.Text.Encoding.UTF8.GetByteCount(html))} · " +
              $"纯文本 {plain.Length} 字";
    }

    // ---------- 发送 ----------

    private async Task SendAsync()
    {
        if (IsBusy) return;

        var html = GetHtml?.Invoke() ?? "";
        var inlineImages = InlineImages;
        var compose = MailComposer.Compose(Cfg, html, Attachments.Select(a => a.FilePath), inlineImages);

        if (!compose.Success)
        {
            LastSendSucceeded = false;
            LastSendFailed = true;
            StatusText = compose.FirstError;
            Log("— 发送前检查没通过 —");
            foreach (var error in compose.Errors) Log("  · " + error);
            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            LastSendSucceeded = false;
            LastSendFailed = true;
            StatusText = "还没填 API Key";
            Log("发送被拦下：API Key 为空（点右上角「API Key」填一个）");
            return;
        }

        IsBusy = true;
        LastSendSucceeded = false;
        LastSendFailed = false;
        StatusText = "正在发送…";
        Progress = 0.15;

        var request = compose.Request!;
        Log("— 开始发送 —");
        Log($"  发件人：{request.From}");
        Log($"  收件人：{string.Join("、", request.To)}" +
            (request.Cc is { Count: > 0 } ? $"；抄送 {string.Join("、", request.Cc)}" : "") +
            (request.Bcc is { Count: > 0 } ? $"；密送 {request.Bcc.Count} 个" : ""));
        Log($"  主题：{request.Subject}");
        Log($"  正文：HTML {Core.Storage.FileSize.Format(System.Text.Encoding.UTF8.GetByteCount(request.Html ?? ""))}" +
            (string.IsNullOrEmpty(request.Text) ? "" : $" + 纯文本 {request.Text!.Length} 字"));
        if (request.Attachments is { Count: > 0 })
            Log($"  附件：{request.Attachments.Count} 个（base64 后 {Core.Storage.FileSize.Format(compose.AttachmentBytesAfterBase64)}）");
        Log($"  API Key：{ApiKeyMasked}" + (string.IsNullOrWhiteSpace(IdempotencyKey) ? "" : $"；幂等键 {IdempotencyKey}"));

        try
        {
            Progress = 0.5;
            var result = await _client.SendAsync(Cfg.Endpoint, _apiKey, request, IdempotencyKey).ConfigureAwait(false);
            Progress = 1;

            LastSendSucceeded = result.Success;
            LastSendFailed = !result.Success;
            StatusText = result.Success
                ? $"发送成功 · id {result.EmailId}（{ResendClient.StatusText(result.HttpStatus)}）"
                : $"发送失败：{result.ErrorMessage}";

            if (result.Success)
            {
                LastEmailId = result.EmailId;
                LastQueryText = "";
                Log($"✔ 发送成功：HTTP {ResendClient.StatusText(result.HttpStatus)}，邮件 id {result.EmailId}");
                Log("  （「推送接口返回 id」只代表邮件已被接收并排队投递；投递结果可点「查询状态」或到控制台看。）");
            }
            else
            {
                Log($"✘ 发送失败：HTTP {ResendClient.StatusText(result.HttpStatus)}" +
                    (result.ErrorName.Length > 0 ? $" · {result.ErrorName}" : ""));
                Log("  " + result.ErrorMessage);
                if (result.RawBody.Length > 0) Log("  服务端响应：" + result.RawBody.Replace("\r", " ").Replace("\n", " "));
            }
        }
        catch (Exception ex)
        {
            // 命令执行体里的异常会被 AsyncRelayCommand 吞掉（界面只会停在「正在发送…」），
            // 所以这里必须自己兜住并写进状态行 / 日志。
            LastSendSucceeded = false;
            LastSendFailed = true;
            StatusText = "发送时出现异常：" + ex.Message;
            Log("✘ 发送时出现异常：" + ex.GetType().Name + "：" + ex.Message);
            Log("  " + (ex.StackTrace ?? "").Replace("\r", " ").Replace("\n", " "));
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            ScrollLogToEnd?.Invoke();
            Cfg.SaveNow();
        }
    }

    /// <summary>
    /// 编辑器里的行内图片（由界面在发送前通过 <see cref="RegisterInlineImage"/> 登记）。
    /// 每张图给一个稳定的 <c>content_id</c>，HTML 里对应 <c>cid:…</c>。
    /// </summary>
    public IReadOnlyList<InlineImage> InlineImages => _inlineImages;

    private readonly List<InlineImage> _inlineImages = new();

    /// <summary>登记一张要随邮件上传的行内图片（界面在解析正文时调用），返回它的 content_id。</summary>
    public string RegisterInlineImage(string filePath)
    {
        var existing = _inlineImages.FirstOrDefault(
            i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.ContentId;

        var image = new InlineImage(filePath, MailComposer.NextContentId(_inlineImages.Count + 1));
        _inlineImages.Add(image);
        return image.ContentId;
    }

    /// <summary>每次发送前清空登记表（界面会重新解析正文，图片列表随之重建）。</summary>
    public void ResetInlineImages() => _inlineImages.Clear();

    // ---------- 查询投递状态 ----------

    private async Task QueryLastAsync()
    {
        if (IsBusy || LastEmailId.Length == 0) return;
        IsBusy = true;
        LastQueryText = "正在查询…";
        try
        {
            var url = BuildEmailQueryUrl(Cfg.Endpoint, LastEmailId);
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var message = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey.Trim());
            message.Headers.TryAddWithoutValidation("User-Agent", MailSenderDefaults.UserAgent);

            var response = await http.SendAsync(message).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var error = ResendClient.ParseError(body, status);
                LastQueryText = $"查询失败（HTTP {status}）：{error.Message}";
                Log("查询状态失败：" + LastQueryText);
                return;
            }

            var lastEvent = ReadLastEvent(body);
            LastQueryText = lastEvent.Length > 0 ? $"投递状态：{TranslateEvent(lastEvent)}" : "查询成功，但响应里没有投递状态字段";
            Log($"查询状态（id {LastEmailId}）：{lastEvent}（{ResendClient.StatusText(status)}）" +
                (lastEvent.Length > 0 ? $" → {TranslateEvent(lastEvent)}" : ""));
        }
        catch (Exception ex)
        {
            LastQueryText = "查询失败：" + ex.Message;
            Log("查询状态失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>由发送地址推出「查这一封」的地址（<c>/emails</c> → <c>/emails/{id}</c>）。</summary>
    public static string BuildEmailQueryUrl(string endpoint, string emailId)
    {
        var trimmed = string.IsNullOrWhiteSpace(endpoint) ? MailSenderDefaults.Endpoint : endpoint.Trim().TrimEnd('/');
        return $"{trimmed}/{emailId}";
    }

    /// <summary>从 <c>GET /emails/{id}</c> 的响应里取 <c>last_event</c>（取不到返回空串）。</summary>
    public static string ReadLastEvent(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("last_event", out var value)
                ? value.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>把 <c>last_event</c>（delivered / bounced / …）翻成中文。</summary>
    public static string TranslateEvent(string? lastEvent) => (lastEvent ?? "").Trim().ToLowerInvariant() switch
    {
        "sent" => "已发给投递服务（sent）",
        "delivered" => "已送达收件服务器（delivered）",
        "delivery_delayed" => "投递延迟（delivery_delayed）",
        "complained" => "收件人标记为垃圾邮件（complained）",
        "bounced" => "被退回（bounced）：收件地址可能不存在",
        "opened" => "收件人已打开（opened）",
        "clicked" => "收件人点了邮件里的链接（clicked）",
        "" => "（还没有状态）",
        _ => lastEvent!,
    };

    // ---------- 生命周期 ----------

    /// <summary>切走工具时把正文草稿落盘（正文是界面那边的，所以由界面回写后这里再 Flush）。</summary>
    protected override void OnClosed()
    {
        try
        {
            WriteBack?.Invoke();       // 界面把编辑区内容回写进 Cfg.BodyHtml
            Cfg.SaveNow();
        }
        catch
        {
            // 退出流程忽略
        }
    }

    /// <summary>界面提供：把编辑区内容回写进配置（切走 / 退出前落盘草稿）。</summary>
    public Action? WriteBack { get; set; }
}
