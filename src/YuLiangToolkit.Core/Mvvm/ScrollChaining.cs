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
/// <para><b>⚠ 必须用隧道的 <c>PreviewMouseWheel</c>，不能用冒泡的 <c>MouseWheel</c></b>：
/// WPF 的 <see cref="ListBox"/> 重写了 <c>OnMouseWheel</c>，直接把它转发给内部的 ScrollViewer
/// 并把 <c>Handled</c> 置为 true，事件<b>根本不会冒泡</b>到外面的处理器上（实测：挂 MouseWheel 一次都不触发）。
/// 隧道是从根往下走的，会先经过我们，才有机会拦下来。</para>
///
/// <para>用法：XAML 上写 <c>local:ScrollChaining.Enabled="True"</c>，或代码里
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
        var outer = FindOuterScrollViewer(element);
        var decision = Decide(inner, outer, e.Delta);

        switch (decision.Action)
        {
            case ScrollAction.Inner:
                // 什么都不做，让隧道继续下去由 ListBox 自己滚
                LastDecision = "内层还能滚 → 交给 ListBox";
                break;

            case ScrollAction.Outer:
                LastDecision = $"内层到头 → 滚外层（从 {outer!.VerticalOffset:0.#} 起）";
                ScrollOnce(outer, decision.Down);
                e.Handled = true;   // 吃掉，免得里外一起滚
                break;

            default:
                LastDecision = "内外都滚不动 → 不处理，让它继续冒泡";
                break;
        }
    }

    /// <summary>该滚谁。单独抽出来是为了能脱离鼠标/窗口直接断言（见自检）。</summary>
    public enum ScrollAction
    {
        /// <summary>谁都滚不动。</summary>
        None = 0,

        /// <summary>内层列表还能滚，交给它。</summary>
        Inner = 1,

        /// <summary>内层到底了，转给外层页面。</summary>
        Outer = 2,
    }

    /// <summary>这一步滚轮该由谁来响应。</summary>
    /// <param name="inner">鼠标所在的内层可滚动控件（可为 null）。</param>
    /// <param name="outer">外层页面的 ScrollViewer（可为 null）。</param>
    /// <param name="delta">滚轮增量：负 = 向下、正 = 向上。</param>
    public static (ScrollAction Action, bool Down) Decide(ScrollViewer? inner, ScrollViewer? outer, int delta)
    {
        var down = delta < 0;

        if (inner is not null && CanScroll(inner, down)) return (ScrollAction.Inner, down);
        if (outer is not null) return (ScrollAction.Outer, down);
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

    /// <summary>按一行滚（<c>LineUp</c>/<c>LineDown</c> 会自己处理方向与步长）。</summary>
    private static void ScrollOnce(ScrollViewer viewer, bool down)
    {
        if (down) viewer.LineDown();
        else viewer.LineUp();
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
