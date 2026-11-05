using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YuLiangToolkit.Tools.ExcelViewer.Parsing;
using YuLiangToolkit.Tools.ExcelViewer.ViewModels;

namespace YuLiangToolkit.Tools.ExcelViewer.Views;

/// <summary>
/// 「Excel 阅读器」的界面后台。只做必须碰控件的事：
/// <list type="number">
///   <item>按当前工作表重建主表 + 冻结行表的 DataGrid 列（列标题 A..Z/AA..，列宽用 Excel 列宽，隐藏列不建）；</item>
///   <item>冻结窗格还原：列用 <c>FrozenColumnCount</c>，行用主表上方第二张只读表并横向同步滚动；</item>
///   <item>行高：有 <c>ht</c> 用 ht（夹在 18 ~ 180 DIP），没有就按内容自动；关掉自动行高时统一 24；</item>
///   <item>行号塞进行头、选中单元格 → ViewModel 状态栏、查找跳转 / 右键菜单定位、拖入文件打开、滚动位置记忆。</item>
/// </list>
///
/// <para><b>为什么列要在代码后置里建</b>：每列对应「这一行的 Cells[列号-1]」，列号是数据的一部分，
/// XAML 里没法写 200 个模板；用 <see cref="FrameworkElementFactory"/> 只生成 200 个「列定义」，
/// 真正的单元格内容由虚拟化面板在滚动时按需创建，不会一次性生成整张表的 UIElement。
/// 冻结行表需要**另一套列定义**（DataGridColumn 不能同时属于两个 DataGrid），列宽在主表列被拖动后同步过去。</para>
/// </summary>
public partial class ExcelViewerView : UserControl
{
    /// <summary>自动行高的最小 / 最大 DIP：太矮看不清、太高会把一屏挤成一两行。</summary>
    private const double MinRowHeight = 18;
    private const double MaxRowHeight = 180;

    /// <summary>关掉自动行高时统一使用的行高（和旧版一致）。</summary>
    private const double FixedRowHeight = 24;

    /// <summary>当前 DataGrid 里实际显示的列（与主表 DataGrid.Columns 一一对应；隐藏列不在里面）。</summary>
    private readonly List<ExcelColumn> _visibleColumns = new();

    /// <summary>主表列定义（和 <see cref="_visibleColumns"/> 一一对应）。</summary>
    private readonly List<DataGridColumn> _mainColumns = new();

    /// <summary>冻结行表的列定义（和主表一一对应，列宽同步）。</summary>
    private readonly List<DataGridColumn> _frozenColumns = new();

    private ExcelViewerViewModel? _viewModel;

    // ---------- 冻结行表 / 滚动同步状态 ----------
    private ScrollViewer? _mainScrollViewer;
    private ScrollViewer? _frozenScrollViewer;

    /// <summary>上次重建列时绑定的工作表：同一张表（视图被缓存后切回来）不要重置滚动位置。</summary>
    private ExcelSheetItem? _layoutSheet;

    /// <summary>界面最近一次滚动比例（卸载时交给 ViewModel，切回来时恢复）。</summary>
    private double _lastKnownScrollRatio;

    /// <summary>切回来 / 重新挂载时要恢复的比例（0 = 顶部，不需要恢复）。</summary>
    private double _restoreOnNextLoad;

    /// <summary>待执行的滚动恢复比例（布局完成后取；重试几次是因为虚拟化要量过行面板才知道可滚高度）。</summary>
    private double? _restoreRatio;
    private int _restoreTries;

    // ---------- 行列标题高亮状态 ----------
    /// <summary>当前选中的行 / 列（1 基坐标的模型对象；切换选中时用来清上一处高亮）。</summary>
    private ExcelRow? _selectedRow;
    private ExcelColumn? _selectedColumn;
    private DataGridColumn? _selectedGridColumn;

    /// <summary>当前被改成高亮样式的表头容器（清高亮时直接对它们 ClearValue，恢复主题样式）。</summary>
    private DataGridRowHeader? _highlightedRowHeader;
    private DataGridRowHeader? _highlightedFrozenRowHeader;
    private DataGridColumnHeader? _highlightedColumnHeader;

    public ExcelViewerView()
    {
        InitializeComponent();

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
        DataContextChanged += OnDataContextChanged;
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ---------------------------------------------------------------- DataContext / 工作表切换

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SheetLayoutChanged -= OnSheetLayoutChanged;
            _viewModel.FreezeLayoutChanged -= OnFreezeLayoutChanged;
            _viewModel.RowHeightChanged -= OnRowHeightChanged;
            _viewModel.SearchNavigateRequested -= OnSearchNavigateRequested;
            _viewModel.CellNavigateRequested -= OnCellNavigateRequested;
        }

        _viewModel = DataContext as ExcelViewerViewModel;

