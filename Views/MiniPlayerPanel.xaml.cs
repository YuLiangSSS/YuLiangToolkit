using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using YuLiangToolkit.Tools.MediaPlayer.ViewModels;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 左下角的迷你播放条（开着「后台播放」时会浮出来）：一行**来回滚**的标题
/// + 快退 / 播放暂停 / 快进，点标题回到播放器。右上角还有个**小 ×**：
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
/// <para>两个快退 / 快进按钮跟播放器界面里完全一样：<b>左键跳步长、右键上一首 / 下一首</b>
/// （右键在这里用代码接，<c>Button.Command</c> 只认左键）。</para>
/// </summary>
public partial class MiniPlayerPanel : UserControl
{
    /// <summary>滚动速度（像素 / 秒）。太快看不清，太慢像卡住。</summary>
    public const double PixelsPerSecond = 34;

    /// <summary>刚开始滚之前先定住一会儿（短一点，别让人等着）。</summary>
    private static readonly TimeSpan StartPause = TimeSpan.FromSeconds(0.4);

    /// <summary>滚到一头之后停多久（让人看清第一个字 / 最后一个字）。</summary>
    private static readonly TimeSpan EndPause = TimeSpan.FromSeconds(1.2);

    /// <summary>一趟最少走这么久（只溢出几个像素时不至于“闪一下”）。</summary>
    private static readonly TimeSpan MinMove = TimeSpan.FromSeconds(1.0);

    public MiniPlayerPanel()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => RestartMarquee();
        Loaded += (_, _) => RestartMarquee();
        Unloaded += (_, _) => TitleShift.BeginAnimation(TranslateTransform.XProperty, null);

        // 标题变长变短、小窗被拉伸 / 字号变了，都要重新算一遍行程
        TitleText.SizeChanged += (_, _) => RestartMarquee();
        TitleClip.SizeChanged += (_, _) => RestartMarquee();

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

    /// <summary>
    /// 标题这一趟要滚多久：**放得下就返回 <c>null</c>（压根不滚）**，放不下返回滚完
    /// “溢出部分”（标题宽 − 可视宽）所需的时间。
    /// <para>纯算术，自检直接断言（见 <c>MediaPlayerChecks</c> 的「迷你条标题」那几条）。</para>
    /// </summary>
    public static TimeSpan? ScrollDuration(double textWidth, double viewWidth, double pixelsPerSecond = PixelsPerSecond)
    {
        if (double.IsNaN(textWidth) || double.IsNaN(viewWidth)) return null;
        if (viewWidth <= 0 || textWidth <= 0) return null;

        var overflow = textWidth - viewWidth;
        if (overflow <= 1) return null;                     // 容 1px：四舍五入的误差不算溢出

        var speed = pixelsPerSecond > 1 ? pixelsPerSecond : 1;
        var seconds = overflow / speed;
        return seconds * 1000 < MinMove.TotalMilliseconds ? MinMove : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// 重新生成标题的动画：停 → 右往左滚到尾 → 停 → 左往右滚回开头（<c>RepeatBehavior.Forever</c>）。
    /// 放得下就把位移清零、不动。
    /// </summary>
    private void RestartMarquee()
    {
        TitleShift.BeginAnimation(TranslateTransform.XProperty, null);   // 先停掉旧动画

        var duration = ScrollDuration(TitleText.ActualWidth, TitleClip.ActualWidth);
        if (duration is not { } move)
        {
            TitleShift.X = 0;   // 放得下：老老实实显示完整标题
            return;
        }

        var overflow = TitleText.ActualWidth - TitleClip.ActualWidth;

        var frames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        frames.KeyFrames.Add(Key(0, TimeSpan.Zero));                        // 开头
        frames.KeyFrames.Add(Key(0, StartPause));                           // 停一下（别让人干等）
        frames.KeyFrames.Add(Key(-overflow, StartPause + move));             // 右往左滚到尾（最后一个字露出来）
        frames.KeyFrames.Add(Key(-overflow, StartPause + move + EndPause));  // 停在结尾
        frames.KeyFrames.Add(Key(0, StartPause + move + EndPause + move));   // 左往右滚回开头（第一个字回来）

        TitleShift.BeginAnimation(TranslateTransform.XProperty, frames);
    }

    private static LinearDoubleKeyFrame Key(double value, TimeSpan time)
        => new(value, KeyTime.FromTimeSpan(time));
}
