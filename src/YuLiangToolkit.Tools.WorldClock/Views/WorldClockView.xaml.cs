using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.WorldClock.Models;
using YuLiangToolkit.Tools.WorldClock.Services;
using YuLiangToolkit.Tools.WorldClock.ViewModels;

namespace YuLiangToolkit.Tools.WorldClock.Views;

/// <summary>
/// 全球时钟页面的代码后置：只管两件界面上的事 ——
/// ① 拖 ≡ 手柄重排城市（<see cref="OnHandleMouseDown"/> 起手，走系统阈值后发 <c>DoDragDrop</c>）；
/// ② 页面 Loaded / Unloaded 时让 ViewModel 开 / 停秒表。
///
/// <para>数据、排序规则、锁定逻辑全在 ViewModel 里（自检能无头断言），这里不写业务。</para>
///
/// <para>⚠ 拖放回调由原生 OLE 直接调：<see cref="OnRowDrop"/> 里必须 try/catch 兜住异常，
/// 否则一个排序失败就能把整个程序带崩（见 docs/dev/pitfalls.md）。</para>
/// </summary>
public partial class WorldClockView : UserControl
{
    /// <summary>拖动时的私有数据格式（与拖文件、外壳拖卡片互不干扰）。</summary>
    private const string ClockDragFormat = "YuLiangToolkit.WorldClock.Item";

    private Point _dragStart;
    private WorldClockItem? _pendingDrag;

    public WorldClockView()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel?.OnPageLoaded();
        Unloaded += (_, _) => ViewModel?.OnPageUnloaded();
    }

    /// <summary>页面的 ViewModel（DataContext 就是它）。</summary>
    private WorldClockViewModel? ViewModel => DataContext as WorldClockViewModel;

    // =====================================================================
    //  拖动排序
    // =====================================================================

    /// <summary>按住 ≡ 手柄：记下起点，等鼠标移出系统阈值才真的开始拖（否则只是点了一下）。</summary>
    private void OnHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorldClockItem clock } || !clock.CanDrag) return;

        _dragStart = e.GetPosition(null);
        _pendingDrag = clock;
        e.Handled = true;   // 别让点击冒泡到卡片（不然会触发双击复制 / 选中）
    }

    /// <summary>鼠标移动：够了阈值就发起拖放（会一直阻塞到放下 / 按 Esc）。</summary>
    private void OnHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDrag is null || e.LeftButton != MouseButtonState.Pressed) return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var clock = _pendingDrag;
        _pendingDrag = null;

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(ClockDragFormat, clock), DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            ViewModel?.Log("拖动手柄失败：" + ex.Message);
        }
    }

    /// <summary>鼠标松开：取消这次"准备拖"的状态（没走够阈值就当点击）。</summary>
    private void OnHandleMouseUp(object sender, MouseButtonEventArgs e) => _pendingDrag = null;

    /// <summary>拖到某一行上：上下半区决定插到它前面还是后面。</summary>
    private void OnRowDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ClockDragFormat)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.Move;
    }

    /// <summary>放下：按落点算目标下标，交给 ViewModel 移动。</summary>
    private void OnRowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ClockDragFormat) is not WorldClockItem dragged) return;
        e.Handled = true;

        try
        {
            var viewModel = ViewModel;
            if (viewModel is null) return;

            if (sender is FrameworkElement { DataContext: WorldClockItem target } element)
            {
                var targetIndex = viewModel.Clocks.IndexOf(target);
                if (targetIndex < 0) return;

                // 指针在下半区 → 插到这一项之后
                var below = e.GetPosition(element).Y > element.ActualHeight / 2;
                viewModel.MoveTo(dragged, below ? targetIndex + 1 : targetIndex);
            }
            else
            {
                // 拖到列表空白处：放到最后
                viewModel.MoveTo(dragged, viewModel.Clocks.Count);
            }
        }
        catch (Exception ex)
        {
            ViewModel?.Log("调整顺序失败：" + ex.Message);
        }
    }

    // =====================================================================
    //  其它界面便利
    // =====================================================================

    /// <summary>
    /// 双击一行：复制这只钟的时间（列表视图的快捷操作）。
    /// <para>⚠ Border 没有 <c>MouseDoubleClick</c>（那是 Control 才有的）：改用
    /// <c>MouseLeftButtonDown</c> 自己数 <see cref="ClickCount"/>。</para>
    /// </summary>
    private void OnRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement { DataContext: WorldClockItem clock }) return;
        ViewModel?.CopyTime(clock);
        e.Handled = true;
    }

    /// <summary>点城市搜索结果：加入列表。</summary>
    private void OnCityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClockCityOption city }) return;
        ViewModel?.AddCity(city);
    }
}
