using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.ChatRoom.Call;
using YuLiangToolkit.Tools.ChatRoom.Net;
using YuLiangToolkit.Tools.ChatRoom.Protocol;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>
/// <see cref="ChatRoomViewModel"/> 的「群语音 / 群视频通话」界面部分（partial，独立文件）。
///
/// <para><b>职责边界</b>：协议 / 网络 / 会话层（<c>ChatRoomSession</c>）已经就绪，本文件只做
/// 「把会话层的通话事件翻译成界面状态」+「把界面上的按钮翻译成会话层调用」：</para>
/// <list type="bullet">
///   <item>加入 / 挂断 / 麦克风 / 摄像头开关（命令）；</item>
///   <item>参与者磁贴（<see cref="CallTiles"/>）、时长、码率统计、空状态提示；</item>
///   <item>视频帧解码（后台解码 + <c>Ui(...)</c> 赋值，每路只留一个解码在跑，新帧直接丢）；</item>
///   <item>摄像头 / 麦克风 / 扬声器 / 画质下拉（写 <c>Cfg</c>，自动落盘）。</item>
/// </list>
///
/// <para><b>线程约定</b>（通话最容易踩的坑）：</para>
/// <list type="bullet">
///   <item><c>CallStateChanged</c> / <c>CallStatsChanged</c> 在网络线程触发；</item>
///   <item><c>CallLocalVideoFrame</c> 在采集 worker 线程触发、<c>CallRemoteVideoFrame</c> 在网络读线程触发；</item>
///   <item>所以：解码可以放 <see cref="Task.Run(Action)"/>，但<b>改 <see cref="CallTiles"/> / 绑定属性必须经 <c>Ui(...)</c></b>；
///         <c>JpegFrameCodec</c> 返回的图已经 Freeze，跨线程持有没问题。</item>
/// </list>
///
/// <para><b>v1 边界</b>：不做「邀请弹窗 / 自动接听」，谁点谁入会；别人开没开麦 / 摄像头看磁贴；
/// 没有回声消除，界面提示戴耳机。</para>
/// </summary>
public sealed partial class ChatRoomViewModel
{
    // ================================================================ 字段

    /// <summary>当前已经接好通话事件的那个会话（换会话时先摘旧事件，防止旧会话还往界面推东西）。</summary>
    private ChatRoomSession? _callWiredSession;

    /// <summary>通话计时器（1 秒一次刷新时长；挂断后必须停）。</summary>
    private DispatcherTimer? _callTimer;

    /// <summary>本轮通话开始的 UTC 时间（从 <c>CallStateInfo.StartedAtUtc</c> 来）。</summary>
    private DateTime _callStartedUtc;

    /// <summary>最近一次统计快照（计时器刷新时复用；免得每秒都等网络层再推一次）。</summary>
    private CallStats? _callLastStats;

    /// <summary>本机视频帧是不是正在解码（1 = 正在解，新帧直接丢，防止积压把内存吃满）。</summary>
    private int _localFrameDecoding;

    /// <summary>每个远端成员是不是正在解码（同上；按成员分，别让一个人的慢帧卡住所有人）。</summary>
    private readonly ConcurrentDictionary<string, byte> _remoteFrameDecoding = new(StringComparer.Ordinal);

    /// <summary>演示数据已经塞过（<c>YLT_CHAT_CALL_DEMO</c> 截图模式专用；真通话事件一律忽略它）。</summary>
    private bool _demoCallSeeded;

    /// <summary>本端正在执行「用户自己点挂断」，用来区分「我被移出 / 主机结束」和「我主动挂断」。</summary>
    private bool _callLeavingByUser;

    private bool _isInCall;
    private bool _callPanelCollapsed;
    private bool _isCallSettingsVisible;
    private bool _isJoiningCall;
    private bool _callDevicesLoaded;
    private bool _callDevicesLoading;
    private bool _isMicOn = true;
    private bool _isCameraOn;
    private bool _canToggleCamera = true;
    private string _callStatsText = "";
    private string _callStatusText = "";

    /// <summary>演示数据里「自己」的成员 Id（没连会话时也有个稳定的 Id 可用）。</summary>
    private const string DemoSelfMemberId = "demo-self";

    // ================================================================ 公开状态（XAML 绑这些）

