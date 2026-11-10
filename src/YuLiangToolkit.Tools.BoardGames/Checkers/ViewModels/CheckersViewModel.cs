using YuLiangToolkit.Tools.BoardGames.Checkers;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.BoardGames.Checkers.ViewModels;

/// <summary>「跳棋」配置。</summary>
public sealed class CheckersConfig : ToolConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "checkers";

    private string _playerName = "";

    /// <summary>玩家名。</summary>
    public string PlayerName
    {
        get => _playerName;
        set => Set(ref _playerName, (value ?? "").Trim());
    }

    private string _lastJoinAddress = "";

    /// <summary>上次加入地址。</summary>
    public string LastJoinAddress
    {
        get => _lastJoinAddress;
        set => Set(ref _lastJoinAddress, (value ?? "").Trim());
    }

    private int _listenPort;

    /// <summary>开房端口。</summary>
    public int ListenPort
    {
        get => _listenPort;
        set => Set(ref _listenPort, value is >= 0 and <= 65535 ? value : 0);
    }

    /// <summary>人数（2/3/4/6）。</summary>
    public int PlayerCount { get; set; } = 2;

    /// <summary>让棋：自己区域内可删的棋子数上限（0 = 不允许让棋）。</summary>
    public int HandicapLimit { get; set; } = 3;

    /// <summary>AI 难度键。</summary>
    public string AiDifficulty { get; set; } = "normal";

    /// <summary>AI 数量。</summary>
    public int AiCount { get; set; } = 1;

    /// <summary>规则：允许走一格。</summary>
    public bool AllowSingleStep { get; set; } = true;

    /// <summary>规则：必须优先清初始区域。</summary>
    public bool MustClearHomeFirst { get; set; } = true;

    /// <summary>玩家名实际值。</summary>
    [JsonIgnore]
    public string EffectivePlayerName
        => PlayerName.Length > 0 ? PlayerName
            : Environment.UserName is { Length: > 0 } name ? name
            : "玩家";
}

/// <summary>棋盘上一枚棋子的显示数据。</summary>
public sealed record CheckersPieceItem(int X, int Y, string OwnerId, string ColorKey, bool IsMine, bool CanMove)
{
    /// <summary>颜色名。</summary>
    public string ColorName => ColorKey switch
    {
        "b" => "蓝",
        "c" => "青",
        "d" => "绿",
        "e" => "橙",
        "f" => "紫",
        _ => "红",
    };
}

/// <summary>座位（角）的界面数据。</summary>
public sealed record CheckersSeatItem(string PointKey, string PointName, string OwnerName, bool IsOccupied, bool IsMe, bool CanPick)
{
    /// <summary>按钮文字。</summary>
    public string DisplayText => $"{PointName}：{(IsOccupied ? OwnerName : "空")}";
}

/// <summary>
/// 「跳棋」界面 VM：选角坐下（第一个人定规则组合）、让棋、走棋、AI、联机。
/// </summary>
public sealed partial class CheckersViewModel : ToolViewModelBase
{
    private readonly List<CheckersPlayer> _seatPlayers = new();
    private CheckersGame _game;
    private CheckersRules _rules = new();
    private readonly Random _random = new();

    private string _myId = "local";
    private string _myName = "";
    private bool _isHost = true;
    private int _playerCount = 2;
    private string _statusText = "先选人数，再点角坐下（第一个选位置的人决定这一局的组合）";
    private string _noticeText = "";
    private string _resultText = "";
    private string _lastText = "";
    private (int X, int Y)? _selected;

