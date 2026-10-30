using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Core.Mvvm;

/// <summary>
/// 可拖拽分隔条：放在 Grid 的**独立一行 / 一列**里，拖动就能改相邻两块的大小
/// （跟画图软件拉图形一样），松手自动记住尺寸，下次打开还是调好的样子。
///
/// <para><b>外观</b>：平时是一条很淡的蓝线，鼠标移上去变亮、光标变成 ↔ / ↕（见 App.xaml 的
/// <c>PaneSplitterVerticalStyle</c> / <c>PaneSplitterHorizontalStyle</c>，别自己再写模板）；
/// 双击这条缝 = 这一处恢复默认。</para>
///
/// <para><b>怎么用</b>（工具页里的标准写法）：</para>
/// <code>
/// &lt;Grid&gt;
///   &lt;Grid.ColumnDefinitions&gt;
///     &lt;ColumnDefinition Width="*" MinWidth="320" /&gt;      &lt;!-- 左：随窗口缩放 --&gt;
///     &lt;ColumnDefinition Width="12" /&gt;                     &lt;!-- 分隔条自己的列 --&gt;
///     &lt;ColumnDefinition Width="560" MinWidth="380" /&gt;     &lt;!-- 右：固定宽度、可拖 --&gt;
///   &lt;/Grid.ColumnDefinitions&gt;
///   ...
///   &lt;core:PaneSplitter Grid.Column="1" Style="{StaticResource PaneSplitterVerticalStyle}"
///                       Key="flash-downloader.主区域左右" /&gt;
/// &lt;/Grid&gt;
/// </code>
///
/// <para><b>尺寸怎么记</b>（<see cref="LayoutMemory"/>）：</para>
/// <list type="bullet">
///   <item>一侧 <c>*</c>、一侧固定像素：只记固定那一侧的像素，<c>*</c> 那侧继续随窗口缩放（推荐）；</item>
///   <item>两侧都固定像素（滚动页面里的上下两块常用）：两侧都记；</item>
///   <item>两侧都 <c>*</c>：按比例记，窗口缩放后比例不变。</item>
/// </list>
/// 相邻两块至少要给一个 <c>MinWidth</c> / <c>MinHeight</c>，否则窗口很小时会被拖没。
/// 主页设置 →「界面布局 → 恢复默认布局」一次清掉所有页面记住的尺寸。
/// </summary>
public class PaneSplitter : Thumb
{
    /// <summary>布局键：<c>"&lt;工具Id&gt;.&lt;区域名&gt;"</c>（工具 Id 里不能带 <c>.</c>）。</summary>
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key),
        typeof(string),
        typeof(PaneSplitter),
        new PropertyMetadata(null));

    /// <summary>拖动方向：<c>Columns</c> = 竖线左右拖（样式里已设好），<c>Rows</c> = 横线上下拖。</summary>
    public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(
        nameof(Direction),
        typeof(GridResizeDirection),
        typeof(PaneSplitter),
        new PropertyMetadata(GridResizeDirection.Auto));

    /// <summary>相邻两块的最小像素（防止拖没；<see cref="FrameworkElement.MinWidth"/> 等仍优先）。</summary>
    public const double MinimumPaneSize = 40;

    private bool _defaultsCaptured;
    private GridLength _defaultPrev;
    private GridLength _defaultNext;

    public PaneSplitter()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DragDelta += OnDragDelta;
        DragCompleted += OnDragCompleted;
        MouseDoubleClick += OnMouseDoubleClick;
    }

    /// <summary>布局键，见 <see cref="KeyProperty"/>。</summary>
    public string? Key
    {
        get => (string?)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    /// <summary>拖动方向，见 <see cref="DirectionProperty"/>。</summary>
    public GridResizeDirection Direction
    {
        get => (GridResizeDirection)GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    // ---------------- 生命周期 ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 允许视图被外壳缓存后重新挂回（例如播放器后台保活）：每次挂回都重新套用记住的尺寸。
        ApplyLayout();

        // 「恢复默认布局」时正开着的页面也要立刻回默认（切到设置页时工具视图通常已卸载，
        // 这里主要是给以后可能常驻的页面兜底）。
        LayoutMemory.ResetCompleted -= OnLayoutReset;
        LayoutMemory.ResetCompleted += OnLayoutReset;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LayoutMemory.ResetCompleted -= OnLayoutReset;
    }

    private void OnLayoutReset(object? sender, EventArgs e) => ApplyLayout();

    // ---------------- 拖拽 ----------------

    private void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!TryResolve(out var ctx)) return;
        var delta = ctx.Columns ? e.HorizontalChange : e.VerticalChange;
        if (!double.IsFinite(delta) || Math.Abs(delta) < 0.01) return;

        EnsureDefaults(ctx);
        var result = ComputeDrag(
            ctx.PrevActual, ctx.NextActual, delta,
            _defaultPrev.IsStar, _defaultNext.IsStar,
            ctx.MinPrev, ctx.MinNext);
        if (result.Mode == PaneDragMode.None) return;

        switch (result.Mode)
        {
            case PaneDragMode.Star:
                // 两侧都是 *：按比例分（窗口缩放比例不变）
                SetStarLengths(ctx, result.Prev, result.Next);
                break;
            case PaneDragMode.PrevPixel:
                SetPixelLength(ctx, prev: true, result.Prev);
                break;
            case PaneDragMode.NextPixel:
                SetPixelLength(ctx, prev: false, result.Next);
                break;
            case PaneDragMode.BothPixel:
                SetPixelLengths(ctx, result.Prev, result.Next);
                break;
        }
    }

    /// <summary>
    /// 拖拽换算（纯函数，自检直接断言）：给定两块当前像素尺寸、拖动增量、哪侧是 <c>*</c>、
    /// 各自最小尺寸，返回新的分配。夹取保证两侧都不小于各自最小尺寸。
    /// </summary>
    /// <param name="prevActual">前一块（左 / 上）当前像素。</param>
    /// <param name="nextActual">后一块（右 / 下）当前像素。</param>
    /// <param name="delta">拖动增量：向右 / 下为正。</param>
    /// <param name="prevIsStar">前一块是否 <c>*</c>。</param>
    /// <param name="nextIsStar">后一块是否 <c>*</c>。</param>
    /// <param name="minPrev">前一块最小像素。</param>
    /// <param name="minNext">后一块最小像素。</param>
    public static PaneDragResult ComputeDrag(
        double prevActual,
        double nextActual,
        double delta,
        bool prevIsStar,
        bool nextIsStar,
        double minPrev,
        double minNext)
    {
        var total = prevActual + nextActual;
        if (!double.IsFinite(total) || total <= 1) return PaneDragResult.None;

        // 前一块最多长到「总量 − 后一块最小值」
        var newPrev = Math.Clamp(prevActual + delta, minPrev, Math.Max(minPrev, total - minNext));

        if (prevIsStar && nextIsStar)
            return new PaneDragResult(newPrev, total - newPrev, PaneDragMode.Star);

        if (!prevIsStar && nextIsStar)
            return new PaneDragResult(newPrev, nextActual, PaneDragMode.PrevPixel);

        if (prevIsStar && !nextIsStar)
        {
            var newNext = Math.Clamp(nextActual - delta, minNext, Math.Max(minNext, total - minPrev));
            return new PaneDragResult(prevActual, newNext, PaneDragMode.NextPixel);
        }

        return new PaneDragResult(newPrev, total - newPrev, PaneDragMode.BothPixel);
    }

    /// <summary>拖拽后的分配方式（见 <see cref="ComputeDrag"/>）。</summary>
    public enum PaneDragMode
    {
        /// <summary>没有有效结果（总量太小，例如页面还没布局完）。</summary>
        None = 0,

        /// <summary>两侧都是 <c>*</c>：<see cref="PaneDragResult.Prev"/> / <see cref="PaneDragResult.Next"/> 是新的星号权重。</summary>
        Star = 1,

        /// <summary>只有前一块固定像素：<see cref="PaneDragResult.Prev"/> 是新的像素值。</summary>
        PrevPixel = 2,

        /// <summary>只有后一块固定像素：<see cref="PaneDragResult.Next"/> 是新的像素值。</summary>
        NextPixel = 3,

        /// <summary>两侧都固定像素：两个都是新的像素值（两块总和不变）。</summary>
        BothPixel = 4,
    }

    /// <summary><see cref="ComputeDrag"/> 的结果（含义取决于 <see cref="Mode"/>）。</summary>
    public readonly record struct PaneDragResult(double Prev, double Next, PaneDragMode Mode)
    {
        /// <summary>没有有效结果。</summary>
        public static PaneDragResult None => new(0, 0, PaneDragMode.None);
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!TryResolve(out var ctx)) return;
        var key = Key?.Trim();
        if (string.IsNullOrEmpty(key)) return;

        EnsureDefaults(ctx);
        var total = ctx.PrevActual + ctx.NextActual;
        if (total <= 1) return;

        if (_defaultPrev.IsStar && _defaultNext.IsStar)
        {
            LayoutMemory.SetRatio(key + ".r", ctx.PrevActual / total);
        }
        else if (_defaultPrev.IsStar)
        {
            LayoutMemory.SetPixel(key + ".px", ctx.NextActual);   // 只有右/下是固定像素
        }
        else if (_defaultNext.IsStar)
        {
            LayoutMemory.SetPixel(key + ".px", ctx.PrevActual);   // 只有左/上是固定像素
        }
        else
        {
            LayoutMemory.SetPixel(key + ".a", ctx.PrevActual);
            LayoutMemory.SetPixel(key + ".b", ctx.NextActual);
        }
    }

    /// <summary>双击分隔条：这一处恢复默认尺寸（不动别的页面 / 别的分隔条）。</summary>
    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!TryResolve(out var ctx)) return;
        EnsureDefaults(ctx);

        SetLengths(ctx, _defaultPrev, _defaultNext);

        var key = Key?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            LayoutMemory.Remove(key + ".px");
            LayoutMemory.Remove(key + ".a");
            LayoutMemory.Remove(key + ".b");
            LayoutMemory.Remove(key + ".r");
        }
        e.Handled = true;
    }

    // ---------------- 套用记住的尺寸 ----------------

    private void ApplyLayout()
    {
        if (!TryResolve(out var ctx)) return;
        EnsureDefaults(ctx);

        var key = Key?.Trim();
        if (string.IsNullOrEmpty(key)) return;

        if (_defaultPrev.IsStar && _defaultNext.IsStar)
        {
            var ratio = LayoutMemory.GetRatio(key + ".r");
            if (ratio is { } r)
                SetStarLengths(ctx, r * 1000, (1 - r) * 1000);
            else
                SetLengths(ctx, _defaultPrev, _defaultNext);
        }
        else if (_defaultPrev.IsStar)
        {
            var pixel = LayoutMemory.GetPixel(key + ".px");
            SetLengths(ctx, _defaultPrev,
                pixel is { } px ? new GridLength(ClampPixel(ctx, px, prev: false)) : _defaultNext);
        }
        else if (_defaultNext.IsStar)
        {
            var pixel = LayoutMemory.GetPixel(key + ".px");
            SetLengths(ctx,
                pixel is { } px ? new GridLength(ClampPixel(ctx, px, prev: true)) : _defaultPrev,
                _defaultNext);
        }
        else
        {
            var a = LayoutMemory.GetPixel(key + ".a");
            var b = LayoutMemory.GetPixel(key + ".b");
            SetLengths(ctx,
                a is { } pa ? new GridLength(ClampPixel(ctx, pa, prev: true)) : _defaultPrev,
                b is { } pb ? new GridLength(ClampPixel(ctx, pb, prev: false)) : _defaultNext);
        }
    }

    /// <summary>第一次挂到界面上时记下 XAML 里写的默认尺寸（双击复位用）。</summary>
    private void EnsureDefaults(PaneContext ctx)
    {
        if (_defaultsCaptured) return;
        _defaultPrev = ctx.PrevLength;
        _defaultNext = ctx.NextLength;
        _defaultsCaptured = true;
    }

    // ---------------- 找相邻两块 ----------------

    /// <summary>找到自己所在的 Grid、方向，以及左右 / 上下相邻的两块。</summary>
    private bool TryResolve(out PaneContext ctx)
    {
        ctx = default!;

        var grid = FindParentGrid(this);
        if (grid is null) return false;

        var columns = ResolveColumns();
        var index = columns ? Grid.GetColumn(this) : Grid.GetRow(this);

        if (columns)
        {
            if (index <= 0 || index >= grid.ColumnDefinitions.Count - 1) return false;
            ctx = new PaneContext
            {
                Grid = grid,
                Columns = true,
                PrevColumn = grid.ColumnDefinitions[index - 1],
                NextColumn = grid.ColumnDefinitions[index + 1],
            };
        }
        else
        {
            if (index <= 0 || index >= grid.RowDefinitions.Count - 1) return false;
            ctx = new PaneContext
            {
                Grid = grid,
                Columns = false,
                PrevRow = grid.RowDefinitions[index - 1],
                NextRow = grid.RowDefinitions[index + 1],
            };
        }
        return ctx.IsValid;
    }

    /// <summary>方向：优先看 <see cref="Direction"/>；<c>Auto</c> 时窄高 = 竖线（改列宽），宽扁 = 横线（改行高）。</summary>
    private bool ResolveColumns()
    {
        if (Direction == GridResizeDirection.Columns) return true;
        if (Direction == GridResizeDirection.Rows) return false;

        // 兜底（样式一般会显式设好）：显式给了 Width 没给 Height = 竖条改列宽，反之改行高。
        var hasWidth = !double.IsNaN(Width) && Width > 0;
        var hasHeight = !double.IsNaN(Height) && Height > 0;
        if (hasWidth && !hasHeight) return true;
        if (hasHeight && !hasWidth) return false;
        if (ActualHeight > 0) return ActualWidth <= ActualHeight;
        return true;
    }

    private static Grid? FindParentGrid(DependencyObject? element)
    {
        for (var node = ParentOf(element); node is not null; node = ParentOf(node))
        {
            if (node is Grid grid) return grid;
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject? element)
    {
        if (element is null) return null;
        if (element is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var visual = VisualTreeHelper.GetParent(element);
            if (visual is not null) return visual;
        }
        return LogicalTreeHelper.GetParent(element);
    }

    // ---------------- 改尺寸 ----------------

    private static void SetLengths(PaneContext ctx, GridLength prev, GridLength next)
    {
        if (ctx.Columns)
        {
            ctx.PrevColumn!.Width = prev;
            ctx.NextColumn!.Width = next;
        }
        else
        {
            ctx.PrevRow!.Height = prev;
            ctx.NextRow!.Height = next;
        }
    }

    private static void SetStarLengths(PaneContext ctx, double prevWeight, double nextWeight)
    {
        SetLengths(ctx,
            new GridLength(Math.Max(1, prevWeight), GridUnitType.Star),
            new GridLength(Math.Max(1, nextWeight), GridUnitType.Star));
    }

    private static void SetPixelLength(PaneContext ctx, bool prev, double value)
    {
        var length = new GridLength(Math.Max(1, value));
        if (ctx.Columns)
        {
            if (prev) ctx.PrevColumn!.Width = length;
            else ctx.NextColumn!.Width = length;
        }
        else
        {
            if (prev) ctx.PrevRow!.Height = length;
            else ctx.NextRow!.Height = length;
        }
    }

    private static void SetPixelLengths(PaneContext ctx, double prev, double next)
    {
        SetLengths(ctx, new GridLength(Math.Max(1, prev)), new GridLength(Math.Max(1, next)));
    }

    /// <summary>套用记住的像素值前夹一下：不小于两块各自的最小尺寸，也不超过自己声明的最大值。</summary>
    private static double ClampPixel(PaneContext ctx, double value, bool prev)
    {
        var min = prev ? ctx.MinPrev : ctx.MinNext;
        var max = prev ? ctx.MaxPrev : ctx.MaxNext;
        return Math.Clamp(value, min, Math.Max(min, max));
    }

    /// <summary>相邻两块 + 方向（内部用；不对外暴露）。</summary>
    private sealed class PaneContext
    {
        public required Grid Grid { get; init; }
        public required bool Columns { get; init; }
        public ColumnDefinition? PrevColumn { get; init; }
        public ColumnDefinition? NextColumn { get; init; }
        public RowDefinition? PrevRow { get; init; }
        public RowDefinition? NextRow { get; init; }

        public bool IsValid => Columns
            ? PrevColumn is not null && NextColumn is not null
            : PrevRow is not null && NextRow is not null;

        public GridLength PrevLength => Columns ? PrevColumn!.Width : PrevRow!.Height;

        public GridLength NextLength => Columns ? NextColumn!.Width : NextRow!.Height;

        public double PrevActual => Columns ? PrevColumn!.ActualWidth : PrevRow!.ActualHeight;

        public double NextActual => Columns ? NextColumn!.ActualWidth : NextRow!.ActualHeight;

        public double MinPrev => MinOf(PrevColumn, PrevRow, Columns);

        public double MinNext => MinOf(NextColumn, NextRow, Columns);

        public double MaxPrev => MaxOf(PrevColumn, PrevRow, Columns);

        public double MaxNext => MaxOf(NextColumn, NextRow, Columns);

        private static double MinOf(ColumnDefinition? column, RowDefinition? row, bool columns)
        {
            var authored = columns ? column?.MinWidth ?? 0 : row?.MinHeight ?? 0;
            return Math.Max(authored, MinimumPaneSize);
        }

        private static double MaxOf(ColumnDefinition? column, RowDefinition? row, bool columns)
        {
            var authored = columns ? column?.MaxWidth ?? double.PositiveInfinity : row?.MaxHeight ?? double.PositiveInfinity;
            return authored > 0 ? authored : double.PositiveInfinity;
        }
    }
}
