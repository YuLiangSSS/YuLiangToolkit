using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 滚轮「滚动链」的判定逻辑（<see cref="ScrollChaining.Decide"/>）。
///
/// <para>这里用**真的 <see cref="ScrollViewer"/>**（塞进临时窗口量一次布局），不是造几个假对象 ——
/// “能不能滚”这件事完全取决于 <c>ExtentHeight / ViewportHeight / VerticalOffset</c> 三个真实值，
/// 用假的就测不到点子上了。</para>
///
/// <para>⚠ 为什么不去模拟鼠标滚轮事件：WPF 的 <c>ScrollViewer.OnMouseWheel</c> 会读**真实鼠标位置**，
/// 鼠标不在控件上就直接返回；用 <c>RaiseEvent</c> 造事件根本走不到 ListBox（实测 0 次触发），
/// 拿它做断言只会得出错误结论。所以这里只断言"该滚谁"这一步纯逻辑，鼠标那一层交给人工验证。</para>
/// </summary>
internal static class ScrollChainingChecks
{
    /// <summary>
    /// 在**自己的 STA 线程**上跑：自检程序的入口是 MTA（顶层的 top-level statements 默认 MTA），
    /// 而 WPF 的 UI 组件（连 <c>InputManager</c> 都是）要求 STA，直接在主线程 new Window 会抛
    /// “调用线程必须为 STA”。这里起一个 STA 线程跑完再把断言结果搬回来。
    /// </summary>
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
            Width = 320,
            Height = 200,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Left = -4000,          // 挪到屏幕外，别闪
            Top = -4000,
        };

        try
        {
            var inner = new ScrollViewer { Height = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var filler = new StackPanel();
            for (var i = 0; i < 20; i++) filler.Children.Add(new TextBlock { Text = $"行 {i}", Height = 20 });
            inner.Content = filler;

            var outer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var outerFiller = new StackPanel();
            for (var i = 0; i < 40; i++) outerFiller.Children.Add(new TextBlock { Text = $"外层 {i}", Height = 20 });
            outerFiller.Children.Add(inner);
            outer.Content = outerFiller;

            window.Content = outer;
            window.Show();
            window.UpdateLayout();

            var innerMax = Math.Max(0, inner.ExtentHeight - inner.ViewportHeight);
            var outerMax = Math.Max(0, outer.ExtentHeight - outer.ViewportHeight);

            check("滚动链测试环境：内层真的能滚（内容比视口高）",
                innerMax > 0 && inner.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                $"内容={inner.ExtentHeight:0.#} 视口={inner.ViewportHeight:0.#} 上限={innerMax:0.#}");
            check("滚动链测试环境：外层也真的能滚",
                outerMax > 0, $"上限={outerMax:0.#}");

            // ---- 内层不在边界：滚轮给内层 ----
            inner.ScrollToVerticalOffset(innerMax / 2);
            window.UpdateLayout();
            report("内层在中间时：向下滚 → 内层",
                ScrollChaining.Decide(inner, outer, -120).Action == ScrollChaining.ScrollAction.Inner);
            report("内层在中间时：向上滚 → 内层",
                ScrollChaining.Decide(inner, outer, 120).Action == ScrollChaining.ScrollAction.Inner);

            // ---- 内层到底：继续往下应该转给外层 ----
            inner.ScrollToEnd();
            window.UpdateLayout();
            report("内层到底时：向下滚 → 外层（这就是用户要的“不要卡住”）",
                ScrollChaining.Decide(inner, outer, -120).Action == ScrollChaining.ScrollAction.Outer);
            report("内层到底时：向上滚 → 还是内层（先把自己滚回去）",
                ScrollChaining.Decide(inner, outer, 120).Action == ScrollChaining.ScrollAction.Inner);

            // ---- 内层到顶：继续往上应该转给外层 ----
            inner.ScrollToTop();
            window.UpdateLayout();
            report("内层到顶时：向上滚 → 外层",
                ScrollChaining.Decide(inner, outer, 120).Action == ScrollChaining.ScrollAction.Outer);
            report("内层到顶时：向下滚 → 还是内层",
                ScrollChaining.Decide(inner, outer, -120).Action == ScrollChaining.ScrollAction.Inner);

            // ---- 内层压根不需要滚动条（内容比视口矮）----
            var shortInner = new ScrollViewer { Height = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            shortInner.Content = new TextBlock { Text = "就一行", Height = 20 };
            var outer2 = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var outer2Filler = new StackPanel();
            for (var i = 0; i < 40; i++) outer2Filler.Children.Add(new TextBlock { Text = $"外层 {i}", Height = 20 });
            outer2Filler.Children.Add(shortInner);
            outer2.Content = outer2Filler;

            var window2 = new Window
            {
                Width = 320, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Left = -4000, Top = -4000, Content = outer2,
            };
            try
            {
                window2.Show();
                window2.UpdateLayout();
                report("内层没内容可滚时：滚轮直接给外层（上下都算）",
                    ScrollChaining.Decide(shortInner, outer2, -120).Action == ScrollChaining.ScrollAction.Outer
                    && ScrollChaining.Decide(shortInner, outer2, 120).Action == ScrollChaining.ScrollAction.Outer);
            }
            finally
            {
                window2.Close();
            }

            // ---- 边界情况：没有外层 / 没有内层 ----
            report("没有外层时不会乱滚（返回 None，让事件自然冒泡）",
                ScrollChaining.Decide(null, null, -120).Action == ScrollChaining.ScrollAction.None);
            report("只有外层、没有内层时：直接滚外层",
                ScrollChaining.Decide(null, outer, -120).Action == ScrollChaining.ScrollAction.Outer);
            report("滚轮增量为 0 时不滚（Decide 仍给出方向，但调用方会先拦掉 delta==0）",
                ScrollChaining.Decide(inner, outer, 0).Down == false);

            // ---- 方向判定 ----
            report("负增量 = 向下、正增量 = 向上",
                ScrollChaining.Decide(inner, outer, -120).Down && !ScrollChaining.Decide(inner, outer, 120).Down);

            // ---- 附加属性开关 ----
            var probe = new ListBox();
            report("默认没挂滚动链", !ScrollChaining.GetEnabled(probe));
            ScrollChaining.Attach(probe);
            report("Attach 之后开关为 true（XAML 里写 ScrollChaining.Enabled 也是走这里）",
                ScrollChaining.GetEnabled(probe));
            ScrollChaining.SetEnabled(probe, false);
            report("可以关掉（重复开关不会重复订阅）", !ScrollChaining.GetEnabled(probe));
        }
        finally
        {
            window.Close();
        }
    }
}
