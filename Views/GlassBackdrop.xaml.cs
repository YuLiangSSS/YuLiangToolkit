using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

    private Storyboard? _ambient;
    private bool _running;

    /// <summary>云间天上的云漂移动画（只在 CloudSky 且可见时跑）。</summary>
    private Storyboard? _cloudDrift;
    private bool _cloudRunning;

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

    private static void OnVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GlassBackdrop)d).RefreshVisualState();

    private Storyboard Ambient => _ambient ??= (Storyboard)FindResource("AmbientStoryboard");

    private Storyboard CloudDrift => _cloudDrift ??= (Storyboard)FindResource("CloudDriftStoryboard");

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

        if (liquid && IsVisible) StartAmbientIfNeeded();
        else StopAmbient();

        if (cloudSky && IsVisible) StartCloudDriftIfNeeded();
        else StopCloudDrift();
    }

    private void StartCloudDriftIfNeeded()
    {
        if (_cloudRunning || !IsVisible || Theme != UiTheme.CloudSky) return;
        if (PresentationSource.FromVisual(this) is null) return;

        _cloudRunning = true;
        CloudDrift.Begin(this, isControllable: true);
    }

    private void StopCloudDrift()
    {
        if (!_cloudRunning) return;
        _cloudRunning = false;
        CloudDrift.Stop(this);
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
