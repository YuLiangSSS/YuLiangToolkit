using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;
using YuLiangToolkit.Tools.BoardGames.Gomoku.Views;
using YuLiangToolkit.Tools.BoardGames.Shared;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Gomoku.ViewModels;

/// <summary>
/// 「联机五子棋」的 ViewModel：<b>棋局规则之外的界面与联机编排</b>都在这里。
///
/// <para>继承 <see cref="BoardGameRoomViewModelBase"/>，房间（开房 / 加入 / 同机双人 / 地址分享 / 聊天 /
/// 悔棋·求和·再来一局·认输的请求应答）由基类处理；本类只负责五子棋自己的部分：</para>
/// <list type="bullet">
///   <item>棋局状态（<see cref="Game"/>）与棋盘渲染状态（<see cref="BoardState"/>）；</item>
///   <item>落子（同机双人直接在本地下；主机下本地并广播；客户端发 <c>move</c> 动作等房主确认）；</item>
///   <item>禁手点（后台计算后回 UI 线程更新）、棋谱、最后一手 / 获胜五连高亮；</item>
///   <item>设置项（禁手 / 显不显示禁手点 / 棋盘路数 / 房主执子）与「开始对局」。</item>
/// </list>
///
/// <para><b>线程</b>：基类钩子（<c>Apply*</c> / <c>OnHostAction</c> 等）都在 UI 线程被调用；
/// 只有禁手点是 <c>Task.Run</c> 算完再 <c>Ui(...)</c> 回来。</para>
/// </summary>
public sealed class GomokuViewModel : BoardGameRoomViewModelBase
{
    private GomokuGame _game = new();                 // 基类在构造里可能问状态，先给一局空的占位
    private GomokuBoardViewState _boardState = new();
    private bool _clientMovePending;
    private int _refreshVersion;
    private bool _insideRefresh;

    // 二次确认（防手滑）：认输 / 离开房间都要点两下，3 秒内不点第二下自动取消。
    private bool _resignArmed;
    private int _resignArmVersion;
    private bool _leaveArmed;
    private int _leaveArmVersion;

    /// <summary>创建五子棋 ViewModel（会加载 <c>Config\gomoku\config.json</c>）。</summary>
    public GomokuViewModel() : base(GomokuConfig.ToolId, "五子棋")
    {
        Cfg = LoadConfig<GomokuConfig>();
        AttachRoomConfig(Cfg);
        _game = new GomokuGame(Cfg.EffectiveBoardSize, Cfg.ForbiddenEnabled) { Started = false };

        BoardSizeOptions = OptionItem.From((15, "15 路（标准）"), (19, "19 路"));
        HostSideOptions = OptionItem.From(("black", "房主默认坐黑（先手位）"), ("white", "房主默认坐白（后手位）"));

        PlaceCommand = new RelayCommand(p =>
        {
            if (p is GomokuPoint point) PlaceAt(point);
        });
        UndoCommand = new RelayCommand(_ => UndoOne(), _ => CanUndo);
        ResignCommand = new RelayCommand(_ => OnResignClicked(), _ => CanResign);
        RematchCommand = new RelayCommand(_ => Rematch(), _ => CanRematch);
        OfferDrawCommand = new RelayCommand(_ => DrawCurrent(), _ => CanOfferDraw);
        CopyMovesCommand = new RelayCommand(_ => CopyMoves());
        NewLocalGameCommand = new RelayCommand(_ => NewLocalGame());
        SafeLeaveRoomCommand = new RelayCommand(_ => OnLeaveRoomClicked(), _ => CanLeaveRoom);

        // 房间状态（IsOnline / IsSeatedPlayer / IsGamePaused / IsReconnecting）一变，本类的派生状态也跟着刷新，
        // 不然「能不能落子 / 按钮禁用 / 提示文案」会慢半拍。
        PropertyChanged += OnRoomStatePropertyChanged;

        RefreshAll();
        LoadDemoIfRequested();
    }

