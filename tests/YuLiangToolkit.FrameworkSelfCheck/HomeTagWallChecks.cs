using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using YuLiangToolkit.App.Views;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 主页标签墙的面板 <see cref="FixedRowsPanel"/> 纯布局自检（v0.33.0：3 行 + 横向滚动）。
///
/// <para>被测的是「列宽 / 行高 / 换列位置」这套排列算法，跟主题、Application、ItemsControl 都无关：
/// 在 STA 线程里直接造面板 + 固定尺寸子元素，Measure / Arrange 一遍再读每项的布局槽位断言。
/// 主页 XAML 用的就是默认 3 行，所以这个面板排错 = 标签墙会歪 / 挤 / 多一行少一行。</para>
/// </summary>
internal static class HomeTagWallChecks
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
        _ = report;   // 本组断言都带明细

        // ---------- 1. 默认 3 行 + 空面板不崩 ----------
        var defaultPanel = new FixedRowsPanel();
        var emptySize = MeasureAndArrange(defaultPanel);
        check("默认 3 行，空面板尺寸是 0×0（没有标签时不占位置）",
            defaultPanel.Rows == 3 && emptySize is { Width: 0, Height: 0 },
            $"Rows={defaultPanel.Rows}, {emptySize.Width}×{emptySize.Height}");

        // ---------- 2. 7 个固定尺寸标签 + 3 行 → 3 列，尺寸 = 列宽和 × 行高和 ----------
        // 每个子元素 20×10 + Margin(0,0,8,8) → 占位 28×18（Margin 也算进测量，就是标签墙的行列间距）。
        var panel = BuildPanel(rows: 3, margins: true,
            (20, 10), (20, 10), (20, 10), (20, 10), (20, 10), (20, 10), (20, 10));
        var size = MeasureAndArrange(panel);
        check("7 个标签按 3 行排成 3 列（3×28 宽、3×18 高）",
            Math.Abs(size.Width - 84) < 0.01 && Math.Abs(size.Height - 54) < 0.01,
            $"{size.Width}×{size.Height}");

        // ---------- 3. 竖着填：同一列 x 相同、y 依次递增；满 3 个换到下一列并从顶部开始 ----------
        var slots = Slots(panel);
        check("第一列从上往下排，第 4 个换到下一列顶部",
            SameX(slots[0], slots[1], slots[2])
            && slots[0].Y < slots[1].Y && slots[1].Y < slots[2].Y
            && slots[3].X > slots[2].X && Math.Abs(slots[3].Y) < 0.01
            && SameX(slots[3], slots[4], slots[5])
            && slots[6].X > slots[5].X && Math.Abs(slots[6].Y) < 0.01,
            Describe(slots));

        // ---------- 4. 列宽取该列最宽；短标签保持自己的宽度、不被拉成等宽格子 ----------
        var mixed = BuildPanel(rows: 2, margins: false,
            (10, 10), (50, 10), (20, 10), (30, 10));
        var mixedSize = MeasureAndArrange(mixed);
        var mixedSlots = Slots(mixed);
        check("列宽 = 该列最宽标签（2 列 50+30），短标签仍是自然宽度（10 而不是 50）",
            Math.Abs(mixedSize.Width - 80) < 0.01
            && Math.Abs(mixedSlots[0].Width - 10) < 0.01
            && Math.Abs(mixedSlots[1].Width - 50) < 0.01
            && Math.Abs(mixedSlots[2].X - 50) < 0.01,
            $"desired={mixedSize.Width}×{mixedSize.Height}, " + Describe(mixedSlots));

        // ---------- 5. 行高取该行最高的那个：跨列的行必须在同一条线上（不会各排各的） ----------
        var tallRows = BuildPanel(rows: 2, margins: false,
            (10, 10), (10, 20), (10, 30), (10, 40), (10, 50), (10, 60));
        var tallSize = MeasureAndArrange(tallRows);
        var tallSlots = Slots(tallRows);
        // 第 0 行最高 50、第 1 行最高 60 → 总高 110；第 2 / 6 项（换列后的第 1 行）y 都从 50 开始
        check("行高取该行最高标签，跨列的第 1 行都从 y=50 开始（总高 110）",
            Math.Abs(tallSize.Height - 110) < 0.01
            && Math.Abs(tallSlots[1].Y - 50) < 0.01
            && Math.Abs(tallSlots[3].Y - 50) < 0.01
            && Math.Abs(tallSlots[5].Y - 50) < 0.01,
            $"desired={tallSize.Width}×{tallSize.Height}, " + Describe(tallSlots));

        // ---------- 6. Rows=1 = 一行横排；Rows=0 被依赖属性校验拒绝 ----------
        var oneRow = BuildPanel(rows: 1, margins: false, (10, 10), (20, 10), (30, 10));
        var oneRowSize = MeasureAndArrange(oneRow);
        var oneRowSlots = Slots(oneRow);
        var rejected = false;
        try { new FixedRowsPanel().Rows = 0; }
        catch (ArgumentException) { rejected = true; }

        check("Rows=1 排成一行（高度 = 单个标签高）；Rows=0 被拒绝（抛 ArgumentException）",
            Math.Abs(oneRowSize.Width - 60) < 0.01 && Math.Abs(oneRowSize.Height - 10) < 0.01
            && oneRowSlots.All(slot => Math.Abs(slot.Y) < 0.01)
            && rejected,
            $"desired={oneRowSize.Width}×{oneRowSize.Height}, Rows=0 被拒绝={rejected}");
    }

    /// <summary>造一个面板，按顺序塞入固定尺寸的子元素（<paramref name="margins"/> = 标签墙的 0,0,8,8 间距）。</summary>
    private static FixedRowsPanel BuildPanel(int rows, bool margins, params (double Width, double Height)[] sizes)
    {
        var panel = new FixedRowsPanel { Rows = rows };
        foreach (var (width, height) in sizes)
        {
            panel.Children.Add(new Border
            {
                Width = width,
                Height = height,
                Margin = margins ? new Thickness(0, 0, 8, 8) : new Thickness(0),
            });
        }
        return panel;
    }

    /// <summary>量 + 排一遍（不接窗口也能拿到子元素的布局槽位），返回面板的期望尺寸。</summary>
    private static Size MeasureAndArrange(FixedRowsPanel panel)
    {
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        return panel.DesiredSize;
    }

    private static List<Rect> Slots(FixedRowsPanel panel)
        => panel.Children.Cast<FrameworkElement>().Select(LayoutInformation.GetLayoutSlot).ToList();

    private static bool SameX(params Rect[] rects)
        => rects.All(rect => Math.Abs(rect.X - rects[0].X) < 0.01);

    private static string Describe(IReadOnlyList<Rect> slots)
        => string.Join(" ", slots.Select((slot, index) => $"[{index}]({slot.X:0.#},{slot.Y:0.#})"));
}
