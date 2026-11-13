using System.Collections.ObjectModel;
using System.Windows;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;
using YuLiangToolkit.Tools.Games.Tetris;
using YuLiangToolkit.Tools.Games.Tetris.Net;

namespace YuLiangToolkit.Tools.Games.Tetris.ViewModels;

/// <summary>界面上的「玩法」：单人 / 联机互攻 / 联机比分数。</summary>
public enum TetrisPlayMode
{
    /// <summary>单人：自己玩自己的，不上网络。</summary>
    Solo = 0,

    /// <summary>联机 · 垃圾行互攻。</summary>
    NetGarbage = 1,

    /// <summary>联机 · 各自计分。</summary>
    NetScore = 2,
}

/// <summary>
/// 「俄罗斯方块」的联机部分（1 对 1）：模式切换、开房间 / 加入 / 离开、双方棋盘驱动、状态广播。
///
/// <para><b>为什么拆一个 partial 文件</b>：单人玩法那套（定时器 / 计分 / 盘面刷新）已经写了 500 行，
/// 网络这部分是**独立的一块**，混在一起会让人读不下去。</para>
///
/// <para><b>房主权威 + 两份引擎</b>：客户端只上报按键，不跑任何规则，所以房主必须同时维护
/// 「自己那份」（<c>_engine</c>）和「客户端那份」（<c>_remoteEngine</c>）——
/// 客户端的按键动作由房主代跑，两份棋盘都由房主算，垃圾行互攻与胜负判定因此只有一处发生。
/// 客户端收到状态后，用自己的那份快照渲染棋盘、用对手那份画缩略图。</para>
///
/// <para><b>线程</b>：会话事件都在后台线程，这里一律用 <c>Ui(...)</c> 切回 UI 线程再改界面状态。</para>
/// </summary>
public sealed partial class TetrisViewModel
{
    private TetrisNetSession? _session;

    /// <summary>房主：替客户端跑的那份引擎（客户端角色下不使用）。</summary>
    private TetrisEngine? _remoteEngine;

    private TetrisNetPhase _netPhase = TetrisNetPhase.Waiting;
    private TetrisNetMode _netMode = TetrisNetMode.Garbage;
    private TetrisPlayMode _playMode = TetrisPlayMode.Solo;
    private bool _netBusy;
    private bool _guestPresent;
    private string _guestName = "";
    private string _netNotice = "开一个房间等好友连进来，或者填对方发来的地址加入";
    private string _netNoticeBrushKey = "TextSecondaryBrush";
    private int _lastStateSeq;
    private int _netSeq;
    private string _resultText = "";
    private string _resultSide = "";

    private TetrisBoardViewState _guestBoardState = TetrisBoardViewState.Empty;
    private int _opponentScore;
    private int _opponentLines;
    private int _opponentLevel = 1;
    private int _opponentPendingGarbage;
    private bool _opponentDead;

    /// <summary>截图 / 演示用：设了 <c>YLT_TETRIS_NET=room</c> 时假装房间已开（不联网、不影响真实逻辑）。</summary>
    private bool _demoRoom;

    /// <summary>本端连进房间后的身份（房主 / 对战 / 观战）。</summary>
    private TetrisMemberRole _netRole = TetrisMemberRole.Host;

    /// <summary>观战者名单（房主与观战者都显示）。</summary>
    public ObservableCollection<string> SpectatorNames { get; } = new();

    /// <summary>本端是不是观战者（观战时不能操作棋盘）。</summary>
    public bool IsSpectating => IsNetMode && _netRole == TetrisMemberRole.Spectator;

    /// <summary>观战者人数文案。</summary>
    public string SpectatorCountText => SpectatorNames.Count == 0
        ? "暂无观战者"
        : $"{SpectatorNames.Count} 人观战：{string.Join("、", SpectatorNames)}";

    /// <summary>观战者名单有没有内容。</summary>
    public bool HasSpectators => SpectatorNames.Count > 0;

    // ---------------------------------------------------------------- 界面状态（模式）

    /// <summary>当前玩法。</summary>
    public TetrisPlayMode PlayMode
    {
        get => _playMode;
        private set
        {
            if (!Set(ref _playMode, value)) return;
            OnPropertyChanged(nameof(IsSolo));
            OnPropertyChanged(nameof(IsNetMode));
            OnPropertyChanged(nameof(IsGarbageMode));
            OnPropertyChanged(nameof(IsScoreMode));
            OnPropertyChanged(nameof(SoloModeIsChecked));
            OnPropertyChanged(nameof(GarbageModeIsChecked));
            OnPropertyChanged(nameof(ScoreModeIsChecked));
            OnPropertyChanged(nameof(ModeHintText));
            OnPropertyChanged(nameof(RoomPanelVisible));
            OnPropertyChanged(nameof(OpponentPanelVisible));
            OnPropertyChanged(nameof(MyGarbageText));
            OnPropertyChanged(nameof(GarbageScoreVisible));
        }
    }

    /// <summary>单人玩法。</summary>
    public bool IsSolo => _playMode == TetrisPlayMode.Solo;

    /// <summary>联机玩法（两种之一）。</summary>
    public bool IsNetMode => _playMode != TetrisPlayMode.Solo;

