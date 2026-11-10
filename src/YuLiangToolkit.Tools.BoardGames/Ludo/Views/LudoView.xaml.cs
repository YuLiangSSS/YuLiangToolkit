using System.Windows.Controls;
using YuLiangToolkit.Tools.BoardGames.Ludo.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Ludo.Views;

/// <summary>
/// 「飞行棋」界面代码后置：把棋盘点击（选棋子走棋）转给 ViewModel。
/// </summary>
public partial class LudoView : UserControl
{
    /// <summary>构造。</summary>
    public LudoView()
    {
        InitializeComponent();
        Board.PieceClicked += OnPieceClicked;
    }

    private void OnPieceClicked(LudoPieceItem piece)
        => (DataContext as LudoViewModel)?.MovePiece(piece.ColorKey, 0);
}
