using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using YuLiangToolkit.Tools.MediaPlayer.ViewModels;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 左下角的迷你播放条（开着「后台播放」时会浮出来）：一行**来回滚**的标题 + 进度条（点 / 拖跳转）
/// + 快退 / 播放暂停 / 快进 + 可点击的播放模式，点标题回到播放器。右上角还有个**小 ×**：
/// 点它 = 关掉小窗 + 停止播放（只收这一次，下次开始播放再切走又会浮出来）。
///
/// <para>标题的滚法（v0.9.2 改的）：<b>先从右往左滚到尾</b>（最后一个字露出来）→ 停一下 →
/// <b>再从左往右滚回开头</b>（第一个字回来）→ 停一下 → 一直这么循环。
/// 这样两端都看得清；标题本来就放得下时**根本不滚**。速度与“一趟多久”见
/// <see cref="ScrollDuration"/>（纯函数，自检直接断言它）。</para>
///
/// <para>⚠ 它的 <c>DataContext</c> 是**播放器的 ViewModel**（外壳的 <c>MiniPanelSource</c> 给过来的），
/// 不是 <c>MainViewModel</c>；所以「回到播放器」那条命令在 XAML 里用 Window 的 DataContext 取。</para>
///
/// <para>⚠ 播放器页里那个 200ms 的计时器在视图被摘下（切到别的页面）时就停了，所以**进度由这里自己的
/// 计时器推进**（<see cref="OnPositionTick"/>）；只在小窗真的可见时跑（<see cref="IsVisibleChanged"/> 开关），
/// 前台播放时还是播放器页负责，不会两队计时器一起改进度。</para>
///
/// <para>两个快退 / 快进按钮跟播放器界面里完全一样：<b>左键跳步长、右键上一首 / 下一首</b>
/// （右键在这里用代码接，<c>Button.Command</c> 只认左键）。</para>
/// </summary>
public partial class MiniPlayerPanel : UserControl
{
    /// <summary>普通长度标题的滚动速度（像素 / 秒）。太快看不清，太慢像卡住。</summary>
    public const double PixelsPerSecond = 34;

    /// <summary>
    /// 超长标题（歌名一百多个字那种）一趟**最多**滚这么久。
    /// 不封顶的话「溢出 700px ÷ 34px/s ≈ 20 秒」才滚到尾，看起来就像一直显示不全；
    /// 封顶后长标题大约 4 秒就能看到结尾。
    /// </summary>
    public const double MaxOneWaySeconds = 4.0;

    /// <summary>回滚（从结尾滚回开头）最多用这么久——去程慢慢看，回程快速复位，别让人干等。</summary>
    public const double MaxReturnSeconds = 0.8;

    /// <summary>滚到尾时多滚 3px：最后一个字不贴着裁剪边（不然右边缘看着像被切掉一点）。</summary>
    private const double OvershootPixels = 3;

    /// <summary>刚开始滚之前先定住一会儿（让人看清开头）。</summary>
    private static readonly TimeSpan StartPause = TimeSpan.FromSeconds(0.6);

    /// <summary>滚到一头之后停多久（让人看清第一个字 / 最后一个字）。</summary>
    private static readonly TimeSpan EndPause = TimeSpan.FromSeconds(1.5);

    /// <summary>一趟最少走这么久（只溢出几个像素时不至于“闪一下”）。</summary>
    private static readonly TimeSpan MinMove = TimeSpan.FromSeconds(1.0);

    /// <summary>
    /// 排错 / 截图用：<c>YLT_MINI_MEASURE=文件</c> 时把标题行的**真实几何**（谁多宽、被谁的
    /// 裁剪框住、当前滚动到哪个像素、裁剪窗口在窗口里的坐标）写进文件。只读，不写配置、不影响界面。
    /// <para>「标题看着被截断」这类问题不能只看 <c>TextBlock.Text</c>：必须看**布局宽度 / 裁剪矩形 /
    /// 祖先的 ClipToBounds / layout clip**，所以这里把整条视觉祖先链都打出来。</para>
    /// </summary>
    private static readonly string? MeasurePath = Environment.GetEnvironmentVariable("YLT_MINI_MEASURE");

