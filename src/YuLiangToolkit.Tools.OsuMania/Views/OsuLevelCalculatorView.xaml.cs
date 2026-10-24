using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.OsuMania.ViewModels;

namespace YuLiangToolkit.Tools.OsuMania.Views;

/// <summary>
/// 「osu! 等级计算器」的界面。代码后置只做拖放接线，其它交互全部走 ViewModel 的命令与绑定。
/// </summary>
public partial class OsuLevelCalculatorView : UserControl
{
    public OsuLevelCalculatorView()
    {
        InitializeComponent();
        Loaded += (_, _) => OsuViewScroll.ApplyIfRequested(PageScroll); // YLT_OSU_SCROLL 调试钩子（见 OsuViewScroll）
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is OsuLevelCalculatorViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            vm.AddPaths(paths);
        }
        e.Handled = true;
    }
}