        if (_viewModel is not null)
        {
            _viewModel.SheetLayoutChanged += OnSheetLayoutChanged;
            _viewModel.FreezeLayoutChanged += OnFreezeLayoutChanged;
            _viewModel.RowHeightChanged += OnRowHeightChanged;
            _viewModel.SearchNavigateRequested += OnSearchNavigateRequested;
            _viewModel.CellNavigateRequested += OnCellNavigateRequested;
            RebuildColumns();   // 视图被缓存后切回来时，列可能已经没了：这里恢复一次
        }
        else
        {
            _layoutSheet = null;
            _visibleColumns.Clear();
            _mainColumns.Clear();
            _frozenColumns.Clear();
            SheetGrid.Columns.Clear();
            FrozenGrid.Columns.Clear();
            FrozenGrid.ItemsSource = null;
            FrozenHost.Visibility = Visibility.Collapsed;
            ResetHeaderHighlightState();
        }
    }

    private void OnSheetLayoutChanged(object? sender, EventArgs e) => RebuildColumns();

    private void OnFreezeLayoutChanged(object? sender, EventArgs e)
    {
        ApplyFreezeLayout();
        UpdateFrozenMaxHeight();
        ApplyHeaderHighlight();   // 冻结行表刚换了 ItemsSource：选中的行如果在冻结区，行号头要跟着高亮
    }

    private void OnRowHeightChanged(object? sender, EventArgs e) => ApplyRowHeightSettings();

    /// <summary>
    /// 按当前工作表重建两套列：主表一套、冻结行表一套（列定义 / 列宽 / 行号 / 行高必须一致）。
    /// </summary>
    private void RebuildColumns()
    {
        var previousSheet = _layoutSheet;

        // 列要重建 → 旧的表头容器都会消失：先把高亮状态清掉，避免引用已废弃的容器
        ResetHeaderHighlightState();

        SheetGrid.Columns.Clear();
        FrozenGrid.Columns.Clear();
        _visibleColumns.Clear();
        _mainColumns.Clear();
        _frozenColumns.Clear();

        var sheet = _viewModel?.CurrentSheet;
        if (sheet is null)
        {
            _layoutSheet = null;
            ApplyFreezeLayout();
            return;
        }

        var cellTemplate = (DataTemplate)FindResource("ExcelCellTemplate");

        foreach (var column in sheet.Columns)
        {
            if (column.IsHidden) continue;   // 隐藏列不建（数量在状态栏说明）

            var cellDataTemplate = CreateCellTemplate(cellTemplate, column);

            var mainColumn = CreateGridColumn(column, cellDataTemplate);
            SheetGrid.Columns.Add(mainColumn);
            _mainColumns.Add(mainColumn);

            var frozenColumn = CreateGridColumn(column, cellDataTemplate);
            FrozenGrid.Columns.Add(frozenColumn);
            _frozenColumns.Add(frozenColumn);

            _visibleColumns.Add(column);
        }

        var sheetChanged = !ReferenceEquals(_viewModel?.SelectedSheet, previousSheet);
        _layoutSheet = _viewModel?.SelectedSheet;

        ApplyRowHeightSettings();
        ApplyFreezeLayout();
        _viewModel?.SelectCell(null);

        // 换表 / 打开新文件：恢复上次阅读位置的滚动比例；否则（同一张表重挂）保持当前滚动位置不动
        if (sheetChanged)
        {
            var restore = _viewModel?.ConsumePendingRestoreRatio();
            ScheduleRestoreScrollRatio(restore ?? 0);
        }
    }

    /// <summary>生成一列的单元格模板：内容 = 这一行 <c>Cells[列号-1]</c>，由 ExcelCellTemplate 渲染。</summary>
    private static DataTemplate CreateCellTemplate(DataTemplate cellTemplate, ExcelColumn column)
    {
        var factory = new FrameworkElementFactory(typeof(ContentControl));
        factory.SetBinding(ContentControl.ContentProperty,
            new Binding($"Cells[{column.ColumnIndex - 1}]"));
        factory.SetValue(ContentControl.ContentTemplateProperty, cellTemplate);
        factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        factory.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
        factory.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);

        return new DataTemplate { VisualTree = factory };
    }

    private static DataGridTemplateColumn CreateGridColumn(ExcelColumn column, DataTemplate cellDataTemplate) => new()
    {
        Header = column.Name,
        Width = new DataGridLength(Math.Clamp(column.WidthDip, 24.0, 600.0)),
        CellTemplate = cellDataTemplate,
        CanUserSort = false,
        CanUserResize = true,
        IsReadOnly = true,
    };

    // ---------------------------------------------------------------- 冻结窗格还原

    /// <summary>
    /// 按当前工作表 + 「冻结窗格」开关应用冻结布局：
    /// 列 → 主表 / 冻结表的 <c>FrozenColumnCount</c>；行 → 冻结行表只显示前 N 行。
    /// 关掉开关 / 没冻结 / 空表时把冻结表收起并清空 ItemsSource（避免上个表的冻结行残留）。
    /// </summary>
    private void ApplyFreezeLayout()
    {
        var viewModel = _viewModel;
        var sheet = viewModel?.CurrentSheet;
        var freeze = viewModel is { RestoreFreezePanes: true } ? sheet?.FreezePane : null;

        var frozenRows = freeze is { IsFrozen: true } ? Math.Min(freeze.FrozenRows, sheet?.Rows.Count ?? 0) : 0;
        var frozenColumns = freeze is { IsFrozen: true } ? Math.Min(freeze.FrozenColumns, _visibleColumns.Count) : 0;

        // FrozenColumnCount 必须在列已就绪后设置，且不能超过实际列数
        SheetGrid.FrozenColumnCount = Math.Clamp(frozenColumns, 0, SheetGrid.Columns.Count);
        FrozenGrid.FrozenColumnCount = Math.Clamp(frozenColumns, 0, FrozenGrid.Columns.Count);

        if (sheet is null || frozenRows <= 0)
        {
            FrozenGrid.ItemsSource = null;
            FrozenHost.Visibility = Visibility.Collapsed;
            return;
        }

        // 冻结行表的 ItemsSource 只取当前表的前 N 行（每次重建都新建列表，换表不会残留）
        FrozenGrid.ItemsSource = sheet.Rows.Take(frozenRows).ToList();
        FrozenHost.Margin = new Thickness(0, Math.Max(SheetGrid.ColumnHeaderHeight, 28), 0, 0);
        FrozenHost.Visibility = Visibility.Visible;
        UpdateFrozenMaxHeight();
        SyncFrozenColumnWidths();

        // 冻结行表刚显示时模板才生成，ScrollViewer 要等布局完才找得到
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(HookScrollViewers));
    }

    /// <summary>冻结行超高时的上限：最多占数据区 40%，不把主表挤死。</summary>
    private void UpdateFrozenMaxHeight()
    {
        if (FrozenHost.Visibility != Visibility.Visible) return;

        var areaHeight = DataArea.ActualHeight;
        var maxHeight = areaHeight > 0
            ? Math.Max(60, Math.Min(areaHeight * 0.4, areaHeight - 120))
            : 240;

        FrozenHost.MaxHeight = maxHeight;
        FrozenGrid.MaxHeight = maxHeight;
    }

    private void OnDataAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFrozenMaxHeight();

    /// <summary>主表列宽变化（拖动列标题分隔线）后，把冻结行表的列宽同步成一样。</summary>
    private void OnColumnHeaderDragCompleted(object? sender, DragCompletedEventArgs e) => SyncFrozenColumnWidths();

    private void SyncFrozenColumnWidths()
    {
        var count = Math.Min(_mainColumns.Count, _frozenColumns.Count);
        for (var index = 0; index < count; index++)
        {
            _frozenColumns[index].Width = _mainColumns[index].Width;
        }
    }

    // ---------------------------------------------------------------- 行号 / 行高 / 选中单元格

    /// <summary>
    /// 行头显示 Excel 原始行号（隐藏行被跳过，行号会跳号，不能拿列表下标当行号）；
    /// 同时按配置设置行高。两张表共用这个处理器，保证同一行高度一致。
    /// <para>Recycling 模式下同一行容器会复用，所以行头高亮也在这里重新挂一次 Loaded：
    /// 容器生成完才能拿到 <see cref="DataGridRowHeader"/>，滚动 / 复用后也不会残留上一行的高亮。</para>
    /// </summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not ExcelRow row) return;
        e.Row.Header = row.RowIndex;
        ApplyRowHeight(e.Row, row);

        // 先摘再挂：同一个容器被复用时避免重复订阅
        e.Row.Loaded -= OnRowContainerLoaded;
        e.Row.Loaded += OnRowContainerLoaded;
    }

    /// <summary>行容器加载完成：行号头已经生成，按当前选中行决定要不要高亮（滚动复用后不残留）。</summary>
    private void OnRowContainerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGridRow container || container.Item is not ExcelRow row) return;

        var grid = FindAncestor<DataGrid>(container);
        if (grid is null) return;

        if (FindVisualChild<DataGridRowHeader>(container) is not { } header) return;

        if (IsSelectedRow(row)) SetHeaderHighlight(header);
        else ClearHeaderHighlight(header);
    }

    /// <summary>这一行是不是当前选中的行（行号相同；冻结行表与主表共用同一个 ExcelRow 实例）。</summary>
    private bool IsSelectedRow(ExcelRow row) => _selectedRow is not null && _selectedRow.RowIndex == row.RowIndex;

    /// <summary>
    /// 自动行高开：有 ht 用 ht（夹 18 ~ 180），没有就交给内容自动撑高（NaN）；
    /// 关：行高统一 24（由 DataGrid.RowHeight 统一控制，这里把行自己的 Height 复位成 NaN）。
    /// <para>⚠ Recycling 模式下同一个 DataGridRow 会被复用给别的行，所以每次 LoadingRow 都重设一遍，
    /// 不让上一行的高度残留。</para>
    /// </summary>
    private void ApplyRowHeight(DataGridRow gridRow, ExcelRow excelRow)
    {
        if (_viewModel?.AutoRowHeight != true)
        {
            gridRow.Height = double.NaN;
            return;
        }

        gridRow.Height = excelRow.HeightDip is { } heightDip
            ? Math.Clamp(heightDip, MinRowHeight, MaxRowHeight)
            : double.NaN;
    }

    /// <summary>切换「自动行高」时重设两张表的行高策略，并刷新已经生成出来的行。</summary>
    private void ApplyRowHeightSettings()
    {
        var auto = _viewModel?.AutoRowHeight ?? true;
        var rowHeight = auto ? double.NaN : FixedRowHeight;
        SheetGrid.RowHeight = rowHeight;
        FrozenGrid.RowHeight = rowHeight;

        RefreshRealizedRowHeights(SheetGrid);
        RefreshRealizedRowHeights(FrozenGrid);
    }

    /// <summary>只刷新已经生成出来的行（虚拟化下没生成的行会在 LoadingRow 时按新策略设置）。</summary>
    private void RefreshRealizedRowHeights(DataGrid grid)
    {
        for (var index = 0; index < grid.Items.Count; index++)
        {
            if (grid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow row
                && row.Item is ExcelRow excelRow)
            {
                ApplyRowHeight(row, excelRow);
            }
        }
    }

    /// <summary>
    /// 选中单元格变化 → 通知 ViewModel 更新状态栏（地址 / 值 / 公式 / 链接 / 格式），
    /// 同时把该行行号头与列字母头高亮（切换选中会清掉上一处）。
    /// </summary>
    private void OnSelectedCellsChanged(object? sender, SelectedCellsChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            SetSelection(null, null, null);
            return;
        }

        var selected = SheetGrid.SelectedCells.FirstOrDefault();
        if (selected.Item is not ExcelRow row || selected.Column is null)
        {
            _viewModel.SelectCell(null);
            SetSelection(null, null, null);
            return;
        }

        var displayIndex = SheetGrid.Columns.IndexOf(selected.Column);
        if (displayIndex < 0 || displayIndex >= _visibleColumns.Count)
        {
            _viewModel.SelectCell(null);
            SetSelection(null, null, null);
            return;
        }

        var excelColumn = _visibleColumns[displayIndex];
        var cellIndex = excelColumn.ColumnIndex - 1;
        if (cellIndex < 0 || cellIndex >= row.Cells.Count)
        {
            _viewModel.SelectCell(null);
            SetSelection(null, null, null);
            return;
        }

        _viewModel.SelectCell(row.Cells[cellIndex]);
        SetSelection(row, excelColumn, selected.Column);
    }

    /// <summary>记录当前选中的行 / 列并刷新表头高亮（传 null 表示清空选择）。</summary>
    private void SetSelection(ExcelRow? row, ExcelColumn? column, DataGridColumn? gridColumn)
    {
        _selectedRow = row;
        _selectedColumn = column;
        _selectedGridColumn = gridColumn;

        ApplyHeaderHighlight();
    }

    // ---------------------------------------------------------------- 右键菜单

    /// <summary>
    /// 右键时先选中鼠标下的单元格（DataGrid 默认右键不改选择），
    /// 这样四个「复制…」命令拿到的就是用户右键的那一格；点在表头 / 空白处就不弹菜单。
    /// </summary>
    private void OnSheetGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is null)
        {
            e.Handled = true;
            return;
        }

        SheetGrid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
        cell.IsSelected = true;
        cell.Focus();
    }

    // ---------------------------------------------------------------- 查找 / 超链接跳转

    private void OnSearchNavigateRequested(object? sender, ExcelSearchNavigateEventArgs e)
        => NavigateToCell(e.Hit.RowIndex, e.Hit.ColumnIndex, "命中");

    /// <summary>
    /// 内部超链接 / YLT_EXCEL_SELECT 触发的定位：工作表已经切好，等布局完成后滚到目标格并选中。
    /// </summary>
    private void OnCellNavigateRequested(object? sender, ExcelCellNavigateEventArgs e)
    {
        var rowIndex = e.RowIndex;
        var columnIndex = e.ColumnIndex;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => NavigateToCell(rowIndex, columnIndex, "目标单元格")));
    }

    /// <summary>
    /// 把指定 Excel 坐标（1 基）滚入视野并选中；找不到（隐藏行 / 截断 / 隐藏列）时只给状态栏提示。
    /// 查找命中、内部超链接、截图自动选中都走这里，保证行为一致。
    /// </summary>
    private void NavigateToCell(int rowIndex, int columnIndex, string targetName)
    {
        var sheet = _viewModel?.CurrentSheet;
        if (sheet is null) return;

        var row = sheet.Rows.FirstOrDefault(item => item.RowIndex == rowIndex);
        if (row is null)
        {
            _viewModel?.NoteNavigationFailed($"{targetName}所在的行已被隐藏或截断，无法定位");
            return;
        }

        var displayIndex = _visibleColumns.FindIndex(column => column.ColumnIndex == columnIndex);
        if (displayIndex < 0 || displayIndex >= SheetGrid.Columns.Count)
        {
            SheetGrid.ScrollIntoView(row);
            _viewModel?.NoteNavigationFailed(
                $"{targetName}在隐藏列 {ExcelAddresses.ColumnName(columnIndex)}，这条列不显示");
            return;
        }

        var column = SheetGrid.Columns[displayIndex];
        SheetGrid.ScrollIntoView(row, column);
        SheetGrid.UpdateLayout();
        SheetGrid.CurrentCell = new DataGridCellInfo(row, column);

        // 单元格容器生成后再选中 + 给焦点（列虚拟化时刚 ScrollIntoView 完可能还没生成）
        if (SheetGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow rowContainer)
        {
            var cell = FindAncestor<DataGridCell>(column.GetCellContent(rowContainer));
            if (cell is not null)
            {
                cell.IsSelected = true;
                cell.Focus();
            }
        }

        SheetGrid.Focus();

        // 状态栏地址 / 值同步 + 行列标题高亮：即使容器还没生成，也先按模型更新
        var cellIndex = columnIndex - 1;
        if (cellIndex >= 0 && cellIndex < row.Cells.Count) _viewModel?.SelectCell(row.Cells[cellIndex]);
        SetSelection(row, _visibleColumns[displayIndex], column);
    }

    // ---------------------------------------------------------------- 双击打开超链接

    /// <summary>
    /// 双击带链接的单元格：交给 ViewModel 处理（外部链接过协议白名单后打开，内部链接切表 + 定位）。
    /// 不含链接的格子双击保持原样（仍然只是选中，没有编辑入口）。
    /// </summary>
    private void OnSheetGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null) return;

        var cellControl = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cellControl?.Column is null || cellControl.DataContext is not ExcelRow row) return;

        var displayIndex = SheetGrid.Columns.IndexOf(cellControl.Column);
        if (displayIndex < 0 || displayIndex >= _visibleColumns.Count) return;

        var cellIndex = _visibleColumns[displayIndex].ColumnIndex - 1;
        if (cellIndex < 0 || cellIndex >= row.Cells.Count) return;

        var cell = row.Cells[cellIndex];
        if (cell.Hyperlink is null) return;

        e.Handled = true;
        _viewModel.ActivateCellHyperlink(cell);
    }

    // ---------------------------------------------------------------- 行列标题高亮

    /// <summary>只给一个已经生成的行号头设置高亮（行容器生成 / 滚动复用后按当前选中重设）。</summary>
    private void SetHeaderHighlight(Control header)
    {
        var background = GetHeaderHighlightBackground();
        var foreground = GetHeaderHighlightForeground();
        if (background is null || foreground is null) return;

        header.Background = background;
        header.Foreground = foreground;
    }

    /// <summary>
    /// 按当前选中行 / 列高亮表头：
    /// <list type="bullet">
    ///   <item>列字母头（主表）→ 该列高亮；</item>
    ///   <item>行号头（主表）→ 该行高亮；</item>
    ///   <item>冻结行表如果显示着同一行，它的行号头也高亮（它在主表行号头上叠着）。</item>
    /// </list>
    /// 只改已生成的表头容器的 Background / Foreground（从主题 <c>AccentBrush</c> 克隆），
    /// 不重建整张表；切换选中时对旧容器 <c>ClearValue</c>，让它回到表头样式。
    /// </summary>
    private void ApplyHeaderHighlight()
    {
        var background = GetHeaderHighlightBackground();
        var foreground = GetHeaderHighlightForeground();
        if (background is null || foreground is null) return;   // 主题里没有 AccentBrush 时不做高亮（不写死颜色）

        // 主表的行号头
        DataGridRowHeader? mainRowHeader = null;
        if (_selectedRow is not null
            && SheetGrid.ItemContainerGenerator.ContainerFromItem(_selectedRow) is DataGridRow mainContainer)
        {
            mainRowHeader = FindVisualChild<DataGridRowHeader>(mainContainer);
        }

        ReplaceHighlightedHeader(ref _highlightedRowHeader, mainRowHeader, background, foreground);

        // 冻结行表的行号头：选中的行在冻结区时才处理（冻结表在数据区顶部叠一层）
        DataGridRowHeader? frozenRowHeader = null;
        if (_selectedRow is not null
            && FrozenHost.Visibility == Visibility.Visible
            && FrozenGrid.ItemContainerGenerator.ContainerFromItem(_selectedRow) is DataGridRow frozenContainer)
        {
            frozenRowHeader = FindVisualChild<DataGridRowHeader>(frozenContainer);
        }

        ReplaceHighlightedHeader(ref _highlightedFrozenRowHeader, frozenRowHeader, background, foreground);

        // 列字母头
        DataGridColumnHeader? columnHeader = _selectedGridColumn is null
            ? null
            : FindColumnHeader(SheetGrid, _selectedGridColumn);
        ReplaceHighlightedHeader(ref _highlightedColumnHeader, columnHeader, background, foreground);
    }

    /// <summary>把旧的高亮表头清掉、新的设置上（同一个容器不重复设置）。</summary>
    private static void ReplaceHighlightedHeader<T>(ref T? previous, T? current, Brush background, Brush foreground)
        where T : Control
    {
        if (ReferenceEquals(previous, current)) return;

        ClearHeaderHighlight(previous);
        previous = current;
        if (current is null) return;

        current.Background = background;
        current.Foreground = foreground;
    }

    /// <summary>清掉某个表头容器上的高亮，让它恢复主题表头样式（等效于「没设置过局部值」）。</summary>
    private static void ClearHeaderHighlight(Control? header)
    {
        if (header is null) return;
        header.ClearValue(Control.BackgroundProperty);
        header.ClearValue(Control.ForegroundProperty);
    }

    /// <summary>高亮背景：主题 AccentBrush 的半透明版本（约 33% 不透明度），克隆后冻结。</summary>
    private Brush? GetHeaderHighlightBackground()
    {
        // 每次都从主题取：四套主题切换后，下一次刷新选中 / 滚动就会用新强调色（不缓存旧主题的画笔）
        if (TryFindResource("AccentBrush") is not SolidColorBrush accent) return null;

        var color = accent.Color;
        var background = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
        background.Freeze();
        return background;
    }

    /// <summary>高亮前景：主题 AccentBrush（在浅色 / 深色表头上都保持对比度），克隆后冻结。</summary>
    private Brush? GetHeaderHighlightForeground()
    {
        if (TryFindResource("AccentBrush") is not SolidColorBrush accent) return null;

        var foreground = accent.Clone();
        foreground.Freeze();
        return foreground;
    }

    /// <summary>清空当前所有高亮状态（切表 / 重建列 / 清空选择时用）。</summary>
    private void ResetHeaderHighlightState()
    {
        ClearHeaderHighlight(_highlightedRowHeader);
        ClearHeaderHighlight(_highlightedFrozenRowHeader);
        ClearHeaderHighlight(_highlightedColumnHeader);

        _highlightedRowHeader = null;
        _highlightedFrozenRowHeader = null;
        _highlightedColumnHeader = null;
        _selectedRow = null;
        _selectedColumn = null;
        _selectedGridColumn = null;
    }

    /// <summary>在 DataGrid 的可视化树里找某一列的列标题容器（列虚拟化时可能还没生成 → 返回 null，滚动后会重试）。</summary>
    private static DataGridColumnHeader? FindColumnHeader(DataGrid grid, DataGridColumn column)
        => FindVisualChild<DataGridColumnHeader>(grid, header => ReferenceEquals(header.Column, column));

    // ---------------------------------------------------------------- 滚动位置记忆 / 冻结行横向同步

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        HookScrollViewers();

        // 视图被外壳缓存后切回来：恢复离开时的滚动比例（同一张表；表格列仍在，不会重置到顶部）
        if (_restoreOnNextLoad <= 0) return;
        var ratio = _restoreOnNextLoad;
        _restoreOnNextLoad = 0;
        ScheduleRestoreScrollRatio(ratio);
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        // 切走工具页（视图可能进缓存）/ 关窗口：把「当前表 + 滚动比例」交给 ViewModel 保存
        var ratio = ComputeScrollRatio(_mainScrollViewer);
        _lastKnownScrollRatio = ratio;
        _restoreOnNextLoad = ratio;
        _viewModel?.NoteScrollPosition(ratio);
        _viewModel?.FlushReadingPosition(saveNow: true);
    }

    /// <summary>找到主表 / 冻结表内部的 ScrollViewer 并挂上事件（列重建、冻结表显示后要重新挂）。</summary>
    private void HookScrollViewers()
    {
        var main = FindVisualChild<ScrollViewer>(SheetGrid);
        if (main is not null && !ReferenceEquals(main, _mainScrollViewer))
        {
            if (_mainScrollViewer is not null)
                _mainScrollViewer.ScrollChanged -= OnMainScrollViewerScrollChanged;

            _mainScrollViewer = main;
            main.ScrollChanged += OnMainScrollViewerScrollChanged;
        }

        var frozen = FindVisualChild<ScrollViewer>(FrozenGrid);
        if (frozen is not null) _frozenScrollViewer = frozen;

        // 冻结行表和主表的横向滚动必须同步（冻结表没有自己的滚动条）
        if (_mainScrollViewer is not null && _frozenScrollViewer is not null)
            _frozenScrollViewer.ScrollToHorizontalOffset(_mainScrollViewer.HorizontalOffset);
    }

    private void OnMainScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_frozenScrollViewer is not null
            && Math.Abs(_frozenScrollViewer.HorizontalOffset - e.HorizontalOffset) > 0.5)
        {
            _frozenScrollViewer.ScrollToHorizontalOffset(e.HorizontalOffset);
        }

        // 列虚拟化时横向滚动会重建列标题容器：重新找一次，保证高亮的列字母头还亮着
        if (Math.Abs(e.HorizontalChange) > 0.5) ApplyHeaderHighlight();

        // 只有纵向滚动 / 行高变化才更新阅读位置，横向滚动不用写配置
        if (Math.Abs(e.VerticalChange) < 0.0001 && Math.Abs(e.ExtentHeightChange) < 0.0001) return;

        var ratio = ComputeScrollRatio(_mainScrollViewer);
        if (Math.Abs(ratio - _lastKnownScrollRatio) < 0.0005) return;

        _lastKnownScrollRatio = ratio;
        _viewModel?.NoteScrollPosition(ratio);
    }

    /// <summary>滚动位置比例 0 ~ 1（可滚高度为 0 时算顶部）。</summary>
    private static double ComputeScrollRatio(ScrollViewer? viewer)
    {
        if (viewer is null || viewer.ScrollableHeight <= 0) return 0;
        return Math.Clamp(viewer.VerticalOffset / viewer.ScrollableHeight, 0, 1);
    }

    /// <summary>等布局完成后把主表滚到指定比例（虚拟化下要重试几次，等可滚高度量出来）。</summary>
    private void ScheduleRestoreScrollRatio(double ratio)
    {
        _restoreRatio = ExcelReadingPosition.ClampRatio(ratio);
        _restoreTries = 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryApplyRestoreScrollRatio));
    }

    private void TryApplyRestoreScrollRatio()
    {
        if (_restoreRatio is not { } ratio) return;

        var viewer = _mainScrollViewer ?? FindVisualChild<ScrollViewer>(SheetGrid);
        if (viewer is null)
        {
            if (_restoreTries++ < 3)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(TryApplyRestoreScrollRatio));
            }
            else
            {
                _restoreRatio = null;
            }
            return;
        }

        _mainScrollViewer = viewer;
        viewer.ScrollToVerticalOffset(Math.Max(0, ratio * viewer.ScrollableHeight));

        if (_restoreTries++ < 2)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(TryApplyRestoreScrollRatio));
            return;
        }

        _restoreRatio = null;
    }

    // ---------------------------------------------------------------- 冻结行覆盖层：禁交互、转发滚轮

    /// <summary>冻结行区域不吃点击（不能在那儿选中单元格 / 弹自己的菜单）。</summary>
    private void OnFrozenHostPreviewMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>在冻结行上滚滚轮 = 滚主表（冻结表自己没有滚动条）。</summary>
    private void OnFrozenHostPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mainScrollViewer is null) return;

        var lines = SystemParameters.WheelScrollLines;
        var step = lines > 0 ? lines : 3;
        var target = _mainScrollViewer.VerticalOffset - e.Delta / 120.0 * step;
        _mainScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
        e.Handled = true;
    }

    // ---------------------------------------------------------------- 键盘：Ctrl+F 聚焦查找 / Ctrl+` 显示公式

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Ctrl+`（反引号 / 波浪号键）。WPF 里 Key.Oem3 与 Key.OemTilde 是同一个值（别名），
        // 不同键盘布局报的名字不一样，这里直接用 OemTilde 即可。
        if (e.Key == Key.OemTilde && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _viewModel?.ToggleShowFormulas();
            e.Handled = true;
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _viewModel?.FindPrevious();
        else _viewModel?.FindNext();
    }

    // ---------------------------------------------------------------- 拖入文件

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedWorkbookPath(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_viewModel is null || !TryGetDroppedWorkbookPath(e.Data, out var path)) return;

        _ = _viewModel.OpenFileAsync(path);
    }

    /// <summary>从拖放数据里找第一个 .xlsx / .xlsm（只接受这两种，其它文件忽略）。</summary>
    private static bool TryGetDroppedWorkbookPath(IDataObject data, out string path)
    {
        path = "";
        var files = data.GetData(DataFormats.FileDrop) as string[];
        if (files is null) return false;

        foreach (var file in files)
        {
            if (XlsxParser.IsSupportedExtension(file))
            {
                path = file;
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- 可视化树小工具

    /// <summary>深度优先找第一个指定类型的孩子（DataGrid 模板里的 ScrollViewer / 单元格）。</summary>
    private static T? FindVisualChild<T>(DependencyObject? root) where T : DependencyObject
        => FindVisualChild<T>(root, predicate: null);

    /// <summary>深度优先找第一个满足条件的指定类型孩子（例如「某一列的列标题容器」）。</summary>
    private static T? FindVisualChild<T>(DependencyObject? root, Predicate<T>? predicate)
        where T : DependencyObject
    {
        if (root is null) return null;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T hit && (predicate is null || predicate(hit))) return hit;

            var deeper = FindVisualChild(child, predicate);
            if (deeper is not null) return deeper;
        }

        return null;
    }

    /// <summary>沿可视化树往上找第一个指定类型的祖先（例如从 TextBlock 找到 DataGridCell）。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T hit) return hit;
        }

        return null;
    }
}

// =====================================================================================
// 单元格转换器：把解析出来的 ExcelCell 变成 WPF 的 Brush / FontWeight / 对齐 / 内边距
// =====================================================================================

/// <summary>颜色 / 画刷的缓存：同一张表里几千个格子通常只有几种颜色，没必要每格 new 一个 Brush。</summary>
internal static class ExcelBrushCache
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ARGB 文本（#AARRGGBB / AARRGGBB）→ 冻结的 SolidColorBrush；非法 / 全透明返回 null。</summary>
    public static SolidColorBrush? FromArgb(string? argb)
    {
        if (string.IsNullOrWhiteSpace(argb)) return null;

        var text = argb.Trim().TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        if (text.Length != 8) return null;

        return Cache.GetOrAdd(text, static value =>
        {
            var color = Color.FromArgb(
                (byte)Convert.ToInt32(value.Substring(0, 2), 16),
                (byte)Convert.ToInt32(value.Substring(2, 2), 16),
                (byte)Convert.ToInt32(value.Substring(4, 2), 16),
                (byte)Convert.ToInt32(value.Substring(6, 2), 16));

            var brush = new SolidColorBrush(color);
            brush.Freeze();   // 冻结后可以跨线程共享，性能也更好
            return brush;
        });
    }

    /// <summary>这个颜色是不是「亮色」（用来决定填充色上的文字该用深色还是浅色）。</summary>
    public static bool IsLightColor(string argb)
    {
        var text = argb.Trim().TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        if (text.Length != 8) return false;

        var red = Convert.ToInt32(text.Substring(2, 2), 16);
        var green = Convert.ToInt32(text.Substring(4, 2), 16);
        var blue = Convert.ToInt32(text.Substring(6, 2), 16);

        // 感知亮度（和常见高对比算法一致），阈值 150 附近效果最稳
        return red * 0.299 + green * 0.587 + blue * 0.114 > 150;
    }

    /// <summary>深色文字（用在亮色填充上）。</summary>
    public static SolidColorBrush DarkText { get; } = CreateFrozen("#FF1E1E1E");

    /// <summary>浅色文字（用在深色填充上）。</summary>
    public static SolidColorBrush LightText { get; } = CreateFrozen("#FFF2F2F2");

    private static SolidColorBrush CreateFrozen(string argb)
    {
        var text = argb.TrimStart('#');
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)Convert.ToInt32(text.Substring(0, 2), 16),
            (byte)Convert.ToInt32(text.Substring(2, 2), 16),
            (byte)Convert.ToInt32(text.Substring(4, 2), 16),
            (byte)Convert.ToInt32(text.Substring(6, 2), 16)));
        brush.Freeze();
        return brush;
    }
}

/// <summary>单元格填充色 → Border.Background（没有填充色时透明，不画白块）。</summary>
public sealed class ExcelCellBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ExcelCell cell) return Brushes.Transparent;

        var brush = ExcelBrushCache.FromArgb(cell.Style.FillColorArgb);
        // alpha 太小（几乎透明）也当没有填充，免得白底上出现灰蒙蒙一块
        if (brush is null || brush.Color.A < 16) return Brushes.Transparent;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 单元格文字颜色：带超链接的格子用主题强调色（蓝字 + 下划线由模板里的
/// <see cref="ExcelCellTextDecorationsConverter"/> 负责）；有显式字体色就用它；
/// 只有填充色就按填充色自动选深 / 浅；都没有则返回 <see cref="DependencyProperty.UnsetValue"/>
/// （让 TextBlock 继承主题的 TextPrimaryBrush）。
/// </summary>
public sealed class ExcelCellForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ExcelCell cell) return DependencyProperty.UnsetValue;

        // 超链接优先：链接文字统一用主题强调色（深色主题下仍然清晰）
        if (cell.Hyperlink is not null)
        {
            return Application.Current?.TryFindResource("AccentBrush") as Brush
                ?? DependencyProperty.UnsetValue;
        }

        var explicitColor = ExcelBrushCache.FromArgb(cell.Style.FontColorArgb);
        if (explicitColor is not null && explicitColor.Color.A >= 16) return explicitColor;

        var fill = ExcelBrushCache.FromArgb(cell.Style.FillColorArgb);
        if (fill is not null && fill.Color.A >= 16)
        {
            return ExcelBrushCache.IsLightColor(cell.Style.FillColorArgb!)
                ? ExcelBrushCache.DarkText
                : ExcelBrushCache.LightText;
        }

        return DependencyProperty.UnsetValue;   // 交给主题默认前景色
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 带超链接的格子加下划线；普通格子返回空装饰集合（不留上一格的样式）。
/// 链接文字颜色由 <see cref="ExcelCellForegroundConverter"/> 提供主题强调色。
/// </summary>
public sealed class ExcelCellTextDecorationsConverter : IValueConverter
{
    /// <summary>空的装饰集合 = 没有下划线（静态共享，避免每格 new）。</summary>
    private static readonly TextDecorationCollection NoDecorations = CreateEmptyDecorations();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ExcelCell { Hyperlink: not null } ? TextDecorations.Underline : NoDecorations;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static TextDecorationCollection CreateEmptyDecorations()
    {
        var decorations = new TextDecorationCollection();
        decorations.Freeze();
        return decorations;
    }
}

/// <summary>
/// 「显示公式」用 MultiBinding 转换器：<c>[0]=ExcelCell</c>、<c>[1]=ShowFormulas</c>。
/// <list type="bullet">
///   <item>不带参数（<c>TextBlock.Text</c>）→ 显示文字：有公式且开关打开时 <c>=公式原文</c>，否则显示值；</item>
///   <item>参数为 <c>ToolTip</c> → 悬停提示：完整公式 / 链接地址 / 很长的显示值，没有可提示内容时返回 UnsetValue（不弹空框）。</item>
/// </list>
/// 纯计算在 <see cref="Parsing.ExcelCellText"/> 里（自检直接断言），这里只做 WPF 桥接。
/// </summary>
public sealed class ExcelCellTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var cell = values.Length > 0 ? values[0] as ExcelCell : null;
        var showFormulas = values.Length > 1 && values[1] is bool flag && flag;

        if (parameter is string mode && mode.Equals("ToolTip", StringComparison.Ordinal))
        {
            var toolTip = ExcelCellText.ToolTipFor(cell, showFormulas);
            return toolTip is null ? DependencyProperty.UnsetValue : toolTip;
        }

        return ExcelCellText.For(cell, showFormulas);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("单元格显示文本是只读的");
}

/// <summary>粗体 → FontWeight。</summary>
public sealed class ExcelCellFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ExcelCell { Style.Bold: true } ? FontWeights.Bold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>斜体 → FontStyle。</summary>
public sealed class ExcelCellFontStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ExcelCell { Style.Italic: true } ? FontStyles.Italic : FontStyles.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>字号（磅）→ WPF 的 DIP（96 / 72 换算），并夹在 8 ~ 48 DIP 防止一格撑爆整行。</summary>
public sealed class ExcelCellFontSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ExcelCell { Style.FontSizePt: { } sizePt }) return 12.5;   // 默认字号（和 DataGrid 一致）

        return Math.Clamp(sizePt * 96.0 / 72.0, 8.0, 48.0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Excel 的水平对齐 → TextBlock.TextAlignment。</summary>
public sealed class ExcelCellTextAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ExcelCell cell
            ? cell.Style.HorizontalAlignment switch
            {
                ExcelHorizontalAlignment.Center => TextAlignment.Center,
                ExcelHorizontalAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            }
            : TextAlignment.Left;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>自动换行 → TextWrapping（行高现在按内容自动撑高，换行内容不再被裁；关掉自动行高时才可能压行）。</summary>
public sealed class ExcelCellTextWrappingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ExcelCell { Style.WrapText: true } ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>单元格内边距：留 4 DIP 呼吸空间，再按 Excel 的缩进级别每级加 8 DIP。</summary>
public sealed class ExcelCellPaddingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var indent = value is ExcelCell cell ? Math.Clamp(cell.Style.Indent, 0, 15) : 0;
        return new Thickness(4 + indent * 8.0, 0, 4, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
