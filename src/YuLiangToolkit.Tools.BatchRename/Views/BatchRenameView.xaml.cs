using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YuLiangToolkit.Tools.BatchRename.ViewModels;

namespace YuLiangToolkit.Tools.BatchRename.Views;

/// <summary>
/// 「批量重命名」界面代码后置。只做三件纯界面层的事，业务全在 <see cref="BatchRenameViewModel"/>：
/// <list type="number">
///   <item><b>拖入文件</b>：把文件 / 文件夹从资源管理器拖进窗口就能添加（<c>AllowDrop</c> 在 XAML 上）；</item>
///   <item><b>拖动规则排序</b>：按住规则行左边那个「☰」把手上下拖，插到别的位置
///         （用 <see cref="DragDrop.DoDragDrop"/> 自己传一个私有数据格式，和拖文件互不干扰）；</item>
///   <item><b>多选同步</b>：ListBox 的选中项同步给 VM，这样「移除选中 / 上移 / 下移」能支持多选。</item>
/// </list>
/// </summary>
public partial class BatchRenameView : UserControl
{
    /// <summary>拖动规则时用的私有剪贴板格式（不是 FileDrop，所以不会和“拖文件进来”搞混）。</summary>
    private const string RuleDragFormat = "YltBatchRename.Rule";

    private Point _dragStart;
    private RenameRuleItem? _pendingDrag;

