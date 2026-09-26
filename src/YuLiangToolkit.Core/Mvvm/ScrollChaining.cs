using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace YuLiangToolkit.Core.Mvvm;

/// <summary>
/// 滚轮「滚动链」：让嵌在可滚动页面里的内层列表，滚到头之后把滚轮交给外层继续滚。
///
/// <para><b>不处理会怎样</b>：鼠标停在游戏列表 / 文件列表上滚轮，<see cref="ListBox"/> 自己就把
/// <c>MouseWheel</c> 吃了；等它滚到底（或里面压根没内容可滚），事件也不往上冒，
/// 外层页面的 <c>ScrollViewer</c> 一动不动 —— 手感上就是"卡住了"。</para>
///
/// <para><b>做法：不自己滚，把事件"让"给外层</b>。
/// 滚轮是**隧道**事件（PreviewMouseWheel，从根往下走），外层页面的 <c>ScrollViewer</c> 就在这条路上、
/// 而且排在我们前面。所以只要：
/// <list type="bullet">
/// <item>内层还能滚 → 把 <c>e.Handled</c> 置 true，<b>拦住外层</b>，让 ListBox 自己滚
/// （ListBox 的 <c>OnMouseWheel</c> 不看这个标志，照样滚）；</item>
/// <item>内层已经到头 / 没内容可滚 → <b>什么都不做</b>，事件继续隧道下去，
/// 外层 <c>ScrollViewer</c> 会像"鼠标直接停在页面上"一样处理它。</item>
/// </list>
/// 关键是<b>别自己调 <c>LineUp</c>/<c>LineDown</c></b>：那是一次滚一行的步长，
/// 而 WPF 原生滚轮是 <c>MouseWheelDelta × 系统行数</c>（还带平滑滚动），
/// 自己滚会明显比正常慢 —— 用户直接就能感觉出来（v0.5.3 踩过）。</para>
///
/// <para><b>⚠ 必须用 <c>PreviewMouseWheel</c>，不能用冒泡的 <c>MouseWheel</c></b>：
/// WPF 的 <see cref="ListBox"/> 重写了 <c>OnMouseWheel</c>，直接把它转发给内部的 ScrollViewer
/// 并把 <c>Handled</c> 置为 true，事件<b>根本不会冒泡</b>到外面的处理器上（实测：挂 MouseWheel 一次都不触发）。</para>
///
/// <para>用法：XAML 上写 <c>core:ScrollChaining.Enabled="True"</c>，或代码里
/// <c>ScrollChaining.Attach(GameList)</c>。</para>
/// </summary>
public static class ScrollChaining
{
    /// <summary>挂上滚动链的开关（附加属性，方便直接在 XAML 里写）。</summary>
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(ScrollChaining),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    /// <summary>给一个可滚动的控件挂上滚动链（等价于 XAML 里的 <c>ScrollChaining.Enabled="True"</c>）。</summary>
    public static void Attach(UIElement element) => SetEnabled(element, true);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    /// <summary>
    /// 上一次滚轮的判断结果（排查用：滚轮没反应时看它一眼就知道是"没走到这里"还是"判断成了别人"）。
    /// </summary>
    public static string LastDecision { get; private set; } = "(还没收到过滚轮)";

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 已经有人处理过了就别抢
        if (e.Handled || e.Delta == 0) return;
        if (sender is not DependencyObject element) return;

        var inner = FindScrollViewer(element);
        var decision = Decide(inner, hasOuter: FindOuterScrollViewer(element) is not null, e.Delta);

        switch (decision.Action)
        {
            case ScrollAction.Inner:
                // 拦住外层（否则一次滚轮里外一起滚）；ListBox 不看 Handled，自己照滚
                LastDecision = "内层还能滚 → 拦住外层，交给 ListBox";
                e.Handled = true;
                break;

            case ScrollAction.Outer:
                // 不碰事件：让它继续隧道下去，由外层 ScrollViewer 按正常速度滚
                LastDecision = "内层到头 → 放行给外层（用 WPF 原生滚轮速度）";
                break;

            default:
                LastDecision = "内外都滚不动 → 不处理，让它继续冒泡";
                break;
        }
    }

    /// <summary>该让谁来响应这一步滚轮。纯函数，自检里直接断言。</summary>
    public enum ScrollAction
    {
        /// <summary>内外都滚不动。</summary>
        None = 0,

        /// <summary>内层列表还能滚，交给它（并拦住外层）。</summary>
        Inner = 1,

        /// <summary>内层到底了，放行给外层页面。</summary>
        Outer = 2,
    }

    /// <summary>这一步滚轮该由谁来响应。</summary>
    /// <param name="inner">鼠标所在的内层可滚动控件（可为 null）。</param>
    /// <param name="hasOuter">上面有没有可滚动的外层页面。</param>
    /// <param name="delta">滚轮增量：负 = 向下、正 = 向上。</param>
    public static (ScrollAction Action, bool Down) Decide(ScrollViewer? inner, bool hasOuter, int delta)
    {
        var down = delta < 0;

        if (inner is not null && CanScroll(inner, down)) return (ScrollAction.Inner, down);
        if (hasOuter) return (ScrollAction.Outer, down);
        return (ScrollAction.None, down);
    }

    /// <summary>内层还能不能往这个方向滚。</summary>
    private static bool CanScroll(ScrollViewer viewer, bool down)
    {
        // 内容还没视口高（或不允许滚动条）→ 内层没得滚
        if (viewer.ComputedVerticalScrollBarVisibility != Visibility.Visible) return false;

        var offset = viewer.VerticalOffset;
        var max = viewer.ExtentHeight - viewer.ViewportHeight;
        return down ? offset < max - 0.5 : offset > 0.5;
    }

    /// <summary>沿可视化树往上找**外层**那个可滚动的 <see cref="ScrollViewer"/>。</summary>
    private static ScrollViewer? FindOuterScrollViewer(DependencyObject element)
    {
        for (var node = ParentOf(element); node is not null; node = ParentOf(node))
        {
            if (node is ScrollViewer viewer
                && viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            {
                return viewer;
            }
        }
        return null;
    }

    /// <summary>往上走一层：可视化父级优先，模板元素则走逻辑父级。</summary>
    private static DependencyObject? ParentOf(DependencyObject element)
    {
        if (element is Visual or Visual3D)
        {
            var visual = VisualTreeHelper.GetParent(element);
            if (visual is not null) return visual;
        }
        return LogicalTreeHelper.GetParent(element);
    }

    /// <summary>找出该元素内部真正负责滚动的 <see cref="ScrollViewer"/>（ListBox / ItemsControl 模板里那个）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer self) return self;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer hit) return hit;
            var deeper = FindScrollViewer(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }
}
