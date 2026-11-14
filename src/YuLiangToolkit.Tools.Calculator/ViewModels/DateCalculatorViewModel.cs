using System.Collections.ObjectModel;
using System.ComponentModel;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Calculator.Core.Dates;

namespace YuLiangToolkit.Tools.Calculator.ViewModels;

/// <summary>日期计算的几种算法（界面第一栏的切换）。</summary>
public enum DateCalcKind
{
    /// <summary>从 A 到 B 相差多少天。</summary>
    Difference = 0,

    /// <summary>A 加上 / 减去 N 天是哪一天。</summary>
    Offset = 1,

    /// <summary>两个日期之间按年月日拆开（几年几个月几天）＋ 含首尾天数。</summary>
    Breakdown = 2,
}

/// <summary>
/// 日期计算模式（计算器工具的第三个模式）：三种算法 —— 相差天数 / 加减天数 / 年月日拆解。
///
/// <para>纯计算在 <see cref="DateMath"/>（好断言）；这里只管输入、选择、结果文案与配置记忆。
/// 三种算法共用两组输入（起止日期 + 偏移天数），切换算法时不丢已填的内容。</para>
/// </summary>
public sealed class DateCalculatorViewModel : ObservableObject
{
    private readonly CalculatorConfig _config;
    private readonly Func<DateOnly> _today;

