using System.Globalization;
using YuLiangToolkit.Tools.Weather.Models;

namespace YuLiangToolkit.Tools.Weather.Services;

/// <summary>
/// 天气相关的纯函数库：接口地址与查询串、风向方位文案、单位换算、
/// 逐小时切片（从「现在」往后取 N 小时）、日期文案，以及把 Open-Meteo 响应组装成展示模型的三个 Build。
///
/// <para>全部是静态纯函数、不碰窗口不碰网络 —— 自检（WeatherChecks）直接断言这一层。</para>
/// </summary>
public static class WeatherMath
{
    // ===================== 接口地址与查询串 =====================

    /// <summary>天气预报接口（GET，免费无 Key）。</summary>
    public const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";

    /// <summary>地名搜索接口（GET，免费无 Key）。</summary>
    public const string GeocodeEndpoint = "https://geocoding-api.open-meteo.com/v1/search";

    /// <summary>本工具拉取的实时 / 逐小时 / 每日变量清单（写死成一条，自检断言参数齐全）。</summary>
    public const string CurrentVariables =
        "temperature_2m,apparent_temperature,relative_humidity_2m,is_day,precipitation,weather_code,cloud_cover,pressure_msl,wind_speed_10m,wind_direction_10m";

    /// <summary>逐小时拉取的变量。</summary>
    public const string HourlyVariables = "temperature_2m,precipitation_probability,weather_code,is_day";

    /// <summary>每日拉取的变量。</summary>
    public const string DailyVariables =
        "weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_sum,precipitation_probability_max";

    /// <summary>拼天气预报请求 URL（固定公制拉取，展示时再本地换算单位 —— 切单位不重新联网）。</summary>
    public static string BuildForecastUrl(double latitude, double longitude, int days = 7)
        => $"{ForecastEndpoint}?latitude={Fmt(latitude)}&longitude={Fmt(longitude)}"
           + $"&current={CurrentVariables}&hourly={HourlyVariables}&daily={DailyVariables}"
           + $"&timezone=auto&forecast_days={days}";

    /// <summary>拼地名搜索 URL（中文搜索 → 中文地名）。</summary>
    public static string BuildGeocodeUrl(string query, int count = 10, string language = "zh")
        => $"{GeocodeEndpoint}?name={Uri.EscapeDataString(query.Trim())}&count={count}&language={language}&format=json";

    /// <summary>城市稳定标识：坐标四舍五入到 4 位小数（约 11 米精度）后拼接。</summary>
    public static string MakePlaceId(double latitude, double longitude)
        => $"{Fmt(latitude)},{Fmt(longitude)}";

