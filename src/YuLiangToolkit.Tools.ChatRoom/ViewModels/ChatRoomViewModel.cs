using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ChatRoom.Media;
using YuLiangToolkit.Tools.ChatRoom.Net;
using YuLiangToolkit.Tools.ChatRoom.Protocol;
using YuLiangToolkit.Tools.ChatRoom.Sessions;
using YuLiangToolkit.Tools.ChatRoom.Traffic;
using ChatSessionStore = YuLiangToolkit.Tools.ChatRoom.Sessions.ChatSessionStore;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>
/// 「联机聊天室」的 ViewModel：把「开房间 / 连主机」「聊天」「发文件」「流量计量」「历史记录」
/// 串成一个界面。
///
/// <para>分工：<see cref="ChatRoomSession"/>（网络 + 落盘）→ 本类（界面状态与命令）→ <c>ChatRoomView</c>（界面）。
/// 聊天室最怕“界面卡住”，所以网络层全在后台线程跑，所有界面更新都经过基类的 <c>Ui(...)</c>。</para>
/// </summary>
public sealed partial class ChatRoomViewModel : ToolViewModelBase
{
    /// <summary>会话根目录下的协议说明子目录。</summary>
    public const string ProtocolSubFolder = "Protocol";

    private readonly ChatSessionStore _store;
    private readonly DispatcherTimer _statusTimer;
    private ChatRoomSession? _session;
    private TrafficMeter? _meter;
    private TrafficSnapshot _snapshot;
    private bool _warned;

    private string _messageText = "";
    private string _noticeText = "还没开始连接：主机点「开房间」，客户端填好地址点「连接」";
    private bool _isOnline;
    private bool _hasUnread;
    private string _peerSummary = "未连接";
    private string _listenSummary = "未监听";
    private string _historySummary = "";
    private string _transferNote = "";
    private string _uploadSummary = "还没有发送过文件";
    private string _downloadSummary = "还没有收到过文件";
    private string _activityText = "";
    private bool _busy;

