using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using YuLiangToolkit.Tools.ExcelViewer.Parsing;
using YuLiangToolkit.Tools.ExcelViewer.ViewModels;

namespace YuLiangToolkit.Tools.ExcelViewer.Views;

/// <summary>
/// 「Excel 阅读器」的界面后台。只做三件必须碰控件的事：
/// <list type="number">
///   <item>按当前工作表重建 DataGrid 列（列标题 A..Z/AA..，列宽用 Excel 列宽，隐藏列不建）；</item>
///   <item>把 Excel 原始行号塞进行头（行号可能不连续：隐藏行会被跳过）；</item>
///   <item>选中单元格 → 交给 ViewModel 组装状态栏文字；拖入文件 → 打开。</item>
/// </list>
///
/// <para><b>为什么列要在代码后置里建</b>：每列对应「这一行的 Cells[列号-1]」，列号是数据的一部分，
/// XAML 里没法写 200 个模板；用 <see cref="FrameworkElementFactory"/> 只生成 200 个「列定义」，
/// 真正的单元格内容由虚拟化面板在滚动时按需创建，不会一次性生成整张表的 UIElement。</para>
/// </summary>
public partial class ExcelViewerView : UserControl
{
    /// <summary>当前 DataGrid 里实际显示的列（与 DataGrid.Columns 一一对应；隐藏列不在里面）。</summary>
    private readonly List<ExcelColumn> _visibleColumns = new();

    private ExcelViewerViewModel? _viewModel;

    public ExcelViewerView()
    {
        InitializeComponent();

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
        DataContextChanged += OnDataContextChanged;
    }

    // ---------------------------------------------------------------- DataContext / 工作表切换

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SheetLayoutChanged -= OnSheetLayoutChanged;

        _viewModel = DataContext as ExcelViewerViewModel;

        if (_viewModel is not null)
        {
            _viewModel.SheetLayoutChanged += OnSheetLayoutChanged;
            RebuildColumns();   // 视图被缓存后切回来时，列可能已经没了：这里恢复一次
        }
        else
        {
            SheetGrid.Columns.Clear();
            _visibleColumns.Clear();
        }
    }

    private void OnSheetLayoutChanged(object? sender, EventArgs e) => RebuildColumns();

    /// <summary>按当前工作表重建列：一行一个 DataGridTemplateColumn，列宽取 Excel 列宽。</summary>
    private void RebuildColumns()
    {
        SheetGrid.Columns.Clear();
        _visibleColumns.Clear();

        var sheet = _viewModel?.CurrentSheet;
        if (sheet is null) return;

        var cellTemplate = (DataTemplate)FindResource("ExcelCellTemplate");

        foreach (var column in sheet.Columns)
        {
            if (column.IsHidden) continue;   // 隐藏列不建（数量在状态栏说明）

            // 单元格内容 = 这一行 Cells[列号-1]；模板 ExcelCellTemplate 负责填充色 / 字体 / 对齐
            var factory = new FrameworkElementFactory(typeof(ContentControl));
            factory.SetBinding(ContentControl.ContentProperty,
                new Binding($"Cells[{column.ColumnIndex - 1}]"));
            factory.SetValue(ContentControl.ContentTemplateProperty, cellTemplate);
            factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            factory.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
            factory.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);

            var cellDataTemplate = new DataTemplate { VisualTree = factory };
            var gridColumn = new DataGridTemplateColumn
            {
                Header = column.Name,
                Width = new DataGridLength(Math.Clamp(column.WidthDip, 24.0, 600.0)),
                CellTemplate = cellDataTemplate,
                CanUserSort = false,
                CanUserResize = true,
                IsReadOnly = true,
            };

            SheetGrid.Columns.Add(gridColumn);
            _visibleColumns.Add(column);
        }

        _viewModel?.SelectCell(null);
    }

    // ---------------------------------------------------------------- 行号 / 选中单元格

    /// <summary>行头显示 Excel 原始行号（隐藏行被跳过，行号会跳号，不能拿列表下标当行号）。</summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is ExcelRow row) e.Row.Header = row.RowIndex;
    }

    /// <summary>选中单元格变化 → 通知 ViewModel 更新状态栏（地址 / 值 / 公式 / 格式）。</summary>
    private void OnSelectedCellsChanged(object? sender, SelectedCellsChangedEventArgs e)
    {
        if (_viewModel is null) return;

        var selected = SheetGrid.SelectedCells.FirstOrDefault();
        if (selected.Item is not ExcelRow row || selected.Column is null)
        {
            _viewModel.SelectCell(null);
            return;
        }

        var displayIndex = SheetGrid.Columns.IndexOf(selected.Column);
        if (displayIndex < 0 || displayIndex >= _visibleColumns.Count)
        {
            _viewModel.SelectCell(null);
            return;
        }

        var excelColumn = _visibleColumns[displayIndex];
        var cellIndex = excelColumn.ColumnIndex - 1;
        if (cellIndex < 0 || cellIndex >= row.Cells.Count)
        {
            _viewModel.SelectCell(null);
            return;
        }

        _viewModel.SelectCell(row.Cells[cellIndex]);
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
/// 单元格文字颜色：有显式字体色就用它；只有填充色就按填充色自动选深 / 浅；
/// 都没有则返回 <see cref="DependencyProperty.UnsetValue"/>（让 TextBlock 继承主题的 TextPrimaryBrush）。
/// </summary>
public sealed class ExcelCellForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ExcelCell cell) return DependencyProperty.UnsetValue;

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

/// <summary>自动换行 → TextWrapping（DataGrid 行高固定，换行内容超出会被裁掉；完整值看状态栏）。</summary>
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