    /// <summary>现在在通话里（决定工具条上显示「加入」还是「通话中」那一组按钮）。</summary>
    public bool IsInCall
    {
        get => _isInCall;
        private set
        {
            if (!Set(ref _isInCall, value)) return;
            OnPropertyChanged(nameof(HasCallPanel));
            OnPropertyChanged(nameof(IsCallPanelVisible));
            OnPropertyChanged(nameof(CallTitleText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>有没有通话面板可看（在通话里就有；收起面板只是把它藏起来，不算没有）。</summary>
    public bool HasCallPanel => IsInCall;

    /// <summary>通话浮层显不显示：在通话里、且用户没有点「收起」。</summary>
    public bool IsCallPanelVisible => IsInCall && !_callPanelCollapsed;

    /// <summary>参与者磁贴（界面用 <c>ItemsControl</c> 铺；按状态增删，已有磁贴不重建）。</summary>
    public ObservableCollection<CallTileItem> CallTiles { get; } = new();

    /// <summary>顶栏那一行「群通话 · N 人」。</summary>
    public string CallTitleText => IsInCall ? $"群通话 · {CallTiles.Count} 人" : "群通话";

    /// <summary>通话时长（00:12；超过一小时变成 01:02:03）。</summary>
    public string CallDurationText
    {
        get
        {
            if (!IsInCall) return "00:00";
            var span = DateTime.UtcNow - _callStartedUtc;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }
    }

    /// <summary>本端收发码率 + 音频 / 视频包数的摘要（网络层每秒推一次）。</summary>
    public string CallStatsText
    {
        get => _callStatsText;
        private set => Set(ref _callStatsText, value);
    }

    /// <summary>参与者为空时的说明（「正在启动麦克风 / 摄像头…」）；有人之后就是空串。</summary>
    public string CallStatusText
    {
        get => _callStatusText;
        private set => Set(ref _callStatusText, value);
    }

    /// <summary>麦克风开着没有（按钮上显示「开 / 关」）。</summary>
    public bool IsMicOn
    {
        get => _isMicOn;
        private set
        {
            if (!Set(ref _isMicOn, value)) return;
            OnPropertyChanged(nameof(MicButtonText));
            OnPropertyChanged(nameof(MicToolTip));
        }
    }

    /// <summary>摄像头开着没有。</summary>
    public bool IsCameraOn
    {
        get => _isCameraOn;
        private set
        {
            if (!Set(ref _isCameraOn, value)) return;
            OnPropertyChanged(nameof(CameraButtonText));
            OnPropertyChanged(nameof(CameraToolTip));
        }
    }

    /// <summary>这台机器有没有可用摄像头（引擎启动后才知道；没有就把 📹 按钮灰掉）。</summary>
    public bool CanToggleCamera
    {
        get => _canToggleCamera;
        private set => Set(ref _canToggleCamera, value);
    }

    /// <summary>通话设置小面板开着没有（摄像头 / 麦克风 / 扬声器 / 画质）。</summary>
    public bool IsCallSettingsVisible
    {
        get => _isCallSettingsVisible;
        private set => Set(ref _isCallSettingsVisible, value);
    }

    /// <summary>设备 / 画质说明（设置面板底部与浮层底部的小字）。</summary>
    public string CallHintText =>
        "通话流量不计入文件流量上限；建议戴耳机（没有回声消除，外放容易啸叫）。"
        + "切到别的工具页时通话会继续，回到本页才能操作，结束请点「挂断」。"
        + Cfg.CallBandwidthHint;

    /// <summary>正在加入通话（按钮转圈 / 防止重复点）。</summary>
    public bool IsJoiningCall
    {
        get => _isJoiningCall;
        private set
        {
            if (!Set(ref _isJoiningCall, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>设备列表已经枚举过没有（设置面板打开时若没有就自动枚举一次）。</summary>
    public bool CallDevicesLoaded
    {
        get => _callDevicesLoaded;
        private set => Set(ref _callDevicesLoaded, value);
    }

    /// <summary>麦克风按钮上的字。</summary>
    public string MicButtonText => IsMicOn ? "🎙 开" : "🔇 关";

    /// <summary>麦克风按钮的提示。</summary>
    public string MicToolTip => IsMicOn ? "麦克风：开（点一下静音）" : "麦克风：关（点一下打开）";

    /// <summary>摄像头按钮上的字。</summary>
    public string CameraButtonText => IsCameraOn ? "📹 开" : "📷 关";

    /// <summary>摄像头按钮的提示。</summary>
    public string CameraToolTip => IsCameraOn ? "摄像头：开（点一下关掉）" : "摄像头：关（点一下打开）";

    // ================================================================ 设备 / 画质设置

    /// <summary>可选摄像头（第一项固定是「系统默认」）。</summary>
    public ObservableCollection<CallDevice> CallCameras { get; } = new();

    /// <summary>可选麦克风（第一项固定是「系统默认」）。</summary>
    public ObservableCollection<CallDevice> CallMicrophones { get; } = new();

    /// <summary>可选扬声器（第一项固定是「系统默认」）。</summary>
    public ObservableCollection<CallDevice> CallSpeakers { get; } = new();

    /// <summary>选中的摄像头（写进 <c>Cfg.CallCameraId</c>）。</summary>
    public CallDevice? SelectedCallCamera
    {
        get => FindDevice(CallCameras, Cfg.CallCameraId);
        set
        {
            if (value is null || string.Equals(Cfg.CallCameraId, value.Id, StringComparison.Ordinal)) return;
            Cfg.CallCameraId = value.Id;
            OnPropertyChanged();
            Log($"通话摄像头：{value.Name}{(value.Id.Length == 0 ? "（系统默认）" : "")}");
        }
    }

    /// <summary>选中的麦克风（写进 <c>Cfg.CallMicrophoneId</c>）。</summary>
    public CallDevice? SelectedCallMicrophone
    {
        get => FindDevice(CallMicrophones, Cfg.CallMicrophoneId);
        set
        {
            if (value is null || string.Equals(Cfg.CallMicrophoneId, value.Id, StringComparison.Ordinal)) return;
            Cfg.CallMicrophoneId = value.Id;
            OnPropertyChanged();
            Log($"通话麦克风：{value.Name}");
        }
    }

    /// <summary>选中的扬声器（写进 <c>Cfg.CallSpeakerId</c>）。</summary>
    public CallDevice? SelectedCallSpeaker
    {
        get => FindDevice(CallSpeakers, Cfg.CallSpeakerId);
        set
        {
            if (value is null || string.Equals(Cfg.CallSpeakerId, value.Id, StringComparison.Ordinal)) return;
            Cfg.CallSpeakerId = value.Id;
            OnPropertyChanged();
            Log($"通话扬声器：{value.Name}");
        }
    }

    /// <summary>画质下拉选项（只影响视频；音频固定 16 kHz μ-law）。</summary>
    public IReadOnlyList<OptionItem<CallQualityPreset>> CallQualityOptions { get; } = OptionItem.From(
        (CallQualityPreset.Saver, CallProtocolRules.LabelOf(CallQualityPreset.Saver)),
        (CallQualityPreset.Standard, CallProtocolRules.LabelOf(CallQualityPreset.Standard)),
        (CallQualityPreset.High, CallProtocolRules.LabelOf(CallQualityPreset.High)));

    /// <summary>选中的画质（写进 <c>Cfg.CallQuality</c>）。</summary>
    public OptionItem<CallQualityPreset> SelectedCallQuality
    {
        get => OptionItem<CallQualityPreset>.Find(CallQualityOptions, Cfg.CallQuality) ?? CallQualityOptions[1];
        set
        {
            if (value is null || value.Value == Cfg.CallQuality) return;
            Cfg.CallQuality = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CallHintText));
            Log($"通话画质：{Cfg.CallQualityText}（{Cfg.CallBandwidthHint}）");
        }
    }

    // ================================================================ 命令（懒创建：partial 文件没法往构造函数里塞）

    private AsyncRelayCommand? _joinVoiceCallCommand;
    private AsyncRelayCommand? _joinVideoCallCommand;
    private AsyncRelayCommand? _leaveCallCommand;
    private RelayCommand? _toggleMicCommand;
    private RelayCommand? _toggleCameraCommand;
    private RelayCommand? _toggleCallPanelCommand;
    private RelayCommand? _toggleCallSettingsCommand;
    private AsyncRelayCommand? _refreshCallDevicesCommand;

    /// <summary>能不能点「加入通话」：在线、不在通话、没在忙。</summary>
    private bool CanJoinCall => IsOnline && !IsInCall && !IsJoiningCall && !IsBusy2;

    /// <summary>加入群语音（不开摄像头）。</summary>
    public AsyncRelayCommand JoinVoiceCallCommand =>
        _joinVoiceCallCommand ??= new AsyncRelayCommand(_ => JoinRoomCallAsync(false), _ => CanJoinCall);

    /// <summary>加入群视频（开摄像头；画质按设置里的档位）。</summary>
    public AsyncRelayCommand JoinVideoCallCommand =>
        _joinVideoCallCommand ??= new AsyncRelayCommand(_ => JoinRoomCallAsync(true), _ => CanJoinCall);

    /// <summary>挂断（离开群通话）。</summary>
    public AsyncRelayCommand LeaveCallCommand =>
        _leaveCallCommand ??= new AsyncRelayCommand(_ => LeaveRoomCallAsync(), _ => IsInCall || IsJoiningCall);

    /// <summary>开 / 关麦克风。</summary>
    public RelayCommand ToggleMicCommand =>
        _toggleMicCommand ??= new RelayCommand(_ => ToggleMic(), _ => IsInCall);

    /// <summary>开 / 关摄像头。</summary>
    public RelayCommand ToggleCameraCommand =>
        _toggleCameraCommand ??= new RelayCommand(_ => ToggleCamera(), _ => IsInCall && CanToggleCamera);

    /// <summary>收起 / 展开通话浮层（收起后只留工具条上的按钮）。</summary>
    public RelayCommand ToggleCallPanelCommand =>
        _toggleCallPanelCommand ??= new RelayCommand(_ => ToggleCallPanel(), _ => IsInCall);

    /// <summary>开 / 关通话设置小面板（第一次打开会自动枚举设备）。</summary>
    public RelayCommand ToggleCallSettingsCommand =>
        _toggleCallSettingsCommand ??= new RelayCommand(_ => ToggleCallSettings(), _ => IsInCall);

    /// <summary>重新枚举摄像头 / 麦克风 / 扬声器（插拔设备后点它）。</summary>
    public AsyncRelayCommand RefreshCallDevicesCommand =>
        _refreshCallDevicesCommand ??= new AsyncRelayCommand(_ => RefreshCallDevicesAsync(), _ => !IsJoiningCall);

    // ================================================================ 会话接线（CallHooks 的两个 partial）

    /// <summary>
    /// 会话层建好后接上通话事件。换会话 / 重连时先摘掉旧会话的事件，
    /// 否则旧会话的 <c>CallFailed</c> / 视频帧还会往界面推，表现成「挂断了又自己弹出来」。
    /// </summary>
    partial void OnCallSessionWired(ChatRoomSession session)
    {
        DetachCallSession();

        _callWiredSession = session;
        session.CallStateChanged += OnCallStateChanged;
        session.CallLocalVideoFrame += OnCallLocalVideoFrame;
        session.CallRemoteVideoFrame += OnCallRemoteVideoFrame;
        session.CallStatsChanged += OnCallStatsChanged;
        session.CallFailed += OnCallFailed;

        // 截图 / 排错：YLT_CHAT_CALL_DEMO=1 时塞一份演示通话，没连上房间也能核对面板版式。
        SeedCallDemoIfRequested();
    }

    /// <summary>
    /// 工具被关闭 / 断开连接：挂断通话并把界面复位（隐藏浮层、清磁贴、停计时器）。
    /// <para>挂断是 fire-and-forget：这里可能在关闭流程里，不能 await 卡住界面。</para>
    /// </summary>
    partial void OnCallUiHangUp()
    {
        var session = _session;
        DetachCallSession();
        ResetCallUi();
        _demoCallSeeded = false;
        if (session is not null) _ = LeaveSessionCallAsync(session);
    }

    /// <summary>摘掉旧会话上的通话事件（换会话 / 关闭时调）。</summary>
    private void DetachCallSession()
    {
        var session = _callWiredSession;
        _callWiredSession = null;
        if (session is null) return;
        try
        {
            session.CallStateChanged -= OnCallStateChanged;
            session.CallLocalVideoFrame -= OnCallLocalVideoFrame;
            session.CallRemoteVideoFrame -= OnCallRemoteVideoFrame;
            session.CallStatsChanged -= OnCallStatsChanged;
            session.CallFailed -= OnCallFailed;
        }
        catch
        {
            // 会话已经在拆了：摘不掉就算了，别影响关闭流程
        }
    }

    /// <summary>后台挂断（关闭 / 断开连接时用；异常一律吞掉，界面已经复位了）。</summary>
    private static async Task LeaveSessionCallAsync(ChatRoomSession session)
    {
        try { await session.LeaveCallAsync().ConfigureAwait(false); }
        catch { /* 关闭流程忽略 */ }
    }

    // ================================================================ 加入 / 挂断

    /// <summary>加入群通话。<paramref name="withVideo"/> = true 是视频通话（开摄像头），false 是语音。</summary>
    private async Task JoinRoomCallAsync(bool withVideo)
    {
        var session = _session;
        if (session is null || !IsOnline || IsJoiningCall)
        {
            Notice("现在不能发起通话：先开房间或连上主机", ChatNoticeLevel.Warn);
            return;
        }

        // 会话层其实已经在通话里（界面状态漏了）：直接跟随会话层，别重复加入
        if (session.IsInCall)
        {
            AdoptRunningCall(session);
            return;
        }

        if (IsInCall) return;

        IsJoiningCall = true;
        CallStatusText = "正在启动麦克风 / 摄像头…";
        try
        {
            if (!CallDevicesLoaded) await RefreshCallDevicesAsync();

            var settings = new CallSettings
            {
                CameraDeviceId = Cfg.CallCameraId,
                MicrophoneDeviceId = Cfg.CallMicrophoneId,
                SpeakerDeviceId = Cfg.CallSpeakerId,
                Quality = Cfg.CallQuality,
                EnableVideo = withVideo,
                EnableAudio = true,
            };

            Log($"开始加入群通话（{(withVideo ? "语音 + 视频" : "语音")}，画质 {Cfg.CallQualityText}）");

            var (ok, error) = await session.JoinCallAsync(settings, WindowsCallMediaFactory.Shared);
            if (!ok)
            {
                CallStatusText = "";
                Notice($"加入通话失败：{error}", ChatNoticeLevel.Error);
                Log($"✗ 加入通话失败：{error}");
                AddSystemMessage($"加入通话失败：{error}");
                return;
            }

            _callPanelCollapsed = false;
            IsInCall = true;
            _callStartedUtc = session.CurrentCallState.StartedAtUtc ?? DateTime.UtcNow;
            SyncEngineSwitches();
            StartCallTimer();
            RebuildCallTiles(session.CurrentCallState);
            OnPropertyChanged(nameof(CallHintText));
            OnPropertyChanged(nameof(CallDurationText));

            AddSystemMessage($"你加入了通话（{(withVideo ? "语音 + 视频" : "语音")}）");
            Notice("已加入群通话");
            Log("✓ 已加入群通话");
        }
        catch (Exception ex)
        {
            CallStatusText = "";
            Notice($"加入通话失败：{ex.Message}", ChatNoticeLevel.Error);
            Log($"✗ 加入通话失败：{ex.Message}");
            AddSystemMessage($"加入通话失败：{ex.Message}");
            ResetCallUi();
        }
        finally
        {
            IsJoiningCall = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>会话层已经在通话里时，把界面直接接过去（重连 / 状态漏刷的兜底）。</summary>
    private void AdoptRunningCall(ChatRoomSession session)
    {
        _callPanelCollapsed = false;
        IsInCall = true;
        _callStartedUtc = session.CurrentCallState.StartedAtUtc ?? DateTime.UtcNow;
        SyncEngineSwitches();
        StartCallTimer();
        RebuildCallTiles(session.CurrentCallState);
        OnPropertyChanged(nameof(CallDurationText));
    }

    /// <summary>「挂断」按钮：先让会话层收设备与媒体连接，再把界面复位。</summary>
    private async Task LeaveRoomCallAsync()
    {
        if (_callLeavingByUser) return;
        _callLeavingByUser = true;
        var session = _session;
        try
        {
            if (session is not null) await session.LeaveCallAsync();
        }
        catch (Exception ex)
        {
            Log($"挂断通话时出错：{ex.Message}");
        }
        finally
        {
            Ui(ResetCallUi);
            _demoCallSeeded = false;
            _callLeavingByUser = false;
            AddSystemMessage("你挂断了通话");
            Notice("已挂断通话");
            Log("已挂断通话");
        }
    }

    /// <summary>把通话界面复位（隐藏浮层、清磁贴、停计时器）；不碰会话层。</summary>
    private void ResetCallUi()
    {
        StopCallTimer();
        _callLastStats = null;
        _localFrameDecoding = 0;
        _remoteFrameDecoding.Clear();

        CallTiles.Clear();
        IsInCall = false;
        _callPanelCollapsed = false;
        IsCallSettingsVisible = false;
        OnPropertyChanged(nameof(IsCallPanelVisible));
        OnPropertyChanged(nameof(CallTitleText));
        OnPropertyChanged(nameof(CallDurationText));
        CallStatsText = "";
        CallStatusText = "";
        IsMicOn = true;
        IsCameraOn = false;
        CanToggleCamera = true;
        CommandManager.InvalidateRequerySuggested();
    }

    // ================================================================ 麦克风 / 摄像头 / 面板 / 设置

    /// <summary>开 / 关麦克风：引擎是真开关（停了就不采集也不发送），界面按引擎实际状态更新。</summary>
    private void ToggleMic()
    {
        if (!IsInCall) return;
        var engine = _session?.ActiveCallEngine;
        if (engine is null)
        {
            // 演示模式（没有真引擎）：只改界面状态，方便截图核对两种样子
            if (!_demoCallSeeded) return;
            IsMicOn = !IsMicOn;
            UpdateSelfTile();
            return;
        }

        var want = !IsMicOn;
        try
        {
            engine.SetMicOn(want);
        }
        catch (Exception ex)
        {
            Notice($"切换麦克风失败：{ex.Message}", ChatNoticeLevel.Error);
            return;
        }

        IsMicOn = engine.MicOn;
        UpdateSelfTile();
        Notice(IsMicOn ? "麦克风已开" : "麦克风已关（别人听不到你说话）");
        Log(IsMicOn ? "麦克风已开" : "麦克风已静音");
    }

    /// <summary>开 / 关摄像头：设备被别的程序占用时给提示，并把引擎的实际状态如实反映到界面。</summary>
    private void ToggleCamera()
    {
        if (!IsInCall || !CanToggleCamera) return;
        var engine = _session?.ActiveCallEngine;
        if (engine is null)
        {
            if (!_demoCallSeeded) return;
            IsCameraOn = !IsCameraOn;
            UpdateSelfTile();
            return;
        }

        var want = !IsCameraOn;
        try
        {
            engine.SetCameraOn(want);
        }
        catch (Exception ex)
        {
            Notice($"切换摄像头失败：{ex.Message}", ChatNoticeLevel.Warn);
            return;
        }

        IsCameraOn = engine.CameraOn;
        UpdateSelfTile();
        if (want && !IsCameraOn)
        {
            Notice("摄像头打不开：可能被其它程序占用，或这台机器没有摄像头", ChatNoticeLevel.Warn);
            Log("✗ 摄像头打不开（设备被占用 / 不存在）");
        }
        else
        {
            Notice(IsCameraOn ? "摄像头已开" : "摄像头已关");
            Log(IsCameraOn ? "摄像头已开" : "摄像头已关");
        }
    }

    /// <summary>收起 / 展开通话浮层（收起后工具条上还留着一排通话按钮）。</summary>
    private void ToggleCallPanel()
    {
        if (!IsInCall) return;
        _callPanelCollapsed = !_callPanelCollapsed;
        if (_callPanelCollapsed) IsCallSettingsVisible = false;
        OnPropertyChanged(nameof(IsCallPanelVisible));
    }

    /// <summary>开 / 关通话设置小面板（第一次打开时自动枚举设备）。</summary>
    private void ToggleCallSettings()
    {
        if (!IsInCall) return;
        IsCallSettingsVisible = !IsCallSettingsVisible;
        if (IsCallSettingsVisible && !CallDevicesLoaded) _ = RefreshCallDevicesAsync();
    }

    /// <summary>把引擎的麦克风 / 摄像头 / 摄像头可用性同步到界面（加入之后调）。</summary>
    private void SyncEngineSwitches()
    {
        var engine = _session?.ActiveCallEngine;
        if (engine is null) return;
        IsMicOn = engine.MicOn;
        IsCameraOn = engine.CameraOn;
        // 语音通话时引擎可能还没打开摄像头采集，CameraAvailable 未必准；
        // 设备列表里除了固定的「系统默认」那一项之外还有摄像头，就允许点开（进来以后还能转视频）。
        CanToggleCamera = engine.CameraAvailable || CallCameras.Count > 1;
    }

    /// <summary>把自己磁贴上的麦克风 / 摄像头状态跟界面同步（不等状态广播回来）。</summary>
    private void UpdateSelfTile()
    {
        var tile = FindTile(_session?.LocalMemberId ?? DemoSelfMemberId);
        if (tile is null) return;
        tile.MicOn = IsMicOn;
        tile.CameraOn = IsCameraOn;
    }

    // ================================================================ 设备枚举

    /// <summary>重新枚举设备：摄像头（WinRT，异步）+ 麦克风 / 扬声器（WASAPI，同步）。</summary>
    private async Task RefreshCallDevicesAsync()
    {
        if (_callDevicesLoading) return;
        _callDevicesLoading = true;
        try
        {
            var factory = WindowsCallMediaFactory.Shared;

            IReadOnlyList<CallDevice> cameras = Array.Empty<CallDevice>();
            try { cameras = await factory.ListCamerasAsync(); }
            catch (Exception ex) { Log($"枚举摄像头失败：{ex.Message}"); }

            IReadOnlyList<CallDevice> microphones = Array.Empty<CallDevice>();
            try { microphones = factory.ListMicrophones(); }
            catch (Exception ex) { Log($"枚举麦克风失败：{ex.Message}"); }

            IReadOnlyList<CallDevice> speakers = Array.Empty<CallDevice>();
            try { speakers = factory.ListSpeakers(); }
            catch (Exception ex) { Log($"枚举扬声器失败：{ex.Message}"); }

            FillDevices(CallCameras, cameras, "系统默认摄像头");
            FillDevices(CallMicrophones, microphones, "系统默认麦克风");
            FillDevices(CallSpeakers, speakers, "系统默认扬声器");

            CallDevicesLoaded = true;
            OnPropertyChanged(nameof(SelectedCallCamera));
            OnPropertyChanged(nameof(SelectedCallMicrophone));
            OnPropertyChanged(nameof(SelectedCallSpeaker));
            Log($"通话设备：摄像头 {cameras.Count} 个，麦克风 {microphones.Count} 个，扬声器 {speakers.Count} 个");
        }
        finally
        {
            _callDevicesLoading = false;
        }
    }

    /// <summary>把枚举结果填进下拉（第一项固定是「系统默认」= 空设备 Id）。</summary>
    private static void FillDevices(ObservableCollection<CallDevice> target, IReadOnlyList<CallDevice> devices, string defaultName)
    {
        target.Clear();
        target.Add(new CallDevice("", defaultName));
        foreach (var device in devices)
            if (!string.IsNullOrWhiteSpace(device.Id)) target.Add(device);
    }

    /// <summary>在下拉里按设备 Id 找当前项（找不到就退回「系统默认」）。</summary>
    private static CallDevice? FindDevice(ObservableCollection<CallDevice> devices, string id)
    {
        if (devices.Count == 0) return null;
        foreach (var device in devices)
            if (string.Equals(device.Id, id, StringComparison.Ordinal)) return device;
        return devices[0];
    }

    // ================================================================ 会话层事件（线程：网络 / 采集线程）

    /// <summary>参与者 / 麦克风 / 摄像头状态变了（主机广播，网络线程 → 切回 UI 线程再改磁贴）。</summary>
    private void OnCallStateChanged(CallStateInfo state) => Ui(() => ApplyCallStateOnUi(state));

    /// <summary>本机摄像头预览帧（采集 worker 线程 → 后台解码 → UI 线程赋图）。</summary>
    private void OnCallLocalVideoFrame(CallRawFrame frame)
    {
        if (_demoCallSeeded) return;
        // 上一帧还没解完就丢新帧：宁可掉帧，也别在内存里堆一长串待解码的 BGRA
        if (Interlocked.CompareExchange(ref _localFrameDecoding, 1, 0) != 0) return;

        var memberId = frame.MemberId is { Length: > 0 } id ? id : (_session?.LocalMemberId ?? "");
        _ = Task.Run(() =>
        {
            try
            {
                var bitmap = DecodeCallFrame(frame);
                if (bitmap is not null) Ui(() => FindTile(memberId)?.SetVideo(bitmap));
            }
            finally
            {
                Interlocked.Exchange(ref _localFrameDecoding, 0);
            }
        });
    }

    /// <summary>远端视频帧（JPEG，网络读线程 → 后台解码 → UI 线程赋图；每个成员各自最多一帧在解）。</summary>
    private void OnCallRemoteVideoFrame(CallRawFrame frame)
    {
        if (_demoCallSeeded || string.IsNullOrEmpty(frame.MemberId)) return;
        if (!_remoteFrameDecoding.TryAdd(frame.MemberId, 0)) return;

        _ = Task.Run(() =>
        {
            try
            {
                var bitmap = DecodeCallFrame(frame);
                if (bitmap is not null) Ui(() => FindTile(frame.MemberId)?.SetVideo(bitmap));
            }
            finally
            {
                _remoteFrameDecoding.TryRemove(frame.MemberId, out _);
            }
        });
    }

    /// <summary>通话统计（约一秒一次，网络线程 → UI 线程显示）。</summary>
    private void OnCallStatsChanged(CallStats stats)
    {
        _callLastStats = stats;
        var text = BuildCallStatsText(stats);
        Ui(() =>
        {
            if (_demoCallSeeded) return;
            CallStatsText = text;
        });
    }

    /// <summary>通话意外出错 / 媒体连接断了（会话层随后会自己挂断，界面这边提示并复位）。</summary>
    private void OnCallFailed(string message) => Ui(() =>
    {
        if (_demoCallSeeded || _callLeavingByUser) return;
        Notice($"通话出错：{message}", ChatNoticeLevel.Error);
        Log($"✗ 通话出错：{message}");
        AddSystemMessage($"通话出错：{message}");
        ResetCallUi();
        _demoCallSeeded = false;
    });

    /// <summary>把一帧原始字节解成 WPF 图（已经 Freeze，可跨线程；坏帧返回 null）。</summary>
    private static BitmapSource? DecodeCallFrame(CallRawFrame frame)
    {
        try
        {
            if (frame.Bytes is not { Length: > 0 }) return null;
            return frame.IsJpeg
                ? JpegFrameCodec.DecodeToBitmap(frame.Bytes)
                : JpegFrameCodec.FromBgra(frame.Bytes, frame.Width, frame.Height);
        }
        catch
        {
            // 坏帧（尺寸不对 / 只收到半帧）：这一帧不要了，下一帧接着来
            return null;
        }
    }

    /// <summary>码率摘要（顶栏那一行；细节留在日志里，顶栏只放一行免得挤）。</summary>
    private static string BuildCallStatsText(CallStats stats) => stats.Summary;

    // ================================================================ 通话状态 → 界面

    /// <summary>UI 线程上处理通话状态：增删磁贴、更新名字 / 麦克风 / 摄像头、时长起点。</summary>
    private void ApplyCallStateOnUi(CallStateInfo state)
    {
        if (_demoCallSeeded || _callLeavingByUser) return;

        var selfId = _session?.LocalMemberId ?? "";
        var self = state.Participants?.FirstOrDefault(p => string.Equals(p.MemberId, selfId, StringComparison.Ordinal));

        if (self is null)
        {
            if (!IsInCall) return;      // 本来就不在通话：v1 不做邀请弹窗，别人的通话与当前界面无关
            // 自己不在参与者名单里 = 被移出 / 主机挂断 / 通话被结束
            ResetCallUi();
            Notice("通话已结束", ChatNoticeLevel.Warn);
            Log("通话已结束：本端不在参与者名单里");
            AddSystemMessage("通话已结束（你已不在参与者名单里）");
            return;
        }

        if (!IsInCall)
        {
            // 加入过程中：等 JoinCallAsync 返回后统一刷新（避免半截状态先把面板拉起来又复位）
            if (IsJoiningCall) return;
            // 兜底：名单里已经有自己、界面却没起来（状态事件早到 / 漏刷）→ 直接接上
            _callPanelCollapsed = false;
            IsInCall = true;
            SyncEngineSwitches();
            StartCallTimer();
        }

        if (state.StartedAtUtc is { } started) _callStartedUtc = started;
        RebuildCallTiles(state);
        OnPropertyChanged(nameof(CallDurationText));
    }

    /// <summary>
    /// 按 <paramref name="state"/> 重建 <see cref="CallTiles"/>：
    /// 已有成员原地更新（保留画面 / 不重建 Image），新成员插磁贴，离开的移除。
    /// </summary>
    private void RebuildCallTiles(CallStateInfo state)
    {
        var participants = state.Participants ?? new List<CallParticipantWire>();
        var selfId = _session?.LocalMemberId ?? DemoSelfMemberId;

        // 1) 移除已经不在名单里的磁贴
        var wanted = new HashSet<string>(participants.Select(p => p.MemberId), StringComparer.Ordinal);
        for (var index = CallTiles.Count - 1; index >= 0; index--)
            if (!wanted.Contains(CallTiles[index].MemberId)) CallTiles.RemoveAt(index);

        // 2) 新成员补磁贴 + 老成员原地更新（画面不动）
        for (var index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            var tile = FindTile(participant.MemberId);
            if (tile is null)
            {
                tile = new CallTileItem(participant.MemberId, participant.Name, participant.Emoji,
                    string.Equals(participant.MemberId, selfId, StringComparison.Ordinal));
                CallTiles.Insert(index, tile);
            }

            tile.UpdateFromWire(participant.Name, participant.Emoji, participant.MicOn, participant.CameraOn);

            if (string.Equals(participant.MemberId, selfId, StringComparison.Ordinal))
            {
                // 自己这一块以引擎的实际状态为准（点按钮后比广播回来得更快）
                var engine = _session?.ActiveCallEngine;
                if (engine is not null)
                {
                    tile.MicOn = engine.MicOn;
                    tile.CameraOn = engine.CameraOn;
                }
                IsMicOn = tile.MicOn;
                IsCameraOn = tile.CameraOn;
            }

            var currentIndex = CallTiles.IndexOf(tile);
            if (currentIndex >= 0 && currentIndex != index) CallTiles.Move(currentIndex, index);
        }

        OnPropertyChanged(nameof(CallTitleText));
        CallStatusText = CallTiles.Count == 0 && (IsInCall || IsJoiningCall) ? "正在启动麦克风 / 摄像头…" : "";
    }

    /// <summary>按成员 Id 找磁贴（找不到返回 null）。</summary>
    private CallTileItem? FindTile(string memberId)
    {
        if (string.IsNullOrEmpty(memberId)) return null;
        foreach (var tile in CallTiles)
            if (string.Equals(tile.MemberId, memberId, StringComparison.Ordinal)) return tile;
        return null;
    }

    // ================================================================ 计时器

    /// <summary>启动通话计时（1 秒一次，只刷新时长；统计数据由网络层推）。</summary>
    private void StartCallTimer()
    {
        _callTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _callTimer.Tick -= OnCallTimerTick;
        _callTimer.Tick += OnCallTimerTick;
        _callTimer.Start();
    }

    /// <summary>停掉计时器（挂断 / 复位时调；可以重复调）。</summary>
    private void StopCallTimer()
    {
        if (_callTimer is null) return;
        _callTimer.Stop();
    }

    private void OnCallTimerTick(object? sender, EventArgs e)
    {
        if (!IsInCall && !_demoCallSeeded)
        {
            StopCallTimer();
            return;
        }
        OnPropertyChanged(nameof(CallDurationText));
        if (_callLastStats is { } stats) CallStatsText = BuildCallStatsText(stats);
    }

    // ================================================================ 演示数据（截图 / 排错专用）

    /// <summary>
    /// <c>YLT_CHAT_CALL_DEMO=1</c> 时塞一份「4 个人在通话」的假数据：磁贴、时长（从 12 秒前开始走）、
    /// 码率统计、设置按钮全都能看到，用来截图核对深色浮层与磁贴版式。
    ///
    /// <para>正常启动（没有这个环境变量）完全不会走到这里；真实通话事件会忽略演示状态
    /// （<see cref="_demoCallSeeded"/> 为 true 时各事件处理直接 return）。</para>
    /// </summary>
    public void SeedCallDemoIfRequested()
    {
        if (_demoCallSeeded) return;
        if (Environment.GetEnvironmentVariable("YLT_CHAT_CALL_DEMO") is null) return;
        if (IsInCall || IsJoiningCall) return;   // 真在通话里就别塞假数据了

        try
        {
            _demoCallSeeded = true;
            IsJoiningCall = false;

            var selfId = _session?.LocalMemberId is { Length: > 0 } self ? self : DemoSelfMemberId;
            var started = DateTime.UtcNow.AddSeconds(-12);
            var state = new CallStateInfo
            {
                Active = true,
                StartedAtUtc = started,
                Participants = new List<CallParticipantWire>
                {
                    new() { MemberId = selfId, Name = Cfg.EffectiveNickname, Emoji = Cfg.Emoji, IsHost = IsHost, MicOn = true, CameraOn = true, JoinedAtUtc = started },
                    new() { MemberId = "demo-zhang", Name = "小张", Emoji = "🐻", MicOn = true, CameraOn = true, JoinedAtUtc = started.AddSeconds(6) },
                    new() { MemberId = "demo-li", Name = "阿李", Emoji = "🐱", MicOn = true, CameraOn = false, JoinedAtUtc = started.AddSeconds(18) },
                    new() { MemberId = "demo-wang", Name = "王五", Emoji = "🐼", MicOn = false, CameraOn = false, JoinedAtUtc = started.AddSeconds(30) },
                },
            };

            _callPanelCollapsed = false;
            _callStartedUtc = started;
            IsInCall = true;
            SyncDemoSwitches();
            RebuildCallTiles(state);

            // 给两块磁贴画两张「假视频画面」：截图里能同时看到「有画面」与「摄像头关」两种磁贴
            var selfTile = FindTile(selfId);
            selfTile?.SetVideo(CreateDemoPicture(Color.FromRgb(0x2B, 0x4C, 0x7E), Color.FromRgb(0x14, 0x9E, 0xA8)));
            var zhangTile = FindTile("demo-zhang");
            zhangTile?.SetVideo(CreateDemoPicture(Color.FromRgb(0x3A, 0x2F, 0x6B), Color.FromRgb(0xAC, 0x5A, 0x68)));

            _callLastStats = new CallStats
            {
                SendKbps = 1260,
                ReceiveKbps = 2310,
                SentAudioPackets = 320,
                SentVideoPackets = 148,
                ReceivedAudioPackets = 1180,
                ReceivedVideoPackets = 486,
                SendVideoFps = 10,
                DroppedVideoFrames = 0,
            };
            CallStatsText = BuildCallStatsText(_callLastStats);
            CallStatusText = "";
            OnPropertyChanged(nameof(CallDurationText));
            OnPropertyChanged(nameof(CallHintText));
            OnPropertyChanged(nameof(CallTitleText));

            StartCallTimer();
            Log("（演示数据：YLT_CHAT_CALL_DEMO 打开了）通话面板只用于截图看样式，不影响真实通话。");
            Notice("（演示数据：YLT_CHAT_CALL_DEMO）通话面板是假的，仅用于截图核对样式。");
        }
        catch (Exception ex)
        {
            // 演示都不该影响正常使用：出错就安静地放弃
            _demoCallSeeded = false;
            Log($"演示通话数据失败：{ex.Message}");
        }
    }

    /// <summary>演示数据下的麦克风 / 摄像头开关（没有真引擎，直接给个「都开着」的状态）。</summary>
    private void SyncDemoSwitches()
    {
        IsMicOn = true;
        IsCameraOn = true;
        CanToggleCamera = true;
    }

    /// <summary>给演示磁贴画一张渐变「假画面」（Freeze 后可直接绑 Image）。</summary>
    private static BitmapSource? CreateDemoPicture(Color from, Color to)
    {
        try
        {
            const int width = (int)CallTileItem.TileWidth;
            const int height = (int)CallTileItem.TileHeight;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new LinearGradientBrush(from, to, 45);
                dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
