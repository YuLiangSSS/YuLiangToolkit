using System.Windows.Controls;
using YuLiangToolkit.Tools.BoardGames.Checkers.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Checkers.Views;

/// <summary>
/// 「跳棋」界面代码后置：棋盘点击转给 ViewModel。
/// </summary>
public partial class CheckersView : UserControl
{
    /// <summary>构造。</summary>
    public CheckersView()
    {
        InitializeComponent();
        Board.CellClicked += (x, y) => (DataContext as CheckersViewModel)?.ClickCell(x, y);
    }
}
