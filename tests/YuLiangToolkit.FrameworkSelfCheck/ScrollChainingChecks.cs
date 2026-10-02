using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 滚轮「滚动链」的**行为**验证：造一个真的嵌套结构（外层页面 ScrollViewer + 内层 ListBox），
/// 扔真的滚轮事件进去，看**谁滚了、滚了多少、事件被谁吃掉**。
///
/// <para>为什么必须真跑：这件事完全取决于 WPF 的真实行为，而真实行为跟直觉相反 ——
/// <c>ScrollViewer.OnMouseWheel</c> 里 <c>e.Handled = true</c> 是**无条件**执行的（滚没滚都置），
/// 所以"什么都不做、指望事件自己冒泡上去"的写法会让滚轮卡在列表上（本文件第 2 条断言钉住的就是它）。</para>
///
/// <para>⚠ 合成滚轮事件**可以用**，但有个前提：先把 <c>ScrollToXxx</c> 这类命令过完 layout。
/// 那些是排进 ScrollViewer 命令队列、在 LayoutUpdated 里执行的；如果测量前队列还没排空，
/// 它会在事件滚完之后才生效、把偏移改回去，看起来就像"合成事件滚不动"（早期就是这么误判的）。</para>
/// </summary>
internal static class ScrollChainingChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnSta(check, report); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new InvalidOperationException($"STA 线程里失败：{failure.Message}", failure);
    }

    private static void RunOnSta(Action<string, bool, string> check, Action<string, bool> report)
    {
        var window = new Window
        {
            Width = 340, Height = 300, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Left = -4000, Top = -4000,
        };

        try
        {
            // ---------- 结构：页面 ScrollViewer（像素滚动） > StackPanel > ListBox（逻辑滚动） ----------
            var list = new ListBox
            {
                Height = 80,
                ItemsSource = Enumerable.Range(0, 30).Select(i => $"游戏 {i}").ToList(),
            };

            var pageContent = new StackPanel();
            for (var i = 0; i < 25; i++) pageContent.Children.Add(new TextBlock { Text = $"页面上方 {i}", Height = 20 });
            pageContent.Children.Add(list);
            for (var i = 0; i < 25; i++) pageContent.Children.Add(new TextBlock { Text = $"页面下方 {i}", Height = 20 });

            var page = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = pageContent,
            };
            window.Content = page;
            window.Show();
            Settle(window);

            var inner = FindChild<ScrollViewer>(list)
                        ?? throw new InvalidOperationException(
                            "找不到 ListBox 内部的 ScrollViewer（模板里那个必须 x:Name=\"ScrollViewer\"）");

            // 事件起点：内层列表里的真实元素（隧道会经过 ListBox，冒泡会到内层 ScrollViewer）
            var target = FindChild<ScrollContentPresenter>(inner) as UIElement ?? inner;

            var innerMax = Math.Max(0, inner.ExtentHeight - inner.ViewportHeight);
            var outerMax = Math.Max(0, page.ExtentHeight - page.ViewportHeight);
            check("测试环境：内层能滚、外层也能滚",
                innerMax > 1 && outerMax > 1,
                $"内层上限={innerMax:0.#}（项）外层上限={outerMax:0.#}（像素）");

            // ---------- 0. 先量出"原生一格"是多少：WheelScrollLines 行 × 一行多少像素 ----------
            page.ScrollToTop();
            Settle(window);
            var beforeLine = page.VerticalOffset;
            page.LineDown();
            Settle(window);
            var lineStep = page.VerticalOffset - beforeLine;

            page.ScrollToTop();
            Settle(window);
            var notchBefore = page.VerticalOffset;
            ScrollChaining.ScrollOneNotch(page, down: true);
            Settle(window);
            var notch = page.VerticalOffset - notchBefore;

            check("★ 一格滚轮 = WheelScrollLines 行（不是一行）",
                lineStep > 0 && notch > lineStep * 1.5,
                $"一行={lineStep:0.#}px，一格={notch:0.#}px" +
                $"（WheelScrollLines={SystemParameters.WheelScrollLines}，期望≈{lineStep * SystemParameters.WheelScrollLines:0.#}px）");

            // ---------- 1. 没挂滚动链：内层到底后事件被 WPF 吞掉，外层不动（"卡住"的复现） ----------
            list.SetValue(ScrollChaining.EnabledProperty, false);
            ScrollToEnd(inner, page, window);
            var beforeOuter = page.VerticalOffset;
            var beforeInner = inner.VerticalOffset;
            var handledWithout = RaiseWheel(target, -120, window);
            check("没挂滚动链时：内层到底后滚轮被 WPF 吃掉，外层一动不动（这就是要修的病）",
                Math.Abs(page.VerticalOffset - beforeOuter) < 0.5
                && Math.Abs(inner.VerticalOffset - beforeInner) < 0.5
                && handledWithout,
                $"外层 {beforeOuter:0.#} → {page.VerticalOffset:0.#}，Handled={handledWithout}");

            // ---------- 2. 挂上滚动链：内层到底 → 这一格归外层 ----------
            ScrollChaining.Attach(list);
            ScrollToEnd(inner, page, window);
            beforeOuter = page.VerticalOffset;
            beforeInner = inner.VerticalOffset;
            var handledAtBottom = RaiseWheel(target, -120, window);

            check("★ 挂上滚动链后：内层到底继续往下滚 → 外层接着往下滚",
                page.VerticalOffset > beforeOuter + 0.5,
                $"外层 {beforeOuter:0.#} → {page.VerticalOffset:0.#}");

            check("★ 而且滚的正好是原生一格（不是只滚一行）",
                Math.Abs(page.VerticalOffset - beforeOuter - notch) < 1.0,
                $"滚了 {page.VerticalOffset - beforeOuter:0.#}px，原生一格={notch:0.#}px");

            check("内层到底时它自己没再动（不会内外一起滚）",
                Math.Abs(inner.VerticalOffset - beforeInner) < 0.5,
                $"{beforeInner:0.#} → {inner.VerticalOffset:0.#}");

            check("★ 事件被我们吃掉了（Handled=true）—— 不然内层会把它吞掉，外层收不到",
                handledAtBottom,
                $"Handled={handledAtBottom}；判断={ScrollChaining.LastDecision}");

            // ---------- 3. 内层还能滚：只滚内层，外层不许动 ----------
            inner.ScrollToTop();
            page.ScrollToTop();
            Settle(window);
            beforeInner = inner.VerticalOffset;
            beforeOuter = page.VerticalOffset;
            RaiseWheel(target, -120, window);

            check("内层还能滚时：内层自己滚了（原生的那一格 = WheelScrollLines 项）",
                inner.VerticalOffset > beforeInner + 0.5,
                $"{beforeInner:0.#} → {inner.VerticalOffset:0.#}（项）");

            check("★ 内层还能滚时：外层一动不动（这一步不归它）",
                Math.Abs(page.VerticalOffset - beforeOuter) < 0.5,
                $"外层 {beforeOuter:0.#} → {page.VerticalOffset:0.#}；判断={ScrollChaining.LastDecision}");

            // ---------- 4. 内层到顶 + 继续往上滚 → 外层往上滚 ----------
            inner.ScrollToTop();
            page.ScrollToVerticalOffset(outerMax / 2);
            Settle(window);
            beforeOuter = page.VerticalOffset;
            RaiseWheel(target, 120, window);

            check("★ 内层到顶继续往上滚 → 外层往上滚",
                page.VerticalOffset < beforeOuter - 0.5,
                $"外层 {beforeOuter:0.#} → {page.VerticalOffset:0.#}");

            // ---------- 5. 内层压根没得滚（空列表 / 只有一行） → 直接归外层 ----------
            var shortList = new ListBox { Height = 60, ItemsSource = new[] { "就一行" } };
            var pageContent2 = new StackPanel();
            for (var i = 0; i < 25; i++) pageContent2.Children.Add(new TextBlock { Text = $"上 {i}", Height = 20 });
            pageContent2.Children.Add(shortList);
            for (var i = 0; i < 25; i++) pageContent2.Children.Add(new TextBlock { Text = $"下 {i}", Height = 20 });

            var page2 = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = pageContent2 };
            window.Content = page2;
            ScrollChaining.Attach(shortList);
            Settle(window);

            var shortInner = FindChild<ScrollViewer>(shortList);
            check("没内容可滚的内层：CanScroll=false（内容比视口矮也算）",
                shortInner is not null && !ScrollChaining.CanScroll(shortInner, true),
                shortInner is null ? "找不到内层 ScrollViewer" : $"上限={Math.Max(0, shortInner.ExtentHeight - shortInner.ViewportHeight):0.#}");

            var shortTarget = (shortInner is not null ? FindChild<ScrollContentPresenter>(shortInner) as UIElement : null)
                              ?? (UIElement?)shortList;
            var page2Before = page2.VerticalOffset;
            if (shortTarget is not null) RaiseWheel(shortTarget, -120, window);

            check("★ 内层没得滚（只有一行）→ 滚轮直接落到外层",
                page2.VerticalOffset > page2Before + 0.5,
                $"外层 {page2Before:0.#} → {page2.VerticalOffset:0.#}");

            // ---------- 6. 内外都到头：谁也不动，也别抢事件 ----------
            var page3Content = new StackPanel();
            var list3 = new ListBox { Height = 80, ItemsSource = Enumerable.Range(0, 30).Select(i => $"游戏 {i}").ToList() };
            page3Content.Children.Add(list3);
            for (var i = 0; i < 30; i++) page3Content.Children.Add(new TextBlock { Text = $"下 {i}", Height = 20 });
            var page3 = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page3Content };
            window.Content = page3;
            ScrollChaining.Attach(list3);
            Settle(window);

            var inner3 = FindChild<ScrollViewer>(list3)!;
            var target3 = FindChild<ScrollContentPresenter>(inner3) as UIElement ?? inner3;
            inner3.ScrollToEnd();
            page3.ScrollToEnd();
            Settle(window);
            var outer3Before = page3.VerticalOffset;
            RaiseWheel(target3, -120, window);

            check("内外都到头时：什么都不做（不抢事件，判断也不是 Outer）",
                Math.Abs(page3.VerticalOffset - outer3Before) < 0.5
                && ScrollChaining.LastDecision.Contains("内外都滚不动", StringComparison.Ordinal),
                $"外层 {outer3Before:0.#} → {page3.VerticalOffset:0.#}；判断={ScrollChaining.LastDecision}");

            // ---------- 7. 纯逻辑：Decide / CanScroll（脱离鼠标也能断言） ----------
            // 先把最初那棵树挂回来（上面的用例换过 Window.Content，旧的树已经脱离布局了）
            window.Content = page;
            Settle(window);
            report("Decide 纯函数断言（下面几条不依赖鼠标）", true);

            string Where() => $"内层 {inner.VerticalOffset:0.#}/{innerMax:0.#}，外层 {page.VerticalOffset:0.#}/{outerMax:0.#}";

            inner.ScrollToVerticalOffset(innerMax / 2);
            Settle(window);
            check("Decide：内层在中间 → Inner",
                ScrollChaining.Decide(inner, page, -120).Action == ScrollChaining.ScrollAction.Inner, Where());

            inner.ScrollToEnd();
            page.ScrollToTop();
            Settle(window);
            check("Decide：内层到底 + 外层还能往下滚 → Outer",
                ScrollChaining.Decide(inner, page, -120).Action == ScrollChaining.ScrollAction.Outer, Where());

            inner.ScrollToTop();
            page.ScrollToVerticalOffset(200);
            Settle(window);
            check("Decide：内层到顶 + 向上滚 → Outer",
                ScrollChaining.Decide(inner, page, 120).Action == ScrollChaining.ScrollAction.Outer, Where());

            inner.ScrollToEnd();          // 先到底，否则"内层还能滚"当然不是 None
            Settle(window);
            check("Decide：外层也是 null → None（滚不动就别抢事件）",
                ScrollChaining.Decide(inner, null, -120).Action == ScrollChaining.ScrollAction.None, Where());

            page.ScrollToTop();
            Settle(window);
            check("Decide：内层为 null + 外层能滚 → Outer（挂在不带滚动条的容器上也管用）",
                ScrollChaining.Decide(null, page, -120).Action == ScrollChaining.ScrollAction.Outer, Where());

            check("CanScroll：空列表 / null 都算滚不动",
                !ScrollChaining.CanScroll(null, true)
                && shortInner is not null && !ScrollChaining.CanScroll(shortInner, false),
                $"shortInner={(shortInner is null ? "(没找到)" : Math.Max(0, shortInner.ExtentHeight - shortInner.ViewportHeight).ToString("0.#"))}");

            inner.ScrollToVerticalOffset(innerMax / 2);
            Settle(window);
            check("CanScroll：能滚的列表在两个方向都算能滚（中间位置）",
                ScrollChaining.CanScroll(inner, true) && ScrollChaining.CanScroll(inner, false), Where());

            // ---------- 8. 附加属性开关 ----------
            var probe = new ListBox { ItemsSource = new[] { "a" } };
            check("默认没挂滚动链", !ScrollChaining.GetEnabled(probe), "GetEnabled 默认 false");
            ScrollChaining.Attach(probe);
            check("Attach 之后开关为 true", ScrollChaining.GetEnabled(probe), "Attach() = SetEnabled(true)");
            ScrollChaining.SetEnabled(probe, false);
            check("可以关掉", !ScrollChaining.GetEnabled(probe), "SetEnabled(false) 会摘掉 PreviewMouseWheel");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 把 ScrollViewer 的 <c>ScrollToXxx</c> 命令彻底过完 layout。
    /// ⚠ 这一步不能省：那些命令是排队在 LayoutUpdated 里执行的，没排空就去发滚轮事件，
    /// 命令会在事件之后生效、把偏移改回去（早期就是因此误判"合成事件滚不动"）。
    /// </summary>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++) window.UpdateLayout();
    }

    private static void ScrollToEnd(ScrollViewer inner, ScrollViewer page, Window window)
    {
        inner.ScrollToEnd();
        page.ScrollToTop();
        Settle(window);
    }

    /// <summary>
    /// 像真实鼠标那样扔一格滚轮：**先隧道（PreviewMouseWheel），没被吃掉再冒泡（MouseWheel）**。
    ///
    /// <para>⚠ 这一步不能省：<c>RaiseEvent</c> 只走你指定的那一个路由事件 —— 直接抛冒泡的
    /// <c>MouseWheelEvent</c>，挂在 <c>PreviewMouseWheel</c> 上的处理器**一次都不会执行**（实测），
    /// 于是测出来的永远是"滚动链没生效"。真实输入是 InputManager 先发 Preview、再发冒泡，
    /// 这里照抄那个顺序。</para>
    /// </summary>
    private static bool RaiseWheel(UIElement target, int delta, Window window)
    {
        static MouseWheelEventArgs Args(UIElement target, int delta, RoutedEvent routedEvent) => new(
            Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = routedEvent,
            Source = target,
        };

        var preview = Args(target, delta, UIElement.PreviewMouseWheelEvent);
        target.RaiseEvent(preview);
        if (preview.Handled)
        {
            Settle(window);
            return true;
        }

        var bubble = Args(target, delta, UIElement.MouseWheelEvent);
        target.RaiseEvent(bubble);
        Settle(window);
        return bubble.Handled;
    }

    private static T? FindChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
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
}