    /// <summary>
    /// 排错 / 截图用：<c>YLT_MINI_SHIFT=像素</c> 把标题**固定在某个滚动位移**上（不跑动画），
    /// 这样「滚到一半」的画面每次截图都一模一样，方便前后对比。
    /// </summary>
    private static readonly double? ShiftOverride =
        double.TryParse(Environment.GetEnvironmentVariable("YLT_MINI_SHIFT"), out var shiftOverride) ? shiftOverride : null;

    /// <summary>后台推进进度用的计时器（只在可见时跑）。</summary>
    private DispatcherTimer? _positionTimer;

    /// <summary>正在拖进度条：这时别让计时器把滑块拽回去（松手后自然恢复）。</summary>
    private bool _seeking;

    public MiniPlayerPanel()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => RestartMarquee();
        Loaded += (_, _) =>
        {
            RestartMarquee();
            UpdatePositionTimer();
            ScheduleMeasure();
        };
        Unloaded += (_, _) =>
        {
            TitleShift.BeginAnimation(TranslateTransform.XProperty, null);
            _positionTimer?.Stop();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RestartMarquee();   // 小窗从藏到显示：宽度这时才量得准，重新算一趟
            UpdatePositionTimer();
        };

        // 标题变长变短、小窗被拉伸 / 字号变了，都要重新算一遍行程；
        // TargetUpdated 兜住「换了首歌但标题像素宽刚好一样」的情况（只靠 SizeChanged 会漏掉）。
        TitleText.SizeChanged += (_, _) => RestartMarquee();
        TitleClip.SizeChanged += (_, _) => RestartMarquee();
        TitleText.TargetUpdated += (_, _) => RestartMarquee();

