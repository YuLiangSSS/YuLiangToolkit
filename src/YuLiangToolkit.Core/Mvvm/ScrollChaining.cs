using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace YuLiangToolkit.Core.Mvvm;

/// <summary>
/// 滚轮「滚动链」：鼠标停在**内层列表**（游戏列表 / 文件列表 / 日志框）上滚滚轮，
/// 内层滚到头、或者压根没得滚（空列表 / 内容比视口矮）时，把这一格滚轮**交给外层页面**继续滚，
/// 而不是卡在列表上不动。
///
/// <para><b>为什么必须自己插手（实测 + .NET 源码，别改成"什么都不做"）</b>：
/// <c>ScrollViewer.OnMouseWheel</c> 的实现是
/// <code>
/// if (e.Handled) return;
/// if (!HandlesMouseWheelScrolling) return;
/// if (ScrollInfo != null) { if (e.Delta &lt; 0) ScrollInfo.MouseWheelDown(); else ScrollInfo.MouseWheelUp(); }
/// e.Handled = true;      // ← 无条件置 true，滚没滚都置
/// </code>
/// 也就是说**它哪怕一点都没滚动，也会把事件标成已处理**，事件再也冒泡不到外层 ——
/// "滚到头就卡住"的根因就是这个。所以要赶在它前面、在**隧道阶段**（本类的
/// <c>PreviewMouseWheel</c>）看清楚内层到底还能不能滚：
/// <list type="bullet">
///   <item>还能滚 → 不插手，内层自己按原生逻辑滚（我们要是抢先置 <c>Handled</c> 会让它直接 return，
///         列表就滚不动了）；</item>
///   <item>滚不动了 → 替外层滚一格，并把事件吃掉（<c>Handled = true</c>），
///         否则内层会在冒泡阶段把它吞掉，外层永远收不到。</item>
/// </list></para>
///
/// <para><b>步长必须和原生一模一样</b>：外层那一格走的是 <c>ScrollViewer.OnMouseWheel</c> 同一条路 ——
/// 拿到外层自己的 <see cref="IScrollInfo"/>（像素滚动的页面就是 <see cref="ScrollContentPresenter"/>）
/// 调它的 <c>MouseWheelUp</c>/<c>MouseWheelDown</c>。实测一格 = 3 行 = <b>48px</b>
/// （即 <c>WheelScrollLines × 16px</c>）。早期版本是自己 <c>LineDown()</c> 一次（只有 16px），
/// 用户一眼就看出"滚到头之后滚上级页面超级慢"。</para>
///
/// <para>用法：XAML 上写 <c>core:ScrollChaining.Enabled="True"</c>，或代码里
/// <c>ScrollChaining.Attach(list)</c>。</para>
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

    /// <summary>
    /// 上一次滚轮的判断结果（排查用：滚轮没反应时看它一眼就知道是"没走到这里"还是"判断成了别人"）。
    /// </summary>
    public static string LastDecision { get; private set; } = "(还没收到过滚轮)";

    /// <summary>
    /// 每判断完一次滚轮就触发一次（参数就是新的 <see cref="LastDecision"/>）。
    /// 排查用：框架示例开着 <c>YLT_WHEEL_TEST</c> 时就靠它"滚一下就把判断写回文件"。
    /// </summary>
    public static event EventHandler<string>? DecisionMade;

    private static void Report(string decision)
    {
        LastDecision = decision;
        DecisionMade?.Invoke(null, decision);
    }

    /// <summary>该让谁来响应这一步滚轮。</summary>
    public enum ScrollAction
    {
        /// <summary>内外都滚不动：什么都不做，随事件继续冒泡。</summary>
        None = 0,

        /// <summary>内层列表还能滚，交给它（我们不插手）。</summary>
        Inner = 1,

        /// <summary>内层到头了，这一格归外层页面（我们替它滚）。</summary>
        Outer = 2,
    }

    /// <summary>这一步滚轮该由谁来响应。纯函数，自检里直接断言。</summary>
    /// <param name="inner">鼠标所在的内层可滚动控件（可为 null）。</param>
    /// <param name="outer">上面那个**这个方向还能滚**的外层页面（没有就是 null）。</param>
    /// <param name="delta">滚轮增量：负 = 向下、正 = 向上。</param>
    public static (ScrollAction Action, bool Down) Decide(ScrollViewer? inner, ScrollViewer? outer, int delta)
    {
        var down = delta < 0;

        if (CanScroll(inner, down)) return (ScrollAction.Inner, down);
        if (CanScroll(outer, down)) return (ScrollAction.Outer, down);
        return (ScrollAction.None, down);
    }

    /// <summary>这个 ScrollViewer 还能不能往这个方向再滚一点（空列表 / 内容比视口矮都算滚不动）。</summary>
    public static bool CanScroll(ScrollViewer? viewer, bool down)
    {
        if (viewer is null) return false;

        // 逻辑滚动时这两个值是"项"，像素滚动时是"像素" —— 两种都成立，不用区分
        var max = viewer.ExtentHeight - viewer.ViewportHeight;
        if (max <= 0.5) return false;

        return down
            ? viewer.VerticalOffset < max - 0.5
            : viewer.VerticalOffset > 0.5;
    }

    /// <summary>
    /// 把外层滚动**一格**（步长与"鼠标直接停在外层上滚"完全一致）。
    /// 干的就是 WPF 自己那件事：调外层 <see cref="IScrollInfo"/> 的
    /// <c>MouseWheelDown</c> / <c>MouseWheelUp</c>。
    /// </summary>
    public static void ScrollOneNotch(ScrollViewer? outer, bool down)
    {
        if (outer is null) return;

        var info = FindScrollInfo(outer);
        if (info is not null)
        {
            if (down) info.MouseWheelDown();
            else info.MouseWheelUp();
            return;
        }

        // 兜底：模板还没套上时拿不到 ScrollInfo，就按 "WheelScrollLines 行" 滚
        // （像素滚动下实测等价：3 × 16px = 48px）
        var lines = Math.Max(1, SystemParameters.WheelScrollLines);
        for (var i = 0; i < lines; i++)
        {
            if (down) outer.LineDown();
            else outer.LineUp();
        }
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        if (sender is not DependencyObject element) return;

        var down = e.Delta < 0;
        var inner = FindScrollViewer(element);
        var outer = FindScrollableAncestor(element, down);
        var (action, _) = Decide(inner, outer, e.Delta);

        if (action != ScrollAction.Outer || outer is null)
        {
            Report(action == ScrollAction.Inner
                ? "内层还能滚 → 交给内层原生滚动（我们不插手）"
                : "内外都滚不动 → 不处理，随事件继续冒泡");
            return;
        }

        // 内层滚不动了，这一格归外层。⚠ 必须在这里就把事件吃掉：
        // 内层 ScrollViewer 在冒泡阶段是**无条件** e.Handled = true 的，放它过去的话外层永远收不到。
        // 反过来，我们在隧道阶段先置 true 也能让它别再滚一遍（它开头就是 if (e.Handled) return;）。
        e.Handled = true;
        ScrollOneNotch(outer, down);
        Report($"内层到头 → 替外层滚了一格（{(down ? "下" : "上")}）");
    }

    /// <summary>
    /// 拿到 ScrollViewer 自己用的那个 <see cref="IScrollInfo"/>（和 <c>ScrollViewer.ScrollInfo</c> 一个规矩）：
    /// 像素滚动（<c>CanContentScroll=false</c>，页面 ScrollViewer 都是这种）就是
    /// <see cref="ScrollContentPresenter"/>；逻辑滚动（ListBox 这类）是内容面板自己。
    /// </summary>
    private static IScrollInfo? FindScrollInfo(ScrollViewer viewer)
    {
        var presenter = FindChild<ScrollContentPresenter>(viewer);
        if (presenter is null) return null;
        if (!viewer.CanContentScroll) return presenter;

        foreach (var node in Descendants(presenter))
        {
            if (!ReferenceEquals(node, presenter) && node is IScrollInfo info) return info;
        }
        return presenter;
    }

    /// <summary>沿可视化树往上找**第一个在这个方向还能滚**的外层 <see cref="ScrollViewer"/>。</summary>
    private static ScrollViewer? FindScrollableAncestor(DependencyObject element, bool down)
    {
        for (var node = ParentOf(element); node is not null; node = ParentOf(node))
        {
            if (node is ScrollViewer viewer && CanScroll(viewer, down)) return viewer;
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

    /// <summary>找出该元素内部真正负责滚动的 <see cref="ScrollViewer"/>（ListBox / TextBox 模板里那个）。</summary>
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

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindChild<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>深度优先遍历所有后代（不含自己）。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
