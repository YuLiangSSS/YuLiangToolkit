using System.Globalization;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Shell;

namespace YuLiangToolkit.Tools.Calendar.ViewModels;

/// <summary>
/// 日历页的 ViewModel：月视图（42 格）+ 上/下月 + 今天 + 选中日 + 键盘移动。
///
/// <para>日期计算全在 <see cref="CalendarMath"/>（纯逻辑，自检可断言），这里只管状态与绑定。</para>
/// </summary>
public sealed class CalendarViewModel : ToolViewModelBase
{
    /// <summary>格子里的一个日期（界面绑定用）。</summary>
    public sealed class DayCell
    {
        /// <summary>这一格是不是空白（关掉「显示上/下月日期」时用）。</summary>
        public bool IsBlank { get; init; }

        /// <summary>日期（空白格为 default）。</summary>
        public DateTime Date { get; init; }

        /// <summary>日期数字文字（空白格为空串）。</summary>
        public string DayText { get; init; } = "";

        /// <summary>是不是本月的日子。</summary>
        public bool IsCurrentMonth { get; init; }

        /// <summary>是不是今天。</summary>
        public bool IsToday { get; init; }

        /// <summary>是不是选中的那天。</summary>
        public bool IsSelected { get; init; }

        /// <summary>是不是周末。</summary>
        public bool IsWeekend { get; init; }

        /// <summary>格子背景色资源名（今天 = 强调色，选中 = 半透明强调色）。</summary>
        public string BackgroundKey => IsSelected ? "AccentBrush"
            : IsToday ? "AccentHoverBrush"
            : "Transparent";

        /// <summary>文字颜色资源名。</summary>
        public string ForegroundKey => IsSelected || IsToday ? "TextPrimaryBrush"
            : !IsCurrentMonth ? "FaintTextBrush"
            : IsWeekend ? "AccentBrush"
            : "TextPrimaryBrush";

        /// <summary>悬停提示。</summary>
        public string ToolTip => IsBlank ? "" : CalendarMath.DayTitle(Date);
    }

    private readonly DispatcherTimer _todayTimer;

    public CalendarViewModel() : base(CalendarConfig.ToolId)
    {
        Cfg = LoadConfig<CalendarConfig>();

        PreviousMonthCommand = new RelayCommand(_ => GoPreviousMonth());
        NextMonthCommand = new RelayCommand(_ => GoNextMonth());
        TodayCommand = new RelayCommand(_ => GoToday());

        var today = DateTime.Today;
        _displayedMonth = CalendarMath.FirstOfMonth(today);
        _selectedDate = today;

        // 跨过零点时「今天」要跟着变（每分钟看一次，够用且几乎不耗 CPU）
        _todayTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _todayTimer.Tick += (_, _) =>
        {
            if (_today != DateTime.Today) Rebuild();
        };

        Cfg.PropertyChanged += (_, _) => Rebuild();
        Rebuild();

        Log($"配置文件：{Cfg.FilePath}");
    }

    /// <summary>强类型配置。</summary>
    public CalendarConfig Cfg { get; }

    // ---------- 命令 ----------

    public RelayCommand PreviousMonthCommand { get; }
    public RelayCommand NextMonthCommand { get; }
    public RelayCommand TodayCommand { get; }

    // ---------- 状态 ----------

    private DateTime _displayedMonth;
    private DateTime _selectedDate;

    /// <summary>当前显示的月份（1 号）。</summary>
    public DateTime DisplayedMonth
    {
        get => _displayedMonth;
        private set
        {
            if (Set(ref _displayedMonth, value)) OnPropertyChanged(nameof(MonthTitle));
        }
    }

    /// <summary>月份标题（<c>2026年11月</c>）。</summary>
    public string MonthTitle => CalendarMath.MonthTitle(_displayedMonth);

    /// <summary>当前选中的日期。</summary>
    public DateTime SelectedDate
    {
        get => _selectedDate;
        private set
        {
            if (Set(ref _selectedDate, value)) OnPropertyChanged(nameof(SelectedDateText));
        }
    }

    /// <summary>选中日期的完整文字。</summary>
    public string SelectedDateText => CalendarMath.DayTitle(_selectedDate);

    private DateTime _today = DateTime.Today;

    /// <summary>「今天」（每分钟跟一次）。</summary>
    public DateTime Today => _today;

    /// <summary>42 个格子（界面直接绑）。</summary>
    public IReadOnlyList<DayCell> Cells { get; private set; } = Array.Empty<DayCell>();

    /// <summary>星期表头（按「每周第一天」排好的 7 个短名）。</summary>
    public IReadOnlyList<string> WeekHeader { get; private set; } = Array.Empty<string>();

    // ---------- 操作 ----------

    /// <summary>上一个月。</summary>
    public void GoPreviousMonth()
    {
        DisplayedMonth = CalendarMath.PreviousMonth(_displayedMonth);
        Rebuild();
    }

    /// <summary>下一个月。</summary>
    public void GoNextMonth()
    {
        DisplayedMonth = CalendarMath.NextMonth(_displayedMonth);
        Rebuild();
    }

    /// <summary>回到今天（月份也切回当月）。</summary>
    public void GoToday()
    {
        _today = DateTime.Today;
        SelectedDate = _today;
        DisplayedMonth = CalendarMath.FirstOfMonth(_today);
        Rebuild();
        StatusText = "已回到今天";
    }

    /// <summary>选中某一天（同时把月份切到该月，键盘跨月移动时用）。</summary>
    public void Select(DateTime date)
    {
        SelectedDate = date;
        var month = CalendarMath.FirstOfMonth(date);
        if (month != _displayedMonth) DisplayedMonth = month;

        Rebuild();
    }

    /// <summary>键盘移动。</summary>
    public void MoveSelection(CalendarMove move) => Select(CalendarMath.Move(_selectedDate, move));

    /// <summary>重新算格子与表头。</summary>
    public void Rebuild()
    {
        _today = DateTime.Today;

        var firstDay = Cfg.FirstDayOfWeek;
        WeekHeader = CalendarMath.WeekOrder(firstDay)
            .Select(CalendarMath.WeekdayShort)
            .ToList();

        var cells = CalendarMath.BuildMonthCells(_displayedMonth, firstDay);
        Cells = cells.Select(cell => new DayCell
        {
            IsBlank = !cell.IsCurrentMonth && !Cfg.ShowDaysOfOtherMonths,
            Date = cell.Date,
            DayText = !cell.IsCurrentMonth && !Cfg.ShowDaysOfOtherMonths
                ? ""
                : cell.Date.Day.ToString(CultureInfo.CurrentCulture),
            IsCurrentMonth = cell.IsCurrentMonth,
            IsToday = cell.Date == _today,
            IsSelected = cell.Date == _selectedDate,
            IsWeekend = Cfg.HighlightWeekend && CalendarMath.IsWeekend(cell.Date),
        }).ToList();

        OnPropertyChanged(nameof(Cells));
        OnPropertyChanged(nameof(WeekHeader));
        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(Today));
        OnPropertyChanged(nameof(SelectedDateText));
    }

    /// <summary>页面显示出来：开始跟「今天」。</summary>
    public void OnPageLoaded() => _todayTimer.Start();

    /// <summary>页面切走：停止跟「今天」（回来会重算）。</summary>
    public void OnPageUnloaded()
    {
        _todayTimer.Stop();
        Rebuild();
    }

    /// <summary>工具被切走 / 关闭。</summary>
    protected override void OnClosed() => _todayTimer.Stop();
}
