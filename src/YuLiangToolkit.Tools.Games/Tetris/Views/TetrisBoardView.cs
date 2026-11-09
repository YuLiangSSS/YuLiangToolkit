using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.Games.Tetris;
using YuLiangToolkit.Tools.Games.Tetris.ViewModels;

namespace YuLiangToolkit.Tools.Games.Tetris.Views;

/// <summary>
/// 俄罗斯方块棋盘的**自绘控件**：底板 → 网格 → 已落下的块 → 幽灵方块 → 当前方块，全部在
/// <see cref="OnRender"/> 里用 <see cref="DrawingContext"/> 画（不 new 子控件）。
///
/// <para><b>为什么自绘</b>：200 个格子如果一格一个 Border，每帧重排一次 200 个控件；
/// 而且半透明的幽灵方块叠在已有方块上必须自己控制层次。自绘只有一次 OnRender，格子尺寸按可用空间算。</para>
///
/// <para><b>颜色</b>：底板 / 网格线 / 边框是依赖属性，由 XAML 用 <c>{DynamicResource …}</c> 传主题 token
/// （换主题立即生效）；七种方块的颜色是**数据色**，来自 <see cref="TetrominoPalette"/>（冻结缓存，不每帧 new）。</para>
/// </summary>
public sealed class TetrisBoardView : FrameworkElement
{
    /// <summary>棋盘设计宽高比用的格子尺寸（只是默认值，实际按可用空间算）。</summary>
    public const double DefaultCellSize = 26;

    /// <summary>方块的圆角 / 内缩（相对格子边长）。</summary>
    private const double CellInset = 0.06;

    private static readonly Typeface LabelTypeface = new("Microsoft YaHei UI");

    // ---------------------------------------------------------------- 依赖属性

    /// <summary>渲染快照（变化即重绘）。</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(TetrisBoardViewState), typeof(TetrisBoardView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>底板颜色（主题 token）。</summary>
    public static readonly DependencyProperty BoardBrushProperty = RegisterBrush(nameof(BoardBrush));

    /// <summary>网格线颜色（主题 token）。</summary>
    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(nameof(GridBrush));

    /// <summary>棋盘外框颜色（主题 token）。</summary>
    public static readonly DependencyProperty BorderBrushProperty = RegisterBrush(nameof(BorderBrush));

    private static DependencyProperty RegisterBrush(string name)
        => DependencyProperty.Register(name, typeof(Brush), typeof(TetrisBoardView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>渲染快照。</summary>
    public TetrisBoardViewState? State
    {
        get => (TetrisBoardViewState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>底板颜色。</summary>
    public Brush? BoardBrush
    {
        get => (Brush?)GetValue(BoardBrushProperty);
        set => SetValue(BoardBrushProperty, value);
    }

    /// <summary>网格线颜色。</summary>
    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <summary>外框颜色。</summary>
    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    /// <summary>构造：格子上有像素对齐，别让 WPF 自动缩放。</summary>
    public TetrisBoardView()
    {
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
    }

    /// <summary>按棋盘比例给一个合理的期望尺寸（放进 Grid 里不会被压成 0）。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? TetrisEngine.Width * DefaultCellSize
            : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height)
            ? TetrisEngine.Height * DefaultCellSize
            : availableSize.Height;
        return new Size(width, height);
    }

    // ---------------------------------------------------------------- 绘制

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var cell = CellSize(out var origin);
        if (cell <= 0) return;

        var boardWidth = cell * TetrisEngine.Width;
        var boardHeight = cell * TetrisEngine.Height;
        var boardRect = new Rect(origin.X, origin.Y, boardWidth, boardHeight);

        // 1) 底板 + 外框
        if (BoardBrush is { } board) dc.DrawRectangle(board, null, boardRect);
        if (BorderBrush is { } border)
        {
            var pen = new Pen(border, 1);
            pen.Freeze();
            dc.DrawRectangle(null, pen, boardRect);
        }

        var state = State;
        if (state is null) return;

        // 2) 网格（每格一条竖线 / 横线，比逐格描边省一半线条）
        if (state.ShowGrid && GridBrush is { } grid)
        {
            var faded = new SolidColorBrush((grid as SolidColorBrush)?.Color ?? Colors.Gray) { Opacity = 0.35 };
            faded.Freeze();
            var pen = new Pen(faded, 1);
            pen.Freeze();
            for (var x = 1; x < TetrisEngine.Width; x++)
            {
                var px = origin.X + x * cell;
                dc.DrawLine(pen, new Point(px, origin.Y), new Point(px, origin.Y + boardHeight));
            }
            for (var y = 1; y < TetrisEngine.Height; y++)
            {
                var py = origin.Y + y * cell;
                dc.DrawLine(pen, new Point(origin.X, py), new Point(origin.X + boardWidth, py));
            }
        }

        // 3) 已落下的方块
        for (var y = 0; y < TetrisEngine.Height; y++)
        {
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                if (state.CellAt(x, y) is not { } kind) continue;
                DrawCell(dc, origin, cell, x, y, kind, opacity: 1.0);
            }
        }

        // 4) 幽灵方块（落在最底的位置，半透明 + 只描边）—— 先画，当前方块盖在上面
        if (state.Ghost is { } ghost && state.Piece is { } piece)
        {
            foreach (var (x, y) in ghost.Cells())
            {
                if (y < 0 || y >= TetrisEngine.Height || x < 0 || x >= TetrisEngine.Width) continue;
                if (piece.Cells().Any(p => p.X == x && p.Y == y)) continue;   // 和本体重合的格子不重复画
                DrawCell(dc, origin, cell, x, y, ghost.Kind, opacity: 0.3);
            }
        }

        // 5) 当前方块
        if (state.Piece is { } current)
        {
            foreach (var (x, y) in current.Cells())
            {
                if (y < 0 || y >= TetrisEngine.Height || x < 0 || x >= TetrisEngine.Width) continue;
                DrawCell(dc, origin, cell, x, y, current.Kind, opacity: 1.0);
            }
        }
    }

