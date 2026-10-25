using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;
using YuLiangToolkit.Tools.BoardGames.Shared;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;
using YuLiangToolkit.Tools.BoardGames.Xiangqi.Views;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi.ViewModels;

/// <summary>
/// 「联机中国象棋」界面逻辑：房间（交给 <see cref="BoardGameRoomViewModelBase"/>）+ 棋局 + 自由摆棋 / 让子 / 让先。
///
/// <para><b>分层</b>：规则与棋局全在 <see cref="XiangqiGame"/> / <see cref="XiangqiRules"/> / <see cref="XiangqiSetup"/> /
/// <see cref="XiangqiNotation"/> 里（纯逻辑、可自检），本类只负责：把草稿棋盘和正式棋局分开、把点击翻译成命令、
/// 把棋局翻译成棋盘控件的渲染状态、以及联机时主机权威 / 客户端只从快照更新的约定。</para>
///
/// <para><b>两种棋盘</b>：<c>_setupBoard</c> 是「摆棋草稿」（随便改，不影响进行中的对局）；
/// <c>_game</c> 是正式棋局。没开局 / 主机等人时棋盘显示草稿，开局后显示正式棋局。</para>
/// </summary>
public sealed class XiangqiViewModel : BoardGameRoomViewModelBase
{
    /// <summary>让先最多几步（界面滑块上限）。</summary>
    public const int MaxOpeningFreeMoves = 5;

    // ---------- 状态 ----------

    private readonly XiangqiBoard _setupBoard;
    private XiangqiBoardViewState _boardState = XiangqiBoardViewState.Empty;
    private XiangqiGame _game;
    private XiangqiPaletteItem? _selectedPalette;
    private XiangqiSetup.XiangqiHandicap? _appliedHandicap;
    private string _setupLabel = "标准开局";
    private (int X, int Y)? _selectedPoint;
    private IReadOnlyList<XiangqiMove> _legalTargets = Array.Empty<XiangqiMove>();
    private bool _isSetupMode;
    private bool _isRestartSetupConfirming;
    private bool _flipped;
    private bool _waitingHostMove;
    private string _mySide = "";

    /// <summary>
    /// 棋局快照的缓存：<b>只在 UI 线程刷新棋局时更新一次</b>。
    /// 这样网络线程调 <see cref="BuildGameSnapshot"/>（握手欢迎 / 广播）时只读一个不可变值，
    /// 不会和界面线程走子抢同一个 List（那时会抛「集合已修改」把握手弄断）。
    /// </summary>
    private JsonElement _snapshotCache;

    /// <summary>造一个象棋工具界面（配置 / 摆棋草稿 / 正式棋局 / 命令全部备好）。</summary>
    public XiangqiViewModel() : base("xiangqi", "中国象棋")
    {
        Cfg = LoadConfig<XiangqiConfig>();
        AttachRoomConfig(Cfg);

        _setupBoard = XiangqiBoard.CreateInitial();
        _game = CreateGameFromSetup(started: false);
        PaletteItems = BuildPaletteItems();

        // ------- 命令 -------
        BoardPointCommand = new RelayCommand(parameter =>
        {
            if (parameter is ValueTuple<int, int> point) HandleBoardClick(point.Item1, point.Item2);
        });
        UndoCommand = new RelayCommand(_ => UndoAction(), _ => CanUndo);
        ResignCommand = new RelayCommand(_ => ResignAction(), _ => CanResign);
        OfferDrawCommand = new RelayCommand(_ => OfferDrawAction(), _ => CanOfferDraw);
        RematchCommand = new RelayCommand(_ => RematchAction(), _ => CanRematch);
        FlipBoardCommand = new RelayCommand(_ => FlipBoard());
        CopyNotationCommand = new RelayCommand(_ => CopyNotation(), _ => _game.History.Count > 0);
        StartOnlineGameCommand = new RelayCommand(_ => StartOnlineGame(), _ => StartGameEnabled);
        ApplySetupAndStartCommand = new RelayCommand(_ => ApplySetupAndStart(), _ => CanApplySetupAndStart);
        ToggleSetupModeCommand = new RelayCommand(_ => ToggleSetupMode(), _ => IsSetupAllowed);
        RequestRestartSetupCommand = new RelayCommand(_ => RequestRestartSetup(), _ => CanRestartSetup);
        ConfirmRestartSetupCommand = new RelayCommand(_ => ConfirmRestartSetup(), _ => CanRestartSetup);
        CancelRestartSetupCommand = new RelayCommand(_ => CancelRestartSetup());
        ApplyDefaultSetupCommand = new RelayCommand(_ => ApplyDefaultSetup(), _ => IsSetupAllowed);
        ClearSetupCommand = new RelayCommand(_ => ClearSetup(), _ => IsSetupAllowed);
        ApplyHandicapCommand = new RelayCommand(parameter =>
        {
            if (parameter is XiangqiSetup.Preset preset) ApplyHandicap(preset);
        }, _ => IsSetupAllowed);
        SelectPaletteCommand = new RelayCommand(parameter =>
        {
            if (parameter is XiangqiPaletteItem item) SelectPalette(item);
        });

        RefreshAll();
        ApplyDemoData();

        // 基类的房间状态（在线 / 对方在线 / 暂停 / 重连 / 玩法）一变，就让「能不能下棋 / 按钮可用性」跟着刷新
        PropertyChanged += OnBaseStatePropertyChanged;
    }

    /// <summary>会让派生状态变化的基类属性名（只监听这些，避免自己通知自己造成递归）。</summary>
    private static readonly string[] BaseStatePropertyNames =
    {
        nameof(HasPeer), nameof(IsOnline), nameof(IsGamePaused), nameof(IsReconnecting),
        nameof(Mode), nameof(IsLocalMode), nameof(IsHost), nameof(IsClient),
    };

