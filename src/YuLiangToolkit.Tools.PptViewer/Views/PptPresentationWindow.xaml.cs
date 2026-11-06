using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using YuLiangToolkit.Tools.PptViewer.Parsing;
using YuLiangToolkit.Tools.PptViewer.Rendering;

namespace YuLiangToolkit.Tools.PptViewer.Views;

/// <summary>
/// 「全屏放映」窗口：黑底无边框最大化，只负责显示一张幻灯片 + 键盘 / 鼠标翻页。
///
/// <para><b>为什么单独开窗口</b>：工具页嵌在主窗口的侧边栏 / 标题栏里，放映要盖住整个屏幕、
/// 不能有工具条干扰；独立窗口也能在主界面被其它操作挡住时仍然置顶显示。</para>
///
/// <para><b>翻页</b>：← / ↑ / PageUp / Backspace = 上一页，→ / ↓ / PageDown / 空格 / 回车 = 下一页，
/// Home / End 跳到首尾，Esc 退出；鼠标左键下一页、右键上一页。
/// 放映时按配置跳过隐藏页（全被隐藏时退回按原顺序放映，避免黑屏）。</para>
///
/// <para><b>图片来源</b>：优先用 ViewModel 的渲染缓存（<c>Func&lt;PptSlide, DrawingImage?&gt;</c>），
/// 这样放映和主界面共用同一份矢量绘制结果；没传时退回自己调
/// <see cref="PptSlideRenderer.CreateImage"/>。</para>
/// </summary>
public partial class PptPresentationWindow : Window
{
    /// <summary>鼠标不动多久后把底部提示条淡出（毫秒）。</summary>
    private const int HintAutoHideMilliseconds = 2500;

    /// <summary>提示条淡入 / 淡出动画时长（毫秒）。</summary>
    private const int HintFadeMilliseconds = 350;

    private readonly PptPresentation _presentation;
    private readonly Func<PptSlide, DrawingImage?> _imageProvider;
    private readonly List<PptSlide> _slides;
    private readonly DispatcherTimer _hintTimer;

    /// <summary>当前在放映序列里的位置（0 开始，不是幻灯片页号）。</summary>
    private int _position;

    /// <summary>提示条正在淡出 / 已经淡出。</summary>
    private bool _hintHidden;

    /// <param name="presentation">已经解析好的演示文稿（只读模型）。</param>
    /// <param name="startSlideIndex">从第几页开始（1 开始；会夹到有效范围）。</param>
    /// <param name="skipHidden">是否跳过隐藏幻灯片（和 PowerPoint 一致，默认跳过）。</param>
    /// <param name="imageProvider">可选的渲染回调（一般传主界面 ViewModel 的缓存方法）。</param>
    public PptPresentationWindow(
        PptPresentation presentation,
        int startSlideIndex,
        bool skipHidden = true,
        Func<PptSlide, DrawingImage?>? imageProvider = null)
    {
        InitializeComponent();

        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _imageProvider = imageProvider ?? (slide => PptSlideRenderer.CreateImage(_presentation, slide));

        _slides = presentation.Slides.Where(slide => !skipHidden || !slide.IsHidden).ToList();
        if (_slides.Count == 0) _slides = presentation.Slides.ToList();

        // 起始页：找到第一张页号 >= 起始页的幻灯片；起始页超过末尾时从最后一张开始
        var startPosition = _slides.FindIndex(slide => slide.Index >= startSlideIndex);
        _position = startPosition < 0 ? Math.Max(0, _slides.Count - 1) : startPosition;

        _hintTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(HintAutoHideMilliseconds),
        };
        _hintTimer.Tick += OnHintTimerTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseRightButtonDown += OnMouseRightButtonDown;
        MouseMove += OnMouseMove;
    }

    /// <summary>关闭时停在第几页（1 开始；主界面据此把当前页同步过去）。</summary>
    public int LastSlideIndex { get; private set; }

    // ---------------------------------------------------------------- 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowCurrentSlide();
        StartHintTimer();

        // 抢焦点：键盘翻页必须落在本窗口上（ShowDialog 的宿主窗口可能还持有焦点）
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hintTimer.Stop();
        _hintTimer.Tick -= OnHintTimerTick;

        // 主动断开绘制结果引用，窗口关掉后不拖着一份大 DrawingImage
        SlideImage.Source = null;
    }

    // ---------------------------------------------------------------- 显示 / 导航

    /// <summary>把当前页画到中间的大图上，并更新底部页码。</summary>
    private void ShowCurrentSlide()
    {
        if (_slides.Count == 0)
        {
            SlideImage.Source = null;
            PageText.Text = "没有可放映的幻灯片";
            return;
        }

        var slide = _slides[_position];

        try
        {
            SlideImage.Source = _imageProvider(slide);
        }
        catch
        {
            // 单页渲染失败：清空画面但保留翻页能力，不让放映窗口崩
            SlideImage.Source = null;
        }

        LastSlideIndex = slide.Index;
        PageText.Text = $"{slide.Index} / {_presentation.SlideCount}";
    }

    /// <summary>往后 / 往前翻一页（到头就停在原页，不循环）。</summary>
    private void Move(int delta)
    {
        if (_slides.Count == 0) return;

        var target = _position + delta;
        if (target < 0 || target >= _slides.Count) return;

        _position = target;
        ShowCurrentSlide();
        ShowHint();
    }

    private void MoveToFirst()
    {
        if (_slides.Count == 0) return;

        _position = 0;
        ShowCurrentSlide();
        ShowHint();
    }

    private void MoveToLast()
    {
        if (_slides.Count == 0) return;

        _position = _slides.Count - 1;
        ShowCurrentSlide();
        ShowHint();
    }

    // ---------------------------------------------------------------- 键盘 / 鼠标

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Right:
            case Key.Down:
            case Key.PageDown:
            case Key.Space:
            case Key.Enter:
                Move(1);
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Up:
            case Key.PageUp:
            case Key.Back:
                Move(-1);
                e.Handled = true;
                break;

            case Key.Home:
                MoveToFirst();
                e.Handled = true;
                break;

            case Key.End:
                MoveToLast();
                e.Handled = true;
                break;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Move(1);
        e.Handled = true;
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Move(-1);
        e.Handled = true;
    }

    /// <summary>动鼠标就把提示条叫回来，然后重新计时淡出。</summary>
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_hintHidden) return;
        ShowHint();
    }

    // ---------------------------------------------------------------- 底部提示条淡入 / 淡出

    private void ShowHint()
    {
        _hintHidden = false;
        HintPanel.BeginAnimation(OpacityProperty, null);
        HintPanel.Opacity = 1;
        StartHintTimer();
    }

    private void StartHintTimer()
    {
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void OnHintTimerTick(object? sender, EventArgs e)
    {
        _hintTimer.Stop();

        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(HintFadeMilliseconds))
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        HintPanel.BeginAnimation(OpacityProperty, animation);
        _hintHidden = true;
    }
}
