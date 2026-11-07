using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Mail.ViewModels;

/// <summary>
/// 「邮箱代收」的 ViewModel：账号管理 + 多账号 IMAP 只读拉取（收件箱 / 已发送 / 垃圾箱）+ 文件夹导航 /
/// 搜索 + 选中邮件后按需拉正文和附件 + 附件拖拽 + 日志。
///
/// <para><b>数据流</b>：点「刷新全部」→ 逐个启用账号连 IMAP（只读）拉三个文件夹最近 N 封摘要 →
/// 合并成一个按时间倒序的列表（<see cref="MailMessageRow"/>）→ 左侧导航只筛当前账号 / 文件夹 →
/// 选中某行时才连回该账号拉完整正文（<see cref="ImapMailService.GetMessageAsync"/>）。列表本身不落盘...
///
/// <para><b>线程</b>：所有 await 都从 UI 线程发起并回到 UI 线程；MailKit 的网络 IO 不会卡界面。
/// 异常按账号隔离：一个账号认证失败只影响它自己的状态，其它账号照常收。</para>
/// </summary>
public sealed class MailCollectorViewModel : ToolViewModelBase
{
    private readonly ImapMailService _service;
    private readonly List<MailMessageRow> _allMessages = new();
    private readonly Dictionary<string, Dictionary<MailFolderKind, string>> _folderNotices = new();

    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _detailCts;
    private int _detailGeneration;

    private MailAccountConfig? _selectedAccount;
    private MailNavigationNode? _selectedNavigationItem;
    private MailMessageRow? _selectedMessage;
    private string _searchText = "";
    private bool _onlyUnread;
    private bool _viewLoadedHintShown;
    private DateTime _lastAutoRefreshUtc = DateTime.MinValue;

    private string _accountsSummary = "";
    private string _messagesSummary = "";
    private bool _showSettingsTab;
    private string _detailSubject = "（未选择邮件）";
    private string _detailFrom = "";
    private string _detailTo = "";
    private string _detailDate = "";
    private string _detailStatus = "选中左侧列表里的一封邮件即可查看正文。";
    private string _messageBodyText = "";
    private string? _messageHtml;
    private bool _forcePlainText;
    private bool _htmlPreviewUnavailable;
    private string _attachmentsSummary = "";

    public MailCollectorViewModel() : base("mail-collector")
    {
        Cfg = LoadConfig<MailCollectorConfig>();
        if (Cfg.Accounts is null) Cfg.Accounts = new ObservableCollection<MailAccountConfig>();
        Cfg.Proxy ??= new MailProxySettings();
        _service = new ImapMailService(Cfg.Proxy);

        Cfg.Accounts.CollectionChanged += OnAccountsCollectionChanged;
        foreach (var account in Cfg.Accounts)
        {
            account.PropertyChanged += OnAccountPropertyChanged;
            // 配置是从别的 Windows 用户 / 电脑拷来的：DPAPI 密文解不开，提示用户重填而不是显示一个空白密码框
            if (!string.IsNullOrEmpty(account.PasswordCipher) && string.IsNullOrEmpty(account.Password))
            {
                account.StatusText = "⚠️ 授权码无法解密（换 Windows 用户 / 电脑后请重新填）";
            }
        }

        RefreshAllCommand = new AsyncRelayCommand(_ => RefreshAllAsync());
        TestConnectionCommand = new AsyncRelayCommand(_ => TestConnectionAsync());
        AddAccountCommand = new RelayCommand(_ => AddAccount());
        DeleteAccountCommand = new RelayCommand(_ => DeleteAccount(), _ => SelectedAccount is not null);
        ToggleSortDirectionCommand = new RelayCommand(_ => SortDescending = !SortDescending);
        CancelCommand = new RelayCommand(_ => _refreshCts?.Cancel(), _ => IsBusy);
        SaveAttachmentCommand = new AsyncRelayCommand(
            p => SaveAttachmentAsync(p as MailAttachmentItem),
            p => p is MailAttachmentItem);
        OpenAttachmentCommand = new AsyncRelayCommand(
            p => OpenAttachmentAsync(p as MailAttachmentItem),
            p => p is MailAttachmentItem);
        SaveAllAttachmentsCommand = new AsyncRelayCommand(_ => SaveAllAttachmentsAsync());
        CopyBodyCommand = new RelayCommand(_ => CopyBody());
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        OpenHelpCommand = new RelayCommand(_ => OpenHelp(), _ => HasPresetHelp);
        ShowSettingsTabCommand = new RelayCommand(_ => ShowSettingsTab = true);
        RefreshProxyStatusCommand = new RelayCommand(_ => OnPropertyChanged(nameof(ProxyStatusText)));
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());
        OpenDataFolderCommand = new RelayCommand(_ => Storage.OpenInExplorer());

        RebuildNavigationNodes();
        SelectedNavigationItem =
            FindNavigationNode(Cfg.LastSelectedAccountId, Cfg.LastSelectedFolder)
            ?? NavigationItems.FirstOrDefault(node => node.Folder == MailFolderKind.Inbox)
            ?? NavigationItems.FirstOrDefault();
        UpdateSummaries();
        ClearDetail();