    public ChatRoomViewModel() : base(ChatRoomConfig.ToolId)
    {
        Cfg = LoadConfig<ChatRoomConfig>();
        _store = new ChatSessionStore(ChatRoomConfig.ToolId);
        if (string.IsNullOrWhiteSpace(Cfg.Nickname)) Cfg.Nickname = $"用户{Environment.MachineName}";
        if (string.IsNullOrWhiteSpace(Cfg.Emoji)) Cfg.Emoji = "🐟";

        // 首次运行：把协议说明释放到 Data\chat-room\Protocol\（用户改过就不覆盖）
        ProtocolDocPath = ChatProtocol.EnsureProtocolDoc(_store.RootDirectory is { Length: > 0 } root
            ? Path.Combine(root, ProtocolSubFolder)
            : Path.Combine(Storage.PathOf(ProtocolSubFolder)));

        // 本机地址：局域网地址用于“告诉对方填什么”，全部地址列在监听输入框下面当提示
        var addresses = ChatRoomSession.LocalAddresses();
        LocalAddresses = new ObservableCollection<string>(addresses);
        LanAddress = ChatRoomSession.DetectLanAddress();
        LocalAddressHint = addresses.Count == 0
            ? "本机地址：没检测到可用网卡（没连网？）—— 可以填 0.0.0.0 让所有网卡都收"
            : $"本机地址：{string.Join("　", addresses)}";

        // ------- 命令 -------
        StartHostCommand = new AsyncRelayCommand(_ => StartHostAsync(), _ => !IsOnline && !_busy);
        ConnectCommand = new AsyncRelayCommand(_ => ConnectAsync(), _ => !IsOnline && !_busy);
        StopCommand = new AsyncRelayCommand(_ => StopAsync(), _ => IsOnline);
        SendCommand = new AsyncRelayCommand(_ => SendTextAsync(), _ => IsOnline && MessageText.Trim().Length > 0);
        AttachCommand = new AsyncRelayCommand(_ => PickAndSendFilesAsync(), _ => IsOnline && !_busy);
        SendImageCommand = new AsyncRelayCommand(_ => PickAndSendImagesAsync(), _ => IsOnline && !_busy);
        PingCommand = new AsyncRelayCommand(_ => PingAsync(), _ => IsOnline);
        CopyAddressCommand = new RelayCommand(_ => CopyAddress());
        ClearChatCommand = new RelayCommand(_ => ClearChat());
        OpenSessionFolderCommand = new RelayCommand(_ => OpenSessionFolder());
        OpenRootFolderCommand = new RelayCommand(_ => _store.OpenInExplorer());
        OpenProtocolDocCommand = new RelayCommand(_ => OpenProtocolDoc());
        OpenFileCommand = new RelayCommand(p => OpenFile(p as ChatMessageItem));
        RevealFileCommand = new RelayCommand(p => RevealFile(p as ChatMessageItem));
        KickCommand = new AsyncRelayCommand(p => KickAsync(p as ChatMemberItem), p => p is ChatMemberItem { CanKick: true });
        KickAllCommand = new AsyncRelayCommand(_ => KickAllAsync(), _ => IsHost && (_session?.ClientCount ?? 0) > 0);
        RefreshMembersCommand = new RelayCommand(_ => RebuildMembers());
        InsertEmojiCommand = new RelayCommand(p => InsertEmoji(p as string));
        ClearTransfersCommand = new RelayCommand(_ => { Transfers.Clear(); UpdateTransferSummary(); });
        OpenTransferCommand = new RelayCommand(p => OpenTransfer(p as FileProgress));
        RefreshHistoryCommand = new RelayCommand(_ => RefreshHistory());
        OpenHistoryCommand = new RelayCommand(p => OpenHistory(p as ChatSessionStore.ChatSessionInfo));
        OpenHistoryFolderCommand = new RelayCommand(p => OpenHistoryFolder(p as ChatSessionStore.ChatSessionInfo));
        OpenTopCommand = new RelayCommand(p => OpenUrl(p as string));
        SetListenLocalCommand = new RelayCommand(_ => ListenAddress = "127.0.0.1");
        SetListenAllCommand = new RelayCommand(_ => ListenAddress = "0.0.0.0");
        SetListenAllV6Command = new RelayCommand(_ => ListenAddress = "::");

        // ------- 界面状态定时刷新（流量 / 人数 / 速度） -------
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        Cfg.PropertyChanged += (_, _) => Ui(OnConfigChanged);

        RefreshHistory();
        UpdateMembersPlaceholder();
        Log($"会话记录根目录：{_store.RootDirectory}");
        Log($"协议说明：{ProtocolDocPath}");
        if (LanAddress.Length > 0) Log($"本机局域网地址：{LanAddress}（告诉对方填这个）");

        // 自动开局：主机就直接监听；客户端只把上次填的地址带出来
        if (Cfg.AutoStart && Environment.GetEnvironmentVariable("YLT_NO_SERVER") is null)
        {
            if (Cfg.LastRole == ChatRole.Host) _ = StartHostAsync();
            else Notice($"上次是客户端。填好主机地址后点「连接」；主机地址也可以用内网穿透工具给的域名:端口。");
        }

        SeedDemoMessagesIfRequested();
    }

    /// <summary>
    /// 只有设了 <c>YLT_CHAT_DEMO=1</c> 时才往界面上塞几条示例消息。
    /// <b>用途</b>：无头截图验证「文字气泡靠左/靠右、图片、文件卡片」长什么样（聊天记录得有人说话才看得见）。
    /// 正常启动完全不会走到这里。
    /// </summary>
    private void SeedDemoMessagesIfRequested()
    {
        if (Environment.GetEnvironmentVariable("YLT_CHAT_DEMO") is null) return;

        AddSystemMessage("房间已开（监听 0.0.0.0:5200）。把地址告诉对方：192.168.1.7:5200");
        AppendMessage(ChatMessageItem.Create("demo-1", "c1-abc", "小张", "🐻", false, false,
            DateTime.Now.AddMinutes(-6), "在吗？我把上次那个视频发你，你看看能不能播。", ChatContentKind.File, null));
        AppendMessage(ChatMessageItem.Create("demo-2", _session?.LocalMemberId ?? "self", Cfg.EffectiveNickname, Cfg.Emoji, true, true,
            DateTime.Now.AddMinutes(-5), "在，发吧。这个房间是我开的，你连的是我的端口。", ChatContentKind.File, null));

        var file = new FileProgress("demo-f1", "演示视频.mp4", 18 * 1024 * 1024, "视频", false)
        { State = TransferState.Receiving, TransferredBytes = 7 * 1024 * 1024, Speed = 1_600_000 };
        AppendMessage(ChatMessageItem.Create("demo-4", "c1-abc", "小张", "🐻", false, false,
            DateTime.Now.AddMinutes(-2), "", ChatContentKind.Video, file));

        var img = new FileProgress("demo-f2", "截图.png", 2 * 1024 * 1024, "图片", true) { State = TransferState.Done, TransferredBytes = 2 * 1024 * 1024 };
        AppendMessage(ChatMessageItem.Create("demo-5", _session?.LocalMemberId ?? "self", Cfg.EffectiveNickname, Cfg.Emoji, true, true,
            DateTime.Now.AddMinutes(-1), "我这张图发过去了（原图 4 MB，压完 2 MB）。", ChatContentKind.Image, img));

        AddSystemMessage("🐻 小张 加入了房间（192.168.1.9:53422）");

        var zip = new FileProgress("demo-f3", "素材打包.zip", 640 * 1024 * 1024, "压缩包", true)
        { State = TransferState.Blocked, Message = "超出单次流量上限：这个文件 640.0 MB，剩余额度只有 38.4 MB" };
        AppendMessage(ChatMessageItem.Create("demo-6", _session?.LocalMemberId ?? "self", Cfg.EffectiveNickname, Cfg.Emoji, true, true,
            DateTime.Now, "", ChatContentKind.Archive, zip));
        AppendMessage(ChatMessageItem.Create("demo-7", _session?.LocalMemberId ?? "self", Cfg.EffectiveNickname, Cfg.Emoji, true, true,
            DateTime.Now, "流量上限我设成 100 MB 了，别一口气传完。", ChatContentKind.File, null));

        Transfers.Insert(0, file);
        Transfers.Insert(0, img);
        Transfers.Insert(0, zip);
        UpdateTransferSummary();
        Notice("（演示数据：YLT_CHAT_DEMO 打开了）它只是为了截图看样式，不影响真实连接。");
    }