    /// <summary>创建 VM。</summary>
    public CheckersViewModel() : base(CheckersConfig.ToolId)
    {
        Cfg = LoadConfig<CheckersConfig>();
        _myName = Cfg.EffectivePlayerName;
        _playerCount = CheckersSeatRules.IsSupported(Cfg.PlayerCount) ? Cfg.PlayerCount : 2;

        _rules = new CheckersRules
        {
            AllowSingleStep = Cfg.AllowSingleStep,
            MustClearHomeFirst = Cfg.MustClearHomeFirst,
        };
        _game = new CheckersGame(_rules);

        Seats = new ObservableCollection<CheckersSeatItem>();
        Pieces = new ObservableCollection<CheckersPieceItem>();

        PlayerCountOptions = OptionItem.From(
            CheckersSeatRules.SupportedCounts.Select(c => ((double)c, $"{c} 人")).ToArray());
        AiLevelOptions = OptionItem.From(Enumerable.Range(1, 3)
            .Select(i => ((double)i, i switch { 1 => "入门", 2 => "普通", _ => "困难" })).ToArray());

        SeatCommands = Enum.GetValues<CheckersPoint>().ToDictionary(
            p => p, p => new RelayCommand(_ => ClickSeat(p)));

        StartCommand = new RelayCommand(_ => StartGame(), _ => CanStart);
        MoveCommand = new RelayCommand(_ => TryMoveSelected(), _ => CanMove);
        AutoMoveCommand = new RelayCommand(_ => AiMove(), _ => CanAutoMove);
        RemoveCommand = new RelayCommand(_ => RemoveSelected(), _ => CanRemove);
        ClearCommand = new RelayCommand(_ => ClearBoard());

        InitializeNetCommands();
        RefreshAll();
    }

    /// <summary>配置。</summary>
    public CheckersConfig Cfg { get; }

    /// <summary>座位（六个角）。</summary>
    public ObservableCollection<CheckersSeatItem> Seats { get; }

    /// <summary>棋子。</summary>
    public ObservableCollection<CheckersPieceItem> Pieces { get; }

    /// <summary>人数选项。</summary>
    public IReadOnlyList<OptionItem<double>> PlayerCountOptions { get; }

    /// <summary>AI 难度选项。</summary>
    public IReadOnlyList<OptionItem<double>> AiLevelOptions { get; }

    /// <summary>六个角的点击命令。</summary>
    public IReadOnlyDictionary<CheckersPoint, RelayCommand> SeatCommands { get; }

    /// <summary>六个角的单独命令（XAML 绑定用）。</summary>
    public RelayCommand SeatNCommand => SeatCommands[CheckersPoint.North];
    public RelayCommand SeatNeCommand => SeatCommands[CheckersPoint.NorthEast];
    public RelayCommand SeatSeCommand => SeatCommands[CheckersPoint.SouthEast];
    public RelayCommand SeatSCommand => SeatCommands[CheckersPoint.South];
    public RelayCommand SeatSwCommand => SeatCommands[CheckersPoint.SouthWest];
    public RelayCommand SeatNwCommand => SeatCommands[CheckersPoint.NorthWest];

    /// <summary>开局。</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>走棋。</summary>
    public RelayCommand MoveCommand { get; }

    /// <summary>AI 代走一步。</summary>
    public RelayCommand AutoMoveCommand { get; }

    /// <summary>让棋：删掉选中的自己棋子。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>清空重来。</summary>
    public RelayCommand ClearCommand { get; }

    // ---------------------------------------------------------------- 状态