    /// <summary>联机 · 垃圾行互攻。</summary>
    public bool IsGarbageMode => _playMode == TetrisPlayMode.NetGarbage;

    /// <summary>联机 · 各自计分。</summary>
    public bool IsScoreMode => _playMode == TetrisPlayMode.NetScore;

    /// <summary>垃圾行相关文案只在互攻模式显示（联机互攻，或单人 vs 电脑勾了互攻）。</summary>
    public bool GarbageScoreVisible => IsGarbageMode || SoloGarbageActive;

    // 三个单选框的桥接属性（XAML 直接绑 IsChecked，点一下就切模式）
    public bool SoloModeIsChecked
    {
        get => IsSolo;
        set { if (value) SwitchMode(TetrisPlayMode.Solo); }
    }

    public bool GarbageModeIsChecked
    {
        get => IsGarbageMode;
        set { if (value) SwitchMode(TetrisPlayMode.NetGarbage); }
    }

    public bool ScoreModeIsChecked
    {
        get => IsScoreMode;
        set { if (value) SwitchMode(TetrisPlayMode.NetScore); }
    }

    /// <summary>当前模式的一句话说明。</summary>
    public string ModeHintText => _playMode switch
    {
        TetrisPlayMode.NetGarbage => TetrisNetProtocol.ModeDescription(TetrisNetMode.Garbage),
        TetrisPlayMode.NetScore => TetrisNetProtocol.ModeDescription(TetrisNetMode.Score),
        _ => "自己玩：消行得分升级，等级越高落得越快，最高分自动记住",
    };

    /// <summary>联机时才显示房间卡。</summary>
    public bool RoomPanelVisible => IsNetMode;

    /// <summary>联机且对手在场时才显示对手信息。</summary>
    public bool OpponentPanelVisible => IsNetMode && _guestPresent;

    // ---------------------------------------------------------------- 界面状态（房间）

    /// <summary>玩家名（联机时对方看到的名字）。</summary>
    public string PlayerName
    {
        get => Cfg.PlayerName;
        set
        {
            Cfg.PlayerName = value;
            OnPropertyChanged();
        }
    }

    /// <summary>加入房间时填的地址。</summary>
    public string JoinAddress
    {
        get => Cfg.LastJoinAddress;
        set
        {
            Cfg.LastJoinAddress = value;
            OnPropertyChanged();
        }
    }

    /// <summary>开房端口（文本形式，0 = 自动挑）。</summary>
    public string ListenPortText
    {
        get => Cfg.ListenPort.ToString();
        set
        {
            var parsed = int.TryParse((value ?? "").Trim(), out var port) ? port : 0;
            Cfg.ListenPort = Math.Clamp(port, 0, 65535);
            OnPropertyChanged();
        }
    }

    /// <summary>房间状态徽标。</summary>
    public string NetBadgeText
    {
        get
        {
            if (_netBusy) return "连接中…";
            if (_demoRoom) return _guestPresent ? "房间里有人" : "等人加入";
            if (_session is null) return "未联机";
            if (_session.IsHost) return _guestPresent ? "房间里有人" : "等人加入";
            return _session.IsGuestConnected ? "已连上房主" : "未联机";
        }
    }

    /// <summary>徽标颜色（SuccessBrush / TextSecondaryBrush）。</summary>
    public string NetBadgeBrushKey
        => _demoRoom || (_session is not null && (_guestPresent || _session.IsGuestConnected))
            ? "SuccessBrush"
            : "TextSecondaryBrush";

    /// <summary>房间状态一句话。</summary>
    public string NetNoticeText => _netNotice;

    /// <summary>状态文字颜色。</summary>
    public string NetNoticeBrushKey => _netNoticeBrushKey;

    /// <summary>是不是在连 / 开房的过程中（按钮要禁用）。</summary>
    public bool NetBusy => _netBusy;

    /// <summary>能不能开房 / 加入（没在联机、也没人在连；演示房间除外）。</summary>
    public bool CanJoinNet => !_netBusy && _session is null && !_demoRoom;

    /// <summary>能不能离开（正在联机时）。</summary>
    public bool CanLeaveNet => _session is not null || _netBusy || _demoRoom;

    /// <summary>离开按钮上的字。</summary>
    public string LeaveNetText => _session?.IsHost == true ? "关闭房间" : "离开房间";

    /// <summary>房主开房后可以复制给对方的地址。</summary>
    public ObservableCollection<TetrisShareAddress> ShareAddresses { get; } = new();

    /// <summary>有没有可复制的地址。</summary>
    public bool HasShareAddresses => ShareAddresses.Count > 0;

    /// <summary>对手的分数 / 行数 / 等级文案。</summary>
    public string GuestScoreText
        => _guestPresent ? $"{_guestName}：{_opponentScore} 分 · {_opponentLines} 行 · {_opponentLevel} 级" : "";

