using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 标题栏点出来的小日历（内容 + 交互）。对标 Windows 任务栏右下角那个：
/// 月视图、上/下月、回今天、点某天选中。
///
/// <para><b>它自己算日期</b>：不依赖日历工具的配置（那是另一个工具），
/// 只按本机日期显示月历 —— 用户点标题栏想看的就是「今天几号」。</para>
///
/// <para>外壳窗口负责「弹出 / 点外部关闭 / Esc 关闭」，这里只管画与切换月份。</para>
/// </summary>
public partial class MiniCalendarPanel : UserControl
{
    /// <summary>每周第一天（默认周一，和日历工具保持一致）。</summary>
    private readonly DayOfWeek _firstDayOfWeek = DayOfWeek.Monday;

    private DateTime _month;      // 当前显示的月（取 1 号）
    private DateTime _selected;   // 选中的那一天

    /// <summary>选中某一天（点格子时触发）。</summary>
    public event EventHandler<DateTime>? DaySelected;

    /// <summary>构造：默认显示本月、选中今天。</summary>
    public MiniCalendarPanel()
    {
        InitializeComponent();

        var today = DateTime.Today;
        _month = new DateTime(today.Year, today.Month, 1);
        _selected = today;

        BuildWeekdayHeader();
        Render();
    }

    /// <summary>当前显示的月份（1 号）。</summary>
    public DateTime DisplayedMonth => _month;

    /// <summary>当前选中的日期。</summary>
    public DateTime SelectedDate => _selected;

    /// <summary>重新渲染（外部改了「今天」之后可以调）。</summary>
    public void Render()
    {
        MonthTitle.Text = _month.ToString("yyyy年M月", CultureInfo.CurrentCulture);

        var cells = CalendarMath.BuildMonthCells(_month, _firstDayOfWeek);
        DayGrid.Children.Clear();

        foreach (var cell in cells)
        {
            DayGrid.Children.Add(CreateDayButton(cell));
        }

        TodayText.Text = $"今天 {DateTime.Today:M月d日} {Weekday(DateTime.Today.DayOfWeek)}";
    }

    /// <summary>星期表头（按 <see cref="_firstDayOfWeek"/> 排）。</summary>
    private void BuildWeekdayHeader()
    {
        WeekdayHeader.Children.Clear();
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)_firstDayOfWeek + i) % 7);
            var isWeekend = day is DayOfWeek.Saturday or DayOfWeek.Sunday;
            WeekdayHeader.Children.Add(new TextBlock
            {
                Text = Weekday(day),
                Style = (Style)FindResource("WeekdayHeaderStyle"),
                Foreground = isWeekend
                    ? (Brush)FindResource("AccentBrush")
                    : (Brush)FindResource("TextSecondaryBrush"),
            });
        }
    }

    /// <summary>一个日期格：今天有圆底、非本月变淡、周末用强调色。</summary>
    private Button CreateDayButton(CalendarMath.CalendarCell cell)
    {
        var isToday = cell.Date == DateTime.Today;
        var isSelected = cell.Date == _selected.Date;
        var isWeekend = cell.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        var text = new TextBlock
        {
            Text = cell.Date.Day.ToString(CultureInfo.CurrentCulture),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isToday || isSelected
                ? Brushes.White
                : cell.IsCurrentMonth
                    ? (isWeekend ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextPrimaryBrush"))
                    : (Brush)FindResource("FaintTextBrush"),
        };

        var content = new Border
        {
            Width = 30,
            Height = 26,
            CornerRadius = new CornerRadius(4),
            Child = text,
            Background = isSelected
                ? (Brush)FindResource("AccentBrush")
                : isToday
                    ? (Brush)FindResource("AccentHoverBrush")
                    : Brushes.Transparent,
        };

        var button = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Tag = cell.Date,
            ToolTip = cell.Date.ToString("yyyy年M月d日", CultureInfo.CurrentCulture) + " " + Weekday(cell.Date.DayOfWeek),
        };

        // 悬停时给个浅底（用 Style 触发器写太啰嗦，这里直接挂事件）
        button.MouseEnter += (_, _) =>
        {
            if (isSelected) return;
            content.Background = (Brush)FindResource("CardHoverBackgroundBrush");
        };
        button.MouseLeave += (_, _) =>
        {
            if (isSelected) return;
            content.Background = isToday
                ? (Brush)FindResource("AccentHoverBrush")
                : Brushes.Transparent;
        };

        button.Click += (_, _) =>
        {
            _selected = cell.Date;
            Render();
            DaySelected?.Invoke(this, cell.Date);
        };

        return button;
    }

    /// <summary>农历？不做。这里只是星期几的短名。</summary>
    internal static string Weekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日",
    };

    /// <summary>上一个月。</summary>
    private void OnPrevMonthClick(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        Render();
    }

    /// <summary>下一个月。</summary>
    private void OnNextMonthClick(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        Render();
    }

    /// <summary>回到今天（并把月份切回当月）。</summary>
    private void OnTodayClick(object sender, RoutedEventArgs e)
    {
        _selected = DateTime.Today;
        _month = new DateTime(_selected.Year, _selected.Month, 1);
        Render();
    }
}

/// <summary>
/// 月历格子计算（**纯逻辑**，自检直接断言）：给定月份与每周第一天，算出 42 个格子。
///
/// <para>42 = 6 行 × 7 列，固定高度（界面不会因为月份长短跳动）。</para>
/// </summary>
internal static class CalendarMath
{
    /// <summary>一个月历格子。</summary>
    /// <param name="Date">这一格是哪天。</param>
    /// <param name="IsCurrentMonth">是不是当前显示的月份（否则是上/下月补位）。</param>
    internal readonly record struct CalendarCell(DateTime Date, bool IsCurrentMonth);

    /// <summary>格子总数（6 行 × 7 列）。</summary>
    internal const int CellCount = 42;

    /// <summary>
    /// 算出一个月的 42 个格子。
    /// </summary>
    /// <param name="month">要显示的月份（任意一天都会归到该月 1 号）。</param>
    /// <param name="firstDayOfWeek">每周第一天。</param>
    internal static List<CalendarCell> BuildMonthCells(DateTime month, DayOfWeek firstDayOfWeek)
    {
        var first = new DateTime(month.Year, month.Month, 1);

        // 1 号之前要补几格：把「星期几」换算成距离每周第一天的偏移
        var offset = ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = first.AddDays(-offset);

        var cells = new List<CalendarCell>(CellCount);
        for (var i = 0; i < CellCount; i++)
        {
            var date = start.AddDays(i);
            cells.Add(new CalendarCell(date, date.Month == first.Month && date.Year == first.Year));
        }

        return cells;
    }
}