        // 右键：上一首 / 下一首（左键仍然走各自的 Command）
        MiniBack.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Vm?.PreviousCommand.Execute(null);
        };
        MiniForward.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Vm?.NextCommand.Execute(null);
        };
    }

    private MediaPlayerViewModel? Vm => DataContext as MediaPlayerViewModel;

    // ---------------------------------------------------------------- 进度条（后台推进 + 点击拖动跳转）

    /// <summary>小窗可见时每 200ms 把播放器位置推进 ViewModel（进度条 / 时间提示跟着走）。</summary>
    private void UpdatePositionTimer()
    {
        if (!IsVisible)
        {
            _positionTimer?.Stop();
            return;
        }

        _positionTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        if (_positionTimer.IsEnabled) return;

        _positionTimer.Tick -= OnPositionTick;
        _positionTimer.Tick += OnPositionTick;
        _positionTimer.Start();
    }

    private void OnPositionTick(object? sender, EventArgs e)
    {
        if (_seeking) return;   // 正在拖：先别覆盖用户选的位置
        if (Vm is { } vm && vm.Player is { IsLoaded: true } player)
        {
            vm.UpdatePosition(player.Position, player.Duration);
            MiniSeekArea.ToolTip = $"{vm.PositionText} / {vm.DurationText}　点 / 拖可以跳转";
        }
    }

    private void OnMiniSeekMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.Player is not { IsLoaded: true }) return;
        _seeking = true;
        MiniSeekArea.CaptureMouse();
        ApplyMiniSeek(e);
        e.Handled = true;
    }

    private void OnMiniSeekMouseMove(object sender, MouseEventArgs e)
    {
        if (_seeking) ApplyMiniSeek(e);
    }

    private void OnMiniSeekMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        ApplyMiniSeek(e);
        _seeking = false;
        if (MiniSeekArea.IsMouseCaptured) MiniSeekArea.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMiniSeekMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        if (MiniSeekArea.IsMouseCaptured) MiniSeekArea.ReleaseMouseCapture();
    }

    private void ApplyMiniSeek(MouseEventArgs e)
    {
        if (Vm is not { } vm) return;
        var width = MiniSeekArea.ActualWidth;
        if (width <= 0) return;
        vm.SeekToRatio(Math.Clamp(e.GetPosition(MiniSeekArea).X / width, 0, 1));
    }

    /// <summary>
    /// 标题这一趟要滚多久：**放得下就返回 <c>null</c>（压根不滚）**，放不下返回滚完
    /// “溢出部分”（标题宽 − 可视宽）所需的时间。
    /// <para>普通长度按 <see cref="PixelsPerSecond"/> 匀速；**超长标题最多
    /// <see cref="MaxOneWaySeconds"/> 秒**（不封顶的话一百多字的歌名要滚 20 秒，看着就像一直显示不全）。</para>
    /// <para>纯算术，自检直接断言（见 <c>MediaPlayerChecks</c> 的「迷你条标题」那几条）。</para>
    /// </summary>
    public static TimeSpan? ScrollDuration(double textWidth, double viewWidth, double pixelsPerSecond = PixelsPerSecond)
    {
        if (double.IsNaN(textWidth) || double.IsNaN(viewWidth)) return null;
        if (viewWidth <= 0 || textWidth <= 0) return null;

        var overflow = textWidth - viewWidth;
        if (overflow <= 1) return null;                     // 容 1px：四舍五入的误差不算溢出

        var speed = pixelsPerSecond > 1 ? pixelsPerSecond : 1;
        var seconds = Math.Min(overflow / speed, MaxOneWaySeconds);
        return seconds * 1000 < MinMove.TotalMilliseconds ? MinMove : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>回滚（结尾 → 开头）用多久：去程慢、回程快，最多 <see cref="MaxReturnSeconds"/> 秒。</summary>
    public static TimeSpan ReturnDuration(TimeSpan move)
    {
        if (move <= TimeSpan.Zero) return TimeSpan.Zero;
        var cap = TimeSpan.FromSeconds(MaxReturnSeconds);
        return move < cap ? move : cap;
    }

    /// <summary>
    /// 重新生成标题的动画：停 → 右往左滚到尾 → 停 → 快速滚回开头（<c>RepeatBehavior.Forever</c>）。
    /// 放得下就把位移清零、不动。
    /// </summary>
    private void RestartMarquee()
    {
        TitleShift.BeginAnimation(TranslateTransform.XProperty, null);   // 先停掉旧动画

        if (ShiftOverride is { } fixedShift)
        {
            TitleShift.X = fixedShift;   // 截图钩子：固定在这个位移上，不跑动画
            return;
        }

        var duration = ScrollDuration(TitleText.ActualWidth, TitleClip.ActualWidth);
        if (duration is not { } move)
        {
            TitleShift.X = 0;   // 放得下：老老实实显示完整标题
            return;
        }

        // 多滚 3px：最后一个字不贴死在裁剪边上（右侧看着不会被切一点）
        var overflow = TitleText.ActualWidth - TitleClip.ActualWidth + OvershootPixels;
        var back = ReturnDuration(move);

        var frames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        frames.KeyFrames.Add(Key(0, TimeSpan.Zero));                        // 开头
        frames.KeyFrames.Add(Key(0, StartPause));                           // 停一下（看清开头）
        frames.KeyFrames.Add(Key(-overflow, StartPause + move));             // 右往左滚到尾（最后一个字露出来）
        frames.KeyFrames.Add(Key(-overflow, StartPause + move + EndPause));  // 停在结尾（看清最后一个字）
        frames.KeyFrames.Add(Key(0, StartPause + move + EndPause + back));   // 快速滚回开头（第一个字回来）

        TitleShift.BeginAnimation(TranslateTransform.XProperty, frames);
    }

    private static LinearDoubleKeyFrame Key(double value, TimeSpan time)
        => new(value, KeyTime.FromTimeSpan(time));

    // ---------------------------------------------------------------- 排错（YLT_MINI_MEASURE）

    /// <summary>
    /// 把迷你条标题行的真实几何写进文件（<see cref="MeasurePath"/>）：真实宽度 / 裁剪矩形 /
    /// 祖先的 ClipToBounds / layout clip / 当前滚动位移，以及标题裁剪窗口在**窗口坐标**里的位置
    /// （给截图脚本按像素核对「最后一个字是不是真的露出来了」用）。
    /// <para>排查「标题被截断」时**光看 TextBlock.Text 没用**——Text 永远是完整的，断的是布局与裁剪
    /// （v0.55.2 那个 bug 就是 WPF 自动加的 layout clip 跟着 RenderTransform 一起走）。</para>
    /// </summary>
    private void ScheduleMeasure()
    {
        if (MeasurePath is null) return;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { DumpMeasure(); } catch (Exception ex) { TryWrite($"测量失败：{ex}"); }
        };
        timer.Start();
    }

    private void DumpMeasure()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"panel={ActualWidth:0.##}x{ActualHeight:0.##} IsVisible={IsVisible} IsLoaded={IsLoaded}");
        sb.AppendLine($"TitleClip.ActualWidth={TitleClip.ActualWidth:0.##} ActualHeight={TitleClip.ActualHeight:0.##}");
        sb.AppendLine($"TitleText.ActualWidth={TitleText.ActualWidth:0.##} ActualHeight={TitleText.ActualHeight:0.##}");
        sb.AppendLine($"TitleShift.X={TitleShift.X:0.##} animated={TitleShift.HasAnimatedProperties}");
        sb.AppendLine($"ScrollDuration={ScrollDuration(TitleText.ActualWidth, TitleClip.ActualWidth)?.TotalMilliseconds.ToString("0") ?? "null"}");
        sb.AppendLine($"TitleText=\"{TitleText.Text}\"");
        if (Window.GetWindow(this) is { } window)
        {
            var clipOrigin = TitleClip.TranslatePoint(new Point(0, 0), window);
            sb.AppendLine(
                $"window={window.ActualWidth:0.##}x{window.ActualHeight:0.##} "
                + $"TitleClipInWindow=({clipOrigin.X:0.##},{clipOrigin.Y:0.##}) "
                + $"{TitleClip.ActualWidth:0.##}x{TitleClip.ActualHeight:0.##}");
        }
        sb.AppendLine("--- 祖先链（TitleText → 本控件；坐标为相对本控件）---");
        DependencyObject? cur = TitleText;
        while (cur is not null)
        {
            if (cur is FrameworkElement fe)
            {
                var origin = fe.TranslatePoint(new Point(0, 0), this);
                var layoutClip = LayoutInformation.GetLayoutClip(fe);
                sb.AppendLine(
                    $"{fe.GetType().Name} \"{fe.Name}\" pos=({origin.X:0.##},{origin.Y:0.##}) "
                    + $"size={fe.ActualWidth:0.##}x{fe.ActualHeight:0.##} hAlign={fe.HorizontalAlignment} "
                    + $"margin={fe.Margin} clipToBounds={fe.ClipToBounds} clip={ClipText(fe)} "
                    + $"layoutClip={(layoutClip is null ? "null" : layoutClip.Bounds.ToString())}");
            }
            if (ReferenceEquals(cur, this)) break;
            cur = VisualTreeHelper.GetParent(cur);
        }
        TryWrite(sb.ToString());
    }

    private static string ClipText(UIElement element)
        => element.Clip is null ? "null" : element.Clip.Bounds.ToString();

    private static void TryWrite(string text)
    {
        try { if (MeasurePath is not null) File.WriteAllText(MeasurePath, text); } catch { /* 排错钩子别把界面搞崩 */ }
    }
}