    // ---------------------------------------------------------------- 配置与路径

    /// <summary>功能选项（改一下就写进 <c>Config\chat-room\config.json</c>）。</summary>
    public ChatRoomConfig Cfg { get; }

    /// <summary>协议说明文件路径（首次运行释放）。</summary>
    public string ProtocolDocPath { get; }

    /// <summary>会话记录根目录。</summary>
    public string SessionRootText => _store.RootDirectory;

    /// <summary>本机局域网 IPv4（空 = 没找到）。</summary>
    public string LanAddress { get; }

    /// <summary>
    /// 本机全部可用地址（含 IPv6）。
    /// <b>只用于展示</b>：监听地址是普通输入框（不是下拉框），这些地址作为提示文字列在它下面。
    /// </summary>
    public ObservableCollection<string> LocalAddresses { get; }

    /// <summary>监听输入框下面那行「本机地址：…」提示。</summary>
    public string LocalAddressHint { get; }

    /// <summary>昵称绑定（写进配置）。</summary>
    public string Nickname
    {
        get => Cfg.Nickname;
        set
        {
            if (Cfg.Nickname == value) return;
            Cfg.Nickname = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelfDisplay));
        }
    }

    /// <summary>头像 emoji。</summary>
    public string Emoji
    {
        get => Cfg.Emoji;
        set
        {
            if (Cfg.Emoji == value) return;
            Cfg.Emoji = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelfDisplay));
        }
    }

    /// <summary>自己的显示名（界面右上角）。</summary>
    public string SelfDisplay => $"{Cfg.Emoji} {Cfg.EffectiveNickname}";

    /// <summary>监听地址（主机）。</summary>
    public string ListenAddress
    {
        get => Cfg.ListenAddress;
        set
        {
            if (Cfg.ListenAddress == value) return;
            Cfg.ListenAddress = value;
            OnPropertyChanged();
        }
    }

    /// <summary>监听端口（主机）。</summary>
    public int ListenPort
    {
        get => Cfg.ListenPort;
        set
        {
            if (Cfg.ListenPort == value) return;
            Cfg.ListenPort = value;
            OnPropertyChanged();
        }
    }

    /// <summary>主机地址（客户端）。</summary>
    public string HostAddress
    {
        get => Cfg.HostAddress;
        set
        {
            if (Cfg.HostAddress == value) return;
            Cfg.HostAddress = value;
            OnPropertyChanged();
        }
    }

    /// <summary>主机端口（客户端）。</summary>
    public int HostPort
    {
        get => Cfg.HostPort;
        set
        {
            if (Cfg.HostPort == value) return;
            Cfg.HostPort = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是不是主机身份（决定界面上哪一栏亮着）。</summary>
    public bool IsHost => _session?.IsHost ?? Cfg.LastRole == ChatRole.Host;

    /// <summary>正在联机。</summary>
    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (!Set(ref _isOnline, value)) return;
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(StatusBadgeText));
            OnPropertyChanged(nameof(StatusBadgeBrushKey));
        }
    }

    public bool IsOffline => !IsOnline;

    /// <summary>状态徽标文案。</summary>
    public string StatusBadgeText => IsOnline ? (IsHost ? "● 房间开着" : "● 已连接") : "○ 未连接";

    /// <summary>状态徽标用的画刷资源名（界面里用 DataTrigger 更直观，这里留个文本给复制用）。</summary>
    public string StatusBadgeBrushKey => IsOnline ? "SuccessBrush" : "TextSecondaryBrush";

    /// <summary>忙碌（正在连接 / 正在处理文件）。</summary>
    public bool IsBusy2
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_busy;

    /// <summary>界面上「连接信息」那块的一行摘要。</summary>
    public string PeerSummary
    {
        get => _peerSummary;
        private set => Set(ref _peerSummary, value);
    }

    /// <summary>监听摘要。</summary>
    public string ListenSummary
    {
        get => _listenSummary;
        private set => Set(ref _listenSummary, value);
    }

    /// <summary>历史记录摘要。</summary>
    public string HistorySummary
    {
        get => _historySummary;
        private set => Set(ref _historySummary, value);
    }

    /// <summary>最近发生的事（右上角一行活动提示）。</summary>
    public string ActivityText
    {
        get => _activityText;
        private set => Set(ref _activityText, value);
    }

    /// <summary>收到未读消息（标题上标星号用）。</summary>
    public bool HasUnread
    {
        get => _hasUnread;
        private set => Set(ref _hasUnread, value);
    }

    // ---------------------------------------------------------------- 聊天

    /// <summary>聊天消息（界面绑这个）。</summary>
    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    /// <summary>成员列表。</summary>
    public ObservableCollection<ChatMemberItem> MemberItems { get; } = new();

    /// <summary>文件传输（本次连接里的，收发都在这里）。</summary>
    public ObservableCollection<FileProgress> Transfers { get; } = new();

    /// <summary>历史会话（每次开始连接自动新建一个文件夹）。</summary>
    public ObservableCollection<ChatSessionStore.ChatSessionInfo> History { get; } = new();

    /// <summary>输入框内容。</summary>
    public string MessageText
    {
        get => _messageText;
        set
        {
            if (!Set(ref _messageText, value)) return;
            OnPropertyChanged(nameof(CanSend));
        }
    }

    public bool CanSend => MessageText.Trim().Length > 0 && IsOnline;

    /// <summary>发送文件时的附带留言。</summary>
    public string TransferNote
    {
        get => _transferNote;
        set => Set(ref _transferNote, value);
    }

    /// <summary>状态栏提示。</summary>
    public string NoticeText
    {
        get => _noticeText;
        private set => Set(ref _noticeText, value);
    }

    /// <summary>已发送文件摘要。</summary>
    public string UploadSummary
    {
        get => _uploadSummary;
        private set => Set(ref _uploadSummary, value);
    }

    /// <summary>已接收文件摘要。</summary>
    public string DownloadSummary
    {
        get => _downloadSummary;
        private set => Set(ref _downloadSummary, value);
    }

    /// <summary>常用 emoji（点一下就插进输入框）。</summary>
    public IReadOnlyList<string> QuickEmojis { get; } = new[]
    {
        "😀", "😂", "🤣", "😊", "😍", "🤔", "😭", "😡", "👍", "👌", "🙏", "🎉", "❤️", "🔥", "🐟", "⭐",
    };

    /// <summary>主机端允许的客户端数量上限（防呆：有人拿这个当小服务器用）。</summary>
    public int MaxClients { get; set; } = 16;

    // ---------------------------------------------------------------- 流量

    /// <summary>流量快照（界面绑它）。</summary>
    public TrafficSnapshot Traffic
    {
        get => _snapshot;
        private set
        {
            _snapshot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrafficSummaryText));
            OnPropertyChanged(nameof(TrafficLimitText));
            OnPropertyChanged(nameof(TrafficRateText));
            OnPropertyChanged(nameof(TrafficRatio));
            OnPropertyChanged(nameof(TrafficRatioText));
            OnPropertyChanged(nameof(IsTrafficExhausted));
        }
    }

    /// <summary>「↑ 已发 … ↓ 已收 …」。</summary>
    public string TrafficSummaryText => Traffic.Summary;

    /// <summary>上限那一行。</summary>
    public string TrafficLimitText => Traffic.Limited
        ? Traffic.LimitText
        : $"未限制流量（当前设置：{(Cfg.TrafficLimitBytes <= 0 ? "不限" : Cfg.TrafficLimitText)}）";

    /// <summary>速度一行。</summary>
    public string TrafficRateText => Traffic.Limited || Traffic.SentBytes + Traffic.ReceivedBytes > 0
        ? Traffic.RateText
        : "↑ 0 B/s　↓ 0 B/s";

    /// <summary>已用比例（进度条）。</summary>
    public double TrafficRatio => Traffic.UsedRatio;

    /// <summary>比例的百分比文本。</summary>
    public string TrafficRatioText => Traffic.Limited ? $"{Traffic.UsedRatio * 100:0.#}%" : "";

    /// <summary>流量到顶了。</summary>
    public bool IsTrafficExhausted => Traffic.Exhausted;

    /// <summary>上限（MB，界面上的数字框）。</summary>
    public double TrafficLimitMb
    {
        get => Cfg.TrafficLimitMb;
        set
        {
            if (Math.Abs(Cfg.TrafficLimitMb - value) < 0.001) return;
            Cfg.TrafficLimitMb = value;
            OnPropertyChanged();
            ApplyTrafficSettings();
        }
    }

    /// <summary>上限方向下拉。</summary>
    public IReadOnlyList<OptionItem<TrafficQuotaScope>> TrafficScopeOptions { get; } = OptionItem.From(
        (TrafficQuotaScope.Both, "收发合计"),
        (TrafficQuotaScope.Upload, "仅上传"),
        (TrafficQuotaScope.Download, "仅下载"),
        (TrafficQuotaScope.Off, "不限制"));

    /// <summary>下拉框桥接。</summary>
    public OptionItem<TrafficQuotaScope> SelectedTrafficScope
    {
        get => OptionItem<TrafficQuotaScope>.Find(TrafficScopeOptions, Cfg.TrafficScope) ?? TrafficScopeOptions[0];
        set
        {
            if (value is null) return;
            Cfg.TrafficScope = value.Value;
            OnPropertyChanged();
            ApplyTrafficSettings();
        }
    }

    /// <summary>发图压缩预设下拉。</summary>
    public IReadOnlyList<OptionItem<ImageQualityPreset>> ImageQualityOptions { get; } = OptionItem.From(
        (ImageQualityPreset.Balance, "只压过大的图（推荐）"),
        (ImageQualityPreset.Small, "压到长边 1280"),
        (ImageQualityPreset.Tiny, "压到长边 800"),
        (ImageQualityPreset.Original, "原图直发"));

    /// <summary>压缩预设桥接。</summary>
    public OptionItem<ImageQualityPreset> SelectedImageQuality
    {
        get => OptionItem<ImageQualityPreset>.Find(ImageQualityOptions, Cfg.ImageQuality) ?? ImageQualityOptions[0];
        set
        {
            if (value is null) return;
            Cfg.ImageQuality = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImageQualityHint));
        }
    }

    /// <summary>压缩设置的一行说明。</summary>
    public string ImageQualityHint => Cfg.ImageQualityText;

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand StartHostCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand AttachCommand { get; }
    public AsyncRelayCommand SendImageCommand { get; }
    public AsyncRelayCommand PingCommand { get; }
    public RelayCommand CopyAddressCommand { get; }
    public RelayCommand ClearChatCommand { get; }
    public RelayCommand OpenSessionFolderCommand { get; }
    public RelayCommand OpenRootFolderCommand { get; }
    public RelayCommand OpenProtocolDocCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand RevealFileCommand { get; }
    public AsyncRelayCommand KickCommand { get; }
    public AsyncRelayCommand KickAllCommand { get; }
    public RelayCommand RefreshMembersCommand { get; }
    public RelayCommand InsertEmojiCommand { get; }
    public RelayCommand ClearTransfersCommand { get; }
    public RelayCommand OpenTransferCommand { get; }
    public RelayCommand RefreshHistoryCommand { get; }
    public RelayCommand OpenHistoryCommand { get; }
    public RelayCommand OpenHistoryFolderCommand { get; }
    public RelayCommand OpenTopCommand { get; }
    public RelayCommand SetListenLocalCommand { get; }
    public RelayCommand SetListenAllCommand { get; }
    public RelayCommand SetListenAllV6Command { get; }

    /// <summary>需要用户二次确认时的出口（自检里替换成“总是同意”，就能无头跑完整流程）。</summary>
    public Func<string, string, bool> Confirm { get; set; } = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
}