    /// <summary>人数。</summary>
    public int PlayerCount
    {
        get => _playerCount;
        set
        {
            if (!CheckersSeatRules.IsSupported(value)) return;
            _playerCount = value;
            Cfg.PlayerCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayerCountText));
            OnPropertyChanged(nameof(AllowedHint));
            RefreshSeats();
        }
    }

    /// <summary>人数下拉。</summary>
    public OptionItem<double> SelectedPlayerCount
    {
        get => OptionItem<double>.Find(PlayerCountOptions, _playerCount) ?? PlayerCountOptions[0];
        set { if (value is not null) PlayerCount = (int)value.Value; }
    }

    /// <summary>人数文字。</summary>
    public string PlayerCountText => $"{_playerCount} 人局";

    /// <summary>规则：允许走一格。</summary>
    public bool AllowSingleStep
    {
        get => _rules.AllowSingleStep;
        set
        {
            _rules.AllowSingleStep = value;
            Cfg.AllowSingleStep = value;
            OnPropertyChanged();
        }
    }

    /// <summary>规则：必须先清初始区域。</summary>
    public bool MustClearHomeFirst
    {
        get => _rules.MustClearHomeFirst;
        set
        {
            _rules.MustClearHomeFirst = value;
            Cfg.MustClearHomeFirst = value;
            OnPropertyChanged();
        }
    }

    /// <summary>AI 难度。</summary>
    public AiDifficulty AiDifficulty
    {
        get => AiDifficulties.Parse(Cfg.AiDifficulty);
        set
        {
            Cfg.AiDifficulty = AiDifficulties.ToKey(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AiHint));
        }
    }

    /// <summary>AI 难度下拉。</summary>
    public OptionItem<double> SelectedAiLevel
    {
        get
        {
            var level = AiDifficulty switch
            {
                AiDifficulty.Beginner => 1,
                AiDifficulty.Hard or AiDifficulty.Master => 3,
                _ => 2,
            };
            return OptionItem<double>.Find(AiLevelOptions, level) ?? AiLevelOptions[1];
        }
        set
        {
            if (value is null) return;
            AiDifficulty = (int)value.Value switch
            {
                1 => AiDifficulty.Beginner,
                3 => AiDifficulty.Hard,
                _ => AiDifficulty.Normal,
            };
        }
    }

    /// <summary>AI 说明。</summary>
    public string AiHint => AiDifficulty switch
    {
        AiDifficulty.Beginner => "入门：随机挑走法",
        AiDifficulty.Hard or AiDifficulty.Master => "困难：一步前瞻",
        _ => "普通：挑离目标角最近的一步",
    };

    /// <summary>「这套组合剩下的位置」提示。</summary>
    public string AllowedHint => CheckersSeatRules.DescribeAllowed(_playerCount, _seatPlayers.Select(p => p.Home).ToList());

    /// <summary>状态文字。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>提示文字。</summary>
    public string NoticeText
    {
        get => _noticeText;
        private set => Set(ref _noticeText, value);
    }

    /// <summary>结果文字。</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (!Set(ref _resultText, value)) return;
            OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>有没有结果。</summary>
    public bool HasResult => ResultText.Length > 0;

    /// <summary>最近一步。</summary>
    public string LastText
    {
        get => _lastText;
        private set => Set(ref _lastText, value);
    }

    /// <summary>现在轮到谁。</summary>
    public string TurnText
    {
        get
        {
            var id = _game.CurrentPlayerId;
            if (id.Length == 0) return "还没开局";
            var player = _game.Players.FirstOrDefault(p => p.Id == id);
            return player is null ? "还没开局" : $"轮到 {player.Name}";
        }
    }

    /// <summary>能不能开局（人数够、且组合合法）。</summary>
    public bool CanStart => _seatPlayers.Count == _playerCount
        && CheckersSeatRules.IsValidSelection(_playerCount, _seatPlayers.Select(p => p.Home).ToList());

    /// <summary>能不能走棋。</summary>
    public bool CanMove => _selected is not null && !_game.IsOver;

    /// <summary>能不能让 AI 代走。</summary>
    public bool CanAutoMove => !_game.IsOver && _game.Players.Any(p => p.IsAi);

    /// <summary>能不能让棋（删棋子）。</summary>
    public bool CanRemove => _selected is not null && _game.Players.Any(p => p.Id == _myId)
        && Cfg.HandicapLimit > 0;

    /// <summary>我在哪个角（没坐 = null）。</summary>
    public CheckersPoint? MyHome => _seatPlayers.FirstOrDefault(p => p.Id == _myId)?.Home;

    /// <summary>我的角的键（棋盘高亮用）。</summary>
    public string MyHomeKey => MyHome is { } home ? CheckersBoard.ToKey(home) : "";

    // ---------------------------------------------------------------- 选座

    /// <summary>点某个角。</summary>
    private void ClickSeat(CheckersPoint point)
    {
        // 联机：客户端只上报，由房主决定
        if (_isOnline && !_isHostOnline)
        {
            var mine = _seatPlayers.FirstOrDefault(p => p.Home == point);
            if (mine is not null && mine.Id == _myId) _ = _session?.SendLeaveSeatAsync();
            else _ = _session?.SendSitAsync(point);
            return;
        }

        var existing = _seatPlayers.FirstOrDefault(p => p.Home == point);

        // 点自己的角 → 退出
        if (existing is not null && existing.Id == _myId)
        {
            _seatPlayers.Remove(existing);
            SetNotice($"你退出了 {CheckersBoard.DisplayName(point)}角", "TextSecondaryBrush");
            RefreshSeats();
            return;
        }

        if (existing is not null)
        {
            SetNotice($"{CheckersBoard.DisplayName(point)}角已经有人了（{existing.Name}）", "DangerBrush");
            return;
        }

        // 第一个人定规则：后续只能在组合内选
        var picked = _seatPlayers.Select(p => p.Home).ToList();
        if (!CheckersSeatRules.Remaining(_playerCount, picked).Contains(point))
        {
            SetNotice($"这个位置不在本局组合里。{AllowedHint}", "DangerBrush");
            return;
        }

        if (_seatPlayers.Count >= _playerCount)
        {
            SetNotice("座位已满", "DangerBrush");
            return;
        }

        _seatPlayers.Add(new CheckersPlayer
        {
            Id = _myId,
            Name = _myName,
            Home = point,
        });

        SetNotice($"你坐到了 {CheckersBoard.DisplayName(point)}角", "SuccessBrush");
        RefreshSeats();
    }

    private void RefreshSeats()
    {
        Seats.Clear();
        foreach (var point in Enum.GetValues<CheckersPoint>())
        {
            var owner = _seatPlayers.FirstOrDefault(p => p.Home == point);
            var canPick = CheckersSeatRules.Remaining(_playerCount, _seatPlayers.Select(p => p.Home).ToList()).Contains(point);

            Seats.Add(new CheckersSeatItem(
                CheckersBoard.ToKey(point),
                CheckersBoard.DisplayName(point),
                owner?.Name ?? "",
                owner is not null,
                owner?.Id == _myId,
                canPick));
        }

        OnPropertyChanged(nameof(AllowedHint));
        OnPropertyChanged(nameof(CanStart));
    }

    // ---------------------------------------------------------------- 开局 / 走棋

    /// <summary>开局。</summary>
    private void StartGame()
    {
        if (!CanStart) { SetNotice("人数没满或组合不合法", "DangerBrush"); return; }

        _game = new CheckersGame(_rules);
        foreach (var player in _seatPlayers)
        {
            _game.AddPlayer(player.Id, player.Name, player.Home, player.IsAi, 2);
        }

        // 行动顺序：按入座顺序（房主可改；随机抽签功能在联机时设置）
        _game.SetTurnOrder(_seatPlayers.Select(p => p.Id).ToList());

        var error = _game.Start();
        if (error.Length > 0) { SetNotice(error, "DangerBrush"); return; }

        _selected = null;
        ResultText = "";
        LastText = "";
        StatusText = $"{_playerCount} 人局开局：{string.Join("、", _seatPlayers.Select(p => $"{p.Name}（{CheckersBoard.DisplayName(p.Home)}）"))}";
        RefreshBoard();
        TryAiTurn();
        BroadcastState();
    }

    /// <summary>点棋盘上的格子（选中 / 走棋）。</summary>
    public void ClickCell(int x, int y)
    {
        if (_game.IsOver) return;

        // 已经有选中 → 试着走到这里
        if (_selected is { } from)
        {
            // 联机客户端：上报给房主
            if (_isOnline && !_isHostOnline)
            {
                _ = _session?.SendMoveAsync(from.X, from.Y, x, y);
                _selected = null;
                RefreshBoard();
                return;
            }

            var error = _game.Move(_game.CurrentPlayerId, from.X, from.Y, x, y);
            if (error.Length == 0)
            {
                _selected = null;
                LastText = _game.LastText;
                RefreshBoard();
                CheckOver();
                TryAiTurn();
                BroadcastState();
                return;
            }

            // 走不了 → 如果这里是自己能动的棋子，改选中
            var own = _game.LegalMoves(_game.CurrentPlayerId)
                .Any(m => m.FromX == x && m.FromY == y);
            if (own) { _selected = (x, y); SetNotice("已选中，再点落点", "TextSecondaryBrush"); RefreshBoard(); return; }

            SetNotice(error, "DangerBrush");
            return;
        }

        // 没选中 → 选中自己的棋子
        var canPick = _game.LegalMoves(_game.CurrentPlayerId)
            .Any(m => m.FromX == x && m.FromY == y);
        if (canPick) { _selected = (x, y); SetNotice("已选中，再点落点", "TextSecondaryBrush"); RefreshBoard(); }
        else SetNotice("这枚棋子现在不能动（是不是还没清初始区域？）", "DangerBrush");
    }

    private void TryMoveSelected()
    {
        if (_selected is not { } from) return;
        SetNotice("点一个落点", "TextSecondaryBrush");
        _ = from;
    }

    /// <summary>AI 代走一步（当前轮到 AI 时）。</summary>
    private void AiMove()
    {
        var id = _game.CurrentPlayerId;
        var player = _game.Players.FirstOrDefault(p => p.Id == id);
        if (player is null) return;

        var move = CheckersAi.ChooseMove(_game, id, AiDifficulty, _random);
        if (move is null)
        {
            _game.Pass(id);
            LastText = _game.LastText;
            RefreshBoard();
            return;
        }

        var error = _game.Move(id, move.FromX, move.FromY, move.ToX, move.ToY);
        if (error.Length > 0) return;

        LastText = _game.LastText;
        RefreshBoard();
        CheckOver();
        TryAiTurn();
    }

    /// <summary>轮到 AI 就自动走。</summary>
    private void TryAiTurn()
    {
        var id = _game.CurrentPlayerId;
        var player = _game.Players.FirstOrDefault(p => p.Id == id);
        if (player is null || !player.IsAi || _game.IsOver) return;

        AiMove();
    }

    private void CheckOver()
    {
        if (!_game.IsOver) return;
        ResultText = _game.ResultText;
        StatusText = _game.ResultText;
    }

    /// <summary>让棋：删掉选中的自己区域里的棋子。</summary>
    private void RemoveSelected()
    {
        if (_selected is not { } point) return;

        var error = _game.RemovePiece(_myId, point.X, point.Y);
        if (error.Length > 0) { SetNotice(error, "DangerBrush"); return; }

        LastText = _game.LastText;
        _selected = null;
        RefreshBoard();
    }

    private void RefreshBoard()
    {
        Pieces.Clear();

        foreach (var player in _game.Players)
        {
            var index = _game.Players.ToList().IndexOf(player);
            var colorKey = index switch
            {
                1 => "b",
                2 => "c",
                3 => "d",
                4 => "e",
                5 => "f",
                _ => "a",
            };

            foreach (var (x, y) in player.Pieces)
            {
                var isMine = player.Id == _myId;
                var canMove = isMine && _game.CurrentPlayerId == _myId
                    && _game.LegalMoves(_myId).Any(m => m.FromX == x && m.FromY == y);

                Pieces.Add(new CheckersPieceItem(x, y, player.Id, colorKey, isMine, canMove));
            }
        }

        OnPropertyChanged(nameof(TurnText));
        OnPropertyChanged(nameof(CanMove));
        OnPropertyChanged(nameof(CanAutoMove));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(NoticeText));
    }

    private void RefreshAll()
    {
        RefreshSeats();
        RefreshBoard();
    }

    private void ClearBoard()
    {
        _seatPlayers.Clear();
        _game = new CheckersGame(_rules);
        _selected = null;
        ResultText = "";
        LastText = "";
        StatusText = "已清空，重新选位置";
        RefreshAll();
    }

    private void SetNotice(string text, string brush)
    {
        NoticeText = text;
        _ = brush;
    }
}
