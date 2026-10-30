using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using YuLiangToolkit.App.Views;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 主页标签墙的面板 <see cref="FixedRowsPanel"/> 纯布局自检（v0.33.0：3 行 + 横向滚动）。
///
/// <para>被测的是「按行紧密排列 / 行与行不对齐 / 每行多宽 / 行高」这套算法，跟主题、Application、
/// ItemsControl 都无关：在 STA 线程里直接造面板 + 固定尺寸子元素，Measure / Arrange 一遍再读
/// 每项的布局槽位断言。主页 XAML 用的就是默认 3 行，所以这个面板排错 = 标签墙会歪 / 挤 / 对齐错。</para>
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

        // ---------- 2. 7 个固定尺寸标签（20×10 + Margin 0,0,8,8 = 占位 28×18）→ 3 行，尺寸 = 最大行宽 × 行高之和 ----------
        var panel = BuildPanel(rows: 3, margins: true,
            (20, 10), (20, 10), (20, 10), (20, 10), (20, 10), (20, 10), (20, 10));
        var size = MeasureAndArrange(panel);
        check("7 个标签按 3 行排：宽度取最宽那行（84）、高度 = 三行行高之和（54）",
            Math.Abs(size.Width - 84) < 0.01 && Math.Abs(size.Height - 54) < 0.01,
            $"{size.Width}×{size.Height}");

        // ---------- 3. 行内一个挨一个紧密排；换行后回到最左边（不按列对齐） ----------
        // 7 个等宽标签分到 3 行（等代价的最优解不止一种 → 只断言布局不变式，不锁死具体分行）
        var slots = Slots(panel);
        var rows = slots.GroupBy(slot => Math.Round(slot.Y, 3)).OrderBy(group => group.Key).ToList();
        var tight = rows.All(row =>
        {
            var items = row.OrderBy(slot => slot.X).ToList();
            if (Math.Abs(items[0].X) > 0.01) return false;                              // 每行都从最左边开始
            for (var i = 1; i < items.Count; i++)
            {
                // 行内紧挨着（每个标签占位 28 宽）
                if (Math.Abs(items[i].X - (items[i - 1].X + 28)) > 0.01) return false;
            }
            return true;
        });
        check("行内左右紧挨（步长 28）、每行都从最左边重新开始（不按列对齐）",
            rows.Count == 3 && rows.Sum(row => row.Count()) == 7 && tight
            && Math.Abs(rows[0].Key) < 0.01
            && Math.Abs(rows[1].Key - 18) < 0.01
            && Math.Abs(rows[2].Key - 36) < 0.01,
            Describe(slots));

        // ---------- 4. 宽度不同的标签：按「离目标行宽最近」分到两行（10+50 | 20+30），短标签不被拉宽 ----------
        var mixed = BuildPanel(rows: 2, margins: false,
            (10, 10), (50, 10), (20, 10), (30, 10));
        var mixedSize = MeasureAndArrange(mixed);
        var mixedSlots = Slots(mixed);
        check("两行尽量均匀（60 | 50），短标签保持自然宽度（10 而不是 50）",
            Math.Abs(mixedSize.Width - 60) < 0.01
            && Math.Abs(mixedSlots[0].Width - 10) < 0.01
            && Math.Abs(mixedSlots[1].X - 10) < 0.01 && Math.Abs(mixedSlots[1].Width - 50) < 0.01
            && Math.Abs(mixedSlots[2].X) < 0.01 && Math.Abs(mixedSlots[2].Y - 10) < 0.01,
            $"desired={mixedSize.Width}×{mixedSize.Height}, " + Describe(mixedSlots));

        // ---------- 5. 行高各算各的：第一行最高 30、第二行最高 60（上下不要求对齐） ----------
        var tallRows = BuildPanel(rows: 2, margins: false,
            (10, 10), (10, 20), (10, 30), (10, 40), (10, 50), (10, 60));
        var tallSize = MeasureAndArrange(tallRows);
        var tallSlots = Slots(tallRows);
        check("行高取本行最高标签：总高 30+60=90，第二行从 y=30 开始（按行排、不按列对齐）",
            Math.Abs(tallSize.Height - 90) < 0.01
            && Math.Abs(tallSlots[0].Y) < 0.01 && Math.Abs(tallSlots[2].Y) < 0.01
            && Math.Abs(tallSlots[3].Y - 30) < 0.01,
            $"desired={tallSize.Width}×{tallSize.Height}, " + Describe(tallSlots));

        // ---------- 6. Rows=1 = 一行横排；Rows=0 被依赖属性校验拒绝 ----------
        var oneRow = BuildPanel(rows: 1, margins: false, (10, 10), (20, 10), (30, 10));
        var oneRowSize = MeasureAndArrange(oneRow);
        var oneRowSlots = Slots(oneRow);
        var rejected = false;
        try { new FixedRowsPanel().Rows = 0; }
        catch (ArgumentException) { rejected = true; }

        check("Rows=1 排成一行（宽 60、高 10）；Rows=0 被拒绝（抛 ArgumentException）",
            Math.Abs(oneRowSize.Width - 60) < 0.01 && Math.Abs(oneRowSize.Height - 10) < 0.01
            && oneRowSlots.All(slot => Math.Abs(slot.Y) < 0.01)
            && Math.Abs(oneRowSlots[2].X - 30) < 0.01
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

    private static string Describe(IReadOnlyList<Rect> slots)
        => string.Join(" ", slots.Select((slot, index) => $"[{index}]({slot.X:0.#},{slot.Y:0.#})"));
}
