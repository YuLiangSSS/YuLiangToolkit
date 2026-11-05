using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.FunctionPlot.ViewModels;

namespace YuLiangToolkit.Tools.FunctionPlot.Views;

/// <summary>
/// 函数绘图页面的后置代码：<b>只做接线</b>——把自绘控件 <see cref="PlotCanvas"/> 的鼠标事件转给
/// ViewModel、把控件尺寸同步给视口。所有计算与状态都在 ViewModel / Core 里，界面逻辑不写在 XAML 后置里。
/// </summary>
public partial class FunctionPlotView : UserControl
{
    private FunctionPlotViewModel? _viewModel;

    public FunctionPlotView()
    {
        InitializeComponent();

        WireCanvasEvents();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void WireCanvasEvents()
    {
        PlotArea.SizeChanged += (_, _) => _viewModel?.UpdateViewportSize(PlotArea.ActualWidth, PlotArea.ActualHeight);
        PlotArea.CursorMoved += (worldX, worldY) => _viewModel?.OnCursorMoved(worldX, worldY);
        PlotArea.CursorLeft += () => _viewModel?.OnCursorLeft();
        PlotArea.PanRequested += (dx, dy) => _viewModel?.PanByPixels(dx, dy);
        PlotArea.ZoomRequested += (point, zoomIn) => _viewModel?.ZoomAt(point, zoomIn);
        PlotArea.ResetRequested += () => _viewModel?.ResetView();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => AttachViewModel(e.NewValue as FunctionPlotViewModel);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as FunctionPlotViewModel);
        _viewModel?.UpdateViewportSize(PlotArea.ActualWidth, PlotArea.ActualHeight);
    }

    private void AttachViewModel(FunctionPlotViewModel? viewModel)
    {
        _viewModel = viewModel;
        _viewModel?.UpdateViewportSize(PlotArea.ActualWidth, PlotArea.ActualHeight);
    }
}