    private void OnBaseStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null) return;
        if (Array.IndexOf(BaseStatePropertyNames, e.PropertyName) < 0) return;
        RefreshTransientState();
    }

    /// <summary>只刷新「随房间状态变化」的派生属性（不重画棋盘、不重建棋谱，免得断线重连时闪）。</summary>
    private void RefreshTransientState()
    {
        OnPropertyChanged(nameof(CanPlayNow));
        OnPropertyChanged(nameof(IsBoardInteractive));
        OnPropertyChanged(nameof(StartGameEnabled));
        OnPropertyChanged(nameof(ShowStartOnlineButton));
        OnPropertyChanged(nameof(IsWaitingStart));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanResign));
        OnPropertyChanged(nameof(CanOfferDraw));
        OnPropertyChanged(nameof(CanRematch));
        OnPropertyChanged(nameof(CanApplySetupAndStart));
        OnPropertyChanged(nameof(IsSetupAllowed));
        OnPropertyChanged(nameof(ShowSetupCard));
        OnPropertyChanged(nameof(StartHintText));
        OnPropertyChanged(nameof(TurnText));
        OnPropertyChanged(nameof(ApplySetupAndStartText));
        OnPropertyChanged(nameof(ShowToggleSetupPreview));
        OnPropertyChanged(nameof(ShowSetupControls));
        OnPropertyChanged(nameof(ShowSetupCollapsedSummary));
        OnPropertyChanged(nameof(CanRestartSetup));
        OnPropertyChanged(nameof(ShowRestartSetupButton));
        OnPropertyChanged(nameof(ShowApplySetupButton));
        OnPropertyChanged(nameof(MySideSummaryText));
        OnPropertyChanged(nameof(PeerSideSummaryText));
        OnPropertyChanged(nameof(MySideText));
        InvalidateCommands();
    }

    // ---------- 配置 ----------

    /// <summary>象棋自己的设置（房间三项由基类接管，其余在这里）。</summary>
    public XiangqiConfig Cfg { get; }

    // ---------- 摆棋调色板 / 让子 / 让先 ----------

    /// <summary>调色板：红方 7 种 + 黑方 7 种 + 删除。</summary>
    public IReadOnlyList<XiangqiPaletteItem> PaletteItems { get; }

    /// <summary>当前选中的调色板项（null = 还没选）。</summary>
    public XiangqiPaletteItem? SelectedPalette
    {
        get => _selectedPalette;
        set
        {
            if (ReferenceEquals(_selectedPalette, value)) return;
            if (_selectedPalette is not null) _selectedPalette.IsSelected = false;
            _selectedPalette = value;
            if (_selectedPalette is not null) _selectedPalette.IsSelected = true;
            OnPropertyChanged();
            if (value is not null && !ShowSetupBoard && IsSetupAllowed) _isSetupMode = true;   // 选棋子 = 进摆棋预览
            RefreshAll();
        }
    }

    /// <summary>让子预设（不含「标准开局」，那个是独立按钮）。</summary>
    public IReadOnlyList<XiangqiSetup.Preset> HandicapPresets { get; } =
        XiangqiSetup.Presets.Where(p => p.Handicap is not null).ToArray();

    /// <summary>让子方下拉选项（红方 / 黑方）。</summary>
    public IReadOnlyList<OptionItem<XiangqiSide>> HandicapGiverOptions { get; } = OptionItem.From(
        (XiangqiSide.Red, "红方让子"),
        (XiangqiSide.Black, "黑方让子"));

    /// <summary>让子方（红方让 / 黑方让）。</summary>
    public OptionItem<XiangqiSide>? SelectedHandicapGiver
    {
        get => OptionItem<XiangqiSide>.Find(HandicapGiverOptions, Cfg.HandicapGiverValue) ?? HandicapGiverOptions[0];
        set
        {
            if (value is null || Cfg.HandicapGiverValue == value.Value) return;
            Cfg.HandicapGiver = value.Value.ToKey();
            OnPropertyChanged();
            ReapplyHandicapAfterGiverChanged();
            RefreshAll();
        }
    }

    /// <summary>让先步数（0~5）：被让方（让子方的对面）开局可以连走这么多步。</summary>
    public int OpeningFreeMoves
    {
        get => Cfg.OpeningFreeMovesEffective;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxOpeningFreeMoves);
            if (Cfg.OpeningFreeMoves == clamped) return;
            Cfg.OpeningFreeMoves = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpeningFreeMovesText));
            RefreshAll();
        }
    }

    /// <summary>让先步数的文字说明。</summary>
    public string OpeningFreeMovesText
        => Cfg.OpeningFreeMovesEffective > 0
            ? $"让先 {Cfg.OpeningFreeMovesEffective} 步：{Cfg.HandicapGiverValue.Opposite().DisplayName()}开局先连走"
            : "不让先（红方先走）";

    /// <summary>房主执棋选项。</summary>
    public IReadOnlyList<OptionItem<XiangqiSide>> HostSideOptions { get; } = OptionItem.From(
        (XiangqiSide.Red, "房主执红"),
        (XiangqiSide.Black, "房主执黑"));

    /// <summary>房主执哪边（改了之后下一局生效；摆棋预览会按执黑自动翻转）。</summary>
    public OptionItem<XiangqiSide>? SelectedHostSide
    {
        get => OptionItem<XiangqiSide>.Find(HostSideOptions, Cfg.HostSideValue) ?? HostSideOptions[0];
        set
        {
            if (value is null || Cfg.HostSideValue == value.Value) return;
            Cfg.HostSide = value.Value.ToKey();
            OnPropertyChanged();
            UpdateFlipFromSettings();
            RefreshAll();
        }
    }

    // ---------- 摆棋草稿的显示状态 ----------

    /// <summary>摆棋草稿是否合法（有错不能开局）。</summary>
    public bool IsSetupValid => SetupErrorText.Length == 0;

    /// <summary>摆棋错误提示（空字符串 = 合法）。</summary>
    public string SetupErrorText
    {
        get
        {
            var first = SetupFirstSide;
            return XiangqiSetup.Validate(_setupBoard, first, first.Opposite()) ?? "";
        }
    }

    /// <summary>有没有摆棋错误。</summary>
    public bool HasSetupError => SetupErrorText.Length > 0;

    /// <summary>摆棋状态一句话（合法 = 绿色提示；有错 = 红色错误原因）。</summary>
    public string SetupStatusText => HasSetupError ? SetupErrorText : "摆棋合法，可以开局";

    /// <summary>当前摆棋的一句话摘要（摆法 / 让子方 / 让先）。</summary>
    public string SetupSummaryText
    {
        get
        {
            var giver = Cfg.HandicapGiverValue;
            var opening = Cfg.OpeningFreeMovesEffective;
            var openingText = opening > 0 ? $"让先 {opening} 步（{giver.Opposite().DisplayName()}先连走）" : "不让先";
            return $"摆法：{_setupLabel}　让子方：{giver.DisplayName()}　{openingText}";
        }
    }

    /// <summary>摆棋草稿的 FEN（排错 / 核对用）。</summary>
    public string SetupFenText => _setupBoard.ToFen(SetupFirstSide);

    /// <summary>草稿棋盘（摆棋界面用；外部不要改它）。</summary>
    public XiangqiBoard SetupBoard => _setupBoard;

    /// <summary>让先时先走的一方（没让先就是红方）。</summary>
    public XiangqiSide SetupFirstSide
        => Cfg.OpeningFreeMovesEffective > 0 ? Cfg.HandicapGiverValue.Opposite() : XiangqiSide.Red;

    // ---------- 对局状态（XAML 绑） ----------

    /// <summary>正式棋局。</summary>
    public XiangqiGame Game => _game;

    /// <summary>棋盘控件的渲染状态（摆棋时是草稿，对局时是正式棋盘）。</summary>
    public XiangqiBoardViewState BoardState => _boardState;

    /// <summary>现在棋盘显示的是不是摆棋草稿。</summary>
    public bool ShowSetupBoard => _isSetupMode
                                  || Mode == BoardPlayMode.Offline
                                  || (Mode == BoardPlayMode.Host && !_game.Started);

    /// <summary>正在摆棋预览（本地模式下改完草稿要看效果时用）。</summary>
    public bool IsSetupMode => _isSetupMode;

    /// <summary>摆棋卡片要不要显示（客户端只负责下棋，不显示摆棋）。</summary>
    public bool ShowSetupCard => Mode != BoardPlayMode.Client;

    /// <summary>摆棋能不能编辑：离线 / 同机双人随时可改；主机在开局前可改；联机开局后 / 客户端不可改。</summary>
    public bool IsSetupAllowed => Mode switch
    {
        BoardPlayMode.Offline => true,
        BoardPlayMode.Local => true,
        BoardPlayMode.Host => !_game.Started,
        _ => false,
    };

    /// <summary>摆棋预览按钮的文字。</summary>
    public string ToggleSetupModeText => _isSetupMode ? "返回对局棋盘" : "在棋盘上摆棋";

    /// <summary>要不要显示「在棋盘上摆棋 / 返回对局棋盘」按钮（只有同机双人开局后才需要切）。</summary>
    public bool ShowToggleSetupPreview => IsLocalMode && _game.Started;

    /// <summary>摆棋卡是否展开（未开局 / 主动回到摆棋时才展开调色板等控件）。</summary>
    public bool ShowSetupControls => !_game.Started || _isSetupMode;

    /// <summary>摆棋卡是否收起成「摘要 + 重新摆棋」状态（对局进行中 / 已结束）。</summary>
    public bool ShowSetupCollapsedSummary => _game.Started && !_isSetupMode;

    /// <summary>能不能「重新摆棋（会结束本局）」：只有本地 / 主机（客户端听房主的）。</summary>
    public bool CanRestartSetup => _game.Started && !IsClient && (IsLocalMode || IsHost);

    /// <summary>正在等用户二次确认「结束本局重新摆棋」。</summary>
    public bool IsRestartSetupConfirming
    {
        get => _isRestartSetupConfirming;
        private set
        {
            if (!Set(ref _isRestartSetupConfirming, value)) return;
            OnPropertyChanged(nameof(ShowRestartSetupButton));
        }
    }

    /// <summary>「重新摆棋（会结束本局）」按钮要不要显示（确认中时换成确定 / 取消）。</summary>
    public bool ShowRestartSetupButton => CanRestartSetup && !_isRestartSetupConfirming;

    /// <summary>「应用并开始」按钮要不要显示：只用于离线 / 同机双人（主机用「对局」卡里的「开始对局」）。</summary>
    public bool ShowApplySetupButton => !IsClient && !IsHost
                                        && (Mode == BoardPlayMode.Offline || IsLocalMode);

    /// <summary>「应用并开始」按钮的文字（离线 / 同机双人都是开一局同机双人）。</summary>
    public string ApplySetupAndStartText => "应用并开始（同机双人）";

    /// <summary>我方执子一句话（同机双人不说「未定」）。</summary>
    public string MySideSummaryText => IsLocalMode ? "我方：同机双人（红黑轮流）" : LocalSideText;

    /// <summary>对方执子一句话（同机双人 = 同一台电脑上的两个人）。</summary>
    public string PeerSideSummaryText => IsLocalMode ? "对方：同一台电脑的两位玩家" : PeerSideText;

    /// <summary>棋盘能不能点（摆棋模式放子；对局模式走子）。</summary>
    public bool IsBoardInteractive => ShowSetupBoard ? IsSetupAllowed : CanPlayNow;

    /// <summary>现在能不能落子（同机双人 = 随时；联机 = 对局中 + 轮到我 + 对方在线 + 没在重连/暂停）。</summary>
    public bool CanPlayNow
    {
        get
        {
            if (!_game.Started || _game.IsOver || _waitingHostMove) return false;
            if (IsGamePaused || IsReconnecting) return false;    // 断线 / 对方掉线：棋局暂停，棋盘只读
            return Mode switch
            {
                BoardPlayMode.Local => true,
                BoardPlayMode.Host => IsOnline && HasPeer && _game.Turn == Cfg.HostSideValue,
                BoardPlayMode.Client => IsOnline && HasPeer
                                        && XiangqiSides.Parse(_mySide) is { } mine
                                        && _game.Turn == mine,
                _ => false,
            };
        }
    }

    /// <summary>对局是否已经开始。</summary>
    public bool IsStarted => _game.Started;

    /// <summary>联机等对方开始 / 等连接。</summary>
    public bool IsWaitingStart => (IsHost || IsClient) && IsOnline && !_game.Started;

    /// <summary>轮次文案。</summary>
    public string TurnText
    {
        get
        {
            if (ShowSetupBoard)
            {
                if (Mode == BoardPlayMode.Host && !_game.Started)
                    return HasPeer ? "摆棋已就绪：点「开始对局」" : "摆棋与让子（等对方加入后开始）";
                return "摆棋中：调好棋子后点「应用并开始」";
            }
            if (!_game.Started) return IsClient ? "等待房主开始对局…" : "对局还没开始";
            if (_game.IsOver) return $"{_game.ResultText} · {_game.ResultReason}";
            var text = _waitingHostMove ? "已提交走法，等房主确认…" : $"轮到{_game.Turn.DisplayName()}走棋";
            if (_game.IsOpeningFreePhase)
                text += $"（让先：{_game.OpeningSide?.DisplayName()}还可连走 {_game.OpeningFreeMovesRemaining} 步）";
            return text;
        }
    }

    /// <summary>被将军提示（没人被将军时为空）。</summary>
    public string CheckText => !ShowSetupBoard && !_game.IsOver && _game.IsInCheck
        ? $"将军：{_game.Turn.DisplayName()}请应将"
        : "";

    /// <summary>结果文案（没结束时为空）。</summary>
    public string ResultText => _game.IsOver ? $"{_game.ResultText}（{_game.ResultReason}）" : "";

    /// <summary>手数 / 吃子数 / 让先剩余。</summary>
    public string MoveCountText
    {
        get
        {
            if (!_game.Started) return "尚未开始";
            var captures = 0;
            foreach (var move in _game.History)
            {
                if (move.Captured.Length > 0) captures++;
            }
            var text = $"已走 {_game.History.Count} 步";
            if (captures > 0) text += $"（吃子 {captures} 次）";
            if (_game.IsOpeningFreePhase) text += $"　让先还剩 {_game.OpeningFreeMovesRemaining} 步";
            return text;
        }
    }

    /// <summary>我方执子文案（同机双人 / 联机各有说法）。</summary>
    public string MySideText => LocalSideText;

    /// <summary>当前选中棋子的坐标（没选中为 null）。</summary>
    public (int X, int Y)? SelectedPoint => _selectedPoint;

    /// <summary>当前选中棋子的合法落点。</summary>
    public IReadOnlyList<XiangqiMove> LegalTargets => _legalTargets;

    /// <summary>棋盘是否翻转（执黑 / 手动翻转）。</summary>
    public bool IsFlipped => _flipped;

    /// <summary>中文记谱（一行两条半回合）。</summary>
    public string NotationText => XiangqiNotation.BuildHistoryText(_game.History);

    /// <summary>棋谱列表（绑定 ListBox）。</summary>
    public ObservableCollection<string> MoveLines { get; } = new();

    /// <summary>主机「开始对局」按钮要不要显示（等对方加入后出现）。</summary>
    public bool ShowStartOnlineButton => IsHost && IsOnline && HasPeer && !_game.Started;

    /// <summary>主机「开始对局」能不能点（对方在线 + 没在重连暂停 + 摆棋合法）。</summary>
    public bool StartGameEnabled => IsHost && !_game.Started && HasPeer && !IsGamePaused && IsSetupValid;

    /// <summary>「应用并开始」能不能点。</summary>
    public bool CanApplySetupAndStart => IsSetupValid && ShowApplySetupButton;

    /// <summary>对局卡片顶部的一句话流程提示。</summary>
    public string StartHintText
    {
        get
        {
            if (Mode == BoardPlayMode.Offline)
                return "先在「摆棋与让子」里调好（默认开局 / 让子 / 让先），再点「应用并开始」进入同机双人对局。";
            if (IsHost)
            {
                if (!IsOnline) return "正在开房间…";
                if (_game.Started) return IsGamePaused ? GamePauseStatusText : "对局进行中：你走完一步会自动同步给对方。";
                return HasPeer ? "对方已加入：点「开始对局」开始。" : "房间已开：把地址发给对方，等对方加入。";
            }
            if (IsClient)
                return _game.Started ? "对局进行中：轮到你时点棋子走棋。" : "已加入房间：等待房主开始对局…";
            if (IsLocalMode)
                return _game.Started
                    ? "同机双人：两个人轮流点棋盘走棋；要换摆法点摆棋卡的「重新摆棋」。"
                    : "同机双人已就绪：点「应用并开始（同机双人）」。";
            return "";
        }
    }

    // ---------- 设置开关（桥接 Cfg，改完立刻刷新棋盘） ----------

    /// <summary>选中棋子时高亮可走点。</summary>
    public bool ShowLegalMoves
    {
        get => Cfg.ShowLegalMoves;
        set
        {
            if (Cfg.ShowLegalMoves == value) return;
            Cfg.ShowLegalMoves = value;
            OnPropertyChanged();
            RefreshBoardState();
        }
    }

    /// <summary>执黑时自动翻转棋盘。</summary>
    public bool AutoFlip
    {
        get => Cfg.AutoFlip;
        set
        {
            if (Cfg.AutoFlip == value) return;
            Cfg.AutoFlip = value;
            OnPropertyChanged();
            UpdateFlipFromSettings();
        }
    }

    /// <summary>显示最后一步标记。</summary>
    public bool ShowLastMove
    {
        get => Cfg.ShowLastMove;
        set
        {
            if (Cfg.ShowLastMove == value) return;
            Cfg.ShowLastMove = value;
            OnPropertyChanged();
            RefreshBoardState();
        }
    }

    /// <summary>显示棋盘坐标。</summary>
    public bool ShowCoordinates
    {
        get => Cfg.ShowCoordinates;
        set
        {
            if (Cfg.ShowCoordinates == value) return;
            Cfg.ShowCoordinates = value;
            OnPropertyChanged();
            RefreshBoardState();
        }
    }

    // ---------- 命令 ----------

    /// <summary>棋盘点击（CommandParameter 是 (x, y) 值元组；界面代码后置也直接调 <see cref="HandleBoardClick"/>）。</summary>
    public RelayCommand BoardPointCommand { get; }

    /// <summary>悔棋（同机双人直接撤；联机发请求等对方同意）。</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>认输。</summary>
    public RelayCommand ResignCommand { get; }

    /// <summary>求和。</summary>
    public RelayCommand OfferDrawCommand { get; }

    /// <summary>再来一局。</summary>
    public RelayCommand RematchCommand { get; }

    /// <summary>翻转棋盘。</summary>
    public RelayCommand FlipBoardCommand { get; }

    /// <summary>复制中文棋谱。</summary>
    public RelayCommand CopyNotationCommand { get; }

    /// <summary>主机开始对局（把当前摆棋作为开局，广播给客户端）。</summary>
    public RelayCommand StartOnlineGameCommand { get; }

    /// <summary>应用当前摆棋并开始（离线 = 开一局同机双人；本地 = 重开；主机 = 开始并广播）。</summary>
    public RelayCommand ApplySetupAndStartCommand { get; }

    /// <summary>切到 / 退出「在棋盘上摆棋」预览。</summary>
    public RelayCommand ToggleSetupModeCommand { get; }

    /// <summary>对局中点「重新摆棋（会结束本局）」：先进入二次确认。</summary>
    public RelayCommand RequestRestartSetupCommand { get; }

    /// <summary>确认结束当前对局、回到摆棋。</summary>
    public RelayCommand ConfirmRestartSetupCommand { get; }

    /// <summary>取消「重新摆棋」的二次确认。</summary>
    public RelayCommand CancelRestartSetupCommand { get; }

    /// <summary>一键恢复标准开局。</summary>
    public RelayCommand ApplyDefaultSetupCommand { get; }

    /// <summary>清空摆棋草稿。</summary>
    public RelayCommand ClearSetupCommand { get; }

    /// <summary>套用让子预设（CommandParameter = <see cref="XiangqiSetup.Preset"/>）。</summary>
    public RelayCommand ApplyHandicapCommand { get; }

    /// <summary>选中调色板里的一枚棋子（CommandParameter = <see cref="XiangqiPaletteItem"/>）。</summary>
    public RelayCommand SelectPaletteCommand { get; }

    // ---------- 棋盘点击 ----------

    /// <summary>
    /// 棋盘点击入口：摆棋时放子 / 删子；对局时选子 / 走子。
    /// 非法点击一律给一句中文提示（不静默）。
    /// </summary>
    public void HandleBoardClick(int x, int y)
    {
        if (!XiangqiBoard.InBounds(x, y)) return;

        if (ShowSetupBoard)
        {
            if (!IsSetupAllowed)
            {
                SetNotice("联机对局已经开始，不能修改摆棋", "DangerBrush");
                return;
            }
            PlaceDraftPiece(x, y);
            return;
        }

        if (!_game.Started)
        {
            SetNotice(IsClient ? "等待房主开始对局…" : "对局还没开始", "TextSecondaryBrush");
            return;
        }
        if (_game.IsOver)
        {
            SetNotice($"对局已经结束：{_game.ResultText}（{_game.ResultReason}）,点「再来一局」", "TextSecondaryBrush");
            return;
        }
        if (_waitingHostMove)
        {
            SetNotice("上一步还在等房主确认，稍等一下", "TextSecondaryBrush");
            return;
        }
        if (IsGamePaused || IsReconnecting)
        {
            SetNotice(GamePauseStatusText.Length > 0 ? GamePauseStatusText : "对局暂停，等对方重连回来再下", "TextSecondaryBrush");
            return;
        }
        if (!CanPlayNow)
        {
            SetNotice($"现在轮到{_game.Turn.DisplayName()}走棋，等对方走完再点", "TextSecondaryBrush");
            return;
        }

        if (_selectedPoint is { } from)
        {
            if (from.X == x && from.Y == y)
            {
                ClearSelection();
                return;
            }
            for (var i = 0; i < _legalTargets.Count; i++)
            {
                if (_legalTargets[i].ToX != x || _legalTargets[i].ToY != y) continue;
                PlayMove(from, (x, y));
                return;
            }
            if (TrySelect(x, y)) return;
            ClearSelection();
            return;
        }

        TrySelect(x, y);
    }

    /// <summary>摆棋：把调色板里选中的棋子放到 (x, y)，「删除」= 清掉这一格。</summary>
    private void PlaceDraftPiece(int x, int y)
    {
        if (SelectedPalette is null)
        {
            SetNotice("先在调色板里选一枚棋子（或「删除」），再点棋盘", "TextSecondaryBrush");
            return;
        }

        var item = SelectedPalette;
        _setupBoard.Set(x, y, item.IsDelete ? XiangqiPiece.Empty : item.Piece);
        _appliedHandicap = null;
        _setupLabel = "自定义摆棋";
        RefreshAll();
        SetNotice(item.IsDelete ? "已删除这一格的棋子" : $"已放置：{item.Piece.DisplayName}", "TextSecondaryBrush");
    }

    /// <summary>对局：选中当前行棋方的一枚棋子，显示它的合法落点。</summary>
    private bool TrySelect(int x, int y)
    {
        var side = CurrentPlaySide;
        if (side is null) return false;
        var piece = _game.Board.Get(x, y);
        if (piece.IsEmpty) return false;
        if (piece.Side != side.Value)
        {
            SetNotice($"这是{(piece.Side == XiangqiSide.Red ? "红方" : "黑方")}的棋子，现在轮到{_game.Turn.DisplayName()}走棋", "TextSecondaryBrush");
            return false;
        }

        var moves = _game.LegalMovesFor(x, y);
        if (moves.Count == 0)
        {
            SetNotice("这枚棋子现在没有合法落点（可能被蹩住 / 会被将军）", "TextSecondaryBrush");
            ClearSelection();
            return false;
        }

        _selectedPoint = (x, y);
        _legalTargets = moves;
        NotifySelection();
        return true;
    }

    /// <summary>真正走一步（同机双人 / 主机本地执行；客户端只上报动作）。</summary>
    private void PlayMove((int X, int Y) from, (int X, int Y) to)
    {
        var side = CurrentPlaySide;
        if (side is null) return;
        var move = new XiangqiMove(from.X, from.Y, to.X, to.Y);
        var captured = _game.Board.Get(to.X, to.Y);

        if (Mode == BoardPlayMode.Client)
        {
            if (!XiangqiRules.IsLegalMove(_game.Board, side.Value, move))
            {
                SetNotice("这步棋不合规则（不能送将 / 走法不对）", "DangerBrush");
                ClearSelection();
                return;
            }
            // 客户端只上报动作，棋盘等主机广播的快照再更新（主机是权威）
            SendAction("move", new { FromX = move.FromX, FromY = move.FromY, ToX = move.ToX, ToY = move.ToY });
            _waitingHostMove = true;
            ClearSelection();
            RefreshAll();
            SetNotice("已提交走法，等房主确认…", "TextSecondaryBrush");
            return;
        }

        var error = _game.Play(side.Value, move.FromX, move.FromY, move.ToX, move.ToY);
        if (error is not null)
        {
            SetNotice(error, "DangerBrush");
            ClearSelection();
            return;
        }

        ClearSelection();
        if (Mode == BoardPlayMode.Host) BroadcastSnapshot();
        RefreshAll();
        AnnounceAfterMove(captured);
    }

    /// <summary>走完 / 收到对方走法后给一句人话提示。</summary>
    private void AnnounceAfterMove(XiangqiPiece captured)
    {
        if (_game.IsOver)
        {
            SetNotice($"{_game.ResultText}：{_game.ResultReason}", _game.Result == XiangqiResult.Draw ? "TextSecondaryBrush" : "SuccessBrush");
            AppendSystemChat($"对局结束：{_game.ResultText}（{_game.ResultReason}）");
            return;
        }
        if (_game.IsInCheck)
        {
            SetNotice($"将军！{_game.Turn.DisplayName()}必须应将", "DangerBrush");
            AppendSystemChat("将军！");
            return;
        }
        if (!captured.IsEmpty)
        {
            SetNotice($"吃掉对方的{captured.DisplayChar}，轮到{_game.Turn.DisplayName()}", "SuccessBrush");
            return;
        }
        SetNotice($"轮到{_game.Turn.DisplayName()}走棋", "TextSecondaryBrush");
    }

    /// <summary>当前该谁走（本地/同机 = 棋局轮次；主机 = 主机执边；客户端 = 我方执边）。</summary>
    private XiangqiSide? CurrentPlaySide => Mode switch
    {
        BoardPlayMode.Local => _game.Turn,
        BoardPlayMode.Host => Cfg.HostSideValue,
        BoardPlayMode.Client => XiangqiSides.Parse(_mySide),
        _ => null,
    };

    /// <summary>选中状态变了：通知界面 + 重画棋盘。</summary>
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedPoint));
        OnPropertyChanged(nameof(LegalTargets));
        RefreshBoardState();
    }

    /// <summary>清掉选中与可走点。</summary>
    private void ClearSelection()
    {
        if (_selectedPoint is null && _legalTargets.Count == 0) return;
        _selectedPoint = null;
        _legalTargets = Array.Empty<XiangqiMove>();
        NotifySelection();
    }

    /// <summary>翻转棋盘（手动）。</summary>
    private void FlipBoard()
    {
        _flipped = !_flipped;
        OnPropertyChanged(nameof(IsFlipped));
    }

    // ---------- 悔棋 / 认输 / 求和 / 再来一局 ----------

    /// <summary>能不能悔棋（联机：对方在线且没在重连暂停；同机双人：有棋可悔）。</summary>
    public bool CanUndo => IsOnline ? HasPeer && !IsGamePaused : IsLocalMode && _game.History.Count > 0;

    private void UndoAction()
    {
        if (IsOnline)
        {
            if (!HasPeer)
            {
                SetNotice("对方不在线，暂时不能悔棋", "DangerBrush");
                return;
            }
            RequestUndo();
            return;
        }
        if (IsLocalMode)
        {
            if (!_game.Undo())
            {
                SetNotice("还没有可悔的棋", "TextSecondaryBrush");
                return;
            }
            ClearSelection();
            RefreshAll();
            SetNotice("已悔棋，对方可以重新走", "SuccessBrush");
            return;
        }
        SetNotice("还没进入对局，不能悔棋", "DangerBrush");
    }

    /// <summary>能不能认输。</summary>
    public bool CanResign => IsOnline
        ? HasPeer && !IsGamePaused && _game.Started && !_game.IsOver
        : IsLocalMode && _game.Started && !_game.IsOver;

    private void ResignAction()
    {
        if (IsOnline)
        {
            if (!HasPeer)
            {
                SetNotice("对方不在线，暂时不能认输", "DangerBrush");
                return;
            }
            ResignOnline();
            return;
        }
        if (!IsLocalMode)
        {
            SetNotice("还没进入对局", "DangerBrush");
            return;
        }
        if (!_game.Started || _game.IsOver) return;
        var losing = _game.Turn;
        _game.MarkResigned(losing);
        ClearSelection();
        RefreshAll();
        SetNotice($"{losing.DisplayName()}认输，对局结束", "DangerBrush");
        AppendSystemChat($"{losing.DisplayName()}认输，对局结束");
    }

    /// <summary>能不能求和。</summary>
    public bool CanOfferDraw => CanResign;

    private void OfferDrawAction()
    {
        if (IsOnline)
        {
            if (!HasPeer)
            {
                SetNotice("对方不在线，暂时不能求和", "DangerBrush");
                return;
            }
            OfferDraw();
            return;
        }
        if (!IsLocalMode)
        {
            SetNotice("还没进入对局", "DangerBrush");
            return;
        }
        if (!_game.Started || _game.IsOver) return;
        _game.MarkDraw("同机双人：双方同意和棋");
        ClearSelection();
        RefreshAll();
        SetNotice("和棋", "TextSecondaryBrush");
        AppendSystemChat("同机双人：双方同意和棋");
    }

    /// <summary>能不能再来一局。</summary>
    public bool CanRematch => IsOnline ? HasPeer && !IsGamePaused : IsLocalMode;

    private void RematchAction()
    {
        if (IsOnline)
        {
            if (!HasPeer)
            {
                SetNotice("对方不在线，暂时不能再来一局", "DangerBrush");
                return;
            }
            RequestRematch();
            return;
        }
        if (!IsLocalMode)
        {
            SetNotice("先进入同机双人或房间，再来一局", "DangerBrush");
            return;
        }
        BeginNewGame(swapSides: false);
    }

    /// <summary>复制中文棋谱。</summary>
    private void CopyNotation()
    {
        var text = NotationText;
        if (text.Length == 0)
        {
            SetNotice("还没有棋谱可复制", "TextSecondaryBrush");
            return;
        }
        CopyTextCommand.Execute(text);
    }

    // ---------- 摆棋按钮 ----------

    /// <summary>一键恢复标准开局（用户点名要的按钮）。</summary>
    private void ApplyDefaultSetup()
    {
        _setupBoard.Clear();
        var initial = XiangqiBoard.CreateInitial();
        for (var x = 0; x < XiangqiBoard.Width; x++)
        {
            for (var y = 0; y < XiangqiBoard.Height; y++) _setupBoard.Set(x, y, initial.Get(x, y));
        }
        _appliedHandicap = null;
        _setupLabel = "标准开局";
        _isSetupMode = true;
        RefreshAll();
        SetNotice("已恢复标准开局", "SuccessBrush");
    }

    /// <summary>清空棋盘（自由摆棋的起点）。</summary>
    private void ClearSetup()
    {
        _setupBoard.Clear();
        _appliedHandicap = null;
        _setupLabel = "空棋盘";
        _isSetupMode = true;
        RefreshAll();
        SetNotice("棋盘已清空：从调色板里挑棋子摆吧", "TextSecondaryBrush");
    }

    /// <summary>套用让子预设（从标准开局去掉让子方的棋子）。</summary>
    private void ApplyHandicap(XiangqiSetup.Preset preset)
    {
        if (preset.Handicap is { } handicap)
        {
            CopyBoard(XiangqiSetup.ApplyHandicap(XiangqiBoard.CreateInitial(), Cfg.HandicapGiverValue, handicap), _setupBoard);
            _appliedHandicap = handicap;
        }
        else
        {
            CopyBoard(XiangqiBoard.CreateInitial(), _setupBoard);
            _appliedHandicap = null;
        }
        _setupLabel = preset.Handicap is null ? "标准开局" : preset.Name;
        _isSetupMode = true;
        RefreshAll();
        SetNotice($"已套用「{preset.Name}」：{preset.Description}", "SuccessBrush");
    }

    /// <summary>让子方换了之后，如果当前是某个让子预设就按新让子方重新套一遍。</summary>
    private void ReapplyHandicapAfterGiverChanged()
    {
        if (_appliedHandicap is not { } handicap) return;
        CopyBoard(XiangqiSetup.ApplyHandicap(XiangqiBoard.CreateInitial(), Cfg.HandicapGiverValue, handicap), _setupBoard);
        SetNotice($"让子方改为{Cfg.HandicapGiverValue.DisplayName()}，已重新套用「{XiangqiSetup.HandicapName(handicap)}」", "TextSecondaryBrush");
    }

    /// <summary>把 <paramref name="source"/> 的棋子复制到 <paramref name="target"/>（草稿棋盘是固定实例，换内容不换对象）。</summary>
    private static void CopyBoard(XiangqiBoard source, XiangqiBoard target)
    {
        target.Clear();
        for (var x = 0; x < XiangqiBoard.Width; x++)
        {
            for (var y = 0; y < XiangqiBoard.Height; y++) target.Set(x, y, source.Get(x, y));
        }
    }

    /// <summary>选中调色板项（同时进入摆棋预览，让用户立刻能在棋盘上放子）。</summary>
    private void SelectPalette(XiangqiPaletteItem item)
    {
        SelectedPalette = item;
    }

    /// <summary>切到 / 退出摆棋预览。</summary>
    private void ToggleSetupMode()
    {
        if (!IsSetupAllowed) return;
        _isSetupMode = !_isSetupMode;
        ClearSelection();
        RefreshAll();
        SetNotice(_isSetupMode ? "摆棋预览：点棋盘放子 / 删子" : "已返回对局棋盘", "TextSecondaryBrush");
    }

    // ---------- 开局 / 应用摆棋 ----------

    /// <summary>按当前摆棋草稿造一局正式棋。</summary>
    private XiangqiGame CreateGameFromSetup(bool started)
    {
        var giver = Cfg.HandicapGiverValue;
        var opening = Cfg.OpeningFreeMovesEffective;
        XiangqiSide? openingSide = opening > 0 ? giver.Opposite() : null;
        return new XiangqiGame(_setupBoard, XiangqiSide.Red, opening, openingSide) { Started = started };
    }

    /// <summary>主机开始对局：当前摆棋作为开局，广播给客户端。</summary>
    private void StartOnlineGame()
    {
        if (HasSetupError)
        {
            SetNotice(SetupErrorText, "DangerBrush");
            return;
        }
        _game = CreateGameFromSetup(started: true);
        _isSetupMode = false;
        _isRestartSetupConfirming = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
        BroadcastSnapshot();
        SetNotice($"对局开始！{_game.Turn.DisplayName()}先行", "SuccessBrush");
        AppendSystemChat($"房主开始了对局：{_game.Turn.DisplayName()}先行");
    }

    /// <summary>应用摆棋并开始：离线 = 直接一局同机双人；本地 = 按新摆棋重开；主机 = 开始并广播。</summary>
    private void ApplySetupAndStart()
    {
        if (HasSetupError)
        {
            SetNotice(SetupErrorText, "DangerBrush");
            return;
        }
        if (IsClient) return;
        if (IsHost)
        {
            StartOnlineGame();
            return;
        }

        if (Mode == BoardPlayMode.Offline)
        {
            // 离线时点「应用并开始」= 用摆好的局面开一局同机双人（基类会调 OnLocalModeStarted）
            StartLocalCommand.Execute(null);
            return;
        }

        _game = CreateGameFromSetup(started: true);
        _isSetupMode = false;
        _isRestartSetupConfirming = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
        SetNotice($"对局开始！{_game.Turn.DisplayName()}先行", "SuccessBrush");
        AppendSystemChat($"用当前摆棋开始了新对局：{_game.Turn.DisplayName()}先行");
    }

    /// <summary>「重新摆棋（会结束本局）」第一步：进入二次确认。</summary>
    private void RequestRestartSetup()
    {
        if (!CanRestartSetup) return;
        IsRestartSetupConfirming = true;
        OnPropertyChanged(nameof(ShowRestartSetupButton));
        SetNotice("确定要结束当前对局、重新摆棋吗？", "DangerBrush");
    }

    /// <summary>确认结束当前对局，回到摆棋（本地 / 主机；主机会广播「等待开始」的新局面）。</summary>
    private void ConfirmRestartSetup()
    {
        if (!CanRestartSetup) return;
        IsRestartSetupConfirming = false;
        _game = CreateGameFromSetup(started: false);
        _isSetupMode = true;
        _waitingHostMove = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
        if (IsHost) BroadcastSnapshot();
        SetNotice("已回到摆棋：改完点「开始对局」重新开始", "TextSecondaryBrush");
        AppendSystemChat("结束了当前对局，重新摆棋");
    }

    /// <summary>取消「重新摆棋」的二次确认。</summary>
    private void CancelRestartSetup()
    {
        IsRestartSetupConfirming = false;
        OnPropertyChanged(nameof(ShowRestartSetupButton));
        SetNotice("已取消，对局继续", "TextSecondaryBrush");
    }

    // ---------- 基类钩子：房间 / 棋局 ----------

    /// <summary>主机执边（"red"/"black"），来自配置。</summary>
    protected override string HostSide => Cfg.HostSideValue.ToKey();

    /// <summary>边 ↔ 对面。</summary>
    protected override string OppositeSide(string side)
        => (XiangqiSides.Parse(side) ?? XiangqiSide.Red).Opposite().ToKey();

    /// <summary>边的中文名（"红方"/"黑方"）。</summary>
    protected override string SideDisplayName(string side) => (XiangqiSides.Parse(side) ?? XiangqiSide.Red).DisplayName();

    /// <summary>主机广播 / 欢迎握手用的棋局快照（只读 UI 线程维护好的缓存，网络线程安全）。</summary>
    protected override JsonElement BuildGameSnapshot()
    {
        if (_snapshotCache.ValueKind != JsonValueKind.Undefined) return _snapshotCache;
        // 兜底（理论上到不了这里：构造函数末尾就 RefreshAll 过一次）
        return JsonSerializer.SerializeToElement(new XiangqiSnapshot { Fen = XiangqiSetup.InitialFen },
            RoomProtocol.JsonOptions);
    }

    /// <summary>客户端收到（任意时刻的）局面快照：按快照重建棋局，清掉选中 / 临时状态。</summary>
    protected override void ApplyGameSnapshot(JsonElement game, string mySide)
    {
        try
        {
            var snapshot = game.Deserialize<XiangqiSnapshot>(RoomProtocol.JsonOptions);
            var (restored, error) = XiangqiGame.FromSnapshot(snapshot);
            if (restored is null)
            {
                SetNotice($"同步局面失败：{error}", "DangerBrush");
                return;
            }
            _game = restored;
            _mySide = mySide ?? "";
            _waitingHostMove = false;
            ClearSelection();
            UpdateFlipFromSettings();
            RefreshAll();
            if (_game.IsOver)
                SetNotice($"{_game.ResultText}（{_game.ResultReason}）", "SuccessBrush");
            else if (_game.IsInCheck)
                SetNotice($"将军！{_game.Turn.DisplayName()}请应将", "DangerBrush");
        }
        catch (Exception ex)
        {
            SetNotice($"同步局面失败：{ex.Message}", "DangerBrush");
        }
    }

    /// <summary>主机执行客户端发来的动作（目前只有 "move"）。</summary>
    protected override void OnHostAction(string type, JsonElement? payload)
    {
        if (!string.Equals(type, "move", StringComparison.Ordinal)) return;
        var peerSide = XiangqiSides.Parse(PeerSideForRoom());
        if (peerSide is null) return;
        if (!TryReadMove(payload, out var move))
        {
            SetNotice("收到一个看不懂的走法，已忽略", "DangerBrush");
            BroadcastSnapshot();
            return;
        }

        var captured = _game.Board.Get(move.ToX, move.ToY);
        var error = _game.Play(peerSide.Value, move.FromX, move.FromY, move.ToX, move.ToY);
        if (error is not null)
        {
            SetNotice($"对方的走法无效：{error}", "DangerBrush");
            AppendSystemChat($"对方走法未通过校验：{error}");
            BroadcastSnapshot();       // 回一个快照让客户端解除「等待确认」
            return;
        }

        ClearSelection();
        RefreshAll();
        BroadcastSnapshot();
        AnnounceAfterMove(captured);
    }

    /// <summary>主机按请求方要求悔棋：撤到请求方该走。</summary>
    protected override bool ApplyUndoFor(string requesterSide)
    {
        var side = XiangqiSides.Parse(requesterSide) ?? XiangqiSide.Red;
        if (!_game.UndoToTurn(side)) return false;
        ClearSelection();
        RefreshAll();
        SetNotice($"已按{side.DisplayName()}的请求悔棋", "TextSecondaryBrush");
        AppendSystemChat($"{side.DisplayName()}悔棋成功");
        return true;
    }

    /// <summary>主机判和。</summary>
    protected override void ApplyDraw()
    {
        _game.MarkDraw("双方同意和棋");
        ClearSelection();
        RefreshAll();
        SetNotice("和棋", "TextSecondaryBrush");
        AppendSystemChat("双方同意和棋，本局结束");
    }

    /// <summary>主机判某方认输。</summary>
    protected override void ApplyResign(string side)
    {
        var resigned = XiangqiSides.Parse(side) ?? XiangqiSide.Red;
        _game.MarkResigned(resigned);
        ClearSelection();
        RefreshAll();
        SetNotice($"{resigned.DisplayName()}认输，对局结束", "DangerBrush");
        AppendSystemChat($"{resigned.DisplayName()}认输，本局结束");
    }

    /// <summary>再做一局：换边（可选）后按当前摆棋重开；聊天记录保留、棋谱清空。</summary>
    protected override void BeginNewGame(bool swapSides)
    {
        if (swapSides)
        {
            Cfg.HostSide = Cfg.HostSideValue.Opposite().ToKey();
            OnPropertyChanged(nameof(SelectedHostSide));
        }
        _game = CreateGameFromSetup(started: true);
        _mySide = "";
        _waitingHostMove = false;
        _isSetupMode = false;
        _isRestartSetupConfirming = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
        SetNotice(swapSides ? "再来一局：双方已换边" : "再来一局：新的一局开始", "SuccessBrush");
        AppendSystemChat(swapSides ? "再来一局（双方换边）" : "再来一局");
    }

    /// <summary>主机开房成功：棋局先不开始，等对方加入后点「开始对局」。</summary>
    protected override void OnHostRoomOpened()
    {
        _game = CreateGameFromSetup(started: false);
        _isSetupMode = false;
        _isRestartSetupConfirming = false;
        _waitingHostMove = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
        SetNotice("房间已开：把上面的地址发给对方，对方加入后点「开始对局」", "SuccessBrush");
    }

    /// <summary>进入同机双人：直接用当前摆棋开一局。</summary>
    protected override void OnLocalModeStarted()
    {
        _game = CreateGameFromSetup(started: true);
        _isSetupMode = false;
        _isRestartSetupConfirming = false;
        _waitingHostMove = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
    }

    /// <summary>对手加入：主机提示点「开始对局」；客户端等快照。</summary>
    protected override void OnPeerJoined()
    {
        _waitingHostMove = false;
        RefreshAll();
        if (IsHost && !_game.Started && IsSetupValid)
            SetNotice("对方已加入：点「开始对局」开始下棋", "SuccessBrush");
    }

    /// <summary>对方离开：棋局不重置（等重连），只是暂停（不能走子）。</summary>
    protected override void OnPeerLeft(string? reason)
    {
        _waitingHostMove = false;
        ClearSelection();
        RefreshAll();
        if (IsHost && _game.Started && !_game.IsOver)
            AppendSystemChat("对方掉线了：对局暂停，等对方重连回来继续（棋局没有丢）");
    }

    /// <summary>断线恢复：客户端重连成功 / 主机掉线的对方重新连回来。</summary>
    protected override void OnReconnected()
    {
        _waitingHostMove = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
        SetNotice("已重新连接，继续对局", "SuccessBrush");
        AppendSystemChat("已重新连接，继续对局");
    }

    /// <summary>玩法切换：刷新能不能走棋 / 摆棋 / 翻转，并清掉临时状态。</summary>
    protected override void OnModeChanged()
    {
        _waitingHostMove = false;
        ClearSelection();
        if (Mode != BoardPlayMode.Local) _isSetupMode = false;
        if (!CanRestartSetup) _isRestartSetupConfirming = false;
        UpdateFlipFromSettings();
        RefreshAll();
    }

    /// <summary>对手加入前校验摆棋（有错不让开房）。</summary>
    protected override string? ValidateHostStart() => IsSetupValid ? null : SetupErrorText;

    /// <summary>客户端收到快照后基类会调这里：再刷一遍界面状态。</summary>
    protected override void OnSnapshotApplied() => RefreshAll();

    // ---------- 界面刷新 ----------

    /// <summary>整套刷新：棋盘渲染状态 + 棋谱 + 摆棋提示 + 快照缓存 + 所有计算属性通知。</summary>
    private void RefreshAll()
    {
        RefreshBoardState();
        RebuildMoveLines();
        RefreshSnapshotCache();
        NotifyAllProperties();
        InvalidateCommands();
    }

    /// <summary>刷新网络线程要读的棋局快照缓存（只在这里做序列化，避免和网络线程抢 List）。</summary>
    private void RefreshSnapshotCache()
    {
        try
        {
            _snapshotCache = JsonSerializer.SerializeToElement(_game.ToSnapshot(), RoomProtocol.JsonOptions);
        }
        catch
        {
            // 序列化失败不该影响界面；保留上一份缓存（下一轮再试）
        }
    }

    /// <summary>按「现在该显示草稿还是正式棋局」生成棋盘控件的渲染状态。</summary>
    private void RefreshBoardState()
    {
        var source = ShowSetupBoard ? _setupBoard : _game.Board;
        var cells = new XiangqiPiece[XiangqiBoard.Width * XiangqiBoard.Height];
        for (var y = 0; y < XiangqiBoard.Height; y++)
        {
            for (var x = 0; x < XiangqiBoard.Width; x++) cells[y * XiangqiBoard.Width + x] = source.Get(x, y);
        }

        var showSetup = ShowSetupBoard;
        _boardState = new XiangqiBoardViewState
        {
            Cells = cells,
            LastMove = showSetup ? null : _game.LastMove,
            Selected = showSetup ? null : _selectedPoint,
            LegalTargets = showSetup || !Cfg.ShowLegalMoves ? Array.Empty<XiangqiMove>() : _legalTargets,
            CheckSide = showSetup ? null : _game.IsInCheck ? _game.Turn : null,
            ShowLastMove = !showSetup && Cfg.ShowLastMove,
            ShowCoordinates = Cfg.ShowCoordinates,
        };
        OnPropertyChanged(nameof(BoardState));
    }

    /// <summary>重新生成中文棋谱行。</summary>
    private void RebuildMoveLines()
    {
        MoveLines.Clear();
        foreach (var line in XiangqiNotation.FormatHistory(_game.History)) MoveLines.Add(line);
        OnPropertyChanged(nameof(NotationText));
    }

    /// <summary>把所有计算属性一起通知（绑定不用一个个手写 PropertyChanged）。</summary>
    private void NotifyAllProperties()
    {
        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(ShowSetupBoard));
        OnPropertyChanged(nameof(IsSetupMode));
        OnPropertyChanged(nameof(ShowSetupCard));
        OnPropertyChanged(nameof(IsSetupAllowed));
        OnPropertyChanged(nameof(ToggleSetupModeText));
        OnPropertyChanged(nameof(ShowToggleSetupPreview));
        OnPropertyChanged(nameof(ApplySetupAndStartText));
        OnPropertyChanged(nameof(IsBoardInteractive));
        OnPropertyChanged(nameof(CanPlayNow));
        OnPropertyChanged(nameof(IsStarted));
        OnPropertyChanged(nameof(IsWaitingStart));
        OnPropertyChanged(nameof(IsGamePaused));
        OnPropertyChanged(nameof(GamePauseStatusText));
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(ReconnectStatusText));
        OnPropertyChanged(nameof(TurnText));
        OnPropertyChanged(nameof(CheckText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(MoveCountText));
        OnPropertyChanged(nameof(MySideText));
        OnPropertyChanged(nameof(SelectedPoint));
        OnPropertyChanged(nameof(LegalTargets));
        OnPropertyChanged(nameof(IsFlipped));
        OnPropertyChanged(nameof(ShowStartOnlineButton));
        OnPropertyChanged(nameof(StartGameEnabled));
        OnPropertyChanged(nameof(CanApplySetupAndStart));
        OnPropertyChanged(nameof(StartHintText));
        OnPropertyChanged(nameof(SetupErrorText));
        OnPropertyChanged(nameof(HasSetupError));
        OnPropertyChanged(nameof(IsSetupValid));
        OnPropertyChanged(nameof(SetupStatusText));
        OnPropertyChanged(nameof(SetupSummaryText));
        OnPropertyChanged(nameof(SetupFenText));
        OnPropertyChanged(nameof(OpeningFreeMovesText));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanResign));
        OnPropertyChanged(nameof(CanOfferDraw));
        OnPropertyChanged(nameof(CanRematch));
        OnPropertyChanged(nameof(ShowSetupControls));
        OnPropertyChanged(nameof(ShowSetupCollapsedSummary));
        OnPropertyChanged(nameof(CanRestartSetup));
        OnPropertyChanged(nameof(ShowRestartSetupButton));
        OnPropertyChanged(nameof(IsRestartSetupConfirming));
        OnPropertyChanged(nameof(ShowApplySetupButton));
        OnPropertyChanged(nameof(MySideSummaryText));
        OnPropertyChanged(nameof(PeerSideSummaryText));
    }

    /// <summary>让命令的可用状态重新评估（WPF 的 RequerySuggested 是懒的）。</summary>
    private static void InvalidateCommands()
    {
        try { CommandManager.InvalidateRequerySuggested(); } catch { /* 无头环境忽略 */ }
    }

    /// <summary>按设置 / 执边决定棋盘要不要翻转。</summary>
    private void UpdateFlipFromSettings()
    {
        var side = Mode switch
        {
            BoardPlayMode.Client => XiangqiSides.Parse(_mySide),
            BoardPlayMode.Host => Cfg.HostSideValue,
            _ => (XiangqiSide?)null,
        };
        var flipped = Cfg.AutoFlip && side == XiangqiSide.Black;
        if (_flipped == flipped) return;
        _flipped = flipped;
        OnPropertyChanged(nameof(IsFlipped));
    }

    // ---------- 小工具 ----------

    /// <summary>能不能按 (x, y) 生成调色板（红方 7 种 + 黑方 7 种 + 删除）。</summary>
    private static IReadOnlyList<XiangqiPaletteItem> BuildPaletteItems()
    {
        var items = new List<XiangqiPaletteItem>(15);
        foreach (var type in new[]
                 {
                     XiangqiPieceType.King, XiangqiPieceType.Advisor, XiangqiPieceType.Elephant,
                     XiangqiPieceType.Horse, XiangqiPieceType.Rook, XiangqiPieceType.Cannon, XiangqiPieceType.Pawn,
                 })
        {
            items.Add(XiangqiPaletteItem.For(new XiangqiPiece(type, XiangqiSide.Red)));
        }
        foreach (var type in new[]
                 {
                     XiangqiPieceType.King, XiangqiPieceType.Advisor, XiangqiPieceType.Elephant,
                     XiangqiPieceType.Horse, XiangqiPieceType.Rook, XiangqiPieceType.Cannon, XiangqiPieceType.Pawn,
                 })
        {
            items.Add(XiangqiPaletteItem.For(new XiangqiPiece(type, XiangqiSide.Black)));
        }
        items.Add(XiangqiPaletteItem.Delete);
        return items;
    }

    /// <summary>从客户端动作载荷里读一步棋（兼容 camelCase / PascalCase / 字符串数字）。</summary>
    private static bool TryReadMove(JsonElement? payload, out XiangqiMove move)
    {
        move = XiangqiMove.None;
        if (payload is not { ValueKind: JsonValueKind.Object } element) return false;
        if (!TryReadInt(element, "fromX", out var fromX) || !TryReadInt(element, "fromY", out var fromY)
            || !TryReadInt(element, "toX", out var toX) || !TryReadInt(element, "toY", out var toY)) return false;
        if (!XiangqiBoard.InBounds(fromX, fromY) || !XiangqiBoard.InBounds(toX, toY)) return false;
        move = new XiangqiMove(fromX, fromY, toX, toY);
        return true;
    }

    /// <summary>读一个整数字段（属性名忽略大小写）。</summary>
    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.Number => property.Value.TryGetInt32(out value),
                JsonValueKind.String => int.TryParse(property.Value.GetString(), out value),
                _ => false,
            };
        }
        return false;
    }

    // ---------- 演示数据（截图 / 冒烟用，不影响真实联机） ----------

    /// <summary>
    /// <c>YLT_XIANGQI_DEMO=1</c>：进入同机双人并走一局演示中盘（含吃子 / 最后一步）；
    /// <c>YLT_XIANGQI_SETUP=1</c>：直接展开摆棋并套用「让单马」。
    /// </summary>
    private void ApplyDemoData()
    {
        var demo = Environment.GetEnvironmentVariable("YLT_XIANGQI_DEMO") == "1";
        var setup = Environment.GetEnvironmentVariable("YLT_XIANGQI_SETUP") == "1";
        if (!demo && !setup) return;

        if (setup)
        {
            _isSetupMode = true;
            var preset = XiangqiSetup.Presets.First(p => p.Handicap == XiangqiSetup.XiangqiHandicap.SingleHorse);
            CopyBoard(XiangqiSetup.ApplyHandicap(XiangqiBoard.CreateInitial(), Cfg.HandicapGiverValue,
                XiangqiSetup.XiangqiHandicap.SingleHorse), _setupBoard);
            _appliedHandicap = XiangqiSetup.XiangqiHandicap.SingleHorse;
            _setupLabel = preset.Name;
            SelectedPalette ??= PaletteItems.FirstOrDefault();
            RefreshAll();
            SetNotice("演示：摆棋面板（已套用「让单马」）", "TextSecondaryBrush");
            return;
        }

        // 演示中盘：先进入同机双人（会按摆棋开局），再按脚本走 11 步
        StartLocalCommand.Execute(null);
        var script = new (int FromX, int FromY, int ToX, int ToY)[]
        {
            (7, 7, 4, 7),   // 1. 红 炮二平五
            (7, 0, 6, 2),   // 2. 黑 马8进7
            (7, 9, 6, 7),   // 3. 红 马二进三
            (8, 0, 7, 0),   // 4. 黑 车9平8
            (8, 9, 7, 9),   // 5. 红 车一平二
            (6, 3, 6, 4),   // 6. 黑 卒7进1
            (2, 6, 2, 5),   // 7. 红 兵七进一
            (7, 2, 7, 6),   // 8. 黑 炮8进4
            (7, 9, 7, 6),   // 9. 红 车二进三（吃炮）
            (1, 0, 2, 2),   // 10. 黑 马2进3
            (4, 7, 4, 3),   // 11. 红 炮五进四（吃卒）
        };
        foreach (var (fromX, fromY, toX, toY) in script)
        {
            if (_game.Play(_game.Turn, fromX, fromY, toX, toY) is not null) break;
        }
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
        SetNotice("演示数据：11 步中盘（含吃子与最后一步标记）", "TextSecondaryBrush");
    }
}
