using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using YuLiangToolkit.App.Models;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 应用内自绘光标（v0.91.0）：窗口自己画一个跟随鼠标的箭头 / 图片，系统光标在启用时隐藏。
///
/// <para><b>层级</b>：本控件和 <c>RootGrid</c> 平级、在 <c>ParticleOverlay</c> 下面（见 MainWindow.xaml），
/// 所以背景 / 界面会被光标盖住，但粒子拖尾、点击爆炸、反色圆环等效果仍然盖在光标上面——
/// 用户要求“不要挡住粒子 / 点击特效，背景光晕可以挡住”就是靠这个层级实现的。</para>
///
/// <para><b>不吃的鼠标事件</b>：整层 <c>IsHitTestVisible=False</c>，位置靠 Window 的 PreviewMouseMove 更新，
/// 所以按钮 / 拖拽 / 输入都不受影响。</para>
///
/// <para><b>自定义图片</b>：<see cref="ImageSource"/> 有值时使用图片；否则按 <see cref="Kind"/> 画内置箭头。
/// 系统光标只在启用时改成 <see cref="Cursors.None"/>，关闭 / 卸载时还原。</para>
///
/// <para><b>深浅两套配色</b>（v0.97.0）：内置箭头按 <see cref="UseDarkTone"/> 选配色 —— 浅色系 / 深色系
/// （见 <see cref="InAppCursorStyle.Palette"/>）。默认跟随界面外观模式，设置页「光标深浅」也可以强制指定，
/// 换外观模式 / 改设置立即生效；自定义图片不受影响。</para>
/// </summary>
public partial class InAppCursorOverlay : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(InAppCursorOverlay),
        new PropertyMetadata(false, OnVisualStateChanged));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(InAppCursorKind), typeof(InAppCursorOverlay),
        new PropertyMetadata(InAppCursorKind.Default, OnVisualStateChanged));

    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource), typeof(ImageSource), typeof(InAppCursorOverlay),
        new PropertyMetadata(null, OnVisualStateChanged));

    public static readonly DependencyProperty UseDarkToneProperty = DependencyProperty.Register(
        nameof(UseDarkTone), typeof(bool), typeof(InAppCursorOverlay),
        new PropertyMetadata(false, OnVisualStateChanged));

    private Window? _window;
    private Cursor? _previousCursor;
    private Cursor? _previousOverrideCursor;
    private bool _hasPosition;

    public InAppCursorOverlay()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public InAppCursorKind Kind
    {
        get => (InAppCursorKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    /// <summary>
    /// 内置箭头是否用深色系（设置页「光标深浅」）：auto 时由 ViewModel 按界面深浅算好传进来，
    /// light / dark 时就是用户强制指定的值（见 <see cref="InAppCursorStyle.UseDarkTone"/>）。
    /// </summary>
    public bool UseDarkTone
    {
        get => (bool)GetValue(UseDarkToneProperty);
        set => SetValue(UseDarkToneProperty, value);
    }

    private static void OnVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((InAppCursorOverlay)d).Refresh();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null) return;

        _window.PreviewMouseMove += OnWindowPreviewMouseMove;
        _window.MouseEnter += OnWindowMouseEnter;
        _window.MouseLeave += OnWindowMouseLeave;
        _window.Deactivated += OnWindowDeactivated;
        _window.Activated += OnWindowActivated;

        _previousCursor = _window.Cursor;
        _previousOverrideCursor = Mouse.OverrideCursor;
        _window.Cursor = IsActive ? Cursors.None : _previousCursor;
        Mouse.OverrideCursor = IsActive ? Cursors.None : _previousOverrideCursor;
        Refresh();

        // 调试 / 截图：YLT_CURSOR_POS=x,y 把自绘光标固定画在这个位置（正常运行时没有这个环境变量）
        if (Environment.GetEnvironmentVariable("YLT_CURSOR_POS") is { Length: > 0 } preview)
        {
            var parts = preview.Split(',');
            if (parts.Length == 2
                && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y))
            {
                _hasPosition = true;
                MoveTo(new Point(x, y));
                CursorVisual.Visibility = Visibility.Visible;
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.PreviewMouseMove -= OnWindowPreviewMouseMove;
            _window.MouseEnter -= OnWindowMouseEnter;
            _window.MouseLeave -= OnWindowMouseLeave;
            _window.Deactivated -= OnWindowDeactivated;
            _window.Activated -= OnWindowActivated;
            _window.Cursor = _previousCursor ?? Cursors.Arrow;
        }

        Mouse.OverrideCursor = _previousOverrideCursor;
        _window = null;
        _hasPosition = false;
        CursorVisual.Visibility = Visibility.Collapsed;
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsActive)
        {
            CursorVisual.Visibility = Visibility.Collapsed;
            return;
        }

        _hasPosition = true;
        MoveTo(e.GetPosition(this));
        CursorVisual.Visibility = Visibility.Visible;
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        if (!IsActive) return;
        if (_hasPosition) CursorVisual.Visibility = Visibility.Visible;
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        CursorVisual.Visibility = Visibility.Collapsed;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
        => CursorVisual.Visibility = Visibility.Collapsed;

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // 重新激活时鼠标可能正好在窗口里：Refresh 会按当前位置重新显示，不用等下一次移动。
        if (IsActive) Refresh();
    }

    private void MoveTo(Point position)
    {
        // 箭头左上角 / 图片左上角贴在鼠标点上；留 2px 让箭尖更接近实际点击位置
        Canvas.SetLeft(CursorVisual, position.X - 2);
        Canvas.SetTop(CursorVisual, position.Y - 2);
    }

    private void Refresh()
    {
        if (RootCanvas is null || CursorVisual is null) return;

        if (_window is not null)
            _window.Cursor = IsActive ? Cursors.None : (_previousCursor ?? Cursors.Arrow);
        Mouse.OverrideCursor = IsActive ? Cursors.None : _previousOverrideCursor;

        if (!IsActive)
        {
            CursorVisual.Visibility = Visibility.Collapsed;
            return;
        }

        var useImage = Kind == InAppCursorKind.Custom && ImageSource is not null;
        CustomImage.Source = useImage ? ImageSource : null;
        CustomImage.Visibility = useImage ? Visibility.Visible : Visibility.Collapsed;
        ArrowPath.Visibility = useImage ? Visibility.Collapsed : Visibility.Visible;
        AccentDot.Visibility = useImage ? Visibility.Collapsed : Visibility.Visible;

        if (useImage)
        {
            CursorVisual.Width = 48;
            CursorVisual.Height = 48;
            ArrowPath.Effect = null;
            return;
        }

        CursorVisual.Width = 40;
        CursorVisual.Height = 44;

        // 配色按界面深浅 / 设置页的「光标深浅」取（v0.97.0 起浅色界面自动用深色系，
        // v0.98.0 起可以独立强制）；每套主题自己的强调色保留，六套样式在两种深浅下各有自己的样子。
        var palette = InAppCursorStyle.Palette(Kind, UseDarkTone);
        ArrowPath.Fill = new SolidColorBrush(Hex(palette.Fill));
        ArrowPath.Stroke = new SolidColorBrush(Hex(palette.Stroke));
        AccentDot.Fill = new SolidColorBrush(Hex(palette.Dot));
        ArrowPath.Effect = palette.Glow
            ? new DropShadowEffect { Color = Color.FromRgb(0x4C, 0x8D, 0xFF), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.55 }
            : null;

        if (_window?.IsMouseOver == true && _hasPosition)
            CursorVisual.Visibility = Visibility.Visible;

        // 刚开启 / 刚改样式时鼠标可能没动过：直接取一次当前位置，避免光标要等下一次移动才出现。
        if (_window?.IsMouseOver == true)
        {
            var position = Mouse.GetPosition(this);
            if (position.X >= 0 && position.Y >= 0)
            {
                _hasPosition = true;
                MoveTo(position);
                CursorVisual.Visibility = Visibility.Visible;
            }
        }
    }

    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);
}