    /// <summary>画一个格子：圆角填充 + 顶面高光 + 描边（看起来有立体感）。</summary>
    private static void DrawCell(DrawingContext dc, Point origin, double cell, int x, int y, TetrominoKind kind, double opacity)
    {
        var inset = cell * CellInset;
        var rect = new Rect(
            origin.X + x * cell + inset,
            origin.Y + y * cell + inset,
            Math.Max(1, cell - inset * 2),
            Math.Max(1, cell - inset * 2));
        var radius = Math.Max(1, cell * 0.16);

        var brush = TetrominoPalette.BrushOf(kind);
        if (opacity < 1.0)
        {
            // 幽灵方块：淡填充 + 稍亮的描边。只描边在深色底上几乎看不见，
            // 只填充又会和「已经落下的同色方块」混在一起。
            var baseColor = ((SolidColorBrush)brush).Color;
            var fadedFill = new SolidColorBrush(baseColor) { Opacity = opacity * 0.45 };
            fadedFill.Freeze();
            var fadedPen = new Pen(new SolidColorBrush(baseColor) { Opacity = opacity * 1.6 }, Math.Max(1.2, cell * 0.08));
            fadedPen.Freeze();
            dc.DrawRoundedRectangle(fadedFill, fadedPen, rect, radius, radius);
            return;
        }

        dc.DrawRoundedRectangle(brush, null, rect, radius, radius);

        // 顶面高光：一条比格子窄一圈的浅色带，让方块看起来是「块」而不是「色块」
        var highlight = new Rect(rect.X + rect.Width * 0.12, rect.Y + rect.Height * 0.12,
            Math.Max(1, rect.Width * 0.76), Math.Max(1, rect.Height * 0.22));
        var highlightBrush = new SolidColorBrush(Colors.White) { Opacity = 0.28 };
        highlightBrush.Freeze();
        dc.DrawRoundedRectangle(highlightBrush, null, highlight, radius * 0.6, radius * 0.6);

        var pen = new Pen(new SolidColorBrush(Colors.Black) { Opacity = 0.28 }, 1);
        pen.Freeze();
        dc.DrawRoundedRectangle(null, pen, rect, radius, radius);
    }

    /// <summary>
    /// 按可用空间算格子边长并居中：棋盘比视口高时按高度缩，宽时按宽度缩，四周留一点边距。
    /// </summary>
    private double CellSize(out Point origin)
    {
        const double margin = 8;
        var available = new Size(
            Math.Max(0, ActualWidth - margin * 2),
            Math.Max(0, ActualHeight - margin * 2));
        if (available.Width <= 0 || available.Height <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        var cell = Math.Min(available.Width / TetrisEngine.Width, available.Height / TetrisEngine.Height);
        if (cell <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        origin = new Point(
            (ActualWidth - cell * TetrisEngine.Width) / 2,
            (ActualHeight - cell * TetrisEngine.Height) / 2);
        return cell;
    }

    /// <summary>预览区 / 暂存区那种「小块」的自绘：把一个方块居中画进控件。</summary>
    internal static void DrawPreview(DrawingContext dc, Rect bounds, TetrominoKind? kind, Brush? emptyBrush)
    {
        if (kind is not { } value)
        {
            if (emptyBrush is not null) dc.DrawRectangle(emptyBrush, null, bounds);
            return;
        }

        var cells = TetrisPieces.CellsOf(value, 0);
        var box = TetrisPieces.BoxSize(value);

        var minX = cells.Min(c => c.X);
        var maxX = cells.Max(c => c.X);
        var minY = cells.Min(c => c.Y);
        var maxY = cells.Max(c => c.Y);
        var usedWidth = maxX - minX + 1;
        var usedHeight = maxY - minY + 1;

        var cell = Math.Min(
            bounds.Width / Math.Max(1, usedWidth) * 0.78,
            bounds.Height / Math.Max(1, usedHeight) * 0.78);
        if (cell <= 0) return;

        var startX = bounds.X + (bounds.Width - cell * usedWidth) / 2;
        var startY = bounds.Y + (bounds.Height - cell * usedHeight) / 2;

        _ = box;   // 包围盒边长只用于说明「形状按包围盒居中」，这里直接用实际占用行列

        foreach (var (cx, cy) in cells)
        {
            var rect = new Rect(
                startX + (cx - minX) * cell + cell * CellInset,
                startY + (cy - minY) * cell + cell * CellInset,
                Math.Max(1, cell * (1 - CellInset * 2)),
                Math.Max(1, cell * (1 - CellInset * 2)));
            var radius = Math.Max(1, cell * 0.16);
            dc.DrawRoundedRectangle(TetrominoPalette.BrushOf(value), null, rect, radius, radius);
        }
    }

    /// <summary>文字（「暂存」「接下来」这类占位提示）用统一的字体与颜色。</summary>
    internal static void DrawHint(DrawingContext dc, Rect bounds, string text, Brush? brush, double fontSize)
    {
        if (brush is null || string.IsNullOrEmpty(text)) return;

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);

        dc.DrawText(formatted, new Point(
            bounds.X + (bounds.Width - formatted.Width) / 2,
            bounds.Y + (bounds.Height - formatted.Height) / 2));
    }
}
