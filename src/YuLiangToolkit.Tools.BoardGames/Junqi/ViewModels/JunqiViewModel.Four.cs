using System.Collections.ObjectModel;
using System.Text.Json;
using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.BoardGames.Junqi;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Junqi.ViewModels;

/// <summary>
/// 四国军棋的界面部分（partial 扩展）：
/// 4 人两队、13×13 十字棋盘、同盟不能互相吃、夺对方两面军旗获胜。
///
/// <para>与二人军棋共用同一个工具页：切换玩法时把棋盘换成 13×13，席位从 2 变成 4。</para>
/// </summary>
public sealed partial class JunqiViewModel
{
    private readonly JunqiFourGame _fourGame = new();
    private JunqiNation _myNation = JunqiNation.Red;
    private (int X, int Y)? _fourSelected;

    /// <summary>四国军棋的棋盘格子（13 × 13 = 169）。</summary>
    public ObservableCollection<JunqiCellItem> FourCells { get; } = new();

    /// <summary>当前显示四国棋盘（否则显示二人棋盘）。</summary>
    public bool IsFourBoardVisible => IsFourKingdoms;

    /// <summary>点四国棋盘（界面绑定）。</summary>
    public RelayCommand FourCellCommand { get; private set; } = null!;

    /// <summary>四方自动布阵命令（界面绑定）。</summary>
    public RelayCommand FourDeployCommand { get; private set; } = null!;

    /// <summary>四国军棋状态文案。</summary>
    public string FourStatusText => _fourGame.Phase switch
    {
        JunqiPhase.Playing => _fourGame.CurrentNation == _myNation ? "轮到你走" : $"轮到{JunqiFourBoard.DisplayName(_fourGame.CurrentNation)}走",
        JunqiPhase.Finished => _fourGame.ResultText,
        _ => "点「四方自动布阵」开始",
    };

    /// <summary>我是哪个阵营（四国）。</summary>
    public JunqiNation MyNation => _myNation;

    /// <summary>我方的中文名。</summary>
    public string MyNationText => $"{JunqiFourBoard.DisplayName(_myNation)}（同盟：{JunqiFourBoard.DisplayName(JunqiFourBoard.Ally(_myNation))}）";

    /// <summary>初始化四国棋盘（主构造函数里调用）。</summary>
    private void InitializeFourBoard()
    {
        FourCellCommand = new RelayCommand(p => ClickFourCell(p as JunqiCellItem));
        FourDeployCommand = new RelayCommand(_ => AutoDeployFour());

        for (var y = 0; y < JunqiFourBoard.Size; y++)
        {
            for (var x = 0; x < JunqiFourBoard.Size; x++)
            {
                var kind = JunqiFourBoard.IsCamp(x, y) ? JunqiCellKind.Camp
                    : JunqiFourBoard.IsHeadquarters(x, y) ? JunqiCellKind.Headquarters
                    : JunqiFourBoard.IsRailway(x, y) ? JunqiCellKind.Railway
                    : JunqiCellKind.Post;

                var item = new JunqiCellItem(x, y, kind);
                if (JunqiFourBoard.NationAt(x, y) is { } nation)
                    item.NationKey = JunqiFourBoard.ToKey(nation);

                FourCells.Add(item);
            }
        }
    }

    /// <summary>截图 / 演示：直接摆好四国局面。</summary>
    private void LoadFourDemoIfRequested()
    {
        if (Environment.GetEnvironmentVariable("YLT_JUNQI_FOUR") != "1") return;
        _variant = JunqiVariant.FourKingdoms;
        Cfg.Variant = JunqiConfig.VariantToKey(_variant);
        AutoDeployFour();
        RefreshFourBoard();
        OnPropertyChanged(nameof(IsFourKingdoms));
        OnPropertyChanged(nameof(IsFourBoardVisible));
        OnPropertyChanged(nameof(MyNationText));
        OnPropertyChanged(nameof(FourStatusText));
        StatusText = "演示：四国军棋（四方已布阵）";
    }

    /// <summary>四方自动布阵并开局。</summary>
    public void AutoDeployFour()
    {
        if (IsClient) return;

        _fourGame.Reset();
        foreach (var nation in new[] { JunqiNation.Red, JunqiNation.Blue, JunqiNation.Green, JunqiNation.Yellow })
        {
            var error = _fourGame.AutoDeploy(nation, new Random());
            if (error.Length > 0)
            {
                StatusText = error;
                return;
            }
        }

        StatusText = "四方已布阵，红方先走";
        RefreshFourBoard();
        BroadcastStateFromRoom();
        TryStartFourAiTurn();
    }

