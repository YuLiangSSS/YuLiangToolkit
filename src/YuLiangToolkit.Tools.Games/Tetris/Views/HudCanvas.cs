using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Tools.Games.Tetris;

namespace YuLiangToolkit.Tools.Games.Tetris.Views;

/// <summary>
/// HUD 自由布局容器：把若干组件按<b>相对位置</b>摆在棋盘上方，<b>编辑模式下可以拖动</b>。
///
/// <para><b>用法</b>：XAML 里放 <c>&lt;views:HudCanvas&gt;</c>，用 <c>Add(part, ui)</c> 加组件；
/// 或者直接在 XAML 里写子元素并设置 <c>HudPart</c> 附加属性。</para>
///
/// <para><b>拖动</b>：编辑模式（<see cref="EditMode"/>=true）下按住组件拖动，
/// 松手抛 <see cref="Moved"/>，由 ViewModel 存进配置。</para>
/// </summary>
public sealed class HudCanvas : Canvas
{
    /// <summary>组件标识（附加属性）。</summary>
    public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
        "Part", typeof(HudPart), typeof(HudCanvas),
        new FrameworkPropertyMetadata(HudPart.Hold));

    /// <summary>取某个元素的组件标识。</summary>
    public static HudPart GetPart(DependencyObject element) => (HudPart)element.GetValue(PartProperty);

    /// <summary>设置某个元素的组件标识。</summary>
    public static void SetPart(DependencyObject element, HudPart value) => element.SetValue(PartProperty, value);

    /// <summary>当前布局。</summary>
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(HudLayout), typeof(HudCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange, OnLayoutChanged));

    /// <summary>是不是编辑模式（可拖动）。</summary>
    public static readonly DependencyProperty EditModeProperty = DependencyProperty.Register(
        nameof(EditMode), typeof(bool), typeof(HudCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>移动了某个组件（松手后触发）。</summary>
    public event Action<HudPart, double, double>? Moved;

    private FrameworkElement? _dragging;
    private Point _dragOffset;

    /// <summary>构造。</summary>
    public HudCanvas()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;      // 让空白处也能收到鼠标事件
    }

    /// <summary>当前布局。</summary>
    public HudLayout? Layout
    {
        get => (HudLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>编辑模式。</summary>
    public bool EditMode
    {
        get => (bool)GetValue(EditModeProperty);
        set => SetValue(EditModeProperty, value);
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HudCanvas canvas) canvas.InvalidateArrange();
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = Layout ?? HudLayout.CreateDefault();

        foreach (UIElement child in Children)
        {
            if (child is not FrameworkElement element) continue;

            var part = GetPart(element);
            var placement = layout.Get(part);
            var width = element.DesiredSize.Width;
            var height = element.DesiredSize.Height;

            // 相对位置 → 实际像素（夹一下，别跑出容器）
            var x = Math.Clamp(placement.X * finalSize.Width, 0, Math.Max(0, finalSize.Width - width));
            var y = Math.Clamp(placement.Y * finalSize.Height, 0, Math.Max(0, finalSize.Height - height));

            element.Arrange(new Rect(x, y, width, height));
        }

        return finalSize;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!EditMode) return;

        var hit = FindChildAt(e.GetPosition(this));
        if (hit is null) return;

        _dragging = hit;
        _dragOffset = e.GetPosition(hit);
        CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is null || ActualWidth <= 0 || ActualHeight <= 0) return;

        var position = e.GetPosition(this);
        var x = (position.X - _dragOffset.X) / ActualWidth;
        var y = (position.Y - _dragOffset.Y) / ActualHeight;

        // 吸附：靠近边缘 / 中线时自动贴齐（拖动更好对齐）
        (x, y) = Snap(x, y, _dragging.ActualWidth, _dragging.ActualHeight);

        var layout = Layout ?? HudLayout.CreateDefault();
        layout.Set(GetPart(_dragging), x, y);
        Layout = layout;

        InvalidateArrange();
    }

    /// <summary>吸附阈值（相对容器尺寸）。</summary>
    private const double SnapThreshold = 0.018;

    /// <summary>
    /// 把位置吸到「贴边 / 居中」的几条线上：
    /// 左贴边、右贴边、水平居中、上贴边、下贴边、垂直居中。
    /// </summary>
    private (double X, double Y) Snap(double x, double y, double elementWidth, double elementHeight)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return (x, y);

        var maxX = Math.Max(0, 1 - elementWidth / ActualWidth);
        var maxY = Math.Max(0, 1 - elementHeight / ActualHeight);

        var centerX = Math.Max(0, (1 - elementWidth / ActualWidth) / 2);
        var centerY = Math.Max(0, (1 - elementHeight / ActualHeight) / 2);

        var xLines = new[] { 0.0, maxX, centerX };
        var yLines = new[] { 0.0, maxY, centerY };

        foreach (var line in xLines)
        {
            if (Math.Abs(x - line) <= SnapThreshold) { x = line; break; }
        }
        foreach (var line in yLines)
        {
            if (Math.Abs(y - line) <= SnapThreshold) { y = line; break; }
        }

        return (x, y);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging is null) return;

        var part = GetPart(_dragging);
        var layout = Layout ?? HudLayout.CreateDefault();
        var placement = layout.Get(part);

        _dragging = null;
        ReleaseMouseCapture();
        Moved?.Invoke(part, placement.X, placement.Y);
    }

    /// <summary>找到鼠标下面的那个 HUD 组件。</summary>
    private FrameworkElement? FindChildAt(Point point)
    {
        foreach (UIElement child in Children)
        {
            if (child is not FrameworkElement element) continue;

            var left = Canvas.GetLeft(element);
            var top = Canvas.GetTop(element);
            var rect = new Rect(left, top, element.ActualWidth, element.ActualHeight);
            if (rect.Contains(point)) return element;

            // Canvas 没用 Left/Top（ArrangeOverride 自己排的），用渲染位置判断
            var origin = element.TranslatePoint(new Point(0, 0), this);
            var actual = new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
            if (actual.Contains(point)) return element;
        }
        return null;
    }
}
