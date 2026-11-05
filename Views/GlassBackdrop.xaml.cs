using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using YuLiangToolkit.App.Themes;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 半透明界面风格 + 道系的窗口背景层（v0.43.0，升级自原来的 <c>LiquidBackdrop</c>）。
///
/// <para>放在 Window/Grid 的最底层、铺满整个窗口，<c>IsHitTestVisible="False"</c>（XAML 里已设）；
/// 主窗口用 <c>Visibility="{Binding ShowBackdrop …}"</c> 控制 —— 默认纯色风格下整层 Collapsed。</para>
///
/// <para><b>六套主题共用一个控件，靠绑定切换</b>：
/// <see cref="Theme"/> 决定流动光晕 / 道系装饰要不要跑；<see cref="ImageSource"/> / <see cref="ImageStretch"/> /
/// <see cref="ImageBlurRadius"/> 来自 <c>MainViewModel</c>（背景图、铺法、按主题的模糊度）。
/// 背景图是**静态**的：模糊结果用 <see cref="BitmapCache"/> 缓存，窗口不动时不重算。</para>
///
/// <para><b>性能约定</b>：流动光晕的动画由这里在「液态玻璃 + 可见」时开始，切走 / 隐藏 / 卸载时停掉。
/// 否则别的主题下这层虽然看不见，时间线还在每秒 60 帧空跑（白白占 CPU）。</para>
/// </summary>
public partial class GlassBackdrop : UserControl
{
    /// <summary>当前界面主题（只有 <see cref="UiTheme.LiquidGlass"/> 才跑流动光晕，<see cref="UiTheme.Daoji"/> 显示远山装饰）。</summary>
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(UiTheme), typeof(GlassBackdrop),
        new PropertyMetadata(UiTheme.Default, OnVisualStateChanged));

    /// <summary>窗口背景图（null = 不显示；主窗口绑定 <c>MainViewModel.BackgroundImageSource</c>）。</summary>
    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource), typeof(ImageSource), typeof(GlassBackdrop),
        new PropertyMetadata(null, OnVisualStateChanged));

    /// <summary>背景图铺法（UniformToFill = 铺满裁边 / Uniform = 完整显示留边）。</summary>
    public static readonly DependencyProperty ImageStretchProperty = DependencyProperty.Register(
        nameof(ImageStretch), typeof(Stretch), typeof(GlassBackdrop),
        new PropertyMetadata(Stretch.UniformToFill, OnVisualStateChanged));

    /// <summary>背景图模糊半径（毛玻璃 28 / 亚克力 14 / 液态玻璃 0 = 清晰）。</summary>
    public static readonly DependencyProperty ImageBlurRadiusProperty = DependencyProperty.Register(
        nameof(ImageBlurRadius), typeof(double), typeof(GlassBackdrop),
        new PropertyMetadata(0d, OnVisualStateChanged));

    /// <summary>当前是不是浅色（云间天上用它切白天 / 夜晚装饰；其它主题不关心）。</summary>
    public static readonly DependencyProperty IsLightProperty = DependencyProperty.Register(
        nameof(IsLight), typeof(bool), typeof(GlassBackdrop),
        new PropertyMetadata(false, OnVisualStateChanged));

    /// <summary>云间天上：云朵速度倍率（1.0 = 基准；设置页滑块 0.3 ~ 150）。</summary>
    public static readonly DependencyProperty CloudSpeedProperty = DependencyProperty.Register(
        nameof(CloudSpeed), typeof(double), typeof(GlassBackdrop),
        new PropertyMetadata(1.5));

    /// <summary>云间天上：云朵密度（每层显示几朵；设置页 1 ~ 100）。</summary>
    public static readonly DependencyProperty CloudDensityProperty = DependencyProperty.Register(
        nameof(CloudDensity), typeof(int), typeof(GlassBackdrop),
        new PropertyMetadata(8, OnVisualStateChanged));

    private Storyboard? _ambient;
    private bool _running;

    /// <summary>云间天上的云漂移：用 DispatcherTimer 按窗口实际宽度循环，形状 / 大小固定，不随窗口拉伸。</summary>
    private readonly List<CloudMotion> _clouds = new();
    private readonly Stopwatch _cloudClock = new();
    private DispatcherTimer? _cloudTimer;
    private bool _cloudRunning;
    private int _cloudBuildDensity = -1;
    private double _cloudBuildWidth = -1;
    private double _cloudBuildHeight = -1;

    private sealed class CloudMotion
    {
        public required TranslateTransform Transform { get; init; }
        public required double Speed { get; init; }
        public required double Width { get; init; }
        public double X;
    }

    public GlassBackdrop()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            RefreshVisualState();
            StartAmbientIfNeeded();
            StartCloudDriftIfNeeded();
        };
        Unloaded += (_, _) =>
        {
            StopAmbient();
            StopCloudDrift();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                StartAmbientIfNeeded();
                StartCloudDriftIfNeeded();
            }
            else
            {
                StopAmbient();
                StopCloudDrift();
            }
        };
        SizeChanged += (_, _) =>
        {
            // 窗口变窄时，别让已经跑到右侧很远的云等太久才重新进入；
            // 云的尺寸仍是固定的，这里只挪位置，不缩放。
            if (!_cloudRunning) return;
            var width = Math.Max(ActualWidth, 200);
            foreach (var cloud in _clouds)
            {
                if (cloud.X > width + cloud.Width) cloud.X = width + 80;
            }
        };
    }

    public UiTheme Theme
    {
        get => (UiTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public Stretch ImageStretch
    {
        get => (Stretch)GetValue(ImageStretchProperty);
        set => SetValue(ImageStretchProperty, value);
    }

    public double ImageBlurRadius
    {
        get => (double)GetValue(ImageBlurRadiusProperty);
        set => SetValue(ImageBlurRadiusProperty, value);
    }

    public bool IsLight
    {
        get => (bool)GetValue(IsLightProperty);
        set => SetValue(IsLightProperty, value);
    }

    public double CloudSpeed
    {
        get => (double)GetValue(CloudSpeedProperty);
        set => SetValue(CloudSpeedProperty, value);
    }

    public int CloudDensity
    {
        get => (int)GetValue(CloudDensityProperty);
        set => SetValue(CloudDensityProperty, value);
    }

    private static void OnVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GlassBackdrop)d).RefreshVisualState();

    private Storyboard Ambient => _ambient ??= (Storyboard)FindResource("AmbientStoryboard");

    /// <summary>把 DP 的值落到实际图层上：背景图显隐 / 铺法 / 模糊、液态玻璃光晕层的显隐。</summary>
    private void RefreshVisualState()
    {
        if (BackgroundLayer is null) return;   // 构造期间（InitializeComponent 之前）的 DP 赋值

        var hasImage = ImageSource is not null;
        BackgroundLayer.Source = ImageSource;
        BackgroundLayer.Stretch = ImageStretch;
        BackgroundLayer.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        ScrimLayer.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;

        var blurRadius = hasImage ? Math.Max(0, ImageBlurRadius) : 0;
        if (blurRadius > 0)
        {
            // 只有「真正要模糊」时才建 effect / cache（液态玻璃保持原图清晰，零额外开销）
            if (BackgroundLayer.Effect is not BlurEffect effect || Math.Abs(effect.Radius - blurRadius) > 0.01)
            {
                BackgroundLayer.Effect = new BlurEffect
                {
                    Radius = blurRadius,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Quality,
                };
            }
            BackgroundLayer.CacheMode ??= new BitmapCache();
        }
        else
        {
            BackgroundLayer.Effect = null;
            BackgroundLayer.CacheMode = null;
        }

        var liquid = Theme == UiTheme.LiquidGlass;
        AmbientLayer.Visibility = liquid ? Visibility.Visible : Visibility.Collapsed;
        DaoLayer.Visibility = Theme == UiTheme.Daoji ? Visibility.Visible : Visibility.Collapsed;

        // 云间天上：有背景图时让图片当天空，装饰云 / 日月让位；没图时按深浅切白天 / 夜晚
        var cloudSky = Theme == UiTheme.CloudSky && !hasImage;
        CloudSkyLayer.Visibility = cloudSky ? Visibility.Visible : Visibility.Collapsed;
        DaySky.Visibility = cloudSky && IsLight ? Visibility.Visible : Visibility.Collapsed;
        NightSky.Visibility = cloudSky && !IsLight ? Visibility.Visible : Visibility.Collapsed;
        if (cloudSky) EnsureClouds();

        if (liquid && IsVisible) StartAmbientIfNeeded();
        else StopAmbient();

        if (cloudSky && IsVisible) StartCloudDriftIfNeeded();
        else StopCloudDrift();
    }

    private void StartCloudDriftIfNeeded()
    {
        if (_cloudRunning || !IsVisible || Theme != UiTheme.CloudSky) return;
        if (PresentationSource.FromVisual(this) is null) return;

        _cloudTimer ??= CreateCloudTimer();
        _cloudClock.Restart();
        _cloudTimer.Start();
        _cloudRunning = true;
    }

    private void StopCloudDrift()
    {
        if (!_cloudRunning) return;
        _cloudRunning = false;
        _cloudTimer?.Stop();
        _cloudClock.Stop();
    }

    /// <summary>按密度动态生成云朵：密度 1~100，每层都按窗口高度均匀分布，不再固定只有上下几朵。</summary>
    private void EnsureClouds()
    {
        if (DayClouds is null || NightClouds is null) return;

        var density = Math.Clamp(CloudDensity, 1, 100);
        var width = Math.Max(ActualWidth, 800);
        var height = Math.Max(ActualHeight, 500);
        if (density == _cloudBuildDensity
            && Math.Abs(width - _cloudBuildWidth) < 1
            && Math.Abs(height - _cloudBuildHeight) < 1
            && DayClouds.Children.Count == density
            && NightClouds.Children.Count == density)
        {
            return;
        }

        DayClouds.Children.Clear();
        NightClouds.Children.Clear();
        _clouds.Clear();
        BuildCloudLayer(DayClouds, density, width, height, day: true);
        BuildCloudLayer(NightClouds, density, width, height, day: false);
        _cloudBuildDensity = density;
        _cloudBuildWidth = width;
        _cloudBuildHeight = height;
    }

    private void BuildCloudLayer(Canvas layer, int count, double width, double height, bool day)
    {
        var geometry = (Geometry)FindResource("CloudShape");
        string[] colors = day
            ? new[] { "#D7E8F8", "#E2EFFB", "#EDF5FD", "#E3EFFA", "#FFFFFF" }
            : new[] { "#132C4A", "#112742", "#0E2138", "#0A1A2E" };
        double[] opacities = day
            ? new[] { 0.85, 0.75, 0.7, 0.65, 0.9 }
            : new[] { 0.9, 0.8, 0.85, 0.7 };

        for (var i = 0; i < count; i++)
        {
            var scale = 1.0 + (i % 5) * 0.28;
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i % colors.Length]));
            brush.Freeze();

            var path = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Fill = brush,
                Opacity = opacities[i % opacities.Length],
                IsHitTestVisible = false,
                RenderTransform = new ScaleTransform(scale, scale),
            };

            // 上下均匀：按序号把整条窗口高度分段，再叠一点固定偏移避免像栅栏
            var usable = Math.Max(120, height - 90);
            var y = 15 + usable * (i + 0.5) / count + ((i * 37) % 23) - 11;
            var x = (i * 173) % Math.Max(500, (int)width) - 120;
            var transform = new TranslateTransform { X = x };
            var group = new Canvas
            {
                IsHitTestVisible = false,
                RenderTransform = transform,
            };
            group.Children.Add(path);
            Canvas.SetTop(group, y);
            layer.Children.Add(group);

            _clouds.Add(new CloudMotion
            {
                Transform = transform,
                Speed = 4.0 + (i % 7) * 0.9,
                Width = 180 * scale,
                X = x,
            });
        }
    }

    private DispatcherTimer CreateCloudTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        timer.Tick += (_, _) =>
        {
            var seconds = Math.Clamp(_cloudClock.Elapsed.TotalSeconds, 0, 0.1);
            _cloudClock.Restart();
            var width = Math.Max(ActualWidth, 200);
            var speedScale = Math.Clamp(CloudSpeed, 0.0, 200.0);
            foreach (var cloud in _clouds)
            {
                cloud.X -= cloud.Speed * speedScale * seconds;
                if (cloud.X < -cloud.Width - 80) cloud.X = width + 80;
                cloud.Transform.X = cloud.X;
            }
        };
        return timer;
    }

    private void StartAmbientIfNeeded()
    {
        if (_running || !IsVisible || Theme != UiTheme.LiquidGlass) return;
        if (PresentationSource.FromVisual(this) is null) return;

        _running = true;
        Ambient.Begin(this, isControllable: true);
    }

    private void StopAmbient()
    {
        if (!_running) return;
        _running = false;
        Ambient.Stop(this);
    }
}