    /// <summary>点四国棋盘的某一格。</summary>
    public void ClickFourCell(JunqiCellItem? cell)
    {
        if (cell is null || _fourGame.Phase != JunqiPhase.Playing) return;
        if (!IsFourKingdoms) return;

        if (_fourSelected is { } from)
        {
            // 点自己另一个子 → 改选中
            if (!cell.Piece.IsEmpty && JunqiFourBoard.NationAt(cell.X, cell.Y) is { } nation
                && JunqiFourBoard.IsAlly(_myNation, nation))
            {
                SelectFour(cell);
                return;
            }

            if (IsClient)
            {
                _ = Session?.SendActionAsync("four-move",
                    new { fromX = from.X, fromY = from.Y, toX = cell.X, toY = cell.Y });
                ClearFourSelection();
                return;
            }

            var error = _fourGame.Move(_myNation, from.X, from.Y, cell.X, cell.Y);
            if (error.Length > 0) { StatusText = error; return; }
            ClearFourSelection();
            AfterFourMove();
            return;
        }

        if (!cell.Piece.IsEmpty && JunqiFourBoard.NationAt(cell.X, cell.Y) is { } owner
            && JunqiFourBoard.IsAlly(_myNation, owner))
        {
            SelectFour(cell);
        }
    }

    private void SelectFour(JunqiCellItem cell)
    {
        ClearFourSelection();
        _fourSelected = (cell.X, cell.Y);
        cell.IsSelected = true;

        foreach (var (tx, ty) in _fourGame.LegalTargets(_myNation, cell.X, cell.Y))
        {
            var item = FourCellAt(tx, ty);
            if (item is not null) item.IsTarget = true;
        }
        StatusText = $"已选中 {JunqiRanks.DisplayName(cell.Piece.Rank)}，点高亮位置走棋";
    }

    private void ClearFourSelection()
    {
        _fourSelected = null;
        foreach (var cell in FourCells)
        {
            cell.IsSelected = false;
            cell.IsTarget = false;
        }
    }

    private JunqiCellItem? FourCellAt(int x, int y)
        => JunqiFourBoard.InBounds(x, y) ? FourCells[y * JunqiFourBoard.Size + x] : null;

    private void AfterFourMove()
    {
        RefreshFourBoard();
        OnPropertyChanged(nameof(FourStatusText));

        if (_fourGame.Phase == JunqiPhase.Finished)
        {
            ResultText = _fourGame.ResultText;
            StatusText = _fourGame.ResultText;
        }
        else
        {
            StatusText = _fourGame.CurrentNation == _myNation ? "轮到你走" : "等待其他家走棋";
        }

        BroadcastStateFromRoom();
        TryStartFourAiTurn();
    }

    /// <summary>刷新四国棋盘（按自己的阵营过滤暗棋）。</summary>
    private void RefreshFourBoard()
    {
        var visible = _fourGame.VisibleBoardFor(_myNation);
        for (var i = 0; i < FourCells.Count && i < visible.Count; i++)
        {
            var piece = visible[i];
            FourCells[i].Piece = piece;

            // 棋子颜色按「格子归属」定（四国四色），不用 JunqiPiece.Side（只有红蓝两值）
            if (!piece.IsEmpty && JunqiFourBoard.NationAt(FourCells[i].X, FourCells[i].Y) is { } nation)
                FourCells[i].NationKey = JunqiFourBoard.ToKey(nation);
        }
    }

    // ---------------------------------------------------------------- 四国 AI

    private bool _fourAiThinking;

    /// <summary>轮到电脑阵营时自动走（单机）。</summary>
    private void TryStartFourAiTurn()
    {
        if (!_aiEnabled || IsOnline) return;
        if (_fourGame.Phase != JunqiPhase.Playing) return;

        var current = _fourGame.CurrentNation;
        if (current == _myNation) return;                    // 轮到自己
        if (_fourAiThinking) return;

        _fourAiThinking = true;
        OnPropertyChanged(nameof(AiThinking));

        var delay = AiDifficulties.ThinkingDelayMs(_aiDifficulty);
        _ = Task.Delay(delay).ContinueWith(_ => Ui(RunFourAiAction));
    }

    private void RunFourAiAction()
    {
        try
        {
            if (_fourGame.Phase != JunqiPhase.Playing) return;
            var current = _fourGame.CurrentNation;
            if (current == _myNation) return;

            var moves = _fourGame.LegalMoves(current);
            if (moves.Count == 0) return;

            var move = moves[Random.Shared.Next(moves.Count)];
            _fourGame.Move(current, move.FromX, move.FromY, move.ToX, move.ToY);

            RefreshFourBoard();
            OnPropertyChanged(nameof(FourStatusText));
            if (_fourGame.Phase == JunqiPhase.Finished)
            {
                ResultText = _fourGame.ResultText;
                StatusText = _fourGame.ResultText;
            }
        }
        finally
        {
            _fourAiThinking = false;
            OnPropertyChanged(nameof(AiThinking));
        }

        TryStartFourAiTurn();      // 可能又轮到下一家电脑
    }
}
