using System.Windows.Controls;
using YuLiangToolkit.Tools.BoardGames.Gomoku.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Gomoku.Views;

/// <summary>
/// 联机五子棋界面代码后置：只做一件必须碰界面的事 —— 把棋盘控件的点击转给 ViewModel。
/// <para>其余全部走绑定（见 <c>GomokuView.xaml</c>）。</para>
/// </summary>
public partial class GomokuView : UserControl
{
    /// <summary>创建界面并接上棋盘点击事件。</summary>
    public GomokuView()
    {
        InitializeComponent();
        Board.PointClicked += OnBoardPointClicked;
    }

    private void OnBoardPointClicked(GomokuPoint point)
        => (DataContext as GomokuViewModel)?.PlaceAt(point);
}