    /// <summary>对手那边的处境提示。</summary>
    public string OpponentGarbageText
    {
        get
        {
            if (!_guestPresent) return "";
            var parts = new List<string>();
            if (_opponentPendingGarbage > 0) parts.Add($"对手即将被塞 {_opponentPendingGarbage} 行垃圾");
            if (_opponentDead) parts.Add("对手已顶死");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>自己这边的垃圾行提示（互攻模式 / 单人互攻时显示）。</summary>
    public string MyGarbageText
    {
        get
        {
            if (!GarbageScoreVisible) return "";
            var parts = new List<string>();
            if (_engine.PendingGarbage > 0) parts.Add($"即将被塞 {_engine.PendingGarbage} 行垃圾");
            if (_engine.GarbageSent > 0) parts.Add($"已送出 {_engine.GarbageSent} 行");
            if (SoloGarbageActive && _soloAiEngine is { PendingGarbage: > 0 } ai)
                parts.Add($"电脑欠 {ai.PendingGarbage} 行");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>对手的棋盘（缩略图用）。</summary>
    public TetrisBoardViewState GuestBoardState
    {
        get => _guestBoardState;
        private set => Set(ref _guestBoardState, value);
    }

    /// <summary>联机对局的结果文案（空 = 没结果）。</summary>
    public string ResultText
    {
        get => _resultText;
        private set => Set(ref _resultText, value);
    }

    /// <summary>联机时是不是在等对方（等人加入 / 等人开始）。</summary>
    public bool IsWaitingForOpponent => IsNetMode && !_guestPresent && _netPhase != TetrisNetPhase.Playing;

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand StartHostCommand { get; private set; } = null!;
    public AsyncRelayCommand JoinRoomCommand { get; private set; } = null!;
    public AsyncRelayCommand JoinSpectatorCommand { get; private set; } = null!;
    public AsyncRelayCommand LeaveRoomCommand { get; private set; } = null!;
    public RelayCommand CopyTextCommand { get; private set; } = null!;

    /// <summary>主构造函数里调用：建联机命令（两个构造函数共用一套）。</summary>
    private void InitializeNetCommands()
    {
        StartHostCommand = new AsyncRelayCommand(_ => StartHostAsync(), _ => CanJoinNet);
        JoinRoomCommand = new AsyncRelayCommand(_ => JoinRoomAsync(), _ => CanJoinNet);
        JoinSpectatorCommand = new AsyncRelayCommand(_ => JoinAsSpectatorAsync(), _ => CanJoinNet);
        LeaveRoomCommand = new AsyncRelayCommand(_ => LeaveRoomAsync(), _ => CanLeaveNet);
        CopyTextCommand = new RelayCommand(parameter =>
        {
            if (parameter is not string text || text.Length == 0) return;
            try
            {
                Clipboard.SetText(text);
                StatusText = $"已复制：{text}";
            }
            catch
            {
                StatusText = "复制失败（剪贴板被别的程序占用了）";
            }
        });

        _netMode = TetrisNetProtocol.ParseMode(Cfg.LastNetMode);
    }

    /// <summary>
    /// 构造末尾调用（<b>必须在 <c>StartNewGame</c> 之后</b>）：按环境变量把联机演示摆出来。
    /// <para>⚠ 放在 <c>InitializeNetCommands</c> 里会被随后的 <c>StartNewGame</c> 冲掉
    /// （表现为演示分数 / 棋盘全是初始值），所以单独一步、且调用点要在最后。</para>
    /// </summary>
    private void ApplyDemoNetMode()
    {
        var demo = (Environment.GetEnvironmentVariable("YLT_TETRIS_NET") ?? "").Trim().ToLowerInvariant();
        if (demo.Length == 0) return;

        SwitchMode(demo.StartsWith("score", StringComparison.Ordinal) ? TetrisPlayMode.NetScore : TetrisPlayMode.NetGarbage);
        if (demo is "room" or "score-room") SeedDemoRoom();
        if (demo is "spectate") SeedDemoSpectator();
    }

    /// <summary>
    /// 截图 / 演示：假装自己是观战者，正在看房主与另一位玩家对战（<b>不联网</b>）。
    /// </summary>
    private void SeedDemoSpectator()
    {
        _demoRoom = true;
        _netRole = TetrisMemberRole.Spectator;
        _guestPresent = true;
        _netPhase = TetrisNetPhase.Playing;
        _started = true;
        IsPaused = false;

        // 大棋盘 = 「房主」的棋盘（观战时左下棋盘显示房主）
        SeedDemoBoardFor(_engine, 3);
        RefreshBoard();
        RefreshScore();

        // 对手区 = 另一位对战玩家
        _remoteEngine = new TetrisEngine();
        SeedDemoBoardFor(_remoteEngine, 5);
        _guestName = "好友";
        SyncOpponentFromDemoEngine();

        SpectatorNames.Clear();
        SpectatorNames.Add("小明");
        SpectatorNames.Add("小红");
        OnPropertyChanged(nameof(SpectatorCountText));
        OnPropertyChanged(nameof(HasSpectators));

        SetNetNotice("（演示）观战视角：正在看 房主 vs 好友", "SuccessBrush");
        StatusText = "观战中（演示）";
        NotifyOverlay();
        NotifyNetState();
    }

    /// <summary>
    /// 给演示用的引擎铺一个「玩到一半」的棋盘：只动棋盘格子（<see cref="TetrisEngine.SetCell"/>），
    /// <b>不推进游戏状态</b>（不落地、不判输），所以永远不会出现「游戏结束」。
    /// </summary>
    private static void SeedDemoBoardFor(TetrisEngine engine, int seed)
    {
        engine.Reset(1, seed);

        // 底部两行铺一段（看起来像刚消过几行），留几个缺口
        for (var y = TetrisEngine.Height - 2; y < TetrisEngine.Height; y++)
        {
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                if ((x + y + seed) % 4 == 0) continue;
                engine.SetCell(x, y, TetrisPieces.All[(x + y + seed) % 7]);
            }
        }
    }

    /// <summary>
    /// 截图 / 演示：假装房间里已经有个对手（一段假的对局进度），<b>不联网、不影响真实房间</b>。
    /// 只在设了 <c>YLT_TETRIS_NET</c> 时调用。
    /// </summary>
    private void SeedDemoRoom()
    {
        _demoRoom = true;
        _guestPresent = true;
        _guestName = "好友";
        _netPhase = TetrisNetPhase.Playing;
        _started = true;

        _remoteEngine = new TetrisEngine();
        _remoteEngine.Reset(Cfg.StartLevel, 12345);
        for (var y = TetrisEngine.Height - 4; y < TetrisEngine.Height; y++)
        {
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                if ((x + y) % 6 == 0) continue;
                _remoteEngine.SetCell(x, y, TetrisPieces.All[(x + y) % 7]);
            }
        }
        _remoteEngine.QueueGarbageForOpponent(4);
        _remoteEngine.EnqueueGarbage(1);

        // 让对手「真的玩过几手」：分数 / 行数 / 等级都是引擎实算出来的（不是硬写数字）
        var guard = 0;
        while (_remoteEngine.Score < 1500 && guard++ < 60)
        {
            _remoteEngine.MoveLeft();
            _remoteEngine.HardDrop();
            if (_remoteEngine.IsGameOver) break;
        }

        SyncOpponentFromDemoEngine();

        _engine.EnqueueGarbage(2);
        SetNetNotice("（演示房间）好友已加入，可以开始了", "SuccessBrush");
        NotifyNetState();
    }

    private void SyncOpponentFromDemoEngine()
    {
        if (_remoteEngine is not { } engine) return;
        _opponentScore = engine.Score;
        _opponentLines = engine.Lines;
        _opponentLevel = engine.Level;
        _opponentPendingGarbage = engine.PendingGarbage;
        GuestBoardState = TetrisBoardViewState.FromSnapshot(engine.Snapshot());
    }

    // ---------------------------------------------------------------- 模式切换

    /// <summary>切换玩法：切走时先把联机断掉（不然关掉页面后对方还连着）。</summary>
    public void SwitchMode(TetrisPlayMode mode)
    {
        if (mode == _playMode) return;

        if (_session is not null) _ = LeaveRoomAsync("切换玩法，已离开房间");

        PlayMode = mode;
        _netMode = mode == TetrisPlayMode.NetScore ? TetrisNetMode.Score : TetrisNetMode.Garbage;
        Cfg.LastNetMode = TetrisNetProtocol.ModeToString(_netMode);

        _netNotice = mode == TetrisPlayMode.Solo
            ? "单人玩法：自己玩自己的"
            : "开一个房间等好友连进来，或者填对方发来的地址加入";
        _netNoticeBrushKey = "TextSecondaryBrush";
        ResetOpponent();
        NewGame();
        NotifyNetState();
    }

    // ---------------------------------------------------------------- 开房 / 加入 / 离开

    private async Task StartHostAsync()
    {
        _netBusy = true;
        NotifyNetState();
        try
        {
            var session = new TetrisNetSession(Cfg.EffectivePlayerName);
            WireSession(session);
            var error = await session.StartHostAsync(Cfg.ListenPort);
            if (error.Length > 0)
            {
                await session.DisposeAsync();
                SetNetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _remoteEngine = new TetrisEngine();
            RefreshShareAddresses();
            SetNetNotice($"房间已开在端口 {session.ListenPort}，把下面的地址发给好友", "SuccessBrush");
            Log($"开房间成功：{session.ListenText}");
            _netPhase = TetrisNetPhase.Waiting;
            NotifyNetState();
        }
        finally
        {
            _netBusy = false;
            NotifyNetState();
        }
    }

    private async Task JoinRoomAsync()
    {
        var address = (JoinAddress ?? "").Trim();
        if (address.Length == 0)
        {
            SetNetNotice("请先填对方发来的地址（例如 192.168.1.5:5310）", "DangerBrush");
            return;
        }

        _netBusy = true;
        NotifyNetState();
        try
        {
            var session = new TetrisNetSession(Cfg.EffectivePlayerName);
            WireSession(session);
            var error = await session.ConnectAsync(address, TetrisNetProtocol.ModeToString(_netMode));
            if (error.Length > 0)
            {
                await session.DisposeAsync();
                SetNetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _netRole = session.LocalRole;
            Cfg.LastJoinAddress = address;

            if (_netRole == TetrisMemberRole.Spectator)
            {
                SetNetNotice($"已连上房主 {session.HostName}，对战位已经有人了：你现在是**观战**身份", "SuccessBrush", log: true);
                _netPhase = TetrisNetPhase.Playing;
            }
            else
            {
                SetNetNotice($"已连上房主 {session.HostName}，等对方开始对局", "SuccessBrush");
                _netPhase = TetrisNetPhase.Ready;
            }

            Log($"已加入房间：{address}（{TetrisNetProtocol.RoleDisplayName(_netRole)}）");
            NotifyNetState();
        }
        finally
        {
            _netBusy = false;
            NotifyNetState();
        }
    }

    /// <summary>以观战身份加入（对战位有人时也能看）。</summary>
    private async Task JoinAsSpectatorAsync()
    {
        var address = (JoinAddress ?? "").Trim();
        if (address.Length == 0)
        {
            SetNetNotice("请先填对方发来的地址（例如 192.168.1.5:5310）", "DangerBrush");
            return;
        }

        _netBusy = true;
        NotifyNetState();
        try
        {
            var session = new TetrisNetSession(Cfg.EffectivePlayerName);
            WireSession(session);
            var error = await session.ConnectAsync(address, TetrisNetProtocol.ModeToString(_netMode), asSpectator: true);
            if (error.Length > 0)
            {
                await session.DisposeAsync();
                SetNetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _netRole = TetrisMemberRole.Spectator;
            Cfg.LastJoinAddress = address;
            _netPhase = TetrisNetPhase.Playing;
            SetNetNotice($"已以观战身份连上房主 {session.HostName}", "SuccessBrush", log: true);
            Log($"加入观战：{address}");
            NotifyNetState();
        }
        finally
        {
            _netBusy = false;
            NotifyNetState();
        }
    }

    private async Task LeaveRoomAsync(string reason = "主动离开房间")
    {
        var session = _session;
        _session = null;
        _remoteEngine = null;
        if (session is not null)
        {
            try { await session.StopAsync(reason); } catch { /* 忽略 */ }
            try { await session.DisposeAsync(); } catch { /* 忽略 */ }
        }

        ResetOpponent();
        ShareAddresses.Clear();
        _netPhase = TetrisNetPhase.Waiting;
        ResultText = "";
        SetNetNotice(IsNetMode ? "已离开房间" : "", "TextSecondaryBrush");
        if (IsNetMode) NewGame();
        NotifyNetState();
    }

    // ---------------------------------------------------------------- 事件接线（后台线程 → UI）

    private void WireSession(TetrisNetSession session)
    {
        session.StateChanged += () => Ui(NotifyNetState);
        session.GuestJoined += guest => Ui(() => OnGuestJoined(guest));
        session.GuestLeft += (guest, reason) => Ui(() => OnGuestLeft(guest, reason));
        session.SpectatorJoined += spectator => Ui(() => OnSpectatorJoined(spectator));
        session.SpectatorLeft += (spectator, reason) => Ui(() => OnSpectatorLeft(spectator, reason));
        session.ActionReceived += action => Ui(() => OnRemoteAction(action));
        session.StateReceived += state => Ui(() => OnRemoteState(state));
        session.NoticeReceived += text => Ui(() => SetNetNotice(text, "TextSecondaryBrush", log: true));
        session.Disconnected += reason => Ui(() => OnDisconnected(reason));
    }

    private void OnSpectatorJoined(TetrisNetSpectator spectator)
    {
        if (!SpectatorNames.Contains(spectator.Name)) SpectatorNames.Add(spectator.Name);
        OnPropertyChanged(nameof(SpectatorCountText));
        OnPropertyChanged(nameof(HasSpectators));
        SetNetNotice($"{spectator.Name} 进来观战了", "TextSecondaryBrush", log: true);
        PublishNetState();
    }

    private void OnSpectatorLeft(TetrisNetSpectator spectator, string reason)
    {
        _ = reason;
        SpectatorNames.Remove(spectator.Name);
        OnPropertyChanged(nameof(SpectatorCountText));
        OnPropertyChanged(nameof(HasSpectators));
        PublishNetState();
    }

    private void OnGuestJoined(TetrisNetPlayer guest)
    {
        _guestPresent = true;
        _guestName = guest.Name;
        ResultText = "";
        SetNetNotice($"{guest.Name} 加入了房间，可以开始了", "SuccessBrush", log: true);

        // 房主：人齐了就直接摆好一局（各自计分模式下两边用同一个种子，出块序列一致）
        StartNetGameAsHost();
        NotifyNetState();
    }

    /// <summary>
    /// 房主：对战玩家离开 / 掉线。
    /// <para><b>规则</b>：对局中掉线 = 判负（本端赢）；没在对局中就是普通离开。</para>
    /// </summary>
    private void OnGuestLeft(TetrisNetPlayer guest, string reason)
    {
        var wasPlaying = _netPhase == TetrisNetPhase.Playing;

        _guestPresent = false;
        _guestName = "";
        ResetOpponent();

        if (!IsNetMode) { NotifyNetState(); return; }

        if (wasPlaying)
        {
            // 掉线判负：对面判负、本端判胜（对手的棋盘已经没了，界面上只留结果文字）
            _netPhase = TetrisNetPhase.Over;
            _started = false;
            UpdateTimer();
            _opponentDead = true;
            _guestName = guest.Name;
            _resultSide = "host";
            ResultText = $"{guest.Name} 掉线了，本局判对方负：{Cfg.EffectivePlayerName} 赢了！";
            StatusText = ResultText;
            SetNetNotice(ResultText, "SuccessBrush", log: true);
            Log($"对手掉线（{reason}），本局判胜");
            RefreshBoard();
            RefreshScore();
            NotifyOverlay();
            PublishNetState();
        }
        else
        {
            _netPhase = TetrisNetPhase.Waiting;
            _started = false;
            UpdateTimer();
            ResultText = "";
            SetNetNotice($"{guest.Name} 离开了房间（{reason}）", "DangerBrush", log: true);
        }

        NotifyNetState();
    }

    private void OnDisconnected(string reason)
    {
        // 客户端：连接断了 = 本端掉线判负（对局中）；没在对局中就是普通断开
        var wasPlaying = _netPhase == TetrisNetPhase.Playing && !IsSpectating;

        ResetOpponent();
        _session = null;
        ShareAddresses.Clear();
        _netPhase = TetrisNetPhase.Waiting;
        _started = false;
        UpdateTimer();

        if (wasPlaying)
        {
            _resultSide = "host";
            ResultText = $"与房主的连接断开了（{reason}），本局判你负";
            StatusText = ResultText;
            SetNetNotice(ResultText, "DangerBrush", log: true);
            NotifyOverlay();
        }
        else
        {
            SetNetNotice($"与房主的连接断开了：{reason}", "DangerBrush", log: true);
        }

        NotifyNetState();
    }

    // ---------------------------------------------------------------- 房主：驱动两份引擎

    /// <summary>房主收到客户端的按键：在「客户端那份引擎」上执行，再广播。</summary>
    private void OnRemoteAction(string action)
    {
        if (_netPhase != TetrisNetPhase.Playing || _remoteEngine is not { } engine || engine.IsGameOver) return;

        var lockResult = ApplyAction(engine, action);
        if (lockResult is { } result) AfterRemoteLock(engine, result);
        else PublishNetState();
    }

    /// <summary>在一个引擎上执行一个动作；落地类动作（硬降 / 软降到底）返回结算结果。</summary>
    private static TetrisLockResult? ApplyAction(TetrisEngine engine, string action)
    {
        switch (action)
        {
            case TetrisNetProtocol.Actions.Left: engine.MoveLeft(); return null;
            case TetrisNetProtocol.Actions.Right: engine.MoveRight(); return null;
            case TetrisNetProtocol.Actions.RotateCw: engine.Rotate(true); return null;
            case TetrisNetProtocol.Actions.RotateCcw: engine.Rotate(false); return null;
            case TetrisNetProtocol.Actions.Hold: engine.Hold(); return null;
            case TetrisNetProtocol.Actions.HardDrop: return engine.HardDrop();
            case TetrisNetProtocol.Actions.SoftDrop:
                // 软降到底就锁（和本地手感一致）
                return engine.SoftDrop() ? null : engine.Lock();
            default: return null;
        }
    }

    /// <summary>客户端那一手落地之后：互攻模式把垃圾行转给房主自己，再判定胜负。</summary>
    private void AfterRemoteLock(TetrisEngine engine, TetrisLockResult result)
    {
        if (IsGarbageMode && result.LinesCleared >= 2)
            _engine.EnqueueGarbage(TetrisEngine.GarbageRowsFor(result.LinesCleared));

        SyncOpponentFromRemoteEngine();
        if (engine.IsGameOver) FinishNetGame();
        else PublishNetState();
    }

    /// <summary>房主自己那一手落地之后（联机）：互攻模式把垃圾行转给客户端，再判定胜负。</summary>
    private void AfterLockForNet(TetrisLockResult result)
    {
        if (IsGarbageMode && result.LinesCleared >= 2 && _remoteEngine is { } remote)
            remote.EnqueueGarbage(TetrisEngine.GarbageRowsFor(result.LinesCleared));

        RefreshBoard();
        RefreshScore();
        OnPropertyChanged(nameof(HeldPieces));
        OnPropertyChanged(nameof(MyGarbageText));

        if (_engine.IsGameOver) FinishNetGame();
        else PublishNetState();
    }

    /// <summary>房主：把「客户端那份引擎」的状态同步到界面用的对手数据。</summary>
    private void SyncOpponentFromRemoteEngine()
    {
        if (_remoteEngine is not { } engine) return;
        _opponentScore = engine.Score;
        _opponentLines = engine.Lines;
        _opponentLevel = engine.Level;
        _opponentPendingGarbage = engine.PendingGarbage;
        _opponentDead = engine.IsGameOver;
        GuestBoardState = TetrisBoardViewState.FromSnapshot(engine.Snapshot());
        OnPropertyChanged(nameof(GuestScoreText));
        OnPropertyChanged(nameof(OpponentGarbageText));
    }

    // ---------------------------------------------------------------- 客户端：收状态

    /// <summary>客户端收到房主的状态：本端棋盘与对手数据都按快照渲染（不跑规则）。</summary>
    private void OnRemoteState(TetrisNetState state)
    {
        if (_session is null || _session.IsHost) return;
        if (state.Seq <= _lastStateSeq) return;      // 乱序旧包直接丢
        _lastStateSeq = state.Seq;

        _netPhase = state.Phase;
        _netMode = TetrisNetProtocol.ParseMode(state.Mode);

        // 客户端角色下：状态里的 Guest 就是本端，Host 是对手
        // 观战者：自己没有引擎角色，两边的棋盘都按快照画
        if (IsSpectating)
        {
            if (state.Host?.Board is { } hostBoard)
            {
                _engine.ApplySnapshot(hostBoard);        // 房主棋盘 → 左下大棋盘
                RefreshBoard();
                RefreshScore();
            }
            if (state.Guest is { } guestPlayer)
            {
                _guestName = guestPlayer.Name;
                _opponentScore = guestPlayer.Score;
                _opponentLines = guestPlayer.Lines;
                _opponentLevel = guestPlayer.Level;
                _opponentPendingGarbage = guestPlayer.PendingGarbage;
                _opponentDead = guestPlayer.IsDead;
                GuestBoardState = guestPlayer.Board is { } gb
                    ? TetrisBoardViewState.FromSnapshot(gb)
                    : TetrisBoardViewState.Empty;
                _guestPresent = true;
            }
        }
        else
        {
            if (state.Guest?.Board is { } myBoard)
            {
                _engine.ApplySnapshot(myBoard);
                RefreshBoard();
                RefreshScore();
            }

            if (state.Host is { } host)
            {
                _guestName = host.Name.Length > 0 ? host.Name : _guestName;
                _opponentScore = host.Score;
                _opponentLines = host.Lines;
                _opponentLevel = host.Level;
                _opponentPendingGarbage = host.PendingGarbage;
                _opponentDead = host.IsDead;
                GuestBoardState = host.Board is { } board ? TetrisBoardViewState.FromSnapshot(board) : TetrisBoardViewState.Empty;
                if (_guestName.Length > 0) _guestPresent = true;
            }
        }

        // 观战者名单（房主也会广播，观战者据此看到「还有谁在看」）
        SpectatorNames.Clear();
        foreach (var spectator in state.Spectators)
        {
            if (!string.IsNullOrWhiteSpace(spectator.Name)) SpectatorNames.Add(spectator.Name);
        }
        OnPropertyChanged(nameof(SpectatorCountText));
        OnPropertyChanged(nameof(HasSpectators));

        ResultText = state.ResultText;
        OnPropertyChanged(nameof(GuestScoreText));
        OnPropertyChanged(nameof(OpponentGarbageText));
        OnPropertyChanged(nameof(MyGarbageText));

        if (state.Phase == TetrisNetPhase.Playing)
        {
            if (!_started) BeginFalling();
            UpdateTimer();
        }
        else if (state.Phase is TetrisNetPhase.Over)
        {
            _started = false;
            UpdateTimer();
            if (ResultText.Length > 0) StatusText = ResultText;
        }

        NotifyNetState();
    }

    // ---------------------------------------------------------------- 房主：开对局 / 判定 / 广播

    /// <summary>房主开始一局联机对战：两份引擎用同一个种子，「各自计分」才比得公平。</summary>
    private void StartNetGameAsHost()
    {
        var seed = Environment.TickCount;
        _engine.Reset(Cfg.StartLevel, seed);
        _remoteEngine ??= new TetrisEngine();
        _remoteEngine.Reset(Cfg.StartLevel, seed);

        _started = true;
        IsPaused = false;
        _resultSide = "";
        ResultText = "";
        _netPhase = TetrisNetPhase.Playing;

        RefreshBoard();
        RefreshScore();
        SyncOpponentFromRemoteEngine();
        BeginFalling();
        PublishNetState();
    }

    /// <summary>房主：判定胜负并广播结果。</summary>
    private void FinishNetGame()
    {
        _netPhase = TetrisNetPhase.Over;
        _started = false;
        UpdateTimer();

        var hostDead = _engine.IsGameOver;
        var guestDead = _remoteEngine?.IsGameOver == true;
        var guestScore = _remoteEngine?.Score ?? 0;

        var side = TetrisNetJudge.Decide(hostDead, guestDead, _engine.Score, guestScore);
        _resultSide = side;
        ResultText = side.Length > 0
            ? TetrisNetJudge.Describe(side, Cfg.EffectivePlayerName, _guestName.Length > 0 ? _guestName : "对手")
            : "对局结束";

        StatusText = ResultText;
        RefreshBoard();
        RefreshScore();
        SyncOpponentFromRemoteEngine();
        NotifyOverlay();
        Log($"联机对局结束：{ResultText}");
        PublishNetState();
    }

    /// <summary>房主：把当前状态推给客户端。</summary>
    private void PublishNetState()
    {
        var session = _session;
        if (session is null || !session.IsHost) return;

        _ = session.BroadcastStateAsync(BuildNetState());
    }

    private TetrisNetState BuildNetState() => new()
    {
        Seq = ++_netSeq,
        Phase = _netPhase,
        Mode = TetrisNetProtocol.ModeToString(_netMode),
        HostName = Cfg.EffectivePlayerName,
        Host = new TetrisNetPlayer
        {
            MemberId = TetrisNetProtocol.HostMemberId,
            Name = Cfg.EffectivePlayerName,
            IsHost = true,
            Score = _engine.Score,
            Lines = _engine.Lines,
            Level = _engine.Level,
            IsDead = _engine.IsGameOver,
            PendingGarbage = _engine.PendingGarbage,
            GarbageSent = _engine.GarbageSent,
            GarbageTaken = _engine.GarbageTaken,
            Board = _engine.Snapshot(),
        },
        Guest = _guestPresent && _remoteEngine is { } remote
            ? new TetrisNetPlayer
            {
                MemberId = "m1",
                Name = _guestName,
                IsHost = false,
                Score = remote.Score,
                Lines = remote.Lines,
                Level = remote.Level,
                IsDead = remote.IsGameOver,
                PendingGarbage = remote.PendingGarbage,
                GarbageSent = remote.GarbageSent,
                GarbageTaken = remote.GarbageTaken,
                Board = remote.Snapshot(),
            }
            : null,
        Spectators = _session?.Spectators.Select(s => s.Clone()).ToList() ?? new List<TetrisNetSpectator>(),
        ResultText = ResultText,
        ResultSide = _resultSide,
    };

    // ---------------------------------------------------------------- 小工具

    private void SetNetNotice(string text, string brushKey, bool log = false)
    {
        _netNotice = text;
        _netNoticeBrushKey = brushKey;
        OnPropertyChanged(nameof(NetNoticeText));
        OnPropertyChanged(nameof(NetNoticeBrushKey));
        if (log) Log(text);
    }

    private void ResetOpponent()
    {
        _guestPresent = false;
        _guestName = "";
        _opponentScore = 0;
        _opponentLines = 0;
        _opponentLevel = 1;
        _opponentPendingGarbage = 0;
        _opponentDead = false;
        GuestBoardState = TetrisBoardViewState.Empty;
        OnPropertyChanged(nameof(GuestScoreText));
        OnPropertyChanged(nameof(OpponentGarbageText));
        OnPropertyChanged(nameof(OpponentPanelVisible));
    }

    private void RefreshShareAddresses()
    {
        ShareAddresses.Clear();
        if (_session is not { IsHost: true } session) return;

        foreach (var address in session.ShareAddresses.Take(3))
        {
            ShareAddresses.Add(new TetrisShareAddress(
                RoomProtocol.WithPort(address.Text, session.ListenPort),
                $"{address.Family} · {address.Interface}",
                address.IsRecommended));
        }

        OnPropertyChanged(nameof(HasShareAddresses));
    }

    /// <summary>状态变了：把徽标 / 按钮 / 对手区一次性通知到位。</summary>
    private void NotifyNetState()
    {
        NotifyDuelState();
        OnPropertyChanged(nameof(NetBadgeText));
        OnPropertyChanged(nameof(NetBadgeBrushKey));
        OnPropertyChanged(nameof(NetBusy));
        OnPropertyChanged(nameof(CanJoinNet));
        OnPropertyChanged(nameof(CanLeaveNet));
        OnPropertyChanged(nameof(LeaveNetText));
        OnPropertyChanged(nameof(OpponentPanelVisible));
        OnPropertyChanged(nameof(IsWaitingForOpponent));
        OnPropertyChanged(nameof(IsSpectating));
        OnPropertyChanged(nameof(SpectatorCountText));
        OnPropertyChanged(nameof(HasSpectators));
        OnPropertyChanged(nameof(GuestScoreText));
        OnPropertyChanged(nameof(OpponentGarbageText));
        OnPropertyChanged(nameof(MyGarbageText));
        OnPropertyChanged(nameof(HasShareAddresses));
        OnPropertyChanged(nameof(GarbageScoreVisible));
        OnPropertyChanged(nameof(OverlayVisible));
        OnPropertyChanged(nameof(OverlayTitle));
        OnPropertyChanged(nameof(OverlayHint));
        OnPropertyChanged(nameof(NetRoleText));
        OnPropertyChanged(nameof(OpponentTitleText));
    }

    /// <summary>本端在房间里的身份文案。</summary>
    public string NetRoleText => !IsNetMode ? ""
        : _netRole switch
        {
            TetrisMemberRole.Spectator => "观战中（只能看，不能操作）",
            TetrisMemberRole.Host => "房主（对战）",
            _ => "对战",
        };

    /// <summary>对手区标题（观战时是「对战玩家」，对战时是「对手」）。</summary>
    public string OpponentTitleText => IsSpectating ? "对战玩家" : "对手";

    /// <summary>联机对局里不能「重新开始」（要两个人同时开始，由房主决定）。</summary>
    public bool CanRestartGame => !IsNetMode || _session is { IsHost: true } && _guestPresent;
}

/// <summary>房间卡上一条「可以复制给对方的地址」。</summary>
/// <param name="AddressText">完整地址（带端口）。</param>
/// <param name="DetailText">补充说明（IPv4 / 网卡名）。</param>
/// <param name="IsRecommended">是不是最推荐的那一条。</param>
public sealed record TetrisShareAddress(string AddressText, string DetailText, bool IsRecommended);
