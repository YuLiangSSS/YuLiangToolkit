using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi.ViewModels;

/// <summary>
/// 摆棋调色板里的一格：红方 / 黑方的七种棋子，或者「删除」。
/// 点一下选中，再点棋盘就把这枚棋子放到那一格（「删除」= 清掉那一格）。
/// </summary>
public sealed class XiangqiPaletteItem : ObservableObject
{
    /// <summary>要放的棋子（「删除」时是空格）。</summary>
    public XiangqiPiece Piece { get; init; } = XiangqiPiece.Empty;

    /// <summary>是不是「删除」工具。</summary>
    public bool IsDelete { get; init; }

    /// <summary>是不是红方棋子（界面用红色字）。</summary>
    public bool IsRed => !IsDelete && Piece.IsRed;

    /// <summary>是不是黑方棋子（界面用深色字）。</summary>
    public bool IsBlack => !IsDelete && Piece.IsBlack;

    /// <summary>按钮上显示的字：帅仕相马车炮兵 / 将士象马车炮卒 / 删。</summary>
    public string DisplayChar => IsDelete ? "删" : Piece.DisplayChar;

    /// <summary>悬停说明：红方帅 / 黑方卒 / 删除这一格的棋子。</summary>
    public string ToolTip => IsDelete ? "删除：点棋盘上的棋子把它拿掉" : $"放置：{Piece.DisplayName}";

    private bool _isSelected;

    /// <summary>是不是当前选中的那一格（界面高亮用）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>造一枚棋子调色板项。</summary>
    public static XiangqiPaletteItem For(XiangqiPiece piece) => new() { Piece = piece };

    /// <summary>「删除」工具。</summary>
    public static XiangqiPaletteItem Delete { get; } = new() { IsDelete = true };
}