    private void OnRoomStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_insideRefresh) return;
        if (e.PropertyName is nameof(IsReconnecting) or nameof(IsGamePaused) or nameof(IsOnline)
            or nameof(Mode) or nameof(IsSeatedPlayer) or nameof(IsSpectator) or nameof(LocalSide)
            or nameof(IsGameRunning) or nameof(CanLeaveRoom) or nameof(ShowPlayerActions))
        {
            RefreshAll();
        }
    }

    // ---------------------------------------------------------------- 配置 / 设置项

    /// <summary>本功能的持久化配置。</summary>
    public GomokuConfig Cfg { get; }

    /// <summary>棋盘路数选项（15 / 19）。</summary>
    public IReadOnlyList<OptionItem<int>> BoardSizeOptions { get; }

    /// <summary>房主执子选项（黑 / 白）。</summary>
    public IReadOnlyList<OptionItem<string>> HostSideOptions { get; }

    /// <summary>棋盘路数下拉框的选中项。</summary>
    public OptionItem<int> SelectedBoardSize
    {
        get => OptionItem<int>.Find(BoardSizeOptions, Cfg.EffectiveBoardSize) ?? BoardSizeOptions[0];
        set
        {
            if (value is null) return;
            var size = value.Value == 19 ? 19 : 15;
            if (Cfg.EffectiveBoardSize == size)
            {
                OnPropertyChanged();
                return;
            }

            Cfg.BoardSize = size;
            OnPropertyChanged();
            OnRuleSettingChanged("棋盘大小");
        }
    }

    /// <summary>「房主默认坐席」下拉框的选中项（房间开着的时候不让改，开房瞬间决定房主自动坐哪一席）。</summary>
    public OptionItem<string> SelectedHostSide
    {
        get => OptionItem<string>.Find(HostSideOptions, Cfg.EffectiveHostSide) ?? HostSideOptions[0];
        set
        {
            if (value is null) return;
            var side = string.Equals(value.Value, "white", StringComparison.OrdinalIgnoreCase) ? "white" : "black";
            if (Cfg.EffectiveHostSide == side)
            {
                OnPropertyChanged();
                return;
            }

            if (IsOnline)
            {
                SetNotice("房间开着时不能改房主默认坐席（先离开房间再改）", "DangerBrush");
                OnPropertyChanged();
                return;
            }

            Cfg.HostSide = side;
            OnPropertyChanged();
        }
    }

    /// <summary>禁手开关：开着时黑棋三三 / 四四 / 长连判负，黑棋正好五连仍算赢。</summary>
    public bool ForbiddenEnabled
    {
        get => Cfg.ForbiddenEnabled;
        set
        {
            if (Cfg.ForbiddenEnabled == value) return;
            Cfg.ForbiddenEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ForbiddenSummary));
            OnRuleSettingChanged("禁手设置");
        }
    }

    /// <summary>要不要在棋盘上标出黑棋禁手点。</summary>
    public bool ShowForbiddenPoints
    {
        get => Cfg.ShowForbiddenPoints;
        set
        {
            if (Cfg.ShowForbiddenPoints == value) return;
            Cfg.ShowForbiddenPoints = value;
            OnPropertyChanged();
            RefreshAll();
        }
    }

    /// <summary>要不要显示右下的棋谱卡片。</summary>
    public bool ShowMoveList
    {
        get => Cfg.ShowMoveList;
        set
        {
            if (Cfg.ShowMoveList == value) return;
            Cfg.ShowMoveList = value;
            OnPropertyChanged();
        }
    }

    /// <summary>当前禁手规则的一行说明。</summary>
    public string ForbiddenSummary => Cfg.ForbiddenSummary;

    /// <summary>设置卡可编辑：离线 / 同机双人 / 演示房间可改；联机只有房主在「对局没进行中」时可改（规则由房主定）。</summary>
    public bool IsSetupAllowed => !IsOnline || IsDemoRoom || (IsHost && !IsGameRunning);

    /// <summary>设置改了之后要不要立刻重建棋盘（没开始的对局直接按新设置摆好）。</summary>
    private void OnRuleSettingChanged(string what)
    {
        OnPropertyChanged(nameof(ForbiddenSummary));
        if (_game.Started)
        {
            SetNotice($"{what}已保存，下一局生效", "TextSecondaryBrush");
            return;
        }

        ResetGameForCurrentSetup(started: false);
    }

    private void ResetGameForCurrentSetup(bool started)
    {
        _game = new GomokuGame(Cfg.EffectiveBoardSize, Cfg.ForbiddenEnabled) { Started = started };
        _clientMovePending = false;
        RefreshAll();
    }

    // ---------------------------------------------------------------- 棋局 / 渲染状态

    /// <summary>当前棋局（规则、轮次、胜负、棋谱都在里面）。</summary>
    public GomokuGame Game => _game;

    /// <summary>给 <see cref="GomokuBoardView"/> 的不可变渲染状态（每次棋局变化整体替换）。</summary>
    public GomokuBoardViewState BoardState
    {
        get => _boardState;
        private set => Set(ref _boardState, value);
    }

    /// <summary>当前轮次文案："轮到黑棋" / "对局已结束" / "等待开始对局"。</summary>
    public string TurnText => !_game.Started
        ? "等待开始对局"
        : _game.IsOver
            ? "对局已结束"
            : $"轮到{GomokuText.SideName(_game.Turn)}";

    /// <summary>结果文案："黑棋获胜" / "白棋获胜" / "和棋" / "对局进行中"。</summary>
    public string ResultText => _game.ResultText;

    /// <summary>结果 + 原因（对局卡片显示用）：禁手判负会说清是哪个点、黑棋三三/四四/长连。</summary>
    public string ResultDetailText
    {
        get
        {
            if (!_game.IsOver) return _game.ResultText;
            if (_game.LastForbiddenPoint is { } point)
            {
                return $"{_game.ResultText}（黑棋禁手判负：{GomokuText.Coordinate(point.X, point.Y, _game.Size)}）";
            }

            return _game.ResultText;
        }
    }

    /// <summary>手数文案，例如 "12 手"。</summary>
    public string MoveCountText => GomokuText.MoveCountText(_game.MoveCount);

    /// <summary>我方的执子说明（同机双人 / 观战 / 联机坐席都说清楚）。</summary>
    public string MySideText
    {
        get
        {
            if (IsLocalMode) return "同机双人（两人轮流）";
            if (!IsOnline) return "我方：未进房间";
            return IsSeatedPlayer ? $"我方：{SideDisplayName(LocalSide)}" : "我方：观战中";
        }
    }

    /// <summary>对手说明（联机坐席时是对手；观战时说明在看棋）。</summary>
    public string OpponentSideText
    {
        get
        {
            if (IsLocalMode) return "对手：同机轮流（换手）";
            if (!IsOnline) return "对手：未进房间";
            return IsSeatedPlayer ? PeerSideText : "对手：—（你在观战）";
        }
    }

    /// <summary>离线时显示「同机双人新开一局」入口；已经在同机双人里就不用了（有「再来一局」）。</summary>
    public bool ShowNewLocalGameButton => !IsOnline && !IsLocalMode;

    /// <summary>棋盘下面那行提示：现在该谁走 / 为什么不能走 / 在观战。</summary>
    public string BoardHintText
    {
        get
        {
            if (IsReconnecting)
            {
                return (ReconnectStatusText.Length > 0 ? ReconnectStatusText : "连接断开，正在自动重连…")
                       + "（棋盘只读，连上后可以继续下）";
            }

            if (IsGamePaused) return GamePauseStatusText + "（棋盘只读）";

            if (!_game.Started)
            {
                if (IsLocalMode) return "点棋盘落子（黑棋先手）";
                if (!IsOnline) return "对局还没开始：先开房间 / 加入房间 / 同机双人，房主再点「开始对局」";
                return SeatStatusText;
            }

            if (_game.IsOver)
            {
                if (IsLocalMode) return $"本局结束：{ResultDetailText}。点「再来一局」继续";
                if (IsHost) return $"本局结束：{ResultDetailText}。安排下一局后点「开始下一局」";
                if (IsSeatedPlayer) return $"本局结束：{ResultDetailText}。可以点「请求再来一局」";
                return $"本局结束：{ResultDetailText}。等房主开始下一局";
            }

            if (IsLocalMode) return $"同机双人：轮到{GomokuText.SideName(_game.Turn)}，点棋盘落子";
            if (IsSpectator) return "你在观战：可以看棋、聊天，等房主安排上场";
            if (_clientMovePending) return "已落子，等房主确认…";
            return CanPlayNow ? "轮到你了，点棋盘落子" : "等对方落子…";
        }
    }

    /// <summary>棋谱（每行一手，例如 "1. 黑 H8"），给界面列表用。</summary>
    public ObservableCollection<string> MoveLines { get; } = new();

    /// <summary>整份棋谱文本（「复制棋谱」复制的就是它）。</summary>
    public string MovesText
    {
        get
        {
            if (_game.MoveCount == 0) return "";
            var lines = _game.Moves.Select(m => GomokuText.MoveLine(m, _game.Size));
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>棋谱里有没有内容（复制按钮 / 空状态用）。</summary>
    public bool HasMoves => _game.MoveCount > 0;

    /// <summary>现在轮到我 / 同机双人可以落子（观战、对局暂停、重连中都算不能下）。</summary>
    public bool CanPlayNow
    {
        get
        {
            if (!_game.Started || _game.IsOver || IsGamePaused) return false;
            if (IsLocalMode) return true;
            if (!IsOnline || !IsSeatedPlayer) return false;

            var my = GomokuText.ParseSide(LocalSide);
            if (my == GomokuCell.Empty || _game.Turn != my) return false;
            if (IsClient) return !IsReconnecting && !_clientMovePending;
            return IsHost;
        }
    }

    /// <summary>
    /// 棋盘能不能点：对局进行中、本端坐在席位上（同机双人也可以）才可点；
    /// 观战 / 房主不坐席 / 暂停 / 重连中一律只读（真正能否落子仍由 <see cref="CanPlayNow"/> 把关）。
    /// </summary>
    public bool IsBoardInteractive =>
        _game.Started && !_game.IsOver && !IsGamePaused && (IsLocalMode || (IsOnline && IsSeatedPlayer));

    /// <summary>对局是否已经开始。</summary>
    public bool IsStarted => _game.Started;

    /// <summary>对局是否已经结束。</summary>
    public bool IsGameOver => _game.IsOver;

    /// <summary>能不能悔棋（联机需要房主/对手同意；同机双人直接撤）。</summary>
    public bool CanUndo => _game.MoveCount > 0 && !IsGamePaused
                           && (IsLocalMode || (IsOnline && IsSeatedPlayer));

    /// <summary>能不能认输。</summary>
    public bool CanResign => _game.Started && !_game.IsOver && !IsGamePaused
                             && (IsLocalMode || (IsOnline && IsSeatedPlayer));

    /// <summary>能不能求和。</summary>
    public bool CanOfferDraw => CanResign;

    /// <summary>「再来一局」按钮可见：同机双人 或 联机坐席客户端玩家（房主用基类的「开始下一局」）。</summary>
    public bool ShowRematchButton => IsLocalMode || (IsClient && IsSeatedPlayer);

    /// <summary>能不能请求再来一局：同机双人随时可重开；联机客户端玩家只在「本局结束」后可请求房主。</summary>
    public bool CanRematch
    {
        get
        {
            if (IsGamePaused) return false;
            if (IsLocalMode) return _game.Started || _game.MoveCount > 0;
            return IsClient && IsSeatedPlayer && IsOnline && _game.IsOver;
        }
    }

    /// <summary>认输按钮的文案：联机对局中第一次点变成「再点一次确认认输」（防手滑）。</summary>
    public string ResignButtonText => _resignArmed ? "再点一次确认认输" : "认输";

    /// <summary>「离开房间」按钮要不要显示（在线 / 正在重连时显示，重连中 = 停止重连）。</summary>
    public bool ShowLeaveRoomButton => CanLeaveRoom;

    /// <summary>「离开房间」按钮文案：重连中是「停止重连」，第一下点击后是「再点一次确认离开」。</summary>
    public string LeaveRoomButtonText => _leaveArmed
        ? "再点一次确认离开"
        : IsReconnecting ? "停止重连" : "离开房间";

    /// <summary>对局进行中离开房间的警告（离开 = 本局中断，对方会看到「对方离开了房间」）。</summary>
    public string LeaveRoomWarning => CanLeaveRoom && _game.Started && !_game.IsOver
        ? "⚠ 对局进行中：离开房间会中断本局（对方会看到你离开）"
        : "";

    // ---------------------------------------------------------------- 命令

    /// <summary>棋盘点击（参数是 <see cref="GomokuPoint"/>）。棋盘控件的 <c>PointClicked</c> 转到这里。</summary>
    public RelayCommand PlaceCommand { get; }

    /// <summary>悔棋（联机需要对方同意；同机双人直接撤最后一步）。</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>认输。</summary>
    public RelayCommand ResignCommand { get; }

    /// <summary>再来一局（联机需要对方同意，同意后双方换边）。</summary>
    public RelayCommand RematchCommand { get; }

    /// <summary>求和。</summary>
    public RelayCommand OfferDrawCommand { get; }

    /// <summary>复制整份棋谱到剪贴板。</summary>
    public RelayCommand CopyMovesCommand { get; }

    /// <summary>同机双人新开一局。</summary>
    public RelayCommand NewLocalGameCommand { get; }

    /// <summary>带二次确认的「离开房间」（对局进行中第一下只变成「再点一次确认离开」，3 秒内有效）。</summary>
    public RelayCommand SafeLeaveRoomCommand { get; }

    // ---------------------------------------------------------------- 落子（界面调用）

    /// <summary>在某个交叉点落子（棋盘控件点击 / 自检可以直接调）。</summary>
    public void PlaceAt(GomokuPoint point)
    {
        if (!_game.Started)
        {
            SetNotice(IsLocalMode ? "点「新开一局」开始下棋" : "对局还没开始：等房主点「开始对局」", "DangerBrush");
            return;
        }

        if (_game.IsOver)
        {
            SetNotice("这一局已经结束了" + (IsLocalMode ? "，点「再来一局」重新开始" : "，等房主开始下一局"), "DangerBrush");
            return;
        }

        if (IsGamePaused)
        {
            SetNotice(GamePauseStatusText + "（现在不能落子）", "DangerBrush");
            return;
        }

        if (IsLocalMode)
        {
            PlayLocally(_game.Turn, point);
            return;
        }

        if (!IsOnline)
        {
            SetNotice("还没进房间：先开房间 / 加入房间 / 同机双人", "DangerBrush");
            return;
        }

        // 观战者（以及没坐席的房主）不能落子：UI 已经隐藏按钮，这里再做一层 VM 拦截。
        if (!IsSeatedPlayer)
        {
            SetNotice("你在观战，不能落子（可以看棋、聊天，等房主安排上场）", "DangerBrush");
            return;
        }

        var my = GomokuText.ParseSide(LocalSide);
        if (my == GomokuCell.Empty)
        {
            SetNotice("还没分配执边，稍等一下", "DangerBrush");
            return;
        }

        if (_game.Turn != my)
        {
            SetNotice("还没轮到你走（等对方落子）", "DangerBrush");
            return;
        }

        if (IsClient)
        {
            if (_clientMovePending)
            {
                SetNotice("上一手还在等房主确认…", "TextSecondaryBrush");
                return;
            }

            // 明显不合法的落子本地先拦掉，别浪费一次网络往返。
            if (!_game.Board.InBounds(point.X, point.Y) || !_game.Board.IsEmpty(point.X, point.Y))
            {
                SetNotice("这个位置不能落子", "DangerBrush");
                return;
            }

            _clientMovePending = true;
            SendAction("move", new { x = point.X, y = point.Y });
            OnPropertyChanged(nameof(CanPlayNow));
            OnPropertyChanged(nameof(IsBoardInteractive));
            OnPropertyChanged(nameof(BoardHintText));
            SetNotice($"已落子 {GomokuText.Coordinate(point.X, point.Y, _game.Size)}，等房主确认…", "TextSecondaryBrush");
            ScheduleClientPendingTimeout();
            return;
        }

        if (IsHost)
        {
            PlayOnHost(my, point);
        }
    }

    /// <summary>同机双人 / 离线练习：本地落子，不广播。</summary>
    private void PlayLocally(GomokuCell side, GomokuPoint point)
    {
        var error = _game.Play(side, point.X, point.Y);
        if (error is not null)
        {
            SetNotice(error, "DangerBrush");
            return;
        }

        RefreshAll();
        AnnounceAfterPlay(side, point);
    }

    /// <summary>主机：本端落子后广播快照。</summary>
    private void PlayOnHost(GomokuCell side, GomokuPoint point)
    {
        var error = _game.Play(side, point.X, point.Y);
        if (error is not null)
        {
            SetNotice(error, "DangerBrush");
            return;
        }

        RefreshAll();
        if (!_game.IsOver) BroadcastSnapshot();    // 结束的那一手由基类处理时统一广播
        AnnounceAfterPlay(side, point);
    }

    private void AnnounceAfterPlay(GomokuCell side, GomokuPoint point)
    {
        var coordinate = GomokuText.Coordinate(point.X, point.Y, _game.Size);
        if (_game.LastForbiddenPoint is not null)
        {
            SetNotice($"禁手！黑棋落在 {coordinate} 判负", "DangerBrush");
            AppendSystemChat($"禁手：黑棋 {coordinate} 判负，白棋获胜");
            return;
        }

        if (_game.IsOver)
        {
            SetNotice($"对局结束：{_game.ResultText}", "SuccessBrush");
            AppendSystemChat($"对局结束：{_game.ResultText}");
            return;
        }

        SetNotice($"{GomokuText.SideName(side)}落在 {coordinate}，轮到{GomokuText.SideName(_game.Turn)}",
            "TextSecondaryBrush");
    }

    /// <summary>客户端落子后房主迟迟不回：把「等待确认」放掉，免得界面一直卡着。</summary>
    private async void ScheduleClientPendingTimeout()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (!_clientMovePending) return;
            Ui(() =>
            {
                if (!_clientMovePending) return;
                _clientMovePending = false;
                OnPropertyChanged(nameof(CanPlayNow));
                OnPropertyChanged(nameof(IsBoardInteractive));
                OnPropertyChanged(nameof(BoardHintText));
                SetNotice("房主没有回应这一手，请再试一次（或检查连接）", "DangerBrush");
            });
        }
        catch
        {
            // 后台计时器失败无所谓，别影响界面。
        }
    }

    // ---------------------------------------------------------------- 二次确认（防手滑）

    /// <summary>认输按钮：联机对局进行中第一次点只是「武装」确认，3 秒内再点一次才真认输。</summary>
    private void OnResignClicked()
    {
        if (!CanResign)
        {
            SetNotice("现在不能认输（对局没开始 / 已结束 / 正在等待对方重连）", "DangerBrush");
            return;
        }

        // 同机双人不是网络对局，认得干脆；联机对局进行中要再点一次，防手滑。
        if (IsOnline && _game.Started && !_game.IsOver)
        {
            if (!_resignArmed)
            {
                ArmResignConfirm();
                SetNotice("再点一次这个按钮才会认输（3 秒内有效）", "DangerBrush");
                return;
            }

            DisarmResignConfirm();
        }

        ResignCurrent();
    }

    private void ArmResignConfirm()
    {
        _resignArmed = true;
        OnPropertyChanged(nameof(ResignButtonText));
        var version = ++_resignArmVersion;
        _ = DisarmResignLaterAsync(version);
    }

    private async Task DisarmResignLaterAsync(int version)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch
        {
            return;
        }

        if (version != _resignArmVersion || !_resignArmed) return;
        Ui(() =>
        {
            if (version != _resignArmVersion || !_resignArmed) return;
            _resignArmed = false;
            OnPropertyChanged(nameof(ResignButtonText));
            SetNotice("认输确认已取消（3 秒内没再点一次）", "TextSecondaryBrush");
        });
    }

    private void DisarmResignConfirm()
    {
        _resignArmVersion++;
        if (!_resignArmed) return;
        _resignArmed = false;
        OnPropertyChanged(nameof(ResignButtonText));
    }

    /// <summary>离开房间按钮：进行中第一次点只是「武装」确认，3 秒内再点一次才真离开；重连中 = 停止重连。</summary>
    private void OnLeaveRoomClicked()
    {
        if (!CanLeaveRoom)
        {
            SetNotice("现在没有房间可以离开", "DangerBrush");
            return;
        }

        // 二次确认统一交给基类钩子 ConfirmLeaveRoom（共享 RoomPanel 的「离开房间」按钮也走同一条路），
        // 这样两个入口的行为一致：第一次点击只提示、第二次才真离开。
        LeaveRoomCommand.Execute(null);
    }

    /// <summary>
    /// 基类的「离开房间」确认钩子：对局进行中的联机对局要点两下（3 秒内第二次才生效），防手滑。
    /// 重连中是「停止重连」，不做二次确认。
    /// </summary>
    protected override bool ConfirmLeaveRoom()
    {
        if (IsReconnecting) return true;
        if (!_game.Started || _game.IsOver)
        {
            DisarmLeaveConfirm();
            return true;
        }

        if (!_leaveArmed)
        {
            ArmLeaveConfirm();
            SetNotice("对局进行中：再点一次「离开房间」才会离开（3 秒内有效）", "DangerBrush");
            return false;
        }

        DisarmLeaveConfirm();
        return true;
    }

    private void ArmLeaveConfirm()
    {
        _leaveArmed = true;
        OnPropertyChanged(nameof(LeaveRoomButtonText));
        var version = ++_leaveArmVersion;
        _ = DisarmLeaveLaterAsync(version);
    }

    private async Task DisarmLeaveLaterAsync(int version)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch
        {
            return;
        }

        if (version != _leaveArmVersion || !_leaveArmed) return;
        Ui(() =>
        {
            if (version != _leaveArmVersion || !_leaveArmed) return;
            _leaveArmed = false;
            OnPropertyChanged(nameof(LeaveRoomButtonText));
            SetNotice("离开房间确认已取消（3 秒内没再点一次）", "TextSecondaryBrush");
        });
    }

    private void DisarmLeaveConfirm()
    {
        _leaveArmVersion++;
        if (!_leaveArmed) return;
        _leaveArmed = false;
        OnPropertyChanged(nameof(LeaveRoomButtonText));
    }

    // ---------------------------------------------------------------- 悔棋 / 认输 / 求和 / 再来一局

    private void UndoOne()
    {
        if (IsLocalMode)
        {
            var undone = _game.LastMove;
            if (undone is null || !_game.Undo())
            {
                SetNotice("还没有棋子可以悔", "DangerBrush");
                return;
            }

            RefreshAll();
            SetNotice($"已悔棋：撤销 {GomokuText.DescribeMove(undone, _game.Size)}，轮到{GomokuText.SideName(_game.Turn)}",
                "SuccessBrush");
            return;
        }

        if (!IsOnline)
        {
            SetNotice("还没进房间：先选玩法再下棋", "DangerBrush");
            return;
        }

        RequestUndo();                       // 基类：按房主/客户端身份路由，等对方同意
    }

    private void ResignCurrent()
    {
        if (!_game.Started || _game.IsOver)
        {
            SetNotice("现在不能认输（对局还没开始或已经结束）", "DangerBrush");
            return;
        }

        if (IsLocalMode)
        {
            var loser = _game.Turn;
            _game.MarkResign(loser);
            RefreshAll();
            SetNotice($"{GomokuText.SideName(loser)}认输，{_game.ResultText}", "DangerBrush");
            AppendSystemChat($"{GomokuText.SideName(loser)}认输");
            return;
        }

        if (!IsOnline)
        {
            SetNotice("还没进房间，不能认输", "DangerBrush");
            return;
        }

        ResignOnline();                      // 基类：房主直接判负并广播 / 客户端玩家发请求
    }

    private void DrawCurrent()
    {
        if (!_game.Started || _game.IsOver)
        {
            SetNotice("现在不能求和（对局还没开始或已经结束）", "DangerBrush");
            return;
        }

        if (IsLocalMode)
        {
            _game.MarkDraw();
            RefreshAll();
            SetNotice("双方同意和棋", "SuccessBrush");
            AppendSystemChat("和棋");
            return;
        }

        if (!IsOnline)
        {
            SetNotice("还没进房间，不能求和", "DangerBrush");
            return;
        }

        OfferDraw();                         // 基类：按房主/客户端身份路由，等对方同意
    }

    private void Rematch()
    {
        if (IsLocalMode)
        {
            BeginNewGame(swapSides: false);
            RefreshAll();
            SetNotice("已开新的一局（同机双人）", "SuccessBrush");
            return;
        }

        if (!IsOnline)
        {
            SetNotice("还没进房间：先开房间 / 加入房间 / 同机双人", "DangerBrush");
            return;
        }

        // 联机：房主 = 基类直接开始下一局；客户端坐席玩家 = 请求房主；观战者由基类回绝。
        RequestRematch();
    }

    private void NewLocalGame()
    {
        if (IsOnline)
        {
            SetNotice("联机时由房主在「房间成员」面板安排下一局", "DangerBrush");
            return;
        }

        if (!IsLocalMode)
        {
            StartLocalCommand.Execute(null); // 基类：进入同机双人（会自动开新局）
            return;
        }

        BeginNewGame(swapSides: false);
        RefreshAll();
        SetNotice("已开新的一局（同机双人）", "SuccessBrush");
    }

    private void CopyMoves()
    {
        var text = MovesText;
        if (text.Length == 0)
        {
            SetNotice("棋谱还是空的，先下几步吧", "DangerBrush");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            SetNotice($"已复制棋谱（{_game.MoveCount} 手）", "SuccessBrush");
        }
        catch (Exception ex)
        {
            SetNotice($"复制失败：{ex.Message}", "DangerBrush");
        }
    }

    // ---------------------------------------------------------------- 基类钩子：房间 / 联机

    /// <inheritdoc />
    protected override string FirstSeatSide => "black";

    /// <inheritdoc />
    protected override string PreferredHostSeatSide => Cfg.EffectiveHostSide;

    /// <inheritdoc />
    protected override string OppositeSide(string side)
    {
        var cell = GomokuText.ParseSide(side);
        return GomokuText.SideKey(cell switch
        {
            GomokuCell.Black => GomokuCell.White,
            GomokuCell.White => GomokuCell.Black,
            _ => GomokuCell.Empty,
        });
    }

    /// <inheritdoc />
    protected override string SideDisplayName(string side) => GomokuText.SideName(GomokuText.ParseSide(side));

    /// <inheritdoc />
    protected override JsonElement BuildGameSnapshot()
        => JsonSerializer.SerializeToElement(_game.ToSnapshot(), RoomProtocol.JsonOptions);

    /// <inheritdoc />
    protected override BoardGameStatus ReportGameStatus()
    {
        var winner = _game.Result switch
        {
            GomokuResult.BlackWin => "black",
            GomokuResult.WhiteWin => "white",
            _ => null,
        };
        return new BoardGameStatus(_game.Started, _game.IsOver, winner);
    }

    /// <inheritdoc />
    protected override void ResetGameToWaiting()
    {
        _game = new GomokuGame(Cfg.EffectiveBoardSize, Cfg.ForbiddenEnabled) { Started = false };
        _clientMovePending = false;
        DisarmResignConfirm();
        DisarmLeaveConfirm();
        RefreshAll();
    }

    /// <inheritdoc />
    protected override void ApplyGameSnapshot(JsonElement game, string mySide)
    {
        GomokuSnapshot? snapshot;
        try
        {
            snapshot = game.Deserialize<GomokuSnapshot>(RoomProtocol.JsonOptions);
        }
        catch (Exception ex)
        {
            SetNotice($"局面数据解析失败：{ex.Message}", "DangerBrush");
            return;
        }

        var (restored, error) = GomokuGame.FromSnapshot(snapshot);
        if (restored is null)
        {
            SetNotice($"同步局面失败：{error}", "DangerBrush");
            return;
        }

        var oldMoveCount = _game.MoveCount;
        var wasOver = _game.IsOver;
        _game = restored;
        _clientMovePending = false;
        RefreshAll();

        // 给用户一句反馈：悔棋撤了几手 / 对方刚落哪 / 对局结束。
        if (oldMoveCount > 0 && restored.MoveCount < oldMoveCount)
        {
            var undone = oldMoveCount - restored.MoveCount;
            SetNotice($"对方同意悔棋：撤销 {undone} 手，轮到{GomokuText.SideName(restored.Turn)}", "SuccessBrush");
            AppendSystemChat($"已悔棋（撤销 {undone} 手）");
        }
        else if (!wasOver && restored.IsOver)
        {
            SetNotice($"对局结束：{ResultDetailText}", restored.LastForbiddenPoint is not null ? "DangerBrush" : "SuccessBrush");
        }
        else if (restored.MoveCount > oldMoveCount && restored.LastMove is { } last && !restored.IsOver)
        {
            var who = GomokuText.ParseSide(last.Player) == GomokuText.ParseSide(mySide) ? "你" : "对方";
            SetNotice($"{who}落子 {GomokuText.DescribeMove(last, restored.Size)}，轮到{GomokuText.SideName(restored.Turn)}",
                "TextSecondaryBrush");
        }

        NotifyGameFinishedIfNeeded();
    }

    /// <inheritdoc />
    protected override void OnHostAction(string fromMemberId, string fromSide, string type, JsonElement? payload)
    {
        if (!string.Equals(type, "move", StringComparison.OrdinalIgnoreCase)) return;
        if (payload is not { } element || element.ValueKind != JsonValueKind.Object) return;

        if (!TryReadCoordinate(element, "x", out var x) || !TryReadCoordinate(element, "y", out var y)) return;

        var side = GomokuText.ParseSide(fromSide);
        if (side == GomokuCell.Empty) return;             // 基类已校验过发送者在席位上，这里再兜一层

        PlayOnHost(side, new GomokuPoint(x, y));
    }

    private static bool TryReadCoordinate(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind != JsonValueKind.Number) return false;
        return property.TryGetInt32(out value);
    }

    /// <inheritdoc />
    protected override bool ApplyUndoFor(string requesterSide)
    {
        var side = GomokuText.ParseSide(requesterSide);
        if (side == GomokuCell.Empty) return false;

        var ok = _game.UndoToTurn(side);
        if (ok) RefreshAll();
        return ok;
    }

    /// <inheritdoc />
    protected override void ApplyDraw()
    {
        _game.MarkDraw();
        RefreshAll();
    }

    /// <inheritdoc />
    protected override void ApplyResign(string side)
    {
        var loser = GomokuText.ParseSide(side);
        if (loser == GomokuCell.Empty) return;

        _game.MarkResign(loser);
        RefreshAll();
    }

    /// <inheritdoc />
    protected override void BeginNewGame(bool swapSides)
    {
        // 换边已经由基类的「席位安排」决定（房主可以把同一个人换到先手/后手位），这里不再动配置。
        _game = new GomokuGame(Cfg.EffectiveBoardSize, Cfg.ForbiddenEnabled)
        {
            Started = IsOnline || IsLocalMode,
        };
        _clientMovePending = false;
        DisarmResignConfirm();
        DisarmLeaveConfirm();
        RefreshAll();
        AppendSystemChat("开始新的一局");
    }

    /// <inheritdoc />
    protected override void OnHostRoomOpened()
    {
        // 棋局已由基类在 PrepareHostRoom 里通过 ResetGameToWaiting 重置；这里只补一句提示。
        _clientMovePending = false;
        RefreshAll();
        AppendSystemChat("房间已开：点「房间成员」里的席位下拉安排上场的人，选好点「开始对局」");
    }

    /// <inheritdoc />
    protected override void OnLocalModeStarted()
    {
        _game = new GomokuGame(Cfg.EffectiveBoardSize, Cfg.ForbiddenEnabled) { Started = true };
        _clientMovePending = false;
        RefreshAll();
        AppendSystemChat("同机双人：黑棋先手，轮流点棋盘落子");
    }

    /// <inheritdoc />
    protected override void OnSeatsChanged()
    {
        // 席位 / 本端座位变了：能不能下棋、执哪边都跟着变（不再缓存 _mySide，直接用基类 LocalSide）。
        _clientMovePending = false;
        RefreshAll();
    }

    /// <inheritdoc />
    protected override void OnMembersChanged()
    {
        _clientMovePending = false;
        RefreshAll();
    }

    /// <inheritdoc />
    protected override void OnReconnected()
    {
        _clientMovePending = false;
        RefreshAll();
        SetNotice(_game.Started && !_game.IsOver ? "已重新连接，继续对局" : "已重新连接", "SuccessBrush");
        AppendSystemChat("已重新连接");
    }

    /// <inheritdoc />
    protected override void OnModeChanged()
    {
        _clientMovePending = false;
        DisarmResignConfirm();
        DisarmLeaveConfirm();
        RefreshAll();
    }

    // ---------------------------------------------------------------- 刷新

    /// <summary>把棋局状态整体同步到界面（每次棋局变化都调它）。</summary>
    private void RefreshAll()
    {
        if (_insideRefresh) return;
        _insideRefresh = true;
        try
        {
            _refreshVersion++;

            BoardState = BuildBoardState(Array.Empty<GomokuPoint>());
            RefreshMoveLines();

            // 状态变了：已经不能执行的二次确认要收回，免得按钮一直停在「再点一次确认」。
            if (_resignArmed && !CanResign) DisarmResignConfirm();
            if (_leaveArmed && !CanLeaveRoom) DisarmLeaveConfirm();

            OnPropertyChanged(nameof(Game));
            OnPropertyChanged(nameof(TurnText));
            OnPropertyChanged(nameof(ResultText));
            OnPropertyChanged(nameof(ResultDetailText));
            OnPropertyChanged(nameof(MoveCountText));
            OnPropertyChanged(nameof(MySideText));
            OnPropertyChanged(nameof(OpponentSideText));
            OnPropertyChanged(nameof(ShowNewLocalGameButton));
            OnPropertyChanged(nameof(BoardHintText));
            OnPropertyChanged(nameof(MovesText));
            OnPropertyChanged(nameof(HasMoves));
            OnPropertyChanged(nameof(CanPlayNow));
            OnPropertyChanged(nameof(IsBoardInteractive));
            OnPropertyChanged(nameof(IsStarted));
            OnPropertyChanged(nameof(IsGameOver));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanResign));
            OnPropertyChanged(nameof(CanOfferDraw));
            OnPropertyChanged(nameof(CanRematch));
            OnPropertyChanged(nameof(ShowRematchButton));
            OnPropertyChanged(nameof(IsSetupAllowed));
            OnPropertyChanged(nameof(ForbiddenSummary));
            OnPropertyChanged(nameof(ResignButtonText));
            OnPropertyChanged(nameof(ShowLeaveRoomButton));
            OnPropertyChanged(nameof(LeaveRoomButtonText));
            OnPropertyChanged(nameof(LeaveRoomWarning));

            try { System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
            catch { /* 无头环境忽略 */ }

            ScheduleForbiddenPoints();
        }
        finally
        {
            _insideRefresh = false;
        }

        // 棋局状态可能刚变成「结束」：交给基类加分 / 自动轮换 / 广播 / 提示（基类每局只处理一次）。
        NotifyGameFinishedIfNeeded();
    }

    private void RefreshMoveLines()
    {
        MoveLines.Clear();
        var size = _game.Size;
        foreach (var move in _game.Moves)
        {
            MoveLines.Add(GomokuText.MoveLine(move, size));
        }
    }

    private GomokuBoardViewState BuildBoardState(IReadOnlyList<GomokuPoint> forbiddenPoints)
    {
        var black = new List<GomokuPoint>(_game.MoveCount);
        var white = new List<GomokuPoint>(_game.MoveCount);
        for (var y = 0; y < _game.Size; y++)
        {
            for (var x = 0; x < _game.Size; x++)
            {
                switch (_game.Board.Get(x, y))
                {
                    case GomokuCell.Black:
                        black.Add(new GomokuPoint(x, y));
                        break;
                    case GomokuCell.White:
                        white.Add(new GomokuPoint(x, y));
                        break;
                }
            }
        }

        return new GomokuBoardViewState
        {
            Size = _game.Size,
            Black = black,
            White = white,
            LastMove = _game.LastMove is { } last ? new GomokuPoint(last.X, last.Y) : null,
            ForbiddenAttempt = _game.LastForbiddenPoint,
            ForbiddenPoints = forbiddenPoints,
            WinningLine = _game.WinningLine.ToArray(),
            Turn = _game.Turn,
            ShowForbidden = _game.ForbiddenEnabled && Cfg.ShowForbiddenPoints,
        };
    }

    /// <summary>禁手点：只在「开禁手 + 黑方回合 + 对局进行中 + 显示开关开着」时算，算完回 UI 线程更新。</summary>
    private void ScheduleForbiddenPoints()
    {
        var need = Cfg.ShowForbiddenPoints
                   && _game.ForbiddenEnabled
                   && _game.Started
                   && !_game.IsOver
                   && _game.Turn == GomokuCell.Black;

        if (!need)
        {
            BoardState = BuildBoardState(Array.Empty<GomokuPoint>());
            return;
        }

        var version = _refreshVersion;
        var board = _game.Board.Clone();
        _ = Task.Run(() =>
        {
            IReadOnlyList<GomokuPoint> points;
            try
            {
                points = GomokuForbidden.FindForbiddenPoints(board, true);
            }
            catch
            {
                points = Array.Empty<GomokuPoint>();
            }

            Ui(() =>
            {
                if (version != _refreshVersion) return;   // 棋局又变了，这份结果作废
                BoardState = BuildBoardState(points);
            });
        });
    }

    // ---------------------------------------------------------------- 演示数据（截图用）

    /// <summary>
    /// <c>YLT_GOMOKU_DEMO=1</c> 时造一局进行中的演示棋（12 手、最后一手有红点、黑方回合、
    /// 开着禁手所以能看到红叉禁手点），只为截图核对版式，<b>不发任何网络请求</b>。
    ///
    /// <para><c>YLT_BOARD_DEMO_ROOM</c>（房主/观战演示房间）生效时<b>不要</b>进同机双人 ——
    /// 基类已经准备好假成员 / 两席 / 在线状态，这里只把演示棋注入进去。</para>
    /// </summary>
    private void LoadDemoIfRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("YLT_GOMOKU_DEMO"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Cfg.ForbiddenEnabled = true;
        Cfg.ShowForbiddenPoints = true;
        OnPropertyChanged(nameof(ForbiddenEnabled));
        OnPropertyChanged(nameof(ShowForbiddenPoints));
        OnPropertyChanged(nameof(ForbiddenSummary));

        if (!IsDemoRoom)
        {
            StartLocalCommand.Execute(null);     // 普通截图：进同机双人，让状态徽标 / 按钮处于「能下」的样子
        }

        var demo = new GomokuGame(15, true) { Started = true };
        var moves = new (GomokuCell Who, int X, int Y)[]
        {
            (GomokuCell.Black, 5, 7), (GomokuCell.White, 2, 2),
            (GomokuCell.Black, 6, 7), (GomokuCell.White, 3, 2),
            (GomokuCell.Black, 7, 5), (GomokuCell.White, 2, 3),
            (GomokuCell.Black, 7, 6), (GomokuCell.White, 3, 3),
            (GomokuCell.Black, 10, 10), (GomokuCell.White, 11, 3),
            (GomokuCell.Black, 11, 10), (GomokuCell.White, 12, 4),
        };
        foreach (var (who, x, y) in moves)
        {
            demo.Play(who, x, y);
        }

        _game = demo;
        RefreshAll();
        AppendSystemChat(IsDemoRoom
            ? "演示房间 + 演示棋（YLT_BOARD_DEMO_ROOM + YLT_GOMOKU_DEMO）：12 手，(7,7) 是三三禁手点"
            : "演示数据（YLT_GOMOKU_DEMO=1）：12 手，黑方回合，(7,7) 是三三禁手点");
    }
}
