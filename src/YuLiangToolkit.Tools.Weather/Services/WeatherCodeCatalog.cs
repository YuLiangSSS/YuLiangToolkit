namespace YuLiangToolkit.Tools.Weather.Services;

/// <summary>
/// WMO 天气代码 → 中文现象文案 + emoji（白天 / 夜间分图标）。
///
/// <para>Open-Meteo 的 <c>weather_code</c> 是 WMO 4677 表（0 晴 / 3 阴 / 61 雨 / 71 雪 / 95 雷阵雨…）。
/// 全表 0~99，这里收录了实际会出现的全部代码；未知代码回退成「未知 + 🌍」，不会让界面开天窗。</para>
/// </summary>
public static class WeatherCodeCatalog
{
    /// <summary>一条描述：现象文案、白天图标、夜间图标。</summary>
    public sealed record WeatherCodeInfo(string Description, string DayIcon, string NightIcon);

    private static readonly Dictionary<int, WeatherCodeInfo> Table = new()
    {
        [0] = new("晴", "☀️", "🌙"),
        [1] = new("基本晴朗", "🌤️", "🌙"),
        [2] = new("局部多云", "⛅", "☁️"),
        [3] = new("阴", "☁️", "☁️"),
        [45] = new("雾", "🌫️", "🌫️"),
        [48] = new("雾凇", "🌫️", "🌫️"),
        [51] = new("轻毛毛雨", "🌦️", "🌧️"),
        [53] = new("毛毛雨", "🌧️", "🌧️"),
        [55] = new("浓毛毛雨", "🌧️", "🌧️"),
        [56] = new("冻毛毛雨", "🌧️", "🌧️"),
        [57] = new("浓冻毛毛雨", "🌧️", "🌧️"),
        [61] = new("小雨", "🌦️", "🌧️"),
        [63] = new("中雨", "🌧️", "🌧️"),
        [65] = new("大雨", "🌧️", "🌧️"),
        [66] = new("冻雨", "🌧️", "🌧️"),
        [67] = new("强冻雨", "🌧️", "🌧️"),
        [71] = new("小雪", "🌨️", "🌨️"),
        [73] = new("中雪", "🌨️", "🌨️"),
        [75] = new("大雪", "❄️", "❄️"),
        [77] = new("雪粒", "🌨️", "🌨️"),
        [80] = new("小阵雨", "🌦️", "🌧️"),
        [81] = new("阵雨", "🌧️", "🌧️"),
        [82] = new("强阵雨", "🌧️", "🌧️"),
        [85] = new("小阵雪", "🌨️", "🌨️"),
        [86] = new("阵雪", "🌨️", "🌨️"),
        [95] = new("雷阵雨", "⛈️", "⛈️"),
        [96] = new("雷阵雨伴冰雹", "⛈️", "⛈️"),
        [99] = new("强雷阵雨伴冰雹", "⛈️", "⛈️"),
    };

    /// <summary>未收录代码的兜底描述。</summary>
    public static readonly WeatherCodeInfo Unknown = new("未知", "🌍", "🌍");

    /// <summary>查一条描述（未知代码安全回退，不抛异常）。</summary>
    public static WeatherCodeInfo Describe(int code) => Table.TryGetValue(code, out var info) ? info : Unknown;

    /// <summary>按白天 / 夜间取图标并带上文案。</summary>
    public static (string Description, string Icon) Describe(int code, bool isDay)
    {
        var info = Describe(code);
        return (info.Description, isDay ? info.DayIcon : info.NightIcon);
    }
}
