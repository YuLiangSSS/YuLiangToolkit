namespace YuLiangToolkit.Tools.Weather.Models;

/// <summary>
/// 「当前天气」卡片的展示模型：由 <c>WeatherMath.BuildNow</c> 从 API 响应一次性组装好，
/// XAML 只做绑定、不做换算 —— 所有文案（单位、风向、缺数显示「—」）都在纯函数里定型，自检能直接断言。
/// </summary>
public sealed class WeatherNowDisplay
{
    /// <summary>城市名。</summary>
    public string PlaceName { get; init; } = "";

    /// <summary>数据时间说明：「更新于 14:32」或「缓存数据 · 2026-11-20 09:10」（断网兜底时）。</summary>
    public string UpdatedText { get; init; } = "";

    /// <summary>当前天气 emoji（白天 / 夜间分图标）。</summary>
    public string Icon { get; init; } = "🌍";

    /// <summary>天气现象文案（例如「局部多云」）。</summary>
    public string ConditionText { get; init; } = "—";

    /// <summary>大字温度（含单位换算后的数值 + 「°」）。</summary>
    public string TemperatureText { get; init; } = "—";

    /// <summary>体感温度。</summary>
    public string FeelsText { get; init; } = "—";

    /// <summary>相对湿度（%）。</summary>
    public string HumidityText { get; init; } = "—";

    /// <summary>风（风速数值 + 单位符号）。</summary>
    public string WindText { get; init; } = "—";

    /// <summary>风向（八方位中文，例如「东南风」）。</summary>
    public string WindDirText { get; init; } = "—";

    /// <summary>海平面气压（hPa）。</summary>
    public string PressureText { get; init; } = "—";

    /// <summary>云量（%）。</summary>
    public string CloudText { get; init; } = "—";

    /// <summary>当前一小时降水量（mm）。</summary>
    public string PrecipText { get; init; } = "—";

    /// <summary>今天日出时间（HH:mm）。</summary>
    public string SunriseText { get; init; } = "—";

    /// <summary>今天日落时间（HH:mm）。</summary>
    public string SunsetText { get; init; } = "—";
}

/// <summary>逐小时预报的一行：时间 / 图标 / 温度 / 降水概率。</summary>
public sealed class HourForecast
{
    /// <summary>时间文案：「现在」或「14时」（跨天时「明天 2时」）。</summary>
    public string TimeText { get; init; } = "";

    /// <summary>天气 emoji。</summary>
    public string Icon { get; init; } = "🌍";

    /// <summary>温度文案。</summary>
    public string TempText { get; init; } = "—";

    /// <summary>降水概率（「30%」或「—」）。</summary>
    public string PrecipText { get; init; } = "—";
}

/// <summary>7 天预报的一行：日期 / 图标 / 天气 / 最低最高温 / 降水概率。</summary>
public sealed class DayForecast
{
    /// <summary>星期文案：「今天」「明天」「周四」。</summary>
    public string DayText { get; init; } = "";

    /// <summary>日期文案：「11/20」。</summary>
    public string DateText { get; init; } = "";

    /// <summary>天气 emoji（用白天图标）。</summary>
    public string Icon { get; init; } = "🌍";

    /// <summary>天气现象文案。</summary>
    public string CondText { get; init; } = "—";

    /// <summary>最低温文案。</summary>
    public string MinText { get; init; } = "—";

    /// <summary>最高温文案。</summary>
    public string MaxText { get; init; } = "—";

    /// <summary>降水概率（「30%」或「—」）。</summary>
    public string PrecipText { get; init; } = "—";
}