    /// <summary>坐标格式化（不变文化、4 位小数、去掉多余 0 —— 「39.9042」「116.4074」）。</summary>
    public static string Fmt(double value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    // ===================== 单位与文案 =====================

    /// <summary>温度单位符号（配置里存 celsius / fahrenheit）。</summary>
    public static string TempUnitSymbol(string unit) => unit == "fahrenheit" ? "°F" : "°C";

    /// <summary>摄氏 → 华氏（展示换算用；接口固定按摄氏拉取）。</summary>
    public static double ConvertTemp(double celsius, string unit)
        => unit == "fahrenheit" ? celsius * 9.0 / 5.0 + 32.0 : celsius;

    /// <summary>km/h → m/s（接口固定按 km/h 拉取）。</summary>
    public static double ConvertWind(double kmh, string unit)
        => unit == "ms" ? kmh / 3.6 : kmh;

    /// <summary>风速单位符号。</summary>
    public static string WindUnitSymbol(string unit) => unit == "ms" ? "m/s" : "km/h";

    /// <summary>温度文案：「25.3°」，缺数显示「—」。</summary>
    public static string TempText(double? value, string unit)
        => value is null ? "—" : $"{ConvertTemp(value.Value, unit).ToString("0.#", CultureInfo.InvariantCulture)}°";

    /// <summary>风速文案：「12.4 km/h」，缺数显示「—」。</summary>
    public static string WindText(double? kmh, string unit)
        => kmh is null ? "—" : $"{ConvertWind(kmh.Value, unit).ToString("0.#", CultureInfo.InvariantCulture)} {WindUnitSymbol(unit)}";

    /// <summary>百分比文案：「30%」，缺数显示「—」。</summary>
    public static string PercentText(int? value) => value is null ? "—" : $"{value}%";

    /// <summary>气压文案：「1012 hPa」，缺数显示「—」。</summary>
    public static string PressureText(double? hPa)
        => hPa is null ? "—" : $"{hPa.Value.ToString("0", CultureInfo.InvariantCulture)} hPa";

    /// <summary>毫米降水文案：「0.2 mm」，缺数显示「—」。</summary>
    public static string PrecipMmText(double? mm)
        => mm is null ? "—" : $"{mm.Value.ToString("0.#", CultureInfo.InvariantCulture)} mm";

    /// <summary>ISO8601（无时区后缀）里抠出「HH:mm」（「2026-11-20T06:42」→「06:42」）；解析不动显示「—」。</summary>
    public static string ShortTimeText(string? iso)
        => iso is not null && iso.Length >= 16 && iso[10] == 'T' ? iso[11..16] : "—";

    /// <summary>ISO 日期（或时间）解析成 DateTime；解析失败返回 null。</summary>
    public static DateTime? TryParseIso(string? iso)
        => DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    /// <summary>风向角 → 八方位中文（北 / 东北 / 东 / …）。负角与超 360° 先归一；缺数显示「—」。</summary>
    public static string WindDirectionText(int? degree)
    {
        if (degree is null) return "—";
        var normalized = ((degree.Value % 360) + 360) % 360;
        string[] names = ["北", "东北", "东", "东南", "南", "西南", "西", "西北"];
        // 每个方位占 45°，中心对齐（±22.5°）：22.5° → 「东北」而不是「北」
        var index = (int)Math.Round(normalized / 45.0, MidpointRounding.AwayFromZero) % 8;
        return names[index] + "风";
    }

    // ===================== 时间切片与日期文案 =====================

    /// <summary>
    /// 找逐小时数组的起点下标：Open-Meteo 的 hourly 是整点序列，取「锚点时刻（当前整点）」
    /// 及其之后的时段。ISO 字符串同格式可直接比较。找不到（数据过期 / 全是过去时段）回退 0。
    /// </summary>
    public static int PickHourlyStartIndex(List<string>? times, string? anchorIso)
    {
        if (times is null || times.Count == 0) return 0;
        var anchor = anchorIso ?? "";
        for (var i = 0; i < times.Count; i++)
        {
            if (string.CompareOrdinal(times[i], anchor) >= 0) return i;
        }

        return 0;
    }

    /// <summary>逐小时的时间文案：第一格「现在」，其余「14时」；跨到第二天时「明天 2时」「后天 23时」。</summary>
    public static string HourLabelText(string? iso, string? todayIso, bool isFirst)
    {
        var time = TryParseIso(iso);
        if (time is null) return "—";

        if (isFirst) return "现在";

        var dayOffset = DayOffsetFrom(todayIso, iso);
        var baseText = $"{time.Value.Hour}时";
        return dayOffset switch
        {
            0 => baseText,
            1 => $"明天 {baseText}",
            2 => $"后天 {baseText}",
            _ => baseText,
        };
    }

    /// <summary>每日预报的星期文案：0「今天」、1「明天」、其余「周四」。</summary>
    public static string DayLabelText(string? dateIso, int index)
    {
        if (index == 0) return "今天";
        if (index == 1) return "明天";
        var date = TryParseIso(dateIso);
        return date is null ? $"第{index + 1}天" : DayOfWeekText(date.Value.DayOfWeek);
    }

    /// <summary>星期 → 中文短文案。</summary>
    public static string DayOfWeekText(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>日期文案：「11/20」。</summary>
    public static string DateLabelText(string? dateIso)
        => TryParseIso(dateIso) is { } date ? $"{date.Month}/{date.Day}" : "—";

    /// <summary>目标 ISO 时刻相对「今天」ISO 日期差了几天（按日历日期算，不看时分秒）。</summary>
    public static int DayOffsetFrom(string? todayIso, string? targetIso)
    {
        var today = TryParseIso(todayIso);
        var target = TryParseIso(targetIso);
        if (today is null || target is null) return 0;
        return (int)(target.Value.Date - today.Value.Date).TotalDays;
    }

    // ===================== 展示模型组装 =====================

    /// <summary>组装「当前天气」卡片。</summary>
    public static WeatherNowDisplay BuildNow(OpenMeteoForecast forecast, string placeName, string fetchedAtText, string unit)
    {
        var current = forecast.Current;
        var isDay = (current?.IsDay ?? 1) != 0;
        var (description, icon) = WeatherCodeCatalog.Describe(current?.WeatherCode ?? -1, isDay);

        // 日出日落取今天（daily 下标 0），接口按 forecast_days=7 拉取时必然带今天
        var sunriseText = forecast.Daily?.Sunrise is { Count: > 0 } sunrises ? ShortTimeText(sunrises[0]) : "—";
        var sunsetText = forecast.Daily?.Sunset is { Count: > 0 } sunsets ? ShortTimeText(sunsets[0]) : "—";

        return new WeatherNowDisplay
        {
            PlaceName = placeName,
            UpdatedText = fetchedAtText,
            Icon = icon,
            ConditionText = description,
            TemperatureText = TempText(current?.Temperature2m, unit),
            FeelsText = TempText(current?.ApparentTemperature, unit),
            HumidityText = PercentText(current?.RelativeHumidity2m),
            WindText = WindText(current?.WindSpeed10m, unit),
            WindDirText = WindDirectionText(current?.WindDirection10m),
            PressureText = PressureText(current?.PressureMsl),
            CloudText = PercentText(current?.CloudCover),
            PrecipText = PrecipMmText(current?.Precipitation),
            SunriseText = sunriseText,
            SunsetText = sunsetText,
        };
    }

    /// <summary>组装逐小时列表：从「现在」起的 <paramref name="hourCount"/> 个小时。</summary>
    public static List<HourForecast> BuildHours(OpenMeteoForecast forecast, string unit, int hourCount)
    {
        var hours = forecast.Hourly;
        var result = new List<HourForecast>();
        if (hours?.Time is null || hours.Time.Count == 0) return result;

        var start = PickHourlyStartIndex(hours.Time, forecast.Current?.Time);
        for (var i = start; i < hours.Time.Count && result.Count < hourCount; i++)
        {
            var code = AtNullable(hours.WeatherCode, i) ?? -1;
            var isDay = (AtNullable(hours.IsDay, i) ?? 1) != 0;
            var (description, icon) = WeatherCodeCatalog.Describe(code, isDay);

            result.Add(new HourForecast
            {
                TimeText = HourLabelText(AtRef(hours.Time, i), forecast.Current?.Time, i == start),
                Icon = icon,
                TempText = TempText(AtNullable(hours.Temperature2m, i), unit),
                PrecipText = PercentText(AtNullable(hours.PrecipitationProbability, i)),
            });
        }

        return result;
    }

    /// <summary>组装 7 天列表（下标 0 是今天）。</summary>
    public static List<DayForecast> BuildDays(OpenMeteoForecast forecast, string unit, int dayCount = 7)
    {
        var daily = forecast.Daily;
        var result = new List<DayForecast>();
        if (daily?.Time is null) return result;

        for (var i = 0; i < daily.Time.Count && i < dayCount; i++)
        {
            var code = AtNullable(daily.WeatherCode, i) ?? -1;
            var (description, icon) = WeatherCodeCatalog.Describe(code, isDay: true);

            // 降水概率优先（一眼能看出哪天要带伞），没有概率再看降水量
            var precip = PercentText(AtNullable(daily.PrecipitationProbabilityMax, i)) != "—"
                ? PercentText(AtNullable(daily.PrecipitationProbabilityMax, i))
                : PrecipMmText(AtNullable(daily.PrecipitationSum, i));

            result.Add(new DayForecast
            {
                DayText = DayLabelText(AtRef(daily.Time, i), i),
                DateText = DateLabelText(AtRef(daily.Time, i)),
                Icon = icon,
                CondText = description,
                MinText = TempText(AtNullable(daily.Temperature2mMin, i), unit),
                MaxText = TempText(AtNullable(daily.Temperature2mMax, i), unit),
                PrecipText = precip,
            });
        }

        return result;
    }

    /// <summary>安全取「可空元素列表」的第 i 项（越界 / 空列表返回 null）。</summary>
    private static T? AtNullable<T>(List<T?>? list, int index) where T : struct
        => list is not null && (uint)index < (uint)list.Count ? list[index] : null;

    /// <summary>安全取「不可空元素列表」的第 i 项（越界 / 空列表返回 null）。</summary>
    private static T? AtRef<T>(List<T>? list, int index) where T : class
        => list is not null && (uint)index < (uint)list.Count ? list[index] : null;
}
