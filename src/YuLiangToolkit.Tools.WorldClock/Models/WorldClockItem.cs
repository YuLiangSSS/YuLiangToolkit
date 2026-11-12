using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.WorldClock.Services;

namespace YuLiangToolkit.Tools.WorldClock.Models;

/// <summary>
/// 一只钟（一个城市）。持久化字段写在 <see cref="WorldClockConfig.Clocks"/> 里，
/// 界面显示用的时间 / 时差 / 昼夜都是**瞬态**字段（[JsonIgnore]，由 ViewModel 每秒刷新）。
///
/// <para>继承 <see cref="ObservableObject"/> 是必须的：锁定 / 改名这类操作要立刻让界面跟着变，
/// 光靠配置落盘的通知（后台内容比对）是不会刷新的。</para>
/// </summary>
public sealed class WorldClockItem : ObservableObject
{
    // ===================== 持久化字段 =====================

    private string _cityName = "";

    /// <summary>城市中文名（界面主标题；用户也可以自己改成「老家」这种称呼）。</summary>
    public string CityName
    {
        get => _cityName;
        set => Set(ref _cityName, value ?? "");
    }

    private string _englishName = "";

    /// <summary>英文名（表盘下方小字；手工添加的自定义时区为空）。</summary>
    public string EnglishName
    {
        get => _englishName;
        set => Set(ref _englishName, value ?? "");
    }

    private string _timeZoneId = WorldClockTimeZones.LocalId;

    /// <summary>Windows 时区 Id（例如 <c>China Standard Time</c>）。自定义偏移时仅作记录。</summary>
    public string TimeZoneId
    {
        get => _timeZoneId;
        set => Set(ref _timeZoneId, value ?? "");
    }

    private int? _customOffsetMinutes;

    /// <summary>
    /// 自定义 UTC 偏移（分钟）。**非 null 时优先于 <see cref="TimeZoneId"/>** —— 城市库里没有的地方、
    /// 想固定成某个偏移不被夏令时影响的场合也用它；null = 正常走系统时区（含夏令时）。
    /// </summary>
    public int? CustomOffsetMinutes
    {
        get => _customOffsetMinutes;
        set => Set(ref _customOffsetMinutes, value);
    }

    private bool _isLocked;

