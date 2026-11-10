using System.Windows.Controls;
using YuLiangToolkit.Tools.BoardGames.Junqi.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Junqi.Views;

/// <summary>
/// 「军棋」界面代码后置：把棋盘控件的点击转给 ViewModel（业务都在 VM 里）。
/// </summary>
public partial class JunqiView : UserControl
{
    /// <summary>构造。</summary>
    public JunqiView()
    {
        InitializeComponent();
        BoardArea.CellClicked += OnCellClicked;
    }

    private void OnCellClicked(JunqiCellItem cell)
        => (DataContext as JunqiViewModel)?.ClickCellCommand.Execute(cell);
}
