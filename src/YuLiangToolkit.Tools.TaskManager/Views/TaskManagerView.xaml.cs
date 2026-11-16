using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using YuLiangToolkit.Tools.TaskManager.ViewModels;

namespace YuLiangToolkit.Tools.TaskManager.Views;

/// <summary>
/// 「任务管理器」界面代码后置：<c>InitializeComponent()</c>、「右键先选中该行」，
/// 以及把 ViewModel 每轮采样后触发的 <see cref="TaskManagerViewModel.PerformanceRefreshed"/>
/// 接到四张性能折线图上重绘（折线是自绘控件，不靠绑定刷新）。
///
/// <para>业务（采样 / 结束任务）全部在 <c>TaskManagerViewModel</c> 里。
/// 进程列表改用 DataGrid 后，列宽拖动 / 列头排序由 DataGrid 原生处理，这里不碰。</para>
/// </summary>
public partial class TaskManagerView : UserControl
{
    private TaskManagerViewModel? _viewModel;

    public TaskManagerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TaskManagerViewModel vm || ReferenceEquals(_viewModel, vm)) return;
        DetachPerformance();
        _viewModel = vm;
        vm.PerformanceRefreshed += OnPerformanceRefreshed;
        RefreshGraphs();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachPerformance();

    private void DetachPerformance()
    {
        if (_viewModel is null) return;
        _viewModel.PerformanceRefreshed -= OnPerformanceRefreshed;
        _viewModel = null;
    }

    private void OnPerformanceRefreshed() => RefreshGraphs();

    private void RefreshGraphs()
    {
        CpuGraph.Refresh();
        MemoryGraph.Refresh();
        GpuGraph.Refresh();
        DiskGraph.Refresh();
    }

    /// <summary>进程列表右键：先把鼠标下那一行的进程选中，右键菜单里的命令才有作用对象。</summary>
    private void OnProcessGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row) return;
        row.IsSelected = true;
        (sender as DataGrid)?.Focus();
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>沿可视树 / 逻辑树往上找指定类型的祖先（与其它工具页一致的最小写法）。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T hit) return hit;
            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