    /// <summary>锁定：不能再被 ✕ 删掉，也不能被拖动排序（防手滑）。</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (!Set(ref _isLocked, value)) return;
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanDrag));
            OnPropertyChanged(nameof(LockGlyph));
            OnPropertyChanged(nameof(LockHint));
        }
    }

    // ===================== 瞬态字段（不写进 JSON，每秒刷新） =====================

    private string _timeText = "--:--";

    /// <summary>本地时间文本（12 / 24 小时制与是否带秒由配置决定）。</summary>
    [JsonIgnore]
    public string TimeText
    {
        get => _timeText;
        private set => Set(ref _timeText, value);
    }

    private string _dateText = "";

    /// <summary>日期 + 星期，例如「3月5日 周三」。</summary>
    [JsonIgnore]
    public string DateText
    {
        get => _dateText;
        private set => Set(ref _dateText, value);
    }

    private string _dayOffsetText = "";

    /// <summary>跨日角标：「明天」「昨天」；同一天为空（绑定用 EmptyToVis 隐藏）。</summary>
    [JsonIgnore]
    public string DayOffsetText
    {
        get => _dayOffsetText;
        private set => Set(ref _dayOffsetText, value);
    }

    private string _offsetText = "";

    /// <summary>与本地时差的说明，例如「快 8 小时」「慢 5 小时 30 分」「与本地相同」。</summary>
    [JsonIgnore]
    public string OffsetText
    {
        get => _offsetText;
        private set => Set(ref _offsetText, value);
    }

    private string _zoneOffsetText = "";

    /// <summary>该城市当前 UTC 偏移，例如「UTC+08:00」（悬停提示用）。</summary>
    [JsonIgnore]
    public string ZoneOffsetText
    {
        get => _zoneOffsetText;
        private set => Set(ref _zoneOffsetText, value);
    }

    private string _phaseEmoji = "☀️";

    /// <summary>昼夜 Emoji（☀️ / 🌇 / 🌙 / 🌅）。</summary>
    [JsonIgnore]
    public string PhaseEmoji
    {
        get => _phaseEmoji;
        private set => Set(ref _phaseEmoji, value);
    }

    private string _phaseText = "";

    /// <summary>昼夜文字（白天 / 傍晚 / 夜晚 / 清晨）。</summary>
    [JsonIgnore]
    public string PhaseText
    {
        get => _phaseText;
        private set => Set(ref _phaseText, value);
    }

    private DayPhase _phase = DayPhase.Day;

    /// <summary>昼夜分档（界面用它决定卡片底色）。</summary>
    [JsonIgnore]
    public DayPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            OnPropertyChanged(nameof(IsNight));
            OnPropertyChanged(nameof(IsDay));
        }
    }

    private bool _isDst;

    /// <summary>此刻是否处于夏令时（伦敦 / 纽约这类一年换两次的地方）。</summary>
    [JsonIgnore]
    public bool IsDst
    {
        get => _isDst;
        private set
        {
            if (!Set(ref _isDst, value)) return;
            OnPropertyChanged(nameof(DstBadgeText));
        }
    }

    private bool _isZoneMissing;

    /// <summary>时区 Id 在本机系统里找不到（配置被手改坏了）——界面会标出来并提示改为自定义偏移。</summary>
    [JsonIgnore]
    public bool IsZoneMissing
    {
        get => _isZoneMissing;
        private set => Set(ref _isZoneMissing, value);
    }

    private int _hour;

    /// <summary>时（0~23）。</summary>
    [JsonIgnore]
    public int Hour
    {
        get => _hour;
        private set => Set(ref _hour, value);
    }

    private int _minute;

    /// <summary>分（0~59）。</summary>
    [JsonIgnore]
    public int Minute
    {
        get => _minute;
        private set => Set(ref _minute, value);
    }

    private int _second;

    /// <summary>秒（0~59）。</summary>
    [JsonIgnore]
    public int Second
    {
        get => _second;
        private set => Set(ref _second, value);
    }

    // ===================== 计算属性（界面直接用） =====================

    /// <summary>能不能删（锁定时 ✕ 隐藏 / 变灰）。</summary>
    [JsonIgnore]
    public bool CanDelete => !IsLocked;

    /// <summary>能不能拖动排序（锁定时 ≡ 手柄禁用）。</summary>
    [JsonIgnore]
    public bool CanDrag => !IsLocked;

    /// <summary>
    /// 锁的图标：未锁定 = 开锁（点一下会锁），已锁定 = 闭合的锁。
    /// <para>⚠ 用 <c>Segoe MDL2 Assets</c> 的码位而不是 emoji：🔒 / 🔓 在部分系统字体里会渲染成豆腐块
    /// （界面上的 FontFamily 也必须是 "Segoe MDL2 Assets"，见 XAML）。</para>
    /// </summary>
    [JsonIgnore]
    public string LockGlyph => IsLocked ? LockGlyphLocked : LockGlyphUnlocked;

    /// <summary>闭合锁的码位（U+E72E）。</summary>
    public const string LockGlyphLocked = "\uE72E";

    /// <summary>开锁的码位（U+E785）。</summary>
    public const string LockGlyphUnlocked = "\uE785";

    /// <summary>锁按钮的悬停提示。</summary>
    [JsonIgnore]
    public string LockHint => IsLocked
        ? "已锁定：点一下解锁（解锁后才能删除 / 拖动排序）"
        : "锁定这一只钟，防止手滑点到 ✕";

    /// <summary>删除按钮的悬停提示。</summary>
    [JsonIgnore]
    public string DeleteHint => IsLocked
        ? "已锁定，先点锁解锁才能删除"
        : "删除这只钟（6 秒内可撤销）";

    /// <summary>夏令时角标（不在夏令时就为空，界面隐藏）。</summary>
    [JsonIgnore]
    public string DstBadgeText => IsDst ? "夏令时" : "";

    /// <summary>现在是不是夜里（夜间底色更暗）。</summary>
    [JsonIgnore]
    public bool IsNight => Phase is DayPhase.Night;

    private bool _dimAtNight;

    /// <summary>要不要按「夜里」把卡片底色压暗（= 配置开了昼夜底色 且 此刻是夜晚）。</summary>
    [JsonIgnore]
    public bool DimAtNight
    {
        get => _dimAtNight;
        private set => Set(ref _dimAtNight, value);
    }

    /// <summary>现在是不是白天。</summary>
    [JsonIgnore]
    public bool IsDay => Phase is DayPhase.Day;

    /// <summary>卡片悬停提示：时区 + 偏移 + 英文名。</summary>
    [JsonIgnore]
    public string DetailTip
    {
        get
        {
            var zone = string.IsNullOrWhiteSpace(EnglishName) ? CityName : $"{CityName}（{EnglishName}）";
            var source = CustomOffsetMinutes is { } minutes
                ? $"自定义偏移 {ClockMath.FormatOffset(TimeSpan.FromMinutes(minutes))}"
                : TimeZoneId;
            return $"{zone}{Environment.NewLine}时区：{source}{Environment.NewLine}当前偏移：{ZoneOffsetText}";
        }
    }

    // ===================== 刷新 =====================

    /// <summary>
    /// 按「现在」刷新这一只钟的全部显示字段（由 ViewModel 每秒调用一次）。
    /// 所有值都从 <paramref name="utcNow"/> 与本城市的时区换算，不自作主张读本机时间。
    /// </summary>
    /// <summary>
    /// 按「现在」刷新这一只钟的全部显示字段（由 ViewModel 每秒调用一次）。
    /// 所有值都从 <paramref name="utcNow"/> 与本城市的时区换算，不自作主张读本机时间。
    /// 关掉的显示项（日期 / 时差 / 昼夜底色）在这里直接输出空串 / false，界面不用再各自判断。
    /// </summary>
    public void Refresh(DateTime utcNow, DateTime localNow, TimeSpan localOffset,
        bool hour24, bool showSeconds, bool showDate, bool showOffset, bool colorByDayNight)
    {
        var resolution = ClockMath.Resolve(TimeZoneId, CustomOffsetMinutes, utcNow);
        var local = ClockMath.ToWallClock(utcNow, resolution.Offset);

        TimeText = ClockMath.FormatTime(local, hour24, showSeconds);
        DateText = showDate ? ClockMath.FormatDate(local, localNow.Year) : "";

        var dayOffset = (local.Date - localNow.Date).Days;
        DayOffsetText = ClockMath.DescribeDayOffset(dayOffset);

        OffsetText = showOffset ? ClockMath.FormatDiff(resolution.Offset - localOffset) : "";
        ZoneOffsetText = ClockMath.FormatOffset(resolution.Offset);

        var phase = ClockMath.ClassifyDayPhase(local.Hour);
        Phase = phase;
        PhaseText = ClockMath.PhaseText(phase);
        PhaseEmoji = ClockMath.PhaseEmoji(phase);
        DimAtNight = colorByDayNight && phase is DayPhase.Night;

        IsDst = resolution.IsDaylightSaving;
        IsZoneMissing = resolution.IsMissing;

        Hour = local.Hour;
        Minute = local.Minute;
        Second = local.Second;
    }

    /// <summary>从城市库条目建一只钟（首次运行 / 「添加」按钮用）。</summary>
    public static WorldClockItem FromCity(ClockCityOption city) => new()
    {
        CityName = city.Name,
        EnglishName = city.EnglishName,
        TimeZoneId = city.TimeZoneId,
    };

    /// <summary>从自定义偏移建一只钟（城市库没有的地方）。</summary>
    public static WorldClockItem FromOffset(string name, int offsetMinutes) => new()
    {
        CityName = string.IsNullOrWhiteSpace(name) ? ClockMath.FormatOffset(TimeSpan.FromMinutes(offsetMinutes)) : name.Trim(),
        EnglishName = "",
        TimeZoneId = WorldClockTimeZones.LocalId,
        CustomOffsetMinutes = Math.Clamp(offsetMinutes,
            ClockMath.MinOffsetMinutes, ClockMath.MaxOffsetMinutes),
    };
}
