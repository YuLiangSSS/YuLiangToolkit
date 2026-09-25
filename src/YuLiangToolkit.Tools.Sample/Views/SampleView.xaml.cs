using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.Sample.Views;

public partial class SampleView : UserControl
{
    public SampleView()
    {
        InitializeComponent();

        // 表头与数据列的对齐全部在 XAML 里解决（表头写在 ListBox 的 ControlTemplate 内，
        // 与数据行同处一列，末尾再占一列滚动条宽度）—— 这里不做任何布局同步。

        // 验证用：YLT_MEASURE=<文件> 时把表头/数据行的真实几何写进文件（见 DumpMeasure）。
        var path = Environment.GetEnvironmentVariable("YLT_MEASURE");
        if (!string.IsNullOrWhiteSpace(path))
        {
            // YLT_MEASURE_RESIZE=<宽>x<高> 时先缩到那个尺寸再测（验证“伸缩窗口后是否还对齐”）
            var resize = Environment.GetEnvironmentVariable("YLT_MEASURE_RESIZE");
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (!string.IsNullOrWhiteSpace(resize))
                {
                    var parts = resize.Split('x');
                    if (parts.Length == 2
                        && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h)
                        && Window.GetWindow(this) is { } win)
                    {
                        win.Width = w;
                        win.Height = h;
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
                DumpMeasure(path);
            }));
        }

        // 验证用：YLT_WHEEL_TEST=<文件> 时把「内层列表 / 外层页面」的滚动几何与滚动链的判定写进文件。
        // 注意：这里**不模拟滚轮事件** —— WPF 的 ScrollViewer.OnMouseWheel 会读真实鼠标位置，
        // 合成的事件走不到 ListBox（试过，一次都不触发），拿它验证会得出错误结论。
        // 判定逻辑本身由自检覆盖（见 tests 里的 ScrollChainingChecks），鼠标那一层靠手动滚一下。
        var wheelPath = Environment.GetEnvironmentVariable("YLT_WHEEL_TEST");
        if (!string.IsNullOrWhiteSpace(wheelPath))
        {
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                DumpWheel(wheelPath);
            }));
        }
    }

    // ---------------------------------------------------------------- 滚动链验证（YLT_WHEEL_TEST）

    private void DumpWheel(string path)
    {
        var sb = new StringBuilder();
        var inner = FindChild<ScrollViewer>(FileListBox);
        var outer = FindOuterScrollViewer(FileListBox);   // 与 ScrollChaining 同一个查找方式
        var chain = YuLiangToolkit.Core.Mvvm.ScrollChaining.Decide(inner, outer, -120);

        sb.AppendLine($"挂了滚动链（ScrollChaining.Enabled）= {YuLiangToolkit.Core.Mvvm.ScrollChaining.GetEnabled(FileListBox)}");
        sb.AppendLine($"内层列表：{Describe(inner)}   当前 {Offset(inner)}");
        sb.AppendLine($"外层页面：{Describe(outer)}   当前 {Offset(outer)}");
        sb.AppendLine();
        sb.AppendLine($"当前状态下滚轮向下会滚谁 = {chain.Action}（Inner=内层自滚 / Outer=交给外层）");
        sb.AppendLine($"ScrollChaining 最后一条判断 = {YuLiangToolkit.Core.Mvvm.ScrollChaining.LastDecision}");
        sb.AppendLine();
        sb.AppendLine("把鼠标放在上面这个列表里滚一下，再重开一次看 LastDecision 的变化：");
        sb.AppendLine("  · 内层没到底 → 应该是「内层还能滚 → 交给 ListBox」");
        sb.AppendLine("  · 内层已经到底 → 应该是「内层到头 → 滚外层」，同时外层偏移变大");

        try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); } catch { /* 忽略 */ }
    }

    private static string Offset(ScrollViewer? v)
        => v is null ? "(无)" : $"{v.VerticalOffset:0.#}/{Math.Max(0, v.ExtentHeight - v.ViewportHeight):0.#}";

    private static string Describe(ScrollViewer? v)
        => v is null
            ? "(没找到)"
            : $"滚动条={v.ComputedVerticalScrollBarVisibility} 视口={v.ViewportHeight:0.#} 内容={v.ExtentHeight:0.#}";


    private static ScrollViewer? FindOuterScrollViewer(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer viewer && viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                return viewer;
        }
        return null;
    }

    private void DumpMeasure(string path)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.AppendLine(s);

        // 表头在 ListBox 的 ControlTemplate 里，具名元素要用 Template.FindName 取
        var header = FileListBox.Template?.FindName("HeaderGrid", FileListBox) as Grid;
        var headerName = FileListBox.Template?.FindName("HeaderName", FileListBox) as TextBlock;
        var headerSize = FileListBox.Template?.FindName("HeaderSize", FileListBox) as TextBlock;
        var headerTime = FileListBox.Template?.FindName("HeaderTime", FileListBox) as TextBlock;

        Line($"ListBox.ActualWidth      = {FileListBox.ActualWidth:0.##}");
        Line($"Header.ActualWidth       = {header?.ActualWidth:0.##}");
        Line($"WindowContent.ActualWidth= {(Content as FrameworkElement)?.ActualWidth:0.##}");

        // 列表内部的 ScrollViewer：真实可用宽度就在它的 ViewportWidth 上
        var sv = FindChild<ScrollViewer>(FileListBox);
        if (sv is not null)
        {
            Line($"ScrollViewer.ActualWidth = {sv.ActualWidth:0.##}");
            Line($"ScrollViewer.ViewportWidth = {sv.ViewportWidth:0.##}");
            Line($"VerticalScrollBarVisibility = {sv.ComputedVerticalScrollBarVisibility}");
        }

        var pres = sv is null ? null : FindChild<ScrollContentPresenter>(sv);
        if (pres is not null) Line($"ScrollContentPresenter.ActualWidth = {pres.ActualWidth:0.##}");

        // 行里“列数最多的那个 Grid”才是决定数据列位置的容器（模板里还有别的 Grid，别抓错）
        var rowGrid = RowGridOf(0);
        Line($"RowGrid  Width = {rowGrid?.ActualWidth:0.##}  相对窗口 X = {OffsetOf(rowGrid, Content as Visual):0.##}  列数={rowGrid?.ColumnDefinitions.Count}");
        Line($"RowItem.ActualWidth      = {(FileListBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.ActualWidth:0.##}");
        if (rowGrid is not null)
        {
            for (var i = 0; i < rowGrid.ColumnDefinitions.Count; i++)
                Line($"  row col{i} = {rowGrid.ColumnDefinitions[i].ActualWidth:0.##}");
        }

        // 表头
        Line($"HeaderGrid Width = {header?.ActualWidth:0.##}  相对窗口 X = {OffsetOf(header, Content as Visual):0.##}  列数={header?.ColumnDefinitions.Count}");
        if (header is not null)
        {
            for (var i = 0; i < header.ColumnDefinitions.Count; i++)
                Line($"  header col{i} = {header.ColumnDefinitions[i].ActualWidth:0.##}");
        }

        // 关键判定：两边**相对各自 Grid** 的列起点是否一样（一样就是对齐了）
        void At(string label, Visual? element, Visual? host)
            => Line($"  {label}: 相对容器 = {OffsetOf(element, host):0.##}  (绝对 X = {OffsetOf(element, Content as Visual):0.##})");

        Line("--- 列起点对照（相对各自 Grid）---");
        At("表头.文件名", headerName, header);
        At("表头.大小  ", headerSize, header);
        At("表头.修改时间", headerTime, header);
        At("行.名字    ", NthTextBlock(0, 0), rowGrid);
        At("行.大小    ", NthTextBlock(0, 1), rowGrid);
        At("行.时间    ", NthTextBlock(0, 2), rowGrid);

        try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); } catch { /* 忽略 */ }
    }

    /// <summary>行里列数最多的那个 Grid（决定数据列位置）。</summary>
    private Grid? RowGridOf(int index)
    {
        if (FileListBox.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) return null;
        var all = new List<Grid>();
        Collect(item, all);
        Grid? best = null;
        foreach (var g in all)
        {
            if (best is null || g.ColumnDefinitions.Count > best.ColumnDefinitions.Count) best = g;
        }
        return best;
    }

    /// <summary>取某一行的第 n 个 TextBlock（0=名字 / 1=大小 / 2=时间）。</summary>
    private TextBlock? NthTextBlock(int rowIndex, int n)
    {
        if (FileListBox.ItemContainerGenerator.ContainerFromIndex(rowIndex) is not ListBoxItem it) return null;
        var all = new List<TextBlock>();
        Collect(it, all);
        return n < all.Count ? all[n] : null;
    }

    private static void Collect(DependencyObject root, List<Grid> into)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Grid g) into.Add(g);
            Collect(child, into);
        }
    }

    private static void Collect(DependencyObject root, List<TextBlock> into)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb) into.Add(tb);
            Collect(child, into);
        }
    }

    /// <summary>元素相对某个祖先的 X 偏移（判定“两个文字是不是从同一条竖线开始”）。</summary>
    private static double OffsetOf(Visual? element, Visual? ancestor)
    {
        if (element is null || ancestor is null) return double.NaN;
        try { return element.TransformToAncestor(ancestor).Transform(new Point(0, 0)).X; }
        catch { return double.NaN; }
    }

    private static T? FindChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindChild<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }
}
