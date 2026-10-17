using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using YuLiangToolkit.Tools.MediaPlayer.ViewModels;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 左下角的迷你播放条（开着「后台播放」时会浮出来）：一行**循环滚动**的标题
/// + 快退 / 播放暂停 / 快进，点标题回到播放器。
///
/// <para>⚠ 它的 <c>DataContext</c> 是**播放器的 ViewModel**（外壳的 <c>MiniPanelSource</c> 给过来的），
/// 不是 <c>MainViewModel</c>；所以「回到播放器」那条命令在 XAML 里用 Window 的 DataContext 取。</para>
///
/// <para>两个快退 / 快进按钮跟播放器界面里完全一样：<b>左键跳步长、右键上一首 / 下一首</b>
/// （右键在这里用代码接，<c>Button.Command</c> 只认左键）。</para>
/// </summary>
public partial class MiniPlayerPanel : UserControl
{
    /// <summary>两遍标题之间的空隙（滚动时看着不断字）。</summary>
    private const double Gap = 30;

    /// <summary>滚动速度（像素 / 秒）。太快看不清，太慢像卡住。</summary>
    private const double PixelsPerSecond = 34;

    public MiniPlayerPanel()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => RestartMarquee();
        Loaded += (_, _) => RestartMarquee();
        Unloaded += (_, _) => TitleShift.BeginAnimation(TranslateTransform.XProperty, null);

        // 标题变长变短 / 字体缩放后要重新算一遍行程
        TitleText1.SizeChanged += (_, _) => RestartMarquee();

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
    /// 标题从右往左循环滚：两份一样的文本并排放，整体左移「一份宽 + 空隙」后重来 ——
    /// 第二份正好补到第一份原来的位置，所以接缝看不出来。
    /// </summary>
    private void RestartMarquee()
    {
        var width = TitleText1.ActualWidth;
        if (width <= 0) return;

        var travel = width + Gap;
        var seconds = Math.Max(3.0, travel / PixelsPerSecond);
        var animation = new DoubleAnimation(0, -travel, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        TitleShift.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