        Log("===== 邮箱代收 =====");
        Log("用法：点「新增账号」→ 选服务商预设（Gmail / QQ / 163…）→ 填邮箱 + 授权码 / 应用专用密码 → 点「刷新全部」。");
        Log("只读：拉取收件箱 / 已发送 / 垃圾箱，不会删除 / 移动 / 标记已读；一个文件夹失败不影响其它文件夹。");
        Log($"配置文件：{ConfigPathText}（授权码已用 Windows DPAPI 加密，换电脑 / 换 Windows 用户要重新填）");
        Log($"代理：{Cfg.Proxy.Describe("imap.gmail.com")}（工具条「网络代理」里可改）");

        // 开发调试 / 截图冒烟用：YLT_MAIL_SETTINGS=1 时直接打开右栏「账号设置」页签（不联网、不改配置）
        if (Environment.GetEnvironmentVariable("YLT_MAIL_SETTINGS") == "1") ShowSettingsTab = true;
    }

    // ---------- 配置 / 账号 ----------

    /// <summary>强类型配置：账号列表 + 拉取数量等。</summary>
    public MailCollectorConfig Cfg { get; }

    /// <summary>账号列表（直接绑定配置里的集合，增删即自动保存）。</summary>
    public ObservableCollection<MailAccountConfig> Accounts => Cfg.Accounts;

    /// <summary>服务商预设（下拉框数据源）。</summary>
    public IReadOnlyList<MailPreset> Presets => MailPresets.All;

    /// <summary>左侧邮件夹导航：每个账号一行「全部邮件」，随后平铺收件箱 / 已发送 / 垃圾箱。</summary>
    public ObservableCollection<MailNavigationNode> NavigationItems { get; } = new();

    /// <summary>当前选中的账号（右侧表单编辑它；绑定直接改配置对象，自动落盘）。</summary>
    public MailAccountConfig? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!Set(ref _selectedAccount, value)) return;
            Cfg.LastSelectedAccountId = value?.Id ?? "";
            OnPropertyChanged(nameof(HasSelectedAccount));
            OnPropertyChanged(nameof(SelectedPreset));
            OnPropertyChanged(nameof(PresetHintText));
            OnPropertyChanged(nameof(HasPresetHelp));
            OnPropertyChanged(nameof(HelpButtonText));
        }
    }

    public bool HasSelectedAccount => SelectedAccount is not null;

    /// <summary>当前选中的邮件夹导航行；它同时决定账号操作对象和中间列表的数据范围。</summary>
    public MailNavigationNode? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (!Set(ref _selectedNavigationItem, value)) return;

            if (value is not null)
            {
                SelectedAccount = value.Account;
                Cfg.LastSelectedAccountId = value.Account.Id;
                Cfg.LastSelectedFolder = value.FolderKey;
            }
            else
            {
                SelectedAccount = null;
            }

            OnPropertyChanged(nameof(CurrentFolderTitle));
            OnPropertyChanged(nameof(EmptyMessagesHint));
            ApplyFilter();
        }
    }

    public string CurrentFolderTitle => SelectedNavigationItem is null
        ? "邮件列表"
        : SelectedNavigationItem.IsAccount
            ? $"{SelectedNavigationItem.Account.DisplayTitle} · 全部邮件"
            : $"{SelectedNavigationItem.Account.DisplayTitle} · {SelectedNavigationItem.DisplayName}";

    public string EmptyMessagesHint => SelectedNavigationItem is null
        ? "先新增账号，然后点「刷新全部」"
        : SelectedNavigationItem.IsAccount
            ? "当前账号还没有代收记录：点「刷新全部」读取收件箱 / 已发送 / 垃圾箱"
            : $"「{SelectedNavigationItem.DisplayName}」还没有代收记录：点「刷新全部」读取";

    /// <summary>表单上的服务商预设：根据当前账号的服务器 / 端口反查；改它会把服务器 / 端口 / SSL 填好。</summary>
    public MailPreset SelectedPreset
    {
        get => SelectedAccount is null
            ? MailPresets.Custom
            : MailPresets.Match(SelectedAccount.Host, SelectedAccount.Port, SelectedAccount.UseSsl);
        set
        {
            if (value is null || SelectedAccount is null) return;
            value.ApplyTo(SelectedAccount);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PresetHintText));
            OnPropertyChanged(nameof(HasPresetHelp));
            OnPropertyChanged(nameof(HelpButtonText));
        }
    }

    /// <summary>当前预设的「授权码怎么弄」提示。</summary>
    public string PresetHintText => SelectedAccount is null
        ? "先新增或选中一个账号；选好服务商预设后，这里会显示授权码 / 应用专用密码的申请提示。"
        : SelectedPreset.Hint;

    public bool HasPresetHelp => SelectedAccount is not null && !string.IsNullOrWhiteSpace(SelectedPreset.HelpUrl);

    public string HelpButtonText => SelectedPreset.HelpLabel ?? "打开帮助页面";

    // ---------- 收件列表 ----------

    /// <summary>筛选 / 搜索后显示在列表里的邮件（数据源 <see cref="_allMessages"/>）。</summary>
    public ObservableCollection<MailMessageRow> VisibleMessages { get; } = new();

    /// <summary>当前选中的邮件（选中后自动拉正文）。</summary>
    public MailMessageRow? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (!Set(ref _selectedMessage, value)) return;
            OnPropertyChanged(nameof(HasSelectedMessage));
            _ = LoadDetailAsync(value);
        }
    }

    public bool HasSelectedMessage => SelectedMessage is not null;

    /// <summary>搜索框（同时匹配主题 / 发件人 / 收件人 / 账号名，空格分隔多个关键词需同时命中）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            ApplyFilter();
        }
    }

    /// <summary>只看未读。</summary>
    public bool OnlyUnread
    {
        get => _onlyUnread;
        set
        {
            if (!Set(ref _onlyUnread, value)) return;
            ApplyFilter();
        }
    }

    // ---------- 排序 ----------

    /// <summary>排序键：日期 / 发件人 / 收件人 / 主题 / 账号 / 附件 / 优先级 / 是否未读 / 大小。</summary>
    public IReadOnlyList<OptionItem<string>> SortOptions { get; } = OptionItem.From(
        ("date", "日期"),
        ("from", "发件人"),
        ("to", "收件人"),
        ("subject", "主题"),
        ("account", "账号"),
        ("attachments", "附件"),
        ("priority", "优先级"),
        ("unread", "是否未读"),
        ("size", "大小"));

    /// <summary>排序下拉框桥接属性（存配置的是干净值）。</summary>
    public OptionItem<string> SelectedSort
    {
        get => OptionItem<string>.Find(SortOptions, Cfg.SortBy) ?? SortOptions[0];
        set
        {
            if (value is null) return;
            Cfg.SortBy = value.Value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    /// <summary>是否降序（默认 true = 日期新的在上）。</summary>
    public bool SortDescending
    {
        get => Cfg.SortDescending;
        set
        {
            if (Cfg.SortDescending == value) return;
            Cfg.SortDescending = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SortDirectionText));
            OnPropertyChanged(nameof(SortDirectionToolTip));
            ApplyFilter();
        }
    }

    /// <summary>排序方向按钮的文字。</summary>
    public string SortDirectionText => SortDescending ? "↓ 降序" : "↑ 升序";

    public string SortDirectionToolTip => SortDescending ? "当前降序；点击改成升序" : "当前升序；点击改成降序";

    public bool HasAccounts => Accounts.Count > 0;

    public bool HasVisibleMessages => VisibleMessages.Count > 0;

    public string AccountsSummary
    {
        get => _accountsSummary;
        private set => Set(ref _accountsSummary, value);
    }

    public string MessagesSummary
    {
        get => _messagesSummary;
        private set => Set(ref _messagesSummary, value);
    }

    // ---------- 邮件详情 ----------

    public string DetailSubject
    {
        get => _detailSubject;
        private set => Set(ref _detailSubject, value);
    }

    public string DetailFrom
    {
        get => _detailFrom;
        private set => Set(ref _detailFrom, value);
    }

    public string DetailTo
    {
        get => _detailTo;
        private set => Set(ref _detailTo, value);
    }

    public string DetailDate
    {
        get => _detailDate;
        private set => Set(ref _detailDate, value);
    }

    public string DetailStatus
    {
        get => _detailStatus;
        private set => Set(ref _detailStatus, value);
    }

    /// <summary>正文（纯文本；HTML 邮件的纯文本降级版本，复制正文用这个）。</summary>
    public string MessageBodyText
    {
        get => _messageBodyText;
        private set => Set(ref _messageBodyText, value);
    }

    /// <summary>HTML 富文本预览内容（完整文档；null = 这封邮件只有纯文本）。由 View 交给 WebView2 渲染。</summary>
    public string? MessageHtml
    {
        get => _messageHtml;
        private set
        {
            if (!Set(ref _messageHtml, value)) return;
            OnPropertyChanged(nameof(MessageIsHtml));
            OnPropertyChanged(nameof(ShowHtmlPreview));
            OnPropertyChanged(nameof(ShowPlainBody));
        }
    }

    /// <summary>当前邮件是否有 HTML 正文。</summary>
    public bool MessageIsHtml => !string.IsNullOrWhiteSpace(MessageHtml);

    /// <summary>用户勾选「纯文本」：即使有 HTML 也用纯文本显示（给怕样式花 / 排版乱的情况留退路）。</summary>
    public bool ForcePlainText
    {
        get => _forcePlainText;
        set
        {
            if (!Set(ref _forcePlainText, value)) return;
            OnPropertyChanged(nameof(ShowHtmlPreview));
            OnPropertyChanged(nameof(ShowPlainBody));
        }
    }

    /// <summary>详情正文区显示 HTML 富文本预览（WebView2）还是纯文本。</summary>
    public bool ShowHtmlPreview => MessageIsHtml && !ForcePlainText && !_htmlPreviewUnavailable;

    /// <summary>纯文本正文是否可见（与 <see cref="ShowHtmlPreview"/> 互斥）。</summary>
    public bool ShowPlainBody => !ShowHtmlPreview;

    /// <summary>
    /// WebView2 不可用（没装运行时 / 启动失败）时由 View 调用：强制纯文本，并在详情状态里说明原因。
    /// 只在本进程里生效，不写配置。
    /// </summary>
    public void SetHtmlPreviewUnavailable(string reason)
    {
        if (_htmlPreviewUnavailable) return;
        _htmlPreviewUnavailable = true;
        ForcePlainText = true;
        DetailStatus = reason;
        Log("[邮箱代收] " + reason);
    }

    public string AttachmentsSummary
    {
        get => _attachmentsSummary;
        private set => Set(ref _attachmentsSummary, value);
    }

    /// <summary>当前邮件的附件（没有附件时列表为空，界面显示提示）。</summary>
    public ObservableCollection<MailAttachmentItem> Attachments { get; } = new();

    public bool HasAttachments => Attachments.Count > 0;

    // ---------- 命令 ----------

    public AsyncRelayCommand RefreshAllCommand { get; }
    public AsyncRelayCommand TestConnectionCommand { get; }
    public RelayCommand AddAccountCommand { get; }
    public RelayCommand DeleteAccountCommand { get; }
    public RelayCommand ToggleSortDirectionCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand SaveAttachmentCommand { get; }
    public AsyncRelayCommand OpenAttachmentCommand { get; }
    public AsyncRelayCommand SaveAllAttachmentsCommand { get; }
    public RelayCommand CopyBodyCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand OpenHelpCommand { get; }
    public RelayCommand ShowSettingsTabCommand { get; }
    public RelayCommand RefreshProxyStatusCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    // ---------- 网络代理（全局） ----------

    /// <summary>代理模式选项：自动跟随 Windows 系统代理 / 直连 / 自定义。</summary>
    public IReadOnlyList<OptionItem<string>> ProxyModeOptions { get; } = OptionItem.From(
        ("auto", "自动（跟随 Windows 系统代理）"),
        ("direct", "直连（不使用代理）"),
        ("custom", "自定义代理"));

    /// <summary>代理类型选项（自定义模式用）。</summary>
    public IReadOnlyList<OptionItem<string>> ProxyTypeOptions { get; } = OptionItem.From(
        ("http", "HTTP 代理"),
        ("socks5", "SOCKS5 代理"));

    /// <summary>代理模式下拉框的桥接属性（真正存进配置的是干净值 auto / direct / custom）。</summary>
    public OptionItem<string> SelectedProxyMode
    {
        get => OptionItem<string>.Find(ProxyModeOptions, Cfg.Proxy.Mode) ?? ProxyModeOptions[0];
        set
        {
            if (value is null) return;
            Cfg.Proxy.Mode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCustomProxy));
            OnPropertyChanged(nameof(ProxyStatusText));
        }
    }

    /// <summary>代理类型下拉框的桥接属性。</summary>
    public OptionItem<string> SelectedProxyType
    {
        get => OptionItem<string>.Find(ProxyTypeOptions, Cfg.Proxy.Type) ?? ProxyTypeOptions[0];
        set
        {
            if (value is null) return;
            Cfg.Proxy.Type = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProxyStatusText));
        }
    }

    /// <summary>是否显示自定义代理的服务器 / 端口 / 类型输入框。</summary>
    public bool ShowCustomProxy
        => string.Equals((Cfg.Proxy.Mode ?? "auto").Trim(), "custom", StringComparison.OrdinalIgnoreCase);

    /// <summary>当前代理路线的一句话状态（不联网，只读设置 / 系统代理）。</summary>
    public string ProxyStatusText
    {
        get
        {
            var mode = (Cfg.Proxy.Mode ?? "auto").Trim().ToLowerInvariant();
            if (mode == "direct") return "当前：直连（不使用代理）";
            if (mode == "custom")
            {
                return $"当前：自定义 {Cfg.Proxy.Host}:{Cfg.Proxy.Port}（{MailProxySettings.TypeLabel(Cfg.Proxy.Type)}）";
            }

            var system = MailProxySettings.ReadWindowsSystemProxy();
            if (!system.Enabled) return "当前：Windows 未启用系统代理，将直连";
            var endpoint = MailProxySettings.ParseSystemProxy(system.Server);
            if (endpoint is null) return "当前：系统代理地址无法解析，将直连";
            return $"当前：系统代理 {endpoint.Host}:{endpoint.Port}（{MailProxySettings.TypeLabel(endpoint.Type)}）";
        }
    }

    /// <summary>把代理弹窗保存的结果抄回全局配置（服务对象持有的是同一个 Cfg.Proxy 实例，改字段立即生效）。</summary>
    public void ApplyProxySettings(MailProxySettings source)
    {
        Cfg.Proxy.Mode = source.Mode;
        Cfg.Proxy.Host = source.Host;
        Cfg.Proxy.Port = source.Port;
        Cfg.Proxy.Type = source.Type;
        OnPropertyChanged(nameof(SelectedProxyMode));
        OnPropertyChanged(nameof(SelectedProxyType));
        OnPropertyChanged(nameof(ShowCustomProxy));
        OnPropertyChanged(nameof(ProxyStatusText));
        Log($"代理设置已更新：{Cfg.Proxy.Describe("imap.gmail.com")}");
    }

    // ---------- 右栏页签（邮件详情 / 账号设置） ----------

    /// <summary>右栏是否显示「账号设置」（false = 显示邮件详情）。</summary>
    public bool ShowSettingsTab
    {
        get => _showSettingsTab;
        set
        {
            if (Set(ref _showSettingsTab, value)) OnPropertyChanged(nameof(ShowDetailTab));
        }
    }

    /// <summary>右栏是否显示「邮件详情」（与 <see cref="ShowSettingsTab"/> 互斥）。</summary>
    public bool ShowDetailTab
    {
        get => !_showSettingsTab;
        set
        {
            if (value && _showSettingsTab) ShowSettingsTab = false;
        }
    }

    // ---------- 视图生命周期 ----------

    /// <summary>
    /// 工具页每次显示时调用（视图 Loaded 事件）：按配置决定是否自动刷新一次。
    /// 视图被外壳缓存后重新显示也会走到这里；20 秒内不重复刷（避免同一次显示被 WPF 多次 Loaded 触发）。
    /// </summary>
    public void OnViewLoaded()
    {
        if (!Cfg.RefreshOnOpen) return;
        if (!Accounts.Any(a => a.Enabled))
        {
            if (_viewLoadedHintShown) return;
            _viewLoadedHintShown = true;
            Log("还没有启用的账号：点「新增账号」填好邮箱和授权码，再点「刷新全部」。");
            return;
        }

        if ((DateTime.UtcNow - _lastAutoRefreshUtc).TotalSeconds < 20) return;
        _lastAutoRefreshUtc = DateTime.UtcNow;
        _ = RefreshAllAsync();
    }

    protected override void OnClosed()
    {
        _refreshCts?.Cancel();
        _detailCts?.Cancel();
    }

    // ---------- 账号增删 / 联动 ----------

    private void AddAccount()
    {
        var account = new MailAccountConfig
        {
            DisplayName = "",
            Email = "",
            Host = "",
            Port = 993,
            UseSsl = true,
            Enabled = true,
            StatusText = "未连接",
        };
        Cfg.Accounts.Add(account);
        SelectAccount(account, MailFolderKind.Inbox);
        ShowSettingsTab = true;
        Log("新增账号：先选服务商预设（会自动填服务器 / 端口），再填邮箱地址和授权码 / 应用专用密码。");
    }

    private void DeleteAccount()
    {
        var account = SelectedAccount;
        if (account is null) return;

        var confirm = MessageBox.Show(
            $"确定删除账号「{account.DisplayTitle}」？\n只会移除本机配置，不会动邮箱里的邮件。",
            "删除账号", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        Cfg.Accounts.Remove(account);
        _allMessages.RemoveAll(row => ReferenceEquals(row.Account, account));
        _folderNotices.Remove(account.Id);
        SelectedMessage = null;
        ShowDetailTab = true;
        ApplyFilter();
        Log($"已删除账号：{account.DisplayTitle}");
    }

    private void OnAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MailAccountConfig account in e.OldItems)
            {
                account.PropertyChanged -= OnAccountPropertyChanged;
                _folderNotices.Remove(account.Id);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MailAccountConfig account in e.NewItems) account.PropertyChanged += OnAccountPropertyChanged;
        }

        var selectedAccountId = SelectedNavigationItem?.Account.Id ?? Cfg.LastSelectedAccountId;
        var selectedFolder = SelectedNavigationItem?.FolderKey ?? Cfg.LastSelectedFolder;
        RebuildNavigationNodes();
        SelectedNavigationItem =
            FindNavigationNode(selectedAccountId, selectedFolder)
            ?? NavigationItems.FirstOrDefault(node => node.Folder == MailFolderKind.Inbox)
            ?? NavigationItems.FirstOrDefault();
        UpdateSummaries();
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MailAccountConfig.DisplayName) or nameof(MailAccountConfig.Email))
        {
            OnPropertyChanged(nameof(CurrentFolderTitle));
            OnPropertyChanged(nameof(EmptyMessagesHint));
        }

        if (e.PropertyName is nameof(MailAccountConfig.Host)
            or nameof(MailAccountConfig.Port)
            or nameof(MailAccountConfig.UseSsl))
        {
            if (ReferenceEquals(sender, SelectedAccount))
            {
                OnPropertyChanged(nameof(SelectedPreset));
                OnPropertyChanged(nameof(PresetHintText));
                OnPropertyChanged(nameof(HasPresetHelp));
                OnPropertyChanged(nameof(HelpButtonText));
            }
        }

        UpdateSummaries();
    }

    /// <summary>从账号列表重建左侧导航；账号行后面紧跟三个系统文件夹。</summary>
    private void RebuildNavigationNodes()
    {
        NavigationItems.Clear();
        foreach (var account in Accounts)
        {
            NavigationItems.Add(new MailNavigationNode(account, null));
            foreach (var folder in MailFolders.All)
                NavigationItems.Add(new MailNavigationNode(account, folder));
        }

        UpdateNavigationCounts();
    }

    /// <summary>代码后置在新增账号后调用：选中新账号的指定系统文件夹。</summary>
    public void SelectAccount(MailAccountConfig account, MailFolderKind? folder = MailFolderKind.Inbox)
    {
        var node = FindNavigationNode(account.Id, folder is { } kind ? MailFolders.Key(kind) : "all");
        if (node is not null) SelectedNavigationItem = node;
    }

    private MailNavigationNode? FindNavigationNode(string? accountId, string? folderKey)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return null;
        var normalizedFolder = string.IsNullOrWhiteSpace(folderKey) ? "inbox" : folderKey.Trim().ToLowerInvariant();
        return NavigationItems.FirstOrDefault(node =>
            string.Equals(node.Account.Id, accountId, StringComparison.Ordinal)
            && string.Equals(node.FolderKey, normalizedFolder, StringComparison.Ordinal));
    }

    // ---------- 代收 ----------

    private async Task RefreshAllAsync()
    {
        if (IsBusy) return;

        var accounts = Accounts.Where(a => a.Enabled).ToList();
        if (accounts.Count == 0)
        {
            StatusText = "还没有启用的账号";
            Log("没有启用的账号：先新增账号并勾选「参与代收」。");
            return;
        }

        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;
        var limit = Cfg.EffectiveFetchCount;

        IsBusy = true;
        Progress = 0;
        ProgressText = $"0 / {accounts.Count}";
        Log($"===== 开始代收：{accounts.Count} 个账号，每个文件夹取最近 {limit} 封 =====");

        var collected = new List<MailBrief>();
        var succeeded = 0;
        var failed = 0;

        try
        {
            for (var index = 0; index < accounts.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var account = accounts[index];

                if (!account.IsConfigured)
                {
                    // 没填授权码 / 邮箱 / 服务器：不发网络请求，状态栏给一句短的，日志里给具体缺什么
                    failed++;
                    account.StatusText = "❌ 未配置：缺授权码 / 服务器";
                    SetAllFolderNotices(account, "未配置");
                    Log($"[{account.DisplayTitle}] 跳过：{DescribeMissing(account)}（在账号弹窗里填好再刷新）");
                }
                else
                {
                    account.StatusText = "连接中…";
                    Log($"[{account.DisplayTitle}] 连接方式：{Cfg.Proxy.Describe(account.Host)}");
                    try
                    {
                        var fetched = await _service.FetchRecentAsync(account, limit, token);
                        var accountMessages = fetched.Folders.SelectMany(folder => folder.Messages).ToList();
                        collected.AddRange(accountMessages);
                        _folderNotices[account.Id] = fetched.Folders.ToDictionary(
                            folder => folder.Folder,
                            folder => folder.Available ? "" : folder.ErrorText);

                        var availableFolders = fetched.Folders.Count(folder => folder.Available);
                        if (availableFolders > 0) succeeded++;
                        else failed++;

                        account.StatusText = FormatAccountFetchStatus(fetched, accountMessages.Count);
                        Log($"[{account.DisplayTitle}] {account.StatusText}（{MailText.MaskEmail(account.Email)}）");
                        foreach (var folder in fetched.Folders)
                        {
                            var folderLabel = MailFolders.Label(folder.Folder);
                            Log(folder.Available
                                ? $"[{account.DisplayTitle}] {folderLabel}：{folder.Messages.Count} 封"
                                : $"[{account.DisplayTitle}] {folderLabel}：{folder.ErrorText}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        var message = ImapMailService.DescribeError(ex);
                        account.StatusText = "❌ " + message;
                        SetAllFolderNotices(account, "读取失败");
                        Log($"[{account.DisplayTitle}] 失败：{message}");
                    }
                }

                Progress = (double)(index + 1) / accounts.Count;
                ProgressText = $"{index + 1} / {accounts.Count}";
            }

            token.ThrowIfCancellationRequested();

            var rows = collected
                .Select(brief => new MailMessageRow(brief))
                .OrderByDescending(row => row.Date)
                .ToList();

            _allMessages.Clear();
            _allMessages.AddRange(rows);
            // ApplyFilter 内部会按「账号 + UID」恢复刷新前选中的那封（还在的话）
            ApplyFilter();

            // 开发截图冒烟用：YLT_MAIL_OPEN_FIRST=1 时自动选中第一封（看 HTML 富文本预览）；
            // 也可以用 YLT_MAIL_OPEN_SUBJECT=关键词 选中第一封主题命中关键词的邮件。
            if (SelectedMessage is null && VisibleMessages.Count > 0
                && Environment.GetEnvironmentVariable("YLT_MAIL_OPEN_FIRST") == "1")
            {
                SelectedMessage = VisibleMessages[0];
            }
            else if (SelectedMessage is null && VisibleMessages.Count > 0
                     && Environment.GetEnvironmentVariable("YLT_MAIL_OPEN_SUBJECT") is { Length: > 0 } subjectFilter)
            {
                SelectedMessage = VisibleMessages.FirstOrDefault(row =>
                                       row.Subject.Contains(subjectFilter, StringComparison.OrdinalIgnoreCase))
                                   ?? VisibleMessages[0];
            }

            var unread = rows.Count(row => row.IsUnread);
            StatusText = $"代收完成：{rows.Count} 封（未读 {unread}）· 成功 {succeeded} / 失败 {failed}";
            Log($"===== 代收完成：共 {rows.Count} 封，成功 {succeeded} 个账号，失败 {failed} 个 =====");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消代收";
            Log("===== 已取消代收（列表保持上次的结果）=====");
        }
        catch (Exception ex)
        {
            StatusText = "代收失败：" + ImapMailService.DescribeError(ex);
            Log($"[代收] 未预期的错误：{ex}");
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            _refreshCts = null;
            UpdateSummaries();
        }
    }

    private static string FormatAccountFetchStatus(MailAccountFetchResult fetched, int totalCount)
    {
        var folderParts = fetched.Folders.Select(folder =>
            $"{MailFolders.Label(folder.Folder)} {folder.Messages.Count}");
        return $"✅ {totalCount} 封 · {string.Join(" / ", folderParts)} · {DateTime.Now:HH:mm:ss}";
    }

    private void SetAllFolderNotices(MailAccountConfig account, string notice)
    {
        _folderNotices[account.Id] = MailFolders.All.ToDictionary(folder => folder, _ => notice);
        UpdateNavigationCounts();
    }

    private async Task TestConnectionAsync()
    {
        var account = SelectedAccount;
        if (account is null)
        {
            StatusText = "先选中一个账号";
            return;
        }

        if (!account.IsConfigured)
        {
            var problem = DescribeMissing(account);
            StatusText = "连接测试前先填完整：" + problem;
            Log($"[{account.DisplayTitle}] 配置不完整：{problem}（在账号弹窗里填写）");
            return;
        }

        IsBusy = true;
        account.StatusText = "连接中…";
        try
        {
            var result = await _service.TestConnectionAsync(account);
            account.StatusText = result;
            StatusText = result;
            Log($"[{account.DisplayTitle}] {result}");
        }
        catch (OperationCanceledException)
        {
            account.StatusText = "已取消";
        }
        catch (Exception ex)
        {
            var message = ImapMailService.DescribeError(ex);
            account.StatusText = "❌ " + message;
            StatusText = "连接失败：" + message;
            Log($"[{account.DisplayTitle}] 连接失败：{message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>账号还缺哪个必填项（给状态栏 / 日志用的短句）。</summary>
    private static string DescribeMissing(MailAccountConfig account)
    {
        if (string.IsNullOrWhiteSpace(account.Email)) return "邮箱地址为空";
        if (string.IsNullOrWhiteSpace(account.Host)) return "IMAP 服务器为空";
        return "授权码 / 应用专用密码为空（QQ 用授权码、Gmail 用应用专用密码）";
    }

    // ---------- 筛选 ----------

    private void ApplyFilter()
    {
        var selectedAccountId = SelectedMessage?.Account.Id;
        var selectedUniqueId = SelectedMessage?.UniqueId ?? 0;
        var selectedFolder = SelectedMessage?.Folder;

        VisibleMessages.Clear();
        IEnumerable<MailMessageRow> query = _allMessages;

        query = query.Where(IsInCurrentNavigationScope);

        if (OnlyUnread) query = query.Where(row => row.IsUnread);

        var terms = (SearchText ?? "")
            .Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var term in terms)
        {
            query = query.Where(row =>
                row.Subject.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.FromText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.ToText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Account.DisplayTitle.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Account.Email.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var row in MailSort.Apply(query, Cfg.SortBy, Cfg.SortDescending)) VisibleMessages.Add(row);

        // 原来选中的那封如果还在筛选结果里，按账号 + 文件夹 + UID 重新选中（刷新后对象是新实例）
        if (selectedAccountId is not null)
        {
            SelectedMessage = VisibleMessages.FirstOrDefault(row =>
                string.Equals(row.Account.Id, selectedAccountId, StringComparison.Ordinal)
                && row.Folder == selectedFolder
                && row.UniqueId == selectedUniqueId);
        }

        UpdateNavigationCounts();
        UpdateSummaries();
    }

    private bool IsInCurrentNavigationScope(MailMessageRow row)
    {
        var navigation = SelectedNavigationItem;
        if (navigation is null) return false;
        if (!string.Equals(row.Account.Id, navigation.Account.Id, StringComparison.Ordinal)) return false;
        return navigation.Folder is not { } folder || row.Folder == folder;
    }

    private void UpdateNavigationCounts()
    {
        foreach (var node in NavigationItems)
        {
            node.MessageCount = _allMessages.Count(row =>
                string.Equals(row.Account.Id, node.Account.Id, StringComparison.Ordinal)
                && (node.Folder is not { } folder || row.Folder == folder));

            node.StatusText = node.Folder is { } folderKind
                              && _folderNotices.TryGetValue(node.Account.Id, out var notices)
                              && notices.TryGetValue(folderKind, out var notice)
                ? notice
                : "";
        }
    }

    private void UpdateSummaries()
    {
        AccountsSummary = Accounts.Count == 0
            ? "还没有账号"
            : $"{Accounts.Count} 个账号 · {Accounts.Count(a => a.Enabled)} 个启用";

        var unread = VisibleMessages.Count(row => row.IsUnread);
        var scopeCount = _allMessages.Count(IsInCurrentNavigationScope);
        MessagesSummary = scopeCount == 0
            ? "当前邮件夹还没有代收记录"
            : VisibleMessages.Count == scopeCount
                ? $"{scopeCount} 封 · 未读 {unread}"
                : $"筛选出 {VisibleMessages.Count} 封（当前范围 {scopeCount} 封）";

        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(HasVisibleMessages));
    }

    // ---------- 邮件详情 ----------

    private async Task LoadDetailAsync(MailMessageRow? row)
    {
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        var token = _detailCts.Token;
        var generation = ++_detailGeneration;

        if (row is null)
        {
            ClearDetail();
            return;
        }

        DetailSubject = row.Subject;
        DetailFrom = row.FromText;
        DetailTo = "读取中…";
        DetailDate = row.DateFullText;
        DetailStatus = $"正在从「{row.Account.DisplayTitle} / {MailFolders.Label(row.Folder)}」读取正文…";
        MessageBodyText = "";
        MessageHtml = null;
        Attachments.Clear();
        AttachmentsSummary = "";
        OnPropertyChanged(nameof(HasAttachments));

        try
        {
            var detail = await _service.GetMessageAsync(row.Account, row.Folder, row.UniqueId, token);
            if (generation != _detailGeneration || token.IsCancellationRequested) return;

            DetailSubject = detail.Subject;
            DetailFrom = detail.FromText;
            DetailTo = detail.ToText;
            DetailDate = detail.DateText;
            MessageBodyText = detail.BodyText;
            MessageHtml = detail.HtmlDocument;
            DetailStatus = detail.HtmlDocument is not null
                ? _htmlPreviewUnavailable
                    ? "已读取（WebView2 不可用，纯文本显示）"
                    : ForcePlainText
                        ? "已读取（纯文本模式）"
                        : "已读取 · HTML 富文本预览"
                : detail.ConvertedFromHtml
                    ? "已读取（HTML 邮件已转成纯文本显示）"
                    : $"已读取 · {row.Account.DisplayTitle} / {MailFolders.Label(row.Folder)}";

            Attachments.Clear();
            foreach (var attachment in detail.Attachments)
                Attachments.Add(new MailAttachmentItem(attachment, row.Account));
            AttachmentsSummary = Attachments.Count == 0 ? "没有附件" : $"附件 {Attachments.Count} 个";
            OnPropertyChanged(nameof(HasAttachments));
        }
        catch (OperationCanceledException)
        {
            // 用户切到别的邮件了，忽略
        }
        catch (Exception ex)
        {
            if (generation != _detailGeneration) return;
            var message = ImapMailService.DescribeError(ex);
            DetailStatus = "读取正文失败：" + message;
            MessageBodyText = "";
            MessageHtml = null;
            Log($"[{row.Account.DisplayTitle}] 读取邮件「{row.Subject}」失败：{message}");
        }
    }

    private void ClearDetail()
    {
        _detailGeneration++;
        DetailSubject = "（未选择邮件）";
        DetailFrom = "";
        DetailTo = "";
        DetailDate = "";
        DetailStatus = "选中左侧列表里的一封邮件即可查看正文。";
        MessageBodyText = "";
        MessageHtml = null;
        Attachments.Clear();
        AttachmentsSummary = "";
        OnPropertyChanged(nameof(HasAttachments));
    }

    private async Task SaveAttachmentAsync(MailAttachmentItem? item)
    {
        if (item is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "保存附件",
            FileName = item.FileName,
            Filter = "所有文件|*.*",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var path = dialog.FileName;
            await Task.Run(() => ImapMailService.SaveAttachment(item.Attachment, path));
            StatusText = "附件已保存：" + path;
            Log($"附件已保存：{path}");
        }
        catch (Exception ex)
        {
            StatusText = "保存附件失败：" + ex.Message;
            Log("保存附件失败：" + ex);
        }
    }

    /// <summary>
    /// 为 WPF 拖放准备一个真实文件。资源管理器只接受 FileDrop 路径，不能直接拖 MIME 对象；
    /// 文件放 <c>Cache\mail-collector\drag</c>，由缓存清理统一回收，并在每次拖拽时顺手清理一天前的残留。
    /// </summary>
    public async Task<string?> PrepareAttachmentDragFileAsync(MailAttachmentItem? item)
    {
        if (item is null) return null;

        try
        {
            var cache = ToolCache.For("mail-collector");
            cache.EnsureCreated();
            var root = cache.PathOf("drag");
            Directory.CreateDirectory(root);
            CleanupOldDragFiles(root);

            var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, item.FileName);
            await Task.Run(() => ImapMailService.SaveAttachment(item.Attachment, path));
            StatusText = "附件已准备好，可拖到桌面或资源管理器：" + item.FileName;
            return path;
        }
        catch (Exception ex)
        {
            StatusText = "准备附件拖拽失败：" + ex.Message;
            Log("准备附件拖拽失败：" + ex);
            return null;
        }
    }

    private static void CleanupOldDragFiles(string root)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // 另一个拖拽可能正在使用；留到下次缓存清理。
                }
            }
        }
        catch
        {
            // 清理是尽力而为，不影响本次拖拽。
        }
    }

    /// <summary>「打开」附件：先保存到临时目录，再用系统默认程序打开（PDF / 图片 / Office 都行）。</summary>
    private async Task OpenAttachmentAsync(MailAttachmentItem? item)
    {
        if (item is null) return;
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "YuLiangToolkit", "mail-attachments", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, item.FileName);
            await Task.Run(() => ImapMailService.SaveAttachment(item.Attachment, path));
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            StatusText = "已用默认程序打开附件：" + item.FileName;
            Log($"打开附件：{path}");
        }
        catch (Exception ex)
        {
            StatusText = "打开附件失败：" + ex.Message;
            Log("打开附件失败：" + ex);
        }
    }

    /// <summary>「全部保存」：选一个文件夹，把当前邮件的所有附件都存进去（重名自动加序号）。</summary>
    private async Task SaveAllAttachmentsAsync()
    {
        if (Attachments.Count == 0)
        {
            StatusText = "当前邮件没有附件";
            return;
        }

        var dialog = new OpenFolderDialog { Title = "选择附件保存文件夹", Multiselect = false };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        var saved = 0;
        try
        {
            foreach (var item in Attachments.ToList())
            {
                var path = MakeUniquePath(dialog.FolderName, item.FileName);
                await Task.Run(() => ImapMailService.SaveAttachment(item.Attachment, path));
                saved++;
            }

            StatusText = $"已保存 {saved} 个附件到：{dialog.FolderName}";
            Log($"已保存 {saved} 个附件到：{dialog.FolderName}");
        }
        catch (Exception ex)
        {
            StatusText = $"保存附件失败（已保存 {saved} 个）：{ex.Message}";
            Log("批量保存附件失败：" + ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string MakeUniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private void CopyBody()
    {
        if (string.IsNullOrWhiteSpace(MessageBodyText))
        {
            StatusText = "当前还没有可复制的正文";
            return;
        }

        try
        {
            Clipboard.SetText(MessageBodyText);
            StatusText = "正文已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ex.Message;
        }
    }

    private void OpenHelp()
    {
        var url = SelectedPreset.HelpUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打开帮助页面失败：" + ex.Message;
        }
    }
}