    public DateCalculatorViewModel(CalculatorConfig config, Func<DateOnly>? today = null)
    {
        _config = config;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));

        KindOptions = OptionItem.From(
            (DateCalcKind.Difference, "相差天数：从 A 到 B 是多少天"),
            (DateCalcKind.Offset, "加减天数：A 加 / 减 N 天是哪一天"),
            (DateCalcKind.Breakdown, "拆开算：几年几个月几天 + 含首尾天数"));

        UseTodayCommand = new RelayCommand(parameter => UseToday(parameter));
        SwapCommand = new RelayCommand(_ => Swap());
        CopyCommand = new RelayCommand(_ => CopyResult(), _ => ResultText.Length > 0);
        SetKindCommand = new RelayCommand(parameter => SetKind(parameter));

        // 配置里的值先落到输入框（手改坏的日期在下面 Refresh 里会给出中文提示）
        _startText = _config.DateStart;
        _endText = _config.DateEnd;
        _offsetText = _config.DateOffset;

        _selectedKind = OptionItem<DateCalcKind>.Find(KindOptions, ParseKind(_config.DateCalcKind)) ?? KindOptions[0];

        config.PropertyChanged += OnConfigChanged;
        Refresh();
    }

    // ---------- 配置 / 算法 ----------

    /// <summary>三种算法的选项（下拉框：<c>DisplayMemberPath="Label"</c>）。</summary>
    public IReadOnlyList<OptionItem<DateCalcKind>> KindOptions { get; }

    private OptionItem<DateCalcKind> _selectedKind;

    /// <summary>当前算法。</summary>
    public OptionItem<DateCalcKind> SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (value is null || !Set(ref _selectedKind, value)) return;
            _config.DateCalcKind = value.Value.ToString();
            Refresh();
        }
    }

    /// <summary>算法选项变了要让结果立刻跟上（也用于代码里按按钮切）。</summary>
    public RelayCommand SetKindCommand { get; }

    // ---------- 输入 ----------

    private string _startText = "";

    /// <summary>开始日期（相差 / 拆开算用作起点；加减天数里是被计算的那一天）。</summary>
    public string StartText
    {
        get => _startText;
        set
        {
            if (!Set(ref _startText, value ?? "")) return;
            _config.DateStart = StartText;
            Refresh();
        }
    }

    private string _endText = "";

    /// <summary>结束日期（只有「相差 / 拆开算」用得到）。</summary>
    public string EndText
    {
        get => _endText;
        set
        {
            if (!Set(ref _endText, value ?? "")) return;
            _config.DateEnd = EndText;
            Refresh();
        }
    }

    private string _offsetText = "0";

    /// <summary>要加 / 减的天数（可为负）。</summary>
    public string OffsetText
    {
        get => _offsetText;
        set
        {
            if (!Set(ref _offsetText, value ?? "")) return;
            _config.DateOffset = OffsetText;
            Refresh();
        }
    }

    /// <summary>「结束日期」这一栏在当前算法下要不要显示。</summary>
    public bool NeedsEndDate => _selectedKind.Value is DateCalcKind.Difference or DateCalcKind.Breakdown;

    /// <summary>「天数」这一栏在当前算法下要不要显示。</summary>
    public bool NeedsOffset => _selectedKind.Value == DateCalcKind.Offset;

    // ---------- 结果 ----------

    private string _resultText = "";

    /// <summary>大号结果（相差天数 / 目标日期 / 「3 年 2 个月 5 天」）。算不出来时为空。</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (!Set(ref _resultText, value)) return;
            CommandManagerInvalidate();
        }
    }

    private string _resultUnit = "";

    /// <summary>结果后面跟的小字单位（「天」「周五」之类）。</summary>
    public string ResultUnit
    {
        get => _resultUnit;
        private set => Set(ref _resultUnit, value);
    }

    private string _errorText = "";

    /// <summary>中文错误提示（哪一栏填错了说哪一栏），成功时为空。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    /// <summary>结果下面的明细行（每行一句：含首尾天数 / 星期几 / 农历 / 反向差 …）。</summary>
    public ObservableCollection<DateResultLine> Details { get; } = new();

    /// <summary>有没有算成功的明细（空状态提示用）。</summary>
    public bool HasDetails => Details.Count > 0;

    /// <summary>结果卡的标题（「A → B 相差」「A 减 30 天」…）。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    /// <summary>
    /// 结果区为空时显示的一句说明（三种情况：日期没填全 / 填错了 / 已经算好）。
    /// </summary>
    public string PlaceholderText
    {
        get
        {
            if (ErrorText.Length > 0) return "上面那一栏填好之后，这里就会出结果。";

            return _selectedKind.Value switch
            {
                DateCalcKind.Offset => "填好开始日期和加 / 减的天数，这里显示目标日期。",
                DateCalcKind.Breakdown => "填好两个日期，这里显示它们之间是几年几个月几天。",
                _ => "填好开始和结束日期，这里显示相差多少天。",
            };
        }
    }

    private string _summaryText = "";

    // ---------- 命令 ----------

    /// <summary>「今天」按钮：把某一栏填成今天（参数是字段名 start / end）。</summary>
    public RelayCommand UseTodayCommand { get; }

    /// <summary>交换开始 / 结束日期。</summary>
    public RelayCommand SwapCommand { get; }

    /// <summary>复制结果（大号结果 + 明细，一次带走）。</summary>
    public RelayCommand CopyCommand { get; }

    // ---------- 操作 ----------

    private void UseToday(object? parameter)
    {
        var today = _today();
        if (string.Equals(parameter as string, "end", StringComparison.OrdinalIgnoreCase))
            EndText = DateMath.Format(today);
        else
            StartText = DateMath.Format(today);
    }

    private void Swap()
    {
        (StartText, EndText) = (EndText, StartText);
    }

    private void SetKind(object? parameter)
    {
        if (parameter is not OptionItem<DateCalcKind> option) return;
        SelectedKind = option;
    }

    private void CopyResult()
    {
        if (ResultText.Length == 0) return;

        var lines = new List<string> { $"{SummaryText}：{ResultText}{ResultUnit}" };
        lines.AddRange(Details.Select(line => $"{line.Label}：{line.Value}"));
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
            StatusText = "已复制结果";
        }
        catch
        {
            // 剪贴板偶尔被别的程序占住：静默失败，别让工具崩（标准模式也是这么处理的）
            StatusText = "复制失败（剪贴板可能被其它程序占用）";
        }
    }

    private string _statusText = "";

    /// <summary>操作反馈（复制成功 / 换算法等）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // ---------- 计算 ----------

    /// <summary>按当前算法重算一遍，并把结果写进结果卡 / 明细。</summary>
    private void Refresh()
    {
        Details.Clear();
        ErrorText = "";
        ResultText = "";
        ResultUnit = "";
        SummaryText = "";

        OnPropertyChanged(nameof(NeedsEndDate));
        OnPropertyChanged(nameof(NeedsOffset));

        var start = DateMath.Parse(StartText);
        if (!start.Success)
        {
            ErrorText = $"开始日期：{start.Error}";
            Finish();
            return;
        }

        switch (_selectedKind.Value)
        {
            case DateCalcKind.Difference:
                ShowDifference(start.Date);
                break;
            case DateCalcKind.Offset:
                ShowOffset(start.Date);
                break;
            case DateCalcKind.Breakdown:
                ShowBreakdown(start.Date);
                break;
        }

        Finish();
    }

    private void ShowDifference(DateOnly start)
    {
        var end = DateMath.Parse(EndText);
        if (!end.Success)
        {
            ErrorText = $"结束日期：{end.Error}";
            return;
        }

        var diff = DateMath.DiffDays(start, end.Date);
        SummaryText = $"{DateMath.Format(start)} → {DateMath.Format(end.Date)}";

        ResultText = Math.Abs(diff).ToString();
        ResultUnit = "天";

        if (diff == 0)
        {
            DetailsAdd("这两个日期", "是同一天");
        }
        else if (diff > 0)
        {
            DetailsAdd("经过了多少天", $"{diff} 天（不算开始当天，算结束当天）");
            DetailsAdd("倒过来", $"从 {DateMath.Format(end.Date)} 到 {DateMath.Format(start)} 是 {-diff} 天");
        }
        else
        {
            DetailsAdd("顺序", $"结束日期在开始日期之前（结果取绝对值 {-diff} 天）");
            DetailsAdd("按时间先后", $"从 {DateMath.Format(end.Date)} 到 {DateMath.Format(start)} 是 {-diff} 天");
        }

        DetailsAdd("含首尾", $"{Math.Abs(diff) + 1} 天（两头都算）");
        DetailsAdd("开始那天", $"{DateMath.Format(start)} · {DateMath.WeekdayText(start)}");
        DetailsAdd("结束那天", $"{DateMath.Format(end.Date)} · {DateMath.WeekdayText(end.Date)}");
        DetailsAdd("折合", DescribeSpan(diff));
    }

    private void ShowOffset(DateOnly start)
    {
        if (!DateMath.TryParseOffset(OffsetText, out var days, out var error))
        {
            ErrorText = error;
            return;
        }

        SummaryText = days == 0
            ? $"{DateMath.Format(start)} 加 0 天"
            : $"{DateMath.Format(start)} {(days > 0 ? "加" : "减")} {Math.Abs(days)} 天";

        if (!DateMath.TryAddDays(start, days, out var target, out var overflow))
        {
            ErrorText = overflow;
            return;
        }

        ResultText = DateMath.Format(target);
        ResultUnit = DateMath.WeekdayText(target);

        DetailsAdd("目标日期", $"{DateMath.Format(target)}（{DateMath.FormatIso(target)}）");
        DetailsAdd("星期", DateMath.WeekdayText(target));
        DetailsAdd("从起点算", $"相差 {DateMath.DiffDays(start, target)} 天");

        // 顺手给一年的第几天、第几周，查表格 / 报表时常用
        DetailsAdd("一年中的第", $"{target.DayOfYear} 天");
        DetailsAdd("倒过来", $"{DateMath.Format(target)} 减 {days} 天 = {DateMath.Format(start)}");
        DetailsAdd("折合", DescribeSpan(days));
    }

    private void ShowBreakdown(DateOnly start)
    {
        var end = DateMath.Parse(EndText);
        if (!end.Success)
        {
            ErrorText = $"结束日期：{end.Error}";
            return;
        }

        var (from, to) = start <= end.Date ? (start, end.Date) : (end.Date, start);
        var diff = DateMath.DiffDays(from, to);

        SummaryText = $"{DateMath.Format(from)} → {DateMath.Format(to)}";

        // 年月日拆开：一个整月一个整月地往前推，剩下的不足一个月按天算
        var years = 0;
        var months = 0;
        var cursor = from;
        while (AddMonths(cursor, 1) is { } next && next <= to)
        {
            cursor = next;
            months++;
            if (months == 12)
            {
                months = 0;
                years++;
            }
        }

        var days = DateMath.DiffDays(cursor, to);

        ResultText = DescribeYearMonthDay(years, months, days);
        ResultUnit = "";

        DetailsAdd("拆开算", DescribeYearMonthDay(years, months, days));
        DetailsAdd("一共多少天", $"{Math.Abs(diff)} 天");
        DetailsAdd("含首尾", $"{Math.Abs(diff) + 1} 天（两头都算）");
        DetailsAdd("折合", DescribeSpan(diff));
        DetailsAdd("星期", $"{DateMath.WeekdayText(from)} → {DateMath.WeekdayText(to)}");
        if (start > end.Date) DetailsAdd("顺序", "结束日期在开始日期之前（按先后顺序算）");
    }

    /// <summary>
    /// 日期加一个自然月（用于「几年几个月」的拆解）。
    /// 月末对齐：1 月 31 日加一个月得到 2 月 28 / 29 日（不溢出到下个月）。
    /// </summary>
    private static DateOnly? AddMonths(DateOnly date, int months)
    {
        var total = date.Year * 12 + (date.Month - 1) + months;
        var year = total / 12;
        var month = total % 12 + 1;
        if (year is < 1 or > 9999) return null;

        var day = Math.Min(date.Day, DateMath.DaysInMonth(year, month));
        return new DateOnly(year, month, day);
    }

    /// <summary>「3 年 2 个月 5 天」（0 的部分省略）。</summary>
    private static string DescribeYearMonthDay(int years, int months, int days)
    {
        var parts = new List<string>();
        if (years > 0) parts.Add($"{years} 年");
        if (months > 0) parts.Add($"{months} 个月");
        if (days > 0) parts.Add($"{days} 天");
        return parts.Count == 0 ? "同一天" : string.Join(" ", parts);
    }

    /// <summary>「约 3 年 2 个月 5 天」「约 7 周 3 天」「约 5 个月」这种口语折合。</summary>
    private static string DescribeSpan(int diffDays)
    {
        var total = Math.Abs(diffDays);
        if (total == 0) return "0 天";

        if (total < 7) return $"{total} 天";
        if (total < 60) return $"{total / 7} 周 {total % 7} 天（{total} 天）";

        var months = total / 30;
        var remDays = total % 30;
        var years = months / 12;
        var remMonths = months % 12;

        var parts = new List<string>();
        if (years > 0) parts.Add($"{years} 年");
        if (remMonths > 0) parts.Add($"{remMonths} 个月");
        if (remDays > 0) parts.Add($"{remDays} 天");
        return $"约 {string.Join(" ", parts)}（按每月 30 天估）";
    }

    private void DetailsAdd(string label, string value) => Details.Add(new DateResultLine(label, value));

    private void Finish()
    {
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(PlaceholderText));
    }

    /// <summary>有没有可复制的结果。</summary>
    public bool CanCopy => ResultText.Length > 0;

    private void CommandManagerInvalidate()
        => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只有外部（重建配置 / 手改 JSON 后重载）改了才回灌，自己写的不重复处理
        switch (e.PropertyName)
        {
            case nameof(CalculatorConfig.DateStart):
                if (_config.DateStart != StartText) StartText = _config.DateStart;
                break;
            case nameof(CalculatorConfig.DateEnd):
                if (_config.DateEnd != EndText) EndText = _config.DateEnd;
                break;
            case nameof(CalculatorConfig.DateOffset):
                if (_config.DateOffset != OffsetText) OffsetText = _config.DateOffset;
                break;
            case nameof(CalculatorConfig.DateCalcKind):
                var option = OptionItem<DateCalcKind>.Find(KindOptions, ParseKind(_config.DateCalcKind));
                if (option is not null && option != _selectedKind)
                {
                    _selectedKind = option;
                    OnPropertyChanged(nameof(SelectedKind));
                    Refresh();
                }
                break;
        }
    }

    /// <summary>配置里的算法文本 → 枚举（不认识回到相差天数）。</summary>
    public static DateCalcKind ParseKind(string? value)
        => Enum.TryParse<DateCalcKind>(value, ignoreCase: true, out var kind) ? kind : DateCalcKind.Difference;

    /// <summary>配置对象是缓存复用的，切走时要摘事件。</summary>
    public void Detach() => _config.PropertyChanged -= OnConfigChanged;
}

/// <summary>结果卡里的一行明细（左边标题、右边值）。</summary>
/// <param name="Label">明细标题（「含首尾」「星期」）。</param>
/// <param name="Value">明细内容。</param>
public sealed record DateResultLine(string Label, string Value)
{
    /// <summary>方便 XAML 里直接显示成「标题：内容」。</summary>
    public string Display => $"{Label}：{Value}";
}
