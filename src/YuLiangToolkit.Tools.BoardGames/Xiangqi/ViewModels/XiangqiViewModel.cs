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
/// 「联机中国象棋」界面逻辑：多人房间（交给 <see cref="BoardGameRoomViewModelBase"/>：成员 / 席位 / 自动轮换 /
/// 观战 / 请求路由）+ 棋局 + 自由摆棋 / 让子 / 让先。
///
/// <para><b>分层</b>：规则与棋局全在 <see cref="XiangqiGame"/> / <see cref="XiangqiRules"/> / <see cref="XiangqiSetup"/> /
/// <see cref="XiangqiNotation"/> 里（纯逻辑、可自检），本类只负责：把草稿棋盘和正式棋局分开、把点击翻译成命令、
/// 把棋局翻译成棋盘控件的渲染状态、以及「主机权威 / 客户端只从状态包更新」的约定。</para>
///
/// <para><b>两种棋盘</b>：<c>_setupBoard</c> 是「摆棋草稿」（房主在房间未开始时随便改，不影响进行中的对局）；
/// <c>_game</c> 是正式棋局。没开局 / 房主摆棋时棋盘显示草稿，开局后显示正式棋局。</para>
///
/// <para><b>席位</b>：基类维护两个上场席位（先手位 = 红方、后手位 = 黑方），本端执哪边只看基类
/// <see cref="BoardGameRoomViewModelBase.LocalSide"/>；观战 / 同机双人 / 离线都为空字符串（只读）。</para>
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

    /// <summary>造一个象棋工具界面（配置 / 摆棋草稿 / 正式棋局 / 命令全部备好）。</summary>
    public XiangqiViewModel() : base("xiangqi", "中国象棋")
    {
        Cfg = LoadConfig<XiangqiConfig>();
        AttachRoomConfig(Cfg);        // 演示房间（YLT_BOARD_DEMO_ROOM）会在这里把成员 / 席位备好

        _setupBoard = XiangqiBoard.CreateInitial();
        _game = CreateGameFromSetup(started: false);
        PaletteItems = BuildPaletteItems();

        // ------- 命令（XAML 直接绑）-------
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
        ApplySetupAndStartCommand = new RelayCommand(_ => ApplySetupAndStart(), _ => CanApplySetupAndStart);
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
            if (parameter is XiangqiPaletteItem item) SelectedPalette = item;
        });

        // 基类的房间 / 座位 / 暂停状态一变，就让「能不能下棋 / 按钮可用性」跟着刷新
        PropertyChanged += OnBaseStatePropertyChanged;
        RefreshAll();
        ApplyDemoData();
    }

    // ---------- 基类状态 → 派生属性刷新 ----------

    /// <summary>会让派生状态变化的基类属性名（只监听这些，避免自己通知自己造成递归）。</summary>
    private static readonly string[] BaseStatePropertyNames =
    {
        nameof(Mode), nameof(IsLocalMode), nameof(IsHost), nameof(IsClient), nameof(IsOnline),
        nameof(IsGamePaused), nameof(IsReconnecting), nameof(IsGameRunning),
        nameof(IsSeatedPlayer), nameof(IsSpectator), nameof(LocalSide), nameof(ShowPlayerActions),
    };

    private void OnBaseStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null) return;
        if (Array.IndexOf(BaseStatePropertyNames, e.PropertyName) < 0) return;
        RefreshTransientState();
    }

    /// <summary>只刷新「随房间状态变化」的派生属性（不重画棋盘 / 不重建棋谱，避免重连时闪）。</summary>
    private void RefreshTransientState()
    {
        OnPropertyChanged(nameof(CanPlayNow));
        OnPropertyChanged(nameof(IsBoardInteractive));
        OnPropertyChanged(nameof(ShowSetupCard));
        OnPropertyChanged(nameof(ShowSetupBoard));
        OnPropertyChanged(nameof(IsSetupAllowed));
        OnPropertyChanged(nameof(ShowSetupControls));
        OnPropertyChanged(nameof(ShowSetupCollapsedSummary));
        OnPropertyChanged(nameof(CanRestartSetup));
        OnPropertyChanged(nameof(ShowRestartSetupButton));
        OnPropertyChanged(nameof(ShowApplySetupButton));
        OnPropertyChanged(nameof(CanApplySetupAndStart));
        OnPropertyChanged(nameof(ApplySetupAndStartText));
        OnPropertyChanged(nameof(CanEditPreferredSeat));
        OnPropertyChanged(nameof(TurnText));
        OnPropertyChanged(nameof(CheckText));
        OnPropertyChanged(nameof(IsStarted));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanResign));
        OnPropertyChanged(nameof(CanOfferDraw));
        OnPropertyChanged(nameof(CanRematch));
        OnPropertyChanged(nameof(ShowRematchButton));
        OnPropertyChanged(nameof(RematchButtonText));
        InvalidateCommands();
    }

    // ---------- 配置 ----------

    /// <summary>象棋自己的设置（房间相关字段由基类接管，其余在这里）。</summary>
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

    /// <summary>房主默认坐席选项（红方 / 黑方）。</summary>
    public IReadOnlyList<OptionItem<XiangqiSide>> HostSideOptions { get; } = OptionItem.From(
        (XiangqiSide.Red, "先手位（红方）"),
        (XiangqiSide.Black, "后手位（黑方）"));

    /// <summary>
    /// 房主默认坐哪一边（开房时由基类按 <see cref="PreferredHostSeatSide"/> 安排）。
    /// <b>在线时不能改</b>（当前房间的席位由 RosterPanel 安排），下次开房生效。
    /// </summary>
    public OptionItem<XiangqiSide>? SelectedHostSide
    {
        get => OptionItem<XiangqiSide>.Find(HostSideOptions, Cfg.HostSideValue) ?? HostSideOptions[0];
        set
        {
            if (value is null) return;
            if (IsOnline)
            {
                OnPropertyChanged();                       // 在线时改不了：把下拉弹回原值
                return;
            }
            if (Cfg.HostSideValue == value.Value) return;
            Cfg.HostSide = value.Value.ToKey();
            OnPropertyChanged();
            UpdateFlipFromSettings();
            RefreshAll();
        }
    }

    /// <summary>「房主默认坐席」下拉能不能改（只有没进房间时能改）。</summary>
    public bool CanEditPreferredSeat => !IsOnline;

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
                                  || ((IsLocalMode || IsHost) && !_game.Started);

    /// <summary>正在摆棋预览（本地「重新摆棋」确认后进入）。</summary>
    public bool IsSetupMode => _isSetupMode;

    /// <summary>摆棋卡片要不要显示（客户端 = 只下棋 / 观战，不显示摆棋）。</summary>
    public bool ShowSetupCard => Mode != BoardPlayMode.Client;

    /// <summary>
    /// 摆棋能不能编辑：房主 / 同机双人 / 离线在<b>对局未开始时</b>可以摆（房主不坐席位也能摆）；
    /// 对局进行中 / 客户端一律锁定。
    /// </summary>
    public bool IsSetupAllowed => ShowSetupCard && !_game.Started;

    /// <summary>摆棋卡展开（未开局 / 本地重新摆棋中）。</summary>
    public bool ShowSetupControls => !_game.Started || _isSetupMode;

    /// <summary>摆棋卡收起成一行摘要（对局进行中 / 已结束；联机时作废由 RosterPanel 的「结束本局」负责）。</summary>
    public bool ShowSetupCollapsedSummary => _game.Started && !_isSetupMode;

    /// <summary>能不能「重新摆棋（会结束本局）」：<b>只有同机双人</b>（联机走基类「结束本局（作废）」）。</summary>
    public bool CanRestartSetup => IsLocalMode && _game.Started;

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

    /// <summary>「应用并开始」按钮要不要显示：只用于离线 / 同机双人（联机由 RosterPanel 开始）。</summary>
    public bool ShowApplySetupButton => Mode == BoardPlayMode.Offline || IsLocalMode;

    /// <summary>「应用并开始」按钮的文字。</summary>
    public string ApplySetupAndStartText => IsLocalMode
        ? "按当前摆棋重开一局"
        : "应用并开始（同机双人）";

    /// <summary>「应用并开始」能不能点。</summary>
    public bool CanApplySetupAndStart => IsSetupValid && ShowApplySetupButton;

    /// <summary>棋盘能不能点（摆棋模式放子；对局模式走子）。</summary>
    public bool IsBoardInteractive => ShowSetupBoard ? IsSetupAllowed : CanPlayNow;

    /// <summary>
    /// 现在能不能落子：
    /// 同机双人 = 对局中即可；联机 = 我坐在席位上 && 在线 && 没暂停 / 重连 && 轮到我执的这边。
    /// 观战 / 离线 / 对局未开始 / 已结束一律 false。
    /// </summary>
    public bool CanPlayNow
    {
        get
        {
            if (!_game.Started || _game.IsOver || _waitingHostMove) return false;
            if (IsGamePaused || IsReconnecting) return false;
            if (IsLocalMode) return true;
            if (!IsSeatedPlayer) return false;                 // 观战 / 未入座：只读
            var side = XiangqiSides.Parse(LocalSide);
            return side is not null && _game.Turn == side.Value;
        }
    }

    /// <summary>对局是否已经开始。</summary>
    public bool IsStarted => _game.Started;

    /// <summary>轮次文案。</summary>
    public string TurnText
    {
        get
        {
            if (ShowSetupBoard)
            {
                return Mode == BoardPlayMode.Offline
                    ? "摆棋中：调好棋子后点「应用并开始」"
                    : "摆棋中：房主摆好后在右侧「房间成员」里点开始";
            }
            if (!_game.Started) return "对局还没开始";
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

    /// <summary>当前选中棋子的坐标（没选中为 null）。</summary>
    public (int X, int Y)? SelectedPoint => _selectedPoint;

    /// <summary>当前选中棋子的合法落点。</summary>
    public IReadOnlyList<XiangqiMove> LegalTargets => _legalTargets;

    /// <summary>棋盘是否翻转（执黑 / 手动翻转 / 房主偏好）。</summary>
    public bool IsFlipped => _flipped;

    /// <summary>中文记谱（一行两条半回合）。</summary>
    public string NotationText => XiangqiNotation.BuildHistoryText(_game.History);

    /// <summary>棋谱列表（绑定 ListBox）。</summary>
    public ObservableCollection<string> MoveLines { get; } = new();

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

    /// <summary>悔棋（同机双人直接撤；联机坐在席位上时发请求等对方 / 房主）。</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>认输。</summary>
    public RelayCommand ResignCommand { get; }

    /// <summary>求和。</summary>
    public RelayCommand OfferDrawCommand { get; }

    /// <summary>再来一局（同机双人直接重开；联机请求房主 / 对手）。</summary>
    public RelayCommand RematchCommand { get; }

    /// <summary>翻转棋盘。</summary>
    public RelayCommand FlipBoardCommand { get; }

    /// <summary>复制中文棋谱。</summary>
    public RelayCommand CopyNotationCommand { get; }

    /// <summary>应用当前摆棋并开始（离线 = 开一局同机双人；同机双人 = 按新摆棋重开）。</summary>
    public RelayCommand ApplySetupAndStartCommand { get; }

    /// <summary>本地对局中点「重新摆棋（会结束本局）」：先进入二次确认。</summary>
    public RelayCommand RequestRestartSetupCommand { get; }

    /// <summary>确认结束当前本地对局、回到摆棋。</summary>
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
    /// 棋盘点击入口：摆棋时放子 / 删子；对局时选子 / 走子。观战、暂停、不是自己回合都给一句中文提示。
    /// </summary>
    public void HandleBoardClick(int x, int y)
    {
        if (!XiangqiBoard.InBounds(x, y)) return;

        if (ShowSetupBoard)
        {
            if (!IsSetupAllowed)
            {
                SetNotice("对局进行中，摆棋已锁定；要改摆法请先结束本局", "DangerBrush");
                return;
            }
            PlaceDraftPiece(x, y);
            return;
        }

        if (IsSpectator)
        {
            SetNotice("你在观战：等房主安排上场后才能走棋；可以看棋、聊天", "TextSecondaryBrush");
            return;
        }
        if (!_game.Started)
        {
            SetNotice("对局还没开始，等房主点「开始对局」", "TextSecondaryBrush");
            return;
        }
        if (_game.IsOver)
        {
            SetNotice($"对局已经结束：{_game.ResultText}（{_game.ResultReason}）", "TextSecondaryBrush");
            return;
        }
        if (_waitingHostMove)
        {
            SetNotice("上一步还在等房主确认，稍等一下", "TextSecondaryBrush");
            return;
        }
        if (IsGamePaused || IsReconnecting)
        {
            SetNotice(GamePauseStatusText.Length > 0 ? GamePauseStatusText : "对局暂停，稍等一下", "TextSecondaryBrush");
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

    /// <summary>真正走一步（同机双人 / 主机本地执行；客户端只上报动作，等状态包更新）。</summary>
    private void PlayMove((int X, int Y) from, (int X, int Y) to)
    {
        var side = CurrentPlaySide;
        if (side is null) return;
        var move = new XiangqiMove(from.X, from.Y, to.X, to.Y);
        var captured = _game.Board.Get(to.X, to.Y);

        if (Mode == BoardPlayMode.Client)
        {
            if (!IsSeatedPlayer)
            {
                SetNotice("你在观战，不能走棋", "DangerBrush");
                ClearSelection();
                return;
            }
            if (!XiangqiRules.IsLegalMove(_game.Board, side.Value, move))
            {
                SetNotice("这步棋不合规则（不能送将 / 走法不对）", "DangerBrush");
                ClearSelection();
                return;
            }
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
            SetNotice($"{_game.ResultText}：{_game.ResultReason}",
                _game.Result == XiangqiResult.Draw ? "TextSecondaryBrush" : "SuccessBrush");
            return;
        }
        if (_game.IsInCheck)
        {
            SetNotice($"将军！{_game.Turn.DisplayName()}必须应将", "DangerBrush");
            return;
        }
        if (!captured.IsEmpty) SetNotice($"吃掉对方的{captured.DisplayChar}，轮到{_game.Turn.DisplayName()}", "SuccessBrush");
        else SetNotice($"轮到{_game.Turn.DisplayName()}走棋", "TextSecondaryBrush");
    }

    /// <summary>当前该谁执子（同机 = 棋局轮次；联机 = 本端座位对应的边）。</summary>
    private XiangqiSide? CurrentPlaySide => IsLocalMode ? _game.Turn : XiangqiSides.Parse(LocalSide);

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

    /// <summary>能不能悔棋（同机双人：有棋可悔；联机：坐在席位上且没暂停 / 重连）。</summary>
    public bool CanUndo => IsLocalMode
        ? _game.History.Count > 0
        : IsSeatedPlayer && IsOnline && !IsGamePaused;

    /// <summary>能不能认输 / 求和。</summary>
    public bool CanResign => IsLocalMode
        ? _game.Started && !_game.IsOver
        : IsSeatedPlayer && IsOnline && !IsGamePaused && _game.Started && !_game.IsOver;

    /// <summary>能不能求和（同认输条件）。</summary>
    public bool CanOfferDraw => CanResign;

    /// <summary>「再来一局」按钮可见：同机双人 或 联机坐席客户端玩家（房主用 RosterPanel 的「开始下一局」）。</summary>
    public bool ShowRematchButton => IsLocalMode || (IsClient && IsSeatedPlayer);

    /// <summary>「再来一局」按钮文案：同机双人直接重开；联机客户端玩家是向房主请求。</summary>
    public string RematchButtonText => IsClient ? "请求再来一局" : "再来一局";

    /// <summary>
    /// 能不能再来一局：同机双人随时可重开；联机客户端玩家只在「本局结束」后请求房主开始下一局；
    /// 房主不走这个入口（用 RosterPanel 的「开始下一局」）。
    /// </summary>
    public bool CanRematch
    {
        get
        {
            if (IsGamePaused) return false;
            if (IsLocalMode) return _game.Started || _game.History.Count > 0;
            return IsClient && IsSeatedPlayer && IsOnline && _game.IsOver;
        }
    }

    private void UndoAction()
    {
        if (IsOnline)
        {
            if (!IsSeatedPlayer)
            {
                SetNotice("你在观战，不能悔棋", "DangerBrush");
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

    private void ResignAction()
    {
        if (IsOnline)
        {
            if (!IsSeatedPlayer)
            {
                SetNotice("你在观战，不能认输", "DangerBrush");
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
    }

    private void OfferDrawAction()
    {
        if (IsOnline)
        {
            if (!IsSeatedPlayer)
            {
                SetNotice("你在观战，不能求和", "DangerBrush");
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
    }

    private void RematchAction()
    {
        if (IsOnline)
        {
            if (!IsClient)
            {
                SetNotice("房主用右侧「房间成员」里的「开始下一局」开始新的一局", "TextSecondaryBrush");
                return;
            }
            if (!IsSeatedPlayer)
            {
                SetNotice("你在观战，不能发起再来一局", "DangerBrush");
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
        SetNotice("新的一局开始", "SuccessBrush");
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
        CopyBoard(XiangqiBoard.CreateInitial(), _setupBoard);
        _appliedHandicap = null;
        _setupLabel = "标准开局";
        RefreshAll();
        SetNotice("已恢复标准开局", "SuccessBrush");
    }

    /// <summary>清空棋盘（自由摆棋的起点）。</summary>
    private void ClearSetup()
    {
        _setupBoard.Clear();
        _appliedHandicap = null;
        _setupLabel = "空棋盘";
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

    /// <summary>把 <paramref name="source"/> 的棋子复制到 <paramref name="target"/>（草稿棋盘是固定实例）。</summary>
    private static void CopyBoard(XiangqiBoard source, XiangqiBoard target)
    {
        target.Clear();
        for (var x = 0; x < XiangqiBoard.Width; x++)
        {
            for (var y = 0; y < XiangqiBoard.Height; y++) target.Set(x, y, source.Get(x, y));
        }
    }

    /// <summary>「重新摆棋（会结束本局）」第一步：进入二次确认（只用于同机双人）。</summary>
    private void RequestRestartSetup()
    {
        if (!CanRestartSetup) return;
        IsRestartSetupConfirming = true;
        OnPropertyChanged(nameof(ShowRestartSetupButton));
        SetNotice("确定要结束当前对局、重新摆棋吗？", "DangerBrush");
    }

    /// <summary>确认结束当前同机双人对局，回到摆棋草稿。</summary>
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
        SetNotice("已回到摆棋：改完点「应用并开始」重新开局", "TextSecondaryBrush");
    }

    /// <summary>取消「重新摆棋」的二次确认。</summary>
    private void CancelRestartSetup()
    {
        IsRestartSetupConfirming = false;
        OnPropertyChanged(nameof(ShowRestartSetupButton));
        SetNotice("已取消，对局继续", "TextSecondaryBrush");
    }

    // ---------- 开局 / 应用摆棋 ----------

    /// <summary>按当前摆棋草稿造一局正式棋（Started 由调用方决定）。</summary>
    private XiangqiGame CreateGameFromSetup(bool started)
    {
        var giver = Cfg.HandicapGiverValue;
        var opening = Cfg.OpeningFreeMovesEffective;
        XiangqiSide? openingSide = opening > 0 ? giver.Opposite() : null;
        return new XiangqiGame(_setupBoard, XiangqiSide.Red, opening, openingSide) { Started = started };
    }

    /// <summary>离线 / 同机双人：按当前摆棋开始一局本地对局。</summary>
    private void ApplySetupAndStart()
    {
        if (HasSetupError)
        {
            SetNotice(SetupErrorText, "DangerBrush");
            return;
        }
        if (Mode == BoardPlayMode.Offline)
        {
            // 离线时点「应用并开始」= 用摆好的局面开一局同机双人（基类会调 OnLocalModeStarted）
            StartLocalCommand.Execute(null);
            return;
        }
        if (!IsLocalMode) return;           // 联机开局走 RosterPanel 的基类开始命令

        _game = CreateGameFromSetup(started: true);
        _isSetupMode = false;
        _isRestartSetupConfirming = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
        SetNotice("对局开始！" + $"{(SetupFirstSide == XiangqiSide.Red ? "红方" : "黑方")}先行", "SuccessBrush");
    }

    // ---------- 基类钩子：房间 / 棋局 ----------

    /// <summary>先手位执红（中国象棋红先）。</summary>
    protected override string FirstSeatSide => "red";

    /// <summary>房主开房时默认坐哪边（读配置：房主默认坐席 / 执红 or 执黑）。</summary>
    protected override string PreferredHostSeatSide => Cfg.HostSideValue.ToKey();

    /// <summary>边 ↔ 对面。</summary>
    protected override string OppositeSide(string side)
        => (XiangqiSides.Parse(side) ?? XiangqiSide.Red).Opposite().ToKey();

    /// <summary>边的中文名（"红方"/"黑方"）。</summary>
    protected override string SideDisplayName(string side) => (XiangqiSides.Parse(side) ?? XiangqiSide.Red).DisplayName();

    /// <summary>当前棋局状态快照（基类判断暂停 / 开始下一局 / 自动轮换用）。</summary>
    protected override BoardGameStatus ReportGameStatus()
    {
        var over = _game.IsOver;
        string? winner = _game.Result switch
        {
            XiangqiResult.RedWin => "red",
            XiangqiResult.BlackWin => "black",
            _ => null,
        };
        return new BoardGameStatus(_game.Started, over, over ? winner : null);
    }

    /// <summary>回到「未开始」：按当前摆棋草稿重置棋盘（开房 / 作废本局时基类调用；不写聊天）。</summary>
    protected override void ResetGameToWaiting()
    {
        _game = CreateGameFromSetup(started: false);
        _isSetupMode = false;
        _isRestartSetupConfirming = false;
        _waitingHostMove = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
    }

    /// <summary>主机广播 / welcome 用的棋局快照（基类会缓存并在网络线程读取；这里只读游戏对象）。</summary>
    protected override JsonElement BuildGameSnapshot()
        => JsonSerializer.SerializeToElement(_game.ToSnapshot(), RoomProtocol.JsonOptions);

    /// <summary>客户端收到（任意时刻的）状态包里的棋局：按快照重建，清掉选中 / 临时状态。</summary>
    protected override void ApplyGameSnapshot(JsonElement game, string mySide)
    {
        try
        {
            var snapshot = game.Deserialize<XiangqiSnapshot>(RoomProtocol.JsonOptions);
            var (restored, error) = XiangqiGame.FromSnapshot(snapshot);
            if (restored is null)
            {
                SetNotice($"同步棋局失败：{error}", "DangerBrush");
                return;
            }
            _game = restored;
            _waitingHostMove = false;
            ClearSelection();
            UpdateFlipFromSettings();
            RefreshAll();
            NotifyGameFinishedIfNeeded();       // 客户端调用无副作用；保持契约一致

            if (_game.IsOver) SetNotice($"{_game.ResultText}（{_game.ResultReason}）", "SuccessBrush");
            else if (_game.IsInCheck) SetNotice($"将军！{_game.Turn.DisplayName()}请应将", "DangerBrush");
        }
        catch (Exception ex)
        {
            SetNotice($"同步棋局失败：{ex.Message}", "DangerBrush");
        }
    }

    /// <summary>主机执行某个成员发来的动作（目前只有 "move"；fromSide 由基类按席位校验后给出）。</summary>
    protected override void OnHostAction(string fromMemberId, string fromSide, string type, JsonElement? payload)
    {
        if (!string.Equals(type, "move", StringComparison.Ordinal)) return;
        var side = XiangqiSides.Parse(fromSide);
        if (side is null)
        {
            SetNotice("收到一个无法识别执边的走法，已忽略", "DangerBrush");
            BroadcastSnapshot();
            return;
        }
        if (!TryReadMove(payload, out var move))
        {
            SetNotice("收到一个看不懂的走法，已忽略", "DangerBrush");
            BroadcastSnapshot();
            return;
        }

        var captured = _game.Board.Get(move.ToX, move.ToY);
        var error = _game.Play(side.Value, move.FromX, move.FromY, move.ToX, move.ToY);
        if (error is not null)
        {
            SetNotice($"对方的走法无效：{error}", "DangerBrush");
            AppendSystemChat($"对方走法未通过校验：{error}");
            BroadcastSnapshot();       // 回一个状态让客户端解除「等待确认」
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
        return true;
    }

    /// <summary>主机判和。</summary>
    protected override void ApplyDraw()
    {
        _game.MarkDraw("双方同意和棋");
        ClearSelection();
        RefreshAll();
        SetNotice("和棋", "TextSecondaryBrush");
    }

    /// <summary>主机判某方认输。</summary>
    protected override void ApplyResign(string side)
    {
        var resigned = XiangqiSides.Parse(side) ?? XiangqiSide.Red;
        _game.MarkResigned(resigned);
        ClearSelection();
        RefreshAll();
        SetNotice($"{resigned.DisplayName()}认输，对局结束", "DangerBrush");
    }

    /// <summary>开始（下一）局：用当前摆棋 / 让子 / 让先重置并 Started=true（换边由基类席位决定）。</summary>
    protected override void BeginNewGame(bool swapSides)
    {
        _game = CreateGameFromSetup(started: true);
        _isSetupMode = false;
        _isRestartSetupConfirming = false;
        _waitingHostMove = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
    }

    /// <summary>开房成功：棋局已在基类里通过 <see cref="ResetGameToWaiting"/> 重置，这里只刷新界面。</summary>
    protected override void OnHostRoomOpened() => RefreshAll();

    /// <summary>进入同机双人：用当前摆棋草稿开一局（基类负责提示 / 聊天）。</summary>
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

    /// <summary>玩法切换：清临时状态、按新玩法刷新翻转与可操作性。</summary>
    protected override void OnModeChanged()
    {
        _waitingHostMove = false;
        ClearSelection();
        if (!IsLocalMode) _isSetupMode = false;
        if (!CanRestartSetup) _isRestartSetupConfirming = false;
        UpdateFlipFromSettings();
        RefreshAll();
    }

    /// <summary>客户端收到并应用完状态包：再刷一遍界面状态。</summary>
    protected override void OnSnapshotApplied() => RefreshAll();

    /// <summary>席位变化 / 本端座位变化：本端执边与能不能走棋都变了。</summary>
    protected override void OnSeatsChanged()
    {
        _waitingHostMove = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
    }

    /// <summary>成员列表变化：刷新与在线成员相关的界面状态。</summary>
    protected override void OnMembersChanged() => RefreshAll();

    /// <summary>断线恢复：基类已经清了暂停 / 写了提示，这里只按最新状态刷新界面。</summary>
    protected override void OnReconnected() => RefreshAll();

    /// <summary>开房 / 开始对局前的校验：摆棋草稿不合法时不允许开始。</summary>
    protected override string? ValidateHostStart() => IsSetupValid ? null : SetupErrorText;

    // ---------- 界面刷新 ----------

    /// <summary>整套刷新：棋盘渲染状态 + 棋谱 + 摆棋提示 + 所有计算属性通知 + 结束判定。</summary>
    private void RefreshAll()
    {
        RefreshBoardState();
        RebuildMoveLines();
        NotifyAllProperties();
        InvalidateCommands();
        NotifyGameFinishedIfNeeded();          // 每局只处理一次：加分 / 自动轮换 / 广播 / 提示
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
        OnPropertyChanged(nameof(BoardState));
        OnPropertyChanged(nameof(ShowSetupBoard));
        OnPropertyChanged(nameof(IsSetupMode));
        OnPropertyChanged(nameof(ShowSetupCard));
        OnPropertyChanged(nameof(IsSetupAllowed));
        OnPropertyChanged(nameof(ShowSetupControls));
        OnPropertyChanged(nameof(ShowSetupCollapsedSummary));
        OnPropertyChanged(nameof(CanRestartSetup));
        OnPropertyChanged(nameof(ShowRestartSetupButton));
        OnPropertyChanged(nameof(IsRestartSetupConfirming));
        OnPropertyChanged(nameof(ShowApplySetupButton));
        OnPropertyChanged(nameof(CanApplySetupAndStart));
        OnPropertyChanged(nameof(ApplySetupAndStartText));
        OnPropertyChanged(nameof(CanEditPreferredSeat));
        OnPropertyChanged(nameof(IsBoardInteractive));
        OnPropertyChanged(nameof(CanPlayNow));
        OnPropertyChanged(nameof(IsStarted));
        OnPropertyChanged(nameof(TurnText));
        OnPropertyChanged(nameof(CheckText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(MoveCountText));
        OnPropertyChanged(nameof(SelectedPoint));
        OnPropertyChanged(nameof(LegalTargets));
        OnPropertyChanged(nameof(IsFlipped));
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
        OnPropertyChanged(nameof(ShowRematchButton));
        OnPropertyChanged(nameof(RematchButtonText));
    }

    /// <summary>让命令的可用状态重新评估（WPF 的 RequerySuggested 是懒的）。</summary>
    private static void InvalidateCommands()
    {
        try { CommandManager.InvalidateRequerySuggested(); } catch { /* 无头环境忽略 */ }
    }

    /// <summary>按设置 / 本端执边决定棋盘要不要翻转（观战不翻）。</summary>
    private void UpdateFlipFromSettings()
    {
        XiangqiSide? side = null;
        if (!IsLocalMode)
        {
            var local = XiangqiSides.Parse(LocalSide);
            if (local is not null) side = local;
            else if (IsHost && IsOnline) side = Cfg.HostSideValue;   // 房主观战时按默认坐席偏好摆
        }
        var flipped = Cfg.AutoFlip && side == XiangqiSide.Black;
        if (_flipped == flipped) return;
        _flipped = flipped;
        OnPropertyChanged(nameof(IsFlipped));
    }

    // ---------- 小工具 ----------

    /// <summary>生成调色板（红方 7 种 + 黑方 7 种 + 删除）。</summary>
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
    /// <c>YLT_XIANGQI_DEMO=1</c>：走一局演示中盘（含吃子 / 最后一步）；
    /// <c>YLT_XIANGQI_SETUP=1</c>：直接展开摆棋并套用「让单马」；
    /// 与基类的 <c>YLT_BOARD_DEMO_ROOM</c> 叠加时不再进同机双人（基类已备好演示房间）。
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

        // 演示中盘：演示房间（房主 / 观战）由基类备好模式与成员；否则进入同机双人
        if (!IsDemoRoom) StartLocalCommand.Execute(null);
        _game = new XiangqiGame(XiangqiBoard.CreateInitial(), XiangqiSide.Red) { Started = true };
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
        _isSetupMode = false;
        ClearSelection();
        UpdateFlipFromSettings();
        RefreshAll();
        SetNotice("演示数据：11 步中盘（含吃子与最后一步标记）", "TextSecondaryBrush");
    }
}
