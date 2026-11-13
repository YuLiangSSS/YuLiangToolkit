using System.Globalization;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Alarm;

/// <summary>一个闹钟。</summary>
public sealed class AlarmEntry : ObservableObject
{
    private string _time = "07:00";

    /// <summary>响铃时间（<c>HH:mm</c>，24 小时制）。</summary>
    public string Time
    {
        get => _time;
        set
        {
            if (!Set(ref _time, NormalizeTime(value))) return;
            OnPropertyChanged(nameof(DisplayTime));
            OnPropertyChanged(nameof(TimeOfDay));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private string _label = "";

    /// <summary>备注（「起床」「开会」…）。</summary>
    public string Label
    {
        get => _label;
        set
        {
            if (!Set(ref _label, value ?? "")) return;
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    private bool _enabled = true;

    /// <summary>启用（关掉就不会响）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(StateText));
        }
    }

    private string _days = "";

    /// <summary>
    /// 重复的星期：空 = 只响一次（响过自动关掉）；否则是 <c>1,2,3,4,5</c> 这种（周一 = 1 … 周日 = 7）。
    /// </summary>
    public string Days
    {
        get => _days;
        set
        {
            if (!Set(ref _days, NormalizeDays(value))) return;
            OnPropertyChanged(nameof(RepeatText));
            OnPropertyChanged(nameof(SummaryText));
            foreach (var name in DayFlags) OnPropertyChanged(name);
        }
    }

    private bool _snoozeEnabled = true;

    /// <summary>响铃时可以「贪睡」（推迟 5 分钟再响）。</summary>
    public bool SnoozeEnabled
    {
        get => _snoozeEnabled;
        set => Set(ref _snoozeEnabled, value);
    }

    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>稳定 Id（贪睡 / 定位用）。</summary>
    public string Id
    {
        get => _id;
        set => Set(ref _id, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
    }

    // ---------- 计算属性 ----------

    /// <summary>显示时间（把 <c>7:5</c> 规范成 <c>07:05</c>）。</summary>
    [JsonIgnore]
    public string DisplayTime => Time;

    /// <summary>时刻。</summary>
    [JsonIgnore]
    public TimeSpan TimeOfDay
        => TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToTimeSpan()
            : TimeSpan.Zero;

    /// <summary>星期几集合（0 = 周日 … 6 = 周六，与 <see cref="DayOfWeek"/> 一致）。</summary>
    [JsonIgnore]
    public IReadOnlyList<DayOfWeek> RepeatDays => AlarmSchedule.ParseDays(Days);

    /// <summary>是不是只响一次。</summary>
    [JsonIgnore]
    public bool IsOneShot => RepeatDays.Count == 0;

    /// <summary>重复说明（「只响一次」/「每天」/「周一、周三」）。</summary>
    [JsonIgnore]
    public string RepeatText
    {
        get
        {
            var days = RepeatDays;
            if (days.Count == 0) return "只响一次";
            if (days.Count == 7) return "每天";

            return string.Join("、", days.OrderBy(d => ((int)d + 6) % 7).Select(AlarmSchedule.WeekdayName));
        }
    }

    /// <summary>状态文字。</summary>
    [JsonIgnore]
    public string StateText => Enabled ? "已开启" : "已关闭";

    /// <summary>列表里显示的一行说明。</summary>
    [JsonIgnore]
    public string SummaryText
        => $"{RepeatText}" + (string.IsNullOrWhiteSpace(Label) ? "" : " · " + Label);

    /// <summary>标题（有备注用备注，否则用时间）。</summary>
    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Label) ? Time : Label;

    // ---------- 星期勾选桥接（界面用 CheckBox 绑这七个） ----------

    /// <summary>「周一」勾选状态（以此类推到周日）。</summary>
    [JsonIgnore]
    public bool Monday { get => Has(DayOfWeek.Monday); set => SetDay(DayOfWeek.Monday, value); }

    /// <summary>周二。</summary>
    [JsonIgnore]
    public bool Tuesday { get => Has(DayOfWeek.Tuesday); set => SetDay(DayOfWeek.Tuesday, value); }

    /// <summary>周三。</summary>
    [JsonIgnore]
    public bool Wednesday { get => Has(DayOfWeek.Wednesday); set => SetDay(DayOfWeek.Wednesday, value); }

    /// <summary>周四。</summary>
    [JsonIgnore]
    public bool Thursday { get => Has(DayOfWeek.Thursday); set => SetDay(DayOfWeek.Thursday, value); }

    /// <summary>周五。</summary>
    [JsonIgnore]
    public bool Friday { get => Has(DayOfWeek.Friday); set => SetDay(DayOfWeek.Friday, value); }

    /// <summary>周六。</summary>
    [JsonIgnore]
    public bool Saturday { get => Has(DayOfWeek.Saturday); set => SetDay(DayOfWeek.Saturday, value); }

    /// <summary>周日。</summary>
    [JsonIgnore]
    public bool Sunday { get => Has(DayOfWeek.Sunday); set => SetDay(DayOfWeek.Sunday, value); }

    private static readonly string[] DayFlags =
    {
        nameof(Monday), nameof(Tuesday), nameof(Wednesday), nameof(Thursday),
        nameof(Friday), nameof(Saturday), nameof(Sunday),
    };

    private bool Has(DayOfWeek day) => RepeatDays.Contains(day);

    private void SetDay(DayOfWeek day, bool enabled)
    {
        var days = RepeatDays.ToList();

        if (enabled)
        {
            if (!days.Contains(day)) days.Add(day);
        }
        else
        {
            days.Remove(day);
        }

        Days = AlarmSchedule.FormatDays(days);
    }

    /// <summary>
    /// 规范时间写法：<c>7:5</c> → <c>07:05</c>；解析不了就退回原值。
    /// </summary>
    private static string NormalizeTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "07:00";

        if (TimeOnly.TryParse(value.Trim(), CultureInfo.CurrentCulture, out var parsed)
            || TimeOnly.TryParseExact(value.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return value.Trim();
    }

    /// <summary>规范重复天写法（去重、排好序）。</summary>
    private static string NormalizeDays(string? value)
        => AlarmSchedule.FormatDays(AlarmSchedule.ParseDays(value ?? ""));

    /// <summary>建一个默认闹钟（工作日 07:00）。</summary>
    public static AlarmEntry CreateWorkdayDefault() => new()
    {
        Time = "07:00",
        Label = "起床",
        Days = "1,2,3,4,5",
    };
}
