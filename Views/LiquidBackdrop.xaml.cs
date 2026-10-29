using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 液态玻璃主题的流动光晕背景层（第二套 UI 的“场景”）。
///
/// <para>用法：放在 Window/Grid 的最底层、铺满整个窗口，<c>IsHitTestVisible="False"</c>（模板里已设），
/// 外层用 <c>Visibility="{Binding IsLiquidGlass, Converter={StaticResource BoolToVis}}"</c> 控制；
/// 默认主题下它是 Collapsed。</para>
///
/// <para><b>性能约定</b>：动画定义在 XAML 资源里的一个 Storyboard 中，但**不用 XAML 触发自动播放** ——
/// 由这里在「可见 / 加载」时开始、隐藏 / 卸载时停掉。否则默认主题下这层虽然看不见，
/// 时间线还在每秒 60 帧空跑（白白占 CPU，笔记本上很明显）。</para>
/// </summary>
public partial class LiquidBackdrop : UserControl
{
    private Storyboard? _ambient;
    private bool _running;

    public LiquidBackdrop()
    {
        InitializeComponent();

        Loaded += (_, _) => StartAmbient();
        Unloaded += (_, _) => StopAmbient();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) StartAmbient();
            else StopAmbient();
        };
    }

    private Storyboard Ambient => _ambient ??= (Storyboard)FindResource("AmbientStoryboard");

    private void StartAmbient()
    {
        if (_running || !IsVisible || PresentationSource.FromVisual(this) is null) return;
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
