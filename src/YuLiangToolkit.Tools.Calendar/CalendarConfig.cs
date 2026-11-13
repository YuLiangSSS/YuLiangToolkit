using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Calendar;

/// <summary>「日历」的强类型配置 → <c>Config\calendar\config.json</c>。</summary>
public sealed class CalendarConfig : ToolConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "calendar";

    private bool _mondayFirst = true;

    /// <summary>每周从周一开始（关掉 = 从周日开始，和英语习惯一致）。</summary>
    public bool MondayFirst
    {
        get => _mondayFirst;
        set => Set(ref _mondayFirst, value);
    }

    private bool _highlightWeekend = true;

    /// <summary>周末用强调色标出来。</summary>
    public bool HighlightWeekend
    {
        get => _highlightWeekend;
        set => Set(ref _highlightWeekend, value);
    }

    private bool _showDaysOfOtherMonths = true;

    /// <summary>格子里显示上/下月的日期（关掉 = 那些格子留空，只显示本月）。</summary>
    public bool ShowDaysOfOtherMonths
    {
        get => _showDaysOfOtherMonths;
        set => Set(ref _showDaysOfOtherMonths, value);
    }

    private bool _showLunar = true;

    /// <summary>格子里第二行显示农历 / 节气 / 节日（关掉 = 只在节日、节气那天显示）。</summary>
    public bool ShowLunar
    {
        get => _showLunar;
        set => Set(ref _showLunar, value);
    }

    /// <summary>当前每周第一天。</summary>
    [JsonIgnore]
    public DayOfWeek FirstDayOfWeek => MondayFirst ? DayOfWeek.Monday : DayOfWeek.Sunday;
}