    public BatchRenameView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachViewModel();
    }

    private BatchRenameViewModel? ViewModel => DataContext as BatchRenameViewModel;

    // =====================================================================
    //  拖文件进来
    // =====================================================================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        vm.AddPaths(paths);
        e.Handled = true;
    }

    // =====================================================================
    //  拖动把手：调整规则顺序
    // =====================================================================

    private void OnRuleHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RenameRuleItem rule }) return;
        _dragStart = e.GetPosition(null);
        _pendingDrag = rule;
    }

    private void OnRuleHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDrag is null || e.LeftButton != MouseButtonState.Pressed) return;

        // 走够系统的最小拖动距离才算“在拖”，否则只是点了一下
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var rule = _pendingDrag;
        _pendingDrag = null;
        DragDebug("DragStarted");

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(RuleDragFormat, rule), DragDropEffects.Move);
        }
        finally
        {
            // DoDragDrop 会一直阻塞到拖放结束（放下或按 Esc 取消），所以这里正好用来清掉指示线
            ViewModel?.ClearDropTarget();
        }
    }

    /// <summary>拖到某条规则上：指示线画在它的上半 / 下半，表示会插到它前面还是后面。</summary>
    private void OnRuleDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(RuleDragFormat)) return;   // 不是规则拖动就交给上层（拖文件用）
        e.Handled = true;
        e.Effects = DragDropEffects.Move;

        if (sender is FrameworkElement element && element.DataContext is RenameRuleItem target)
        {
            var before = IsAboveMiddle(e, element);
            DragDebug($"RowDragOver y={e.GetPosition(element).Y:0.0} h={element.ActualHeight:0.0} -> {target.Rule.KindName} (before={before})");
            ViewModel?.SetDropTarget(target, before);
        }
    }

    private void OnRuleDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(RuleDragFormat) is not RenameRuleItem dragged) return;
        e.Handled = true;
        DragDebug($"RowDrop {dragged.Rule.KindName}");

        if (sender is FrameworkElement element && element.DataContext is RenameRuleItem target)
            TryReorder(() => ViewModel?.MoveRuleTo(dragged, target, before: IsAboveMiddle(e, element)));
    }

    /// <summary>
    /// 排查拖放问题用的开关：设了环境变量 <c>YLT_DRAG_DEBUG=&lt;日志文件路径&gt;</c> 时，
    /// 把每次 DragOver / Drop 记进去（谁被当成目标、插在前面还是后面）。
    ///
    /// <para>拖放的判定全靠命中测试，出错时“为什么是这一条”从界面上看不出来，
    /// 只能靠这份日志。关掉时（不设环境变量）每次调用只是读一次缓存字段，没有额外开销。
    /// <c>scripts\e2e-batch-rename-drag.ps1</c> 的边界用例会读它。</para>
    /// </summary>
    internal static void DragDebug(string line)
    {
        if (string.IsNullOrEmpty(DebugLogPath)) return;
        try { File.AppendAllText(DebugLogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { }
    }

    /// <summary>日志文件路径（启动时读一次环境变量；为空 = 不记录）。</summary>
    private static readonly string? DebugLogPath = Environment.GetEnvironmentVariable("YLT_DRAG_DEBUG");

    /// <summary>
    /// 拖到清单空白处（所有卡片下面）：插到最末尾。
    ///
    /// <para>正常情况下卡片已经占满整行（连指示线的边距都在行内），指针走不到“空白处”；
    /// 这里保留一个判断，是为了防止以后调整间距时又冒出“鼠标悬在行间空隙 → 兜底判定把指示线抢到末尾”：
    /// 那种情况和卡片自身的判定会互相打架，指示线会在两个位置之间反复跳。</para>
    /// </summary>
    private void OnRuleListDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(RuleDragFormat)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.Move;

        var vm = ViewModel;
        if (vm is null || vm.Rules.Count == 0) return;
        if (IsPointerInsideLastRule(e)) return;   // 其实还在最后一张卡片范围内 → 不抢它的判定

        DragDebug("ListDragOver -> 兜底：放到最末尾");
        vm.SetDropTarget(vm.Rules[^1], before: false);
    }

    /// <summary>指针是否落在最后一张规则卡片（含它那一行的指示线占位）的垂直范围内。</summary>
    private bool IsPointerInsideLastRule(DragEventArgs e)
    {
        if (ViewModel is not { } vm || vm.Rules.Count == 0) return false;
        if (RuleList.ItemContainerGenerator.ContainerFromItem(vm.Rules[^1]) is not FrameworkElement last)
            return false;   // 容器还没生成（几乎不会）→ 按“在空白处”处理

        var y = e.GetPosition(last).Y;
        return y >= 0 && y <= last.ActualHeight;
    }

    private void OnRuleListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(RuleDragFormat) is not RenameRuleItem dragged) return;
        e.Handled = true;
        TryReorder(() => ViewModel?.MoveRuleTo(dragged, null, before: false));
    }

    /// <summary>
    /// 拖放回调是<b>从原生 OLE 拖放直接调进来的</b>，这里抛异常会直接把整个程序带崩（没有全局兜底），
    /// 所以兜一层：出问题就写进日志，界面继续用。
    /// </summary>
    private void TryReorder(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ViewModel?.Log("✗ 调整规则顺序失败：" + ex.Message);
        }
    }

    private static bool IsAboveMiddle(DragEventArgs e, FrameworkElement element)
        => e.GetPosition(element).Y < element.ActualHeight / 2;

    // =====================================================================
    //  多选同步 + 新规则自动滚进视野
    // =====================================================================

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel?.UpdateSelection(FileList.SelectedItems);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        if (e.NewValue is BatchRenameViewModel vm) vm.RuleAdded += OnRuleAdded;
    }

    private void DetachViewModel()
    {
        if (ViewModel is { } vm) vm.RuleAdded -= OnRuleAdded;
    }

    /// <summary>
    /// 新加的规则自动滚进视野，不用手动往下找。
    ///
    /// <para>⚠️ 这里必须<b>延到下一个 UI 回合</b>再做：<c>Rules.Add</c> 刚返回时，
    /// ListBox 自己的 ItemCollection 还在处理这次变更，此时调 <c>ScrollIntoView</c> 会强制布局，
    /// 触发容器生成器的状态校验失败（<c>InvalidOperationException: 某个 ItemsControl 与它的项源不一致</c>）——
    /// 直接闪退。同理，绝不要在 <c>CollectionChanged</c> 处理函数里强行布局（踩过）。</para>
    /// </summary>
    private void OnRuleAdded(object? sender, RenameRuleItem item)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (RuleList.Items.Contains(item)) RuleList.ScrollIntoView(item);
        }));
    }
}
