using System.Windows.Controls;
using YuLiangToolkit.Tools.BoardGames.Go.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Go.Views;

/// <summary>
/// 「围棋」界面代码后置：把棋盘点击转给 ViewModel（业务都在 VM 里）。
/// </summary>
public partial class GoView : UserControl
{
    /// <summary>构造。</summary>
    public GoView()
    {
        InitializeComponent();
        Board.PointClicked += OnPointClicked;
    }

    private void OnPointClicked(GoCellItem item)
        => (DataContext as GoViewModel)?.PlaceCommand.Execute(item);
}
