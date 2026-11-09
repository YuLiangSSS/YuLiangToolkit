using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YuLiangToolkit.Tools.Games.Tetris;

namespace YuLiangToolkit.Tools.Games.Tetris.Views;

/// <summary>
/// 「接下来几块 / 暂存」的方格预览：每格一个 <see cref="TetrominoSlotView"/>，
/// 条目很少（最多 3 块），所以直接画成控件比自绘整块更清楚（哪个空了、哪个是哪一块一目了然）。
/// </summary>
public sealed class TetrominoPoolView : FrameworkElement
{
    /// <summary>要显示的方块序列（null = 空格占位）。</summary>
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<TetrominoKind>), typeof(TetrominoPoolView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>空位占位文字。</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(TetrominoPoolView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>标题文字（「接下来」「暂存」）。</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TetrominoPoolView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>标题 / 占位文字颜色（主题 token）。</summary>
    public static readonly DependencyProperty HintBrushProperty = DependencyProperty.Register(
        nameof(HintBrush), typeof(Brush), typeof(TetrominoPoolView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>空位底板（主题 token）。</summary>
    public static readonly DependencyProperty SlotBrushProperty = DependencyProperty.Register(
        nameof(SlotBrush), typeof(Brush), typeof(TetrominoPoolView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>排列方向（默认纵向；预览区用横向更省高度）。</summary>
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(TetrominoPoolView),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>要显示的方块序列。</summary>
    public IReadOnlyList<TetrominoKind>? Items
    {
        get => (IReadOnlyList<TetrominoKind>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>排列方向。</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>空位占位文字。</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>标题文字。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>标题 / 占位文字颜色。</summary>
    public Brush? HintBrush
    {
        get => (Brush?)GetValue(HintBrushProperty);
        set => SetValue(HintBrushProperty, value);
    }

    /// <summary>空位底板颜色。</summary>
    public Brush? SlotBrush
    {
        get => (Brush?)GetValue(SlotBrushProperty);
        set => SetValue(SlotBrushProperty, value);
    }

    /// <summary>构造。</summary>
    public TetrominoPoolView()
    {
        Focusable = false;
        UseLayoutRounding = true;
        ClipToBounds = true;
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Microsoft YaHei UI");
        var top = 0.0;

        if (!string.IsNullOrEmpty(Title) && HintBrush is { } titleBrush)
        {
            var title = new FormattedText(Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 11, titleBrush, dpi);
            dc.DrawText(title, new Point(0, 0));
            top = title.Height + 6;
        }

        var count = Math.Max(1, Items?.Count ?? 0);
        var horizontal = Orientation == Orientation.Horizontal;
        var slotHeight = horizontal ? ActualHeight : Math.Max(20, (ActualHeight - top) / count - 6);
        var slotWidth = horizontal ? Math.Max(20, ActualWidth / count - 6) : ActualWidth;
        var y = top;
        var x = 0.0;

        for (var i = 0; i < count; i++)
        {
            var kind = Items is { Count: > 0 } items && i < items.Count ? items[i] : (TetrominoKind?)null;
            var rect = new Rect(x, y, slotWidth, slotHeight);

            if (SlotBrush is { } slot) dc.DrawRoundedRectangle(slot, null, rect, 6, 6);

            if (kind is { } value)
            {
                TetrisBoardView.DrawPreview(dc, rect, value, null);
            }
            else if (!string.IsNullOrEmpty(Placeholder) && HintBrush is { } hint)
            {
                TetrisBoardView.DrawHint(dc, rect, Placeholder, hint, 10.5);
            }

            if (horizontal) x += slotWidth + 6;
            else y += slotHeight + 6;
        }
    }
}
