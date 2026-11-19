using System.Net;
using System.Net.Http;
using System.Text.Json;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Weather;
using YuLiangToolkit.Tools.Weather.Models;
using YuLiangToolkit.Tools.Weather.Services;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「天气预报」自检：工具元数据 / WMO 天气代码表（未知回退 + 昼夜图标）/ 风向方位 /
/// 单位换算与文案（缺数「—」）/ 接口 URL 组装 / 时间与日期文案 / 逐小时切片 /
/// 展示模型组装 / 假 HttpMessageHandler 走请求层（geocoding + forecast + error 翻译）/
/// 缓存文件往返 / 配置默认值与 JSON 往返。
///
/// <para><b>全部纯逻辑 + 假 handler</b>：不创建窗口、不联网（真实拉取由人工冒烟验收）、
/// 不碰仓库的 Data/Config（配置与缓存往返都在 <c>YLT_HOME</c> 指向的临时数据根里走）。</para>
/// </summary>
internal static class WeatherChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckCodeCatalog(assert);
        CheckWindDirection(assert);
        CheckUnitAndText(assert);
        CheckUrlsAndPlaceId(assert);
        CheckTimeText(assert);
        CheckHourSlice(assert);
        CheckDisplays(assert);
        CheckClientWithFakeHandler(assert);
        CheckCacheRoundTrip(assert);
        CheckConfig(assert);
    }

    // ---------------------------------------------------------------- 样例数据

    /// <summary>造一份带完整数据的预报响应（展示模型 / 切片自检共用）。</summary>
    private static OpenMeteoForecast MakeSampleForecast(int hourlyCount = 48)
    {
        var times = new List<string>();
        for (var i = 0; i < hourlyCount; i++)
        {
            times.Add($"2026-11-{(i < 24 ? "20" : "21")}T{i % 24:00}:00");
        }

        var daily = new OpenMeteoDaily
        {
            Time = ["2026-11-20", "2026-11-21", "2026-11-22", "2026-11-23", "2026-11-24", "2026-11-25", "2026-11-26"],
            WeatherCode = [2, 61, 0, 3, 95, 71, 2],
            Temperature2mMax = [12.5, 9.2, 14.0, 10.0, 8.5, 4.0, 11.0],
            Temperature2mMin = [3.5, 1.2, 5.0, 2.0, -1.0, -3.0, 4.0],
            Sunrise = ["2026-11-20T06:42", "2026-11-21T06:43"],
            Sunset = ["2026-11-20T17:10", "2026-11-21T17:09"],
            PrecipitationSum = [0.0, 4.5, 0.0, 0.0, 12.0, 2.0, 0.0],
            PrecipitationProbabilityMax = [10, 80, 0, 5, 95, null, 20],
        };

        return new OpenMeteoForecast
        {
            Timezone = "Asia/Shanghai",
            Current = new OpenMeteoCurrent
            {
                Time = "2026-11-20T14:00",
                Temperature2m = 25.3,
                ApparentTemperature = 26.1,
                RelativeHumidity2m = 60,
                IsDay = 1,
                Precipitation = 0.2,
                WeatherCode = 2,
                CloudCover = 40,
                PressureMsl = 1012.3,
                WindSpeed10m = 12.4,
                WindDirection10m = 135,
            },
            Hourly = new OpenMeteoHourly
            {
                Time = times,
                Temperature2m = times.Select((_, i) => (double?)(i % 12)).ToList(),
                PrecipitationProbability = times.Select((_, i) => (int?)(i * 3 % 100)).ToList(),
                WeatherCode = times.Select((_, i) => (int?)(i % 4)).ToList(),
                IsDay = times.Select(t => int.Parse(t[11..13], System.Globalization.CultureInfo.InvariantCulture) is >= 6 and < 19 ? (int?)1 : 0).ToList(),
            },
            Daily = daily,
        };
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new WeatherTool();
        check("[天气预报] 工具 Id / 名称 / 分类 / 排序符合约定，且实现了 ITool",
            tool.Id == "weather" && tool.Name == "天气预报"
            && tool.Category == "生活" && tool.Order == 168
            && tool is ITool,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[天气预报] 归在「生活」类别下（CategoryPath 与 Category 一致）",
            tool.CategoryPath.Count == 1 && tool.CategoryPath[0] == "生活",
            string.Join(" / ", tool.CategoryPath));

        check("[天气预报] 主页图标非空、标签含天气 / 天气预报 / 温度",
            tool.EmojiIcon.Length > 0
            && tool.Tags.Contains("天气") && tool.Tags.Contains("天气预报") && tool.Tags.Contains("温度"),
            string.Join(" / ", tool.Tags) + " · " + tool.EmojiIcon);

        check("[天气预报] 描述能当主页搜索文案（写明体感 / 7 天 / 缓存 / Open-Meteo）",
            tool.Description.Contains("体感") && tool.Description.Contains("7 天")
            && tool.Description.Contains("缓存") && tool.Description.Contains("Open-Meteo"),
            tool.Description);
    }

    // ---------------------------------------------------------------- WMO 天气代码表

    private static void CheckCodeCatalog(CheckFn check)
    {
        check("[天气预报] 0=晴（白天 ☀️ / 夜间 🌙）",
            WeatherCodeCatalog.Describe(0).Description == "晴"
            && WeatherCodeCatalog.Describe(0).DayIcon == "☀️" && WeatherCodeCatalog.Describe(0).NightIcon == "🌙",
            WeatherCodeCatalog.Describe(0).ToString());

        check("[天气预报] 2=局部多云（夜间 ☁️）/ 3=阴 / 61=小雨 / 95=雷阵雨（⛈️）",
            WeatherCodeCatalog.Describe(2, true) == ("局部多云", "⛅")
            && WeatherCodeCatalog.Describe(2, false).Icon == "☁️"
            && WeatherCodeCatalog.Describe(3).Description == "阴"
            && WeatherCodeCatalog.Describe(61).Description == "小雨"
            && WeatherCodeCatalog.Describe(95).DayIcon == "⛈️",
            $"{WeatherCodeCatalog.Describe(2, false)} / {WeatherCodeCatalog.Describe(61)} / {WeatherCodeCatalog.Describe(95)}");

        check("[天气预报] 雾 / 雪 / 阵雨 / 雷阵雨伴冰雹都有条目（45·71·80·96）",
            WeatherCodeCatalog.Describe(45).Description == "雾"
            && WeatherCodeCatalog.Describe(71).Description == "小雪"
            && WeatherCodeCatalog.Describe(80).Description == "小阵雨"
            && WeatherCodeCatalog.Describe(96).Description == "雷阵雨伴冰雹",
            $"{WeatherCodeCatalog.Describe(45)} / {WeatherCodeCatalog.Describe(71)} / {WeatherCodeCatalog.Describe(80)} / {WeatherCodeCatalog.Describe(96)}");

        check("[天气预报] 未知代码（999 / -1）安全回退「未知 🌍」，不抛异常",
            WeatherCodeCatalog.Describe(999) == WeatherCodeCatalog.Unknown
            && WeatherCodeCatalog.Describe(-1).Description == "未知"
            && WeatherCodeCatalog.Describe(-1).DayIcon == "🌍",
            WeatherCodeCatalog.Describe(999).ToString());

        var covered = new[] { 0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67, 71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99 };
        check("[天气预报] WMO 实际会出现的代码全部收录（无遗漏）",
            covered.All(c => WeatherCodeCatalog.Describe(c) != WeatherCodeCatalog.Unknown),
            $"收录 {covered.Length} 个");
    }

    // ---------------------------------------------------------------- 风向方位

    private static void CheckWindDirection(CheckFn check)
    {
        check("[天气预报] 八方位：0 北 / 45 东北 / 90 东 / 135 东南 / 180 南 / 225 西南 / 270 西 / 315 西北",
            WeatherMath.WindDirectionText(0) == "北风"
            && WeatherMath.WindDirectionText(45) == "东北风"
            && WeatherMath.WindDirectionText(90) == "东风"
            && WeatherMath.WindDirectionText(135) == "东南风"
            && WeatherMath.WindDirectionText(180) == "南风"
            && WeatherMath.WindDirectionText(225) == "西南风"
            && WeatherMath.WindDirectionText(270) == "西风"
            && WeatherMath.WindDirectionText(315) == "西北风",
            $"{WeatherMath.WindDirectionText(0)} / {WeatherMath.WindDirectionText(45)} / {WeatherMath.WindDirectionText(135)}");

        check("[天气预报] 角度归一：23° → 东北（22.5° 为界）/ 359° → 北 / -90° → 西",
            WeatherMath.WindDirectionText(23) == "东北风"
            && WeatherMath.WindDirectionText(359) == "北风"
            && WeatherMath.WindDirectionText(-90) == "西风",
            $"{WeatherMath.WindDirectionText(23)} / {WeatherMath.WindDirectionText(359)} / {WeatherMath.WindDirectionText(-90)}");

        check("[天气预报] 风向缺数显示「—」",
            WeatherMath.WindDirectionText(null) == "—",
            WeatherMath.WindDirectionText(null));
    }

    // ---------------------------------------------------------------- 单位换算与文案

    private static void CheckUnitAndText(CheckFn check)
    {
        check("[天气预报] 摄氏不变、华氏 ×9/5+32（100℃→212℉ / -40 两边相等）",
            WeatherMath.ConvertTemp(25, "celsius") == 25
            && Math.Abs(WeatherMath.ConvertTemp(100, "fahrenheit") - 212) < 1e-9
            && Math.Abs(WeatherMath.ConvertTemp(-40, "fahrenheit") - (-40)) < 1e-9,
            $"{WeatherMath.ConvertTemp(100, "fahrenheit")} / {WeatherMath.ConvertTemp(-40, "fahrenheit")}");

        check("[天气预报] 风速 km/h → m/s（36→10），单位符号 km/h 与 m/s",
            Math.Abs(WeatherMath.ConvertWind(36, "ms") - 10) < 1e-9
            && WeatherMath.WindUnitSymbol("kmh") == "km/h" && WeatherMath.WindUnitSymbol("ms") == "m/s"
            && WeatherMath.TempUnitSymbol("fahrenheit") == "°F" && WeatherMath.TempUnitSymbol("celsius") == "°C",
            $"{WeatherMath.ConvertWind(36, "ms")} / {WeatherMath.WindUnitSymbol("ms")}");

        check("[天气预报] 温度文案 25.3°，风速 12.4 km/h，缺数统一「—」",
            WeatherMath.TempText(25.3, "celsius") == "25.3°"
            && WeatherMath.TempText(25.36, "celsius") == "25.4°"
            && WeatherMath.WindText(12.4, "kmh") == "12.4 km/h"
            && WeatherMath.TempText(null, "celsius") == "—"
            && WeatherMath.WindText(null, "ms") == "—",
            $"{WeatherMath.TempText(25.3, "celsius")} / {WeatherMath.WindText(12.4, "kmh")}");

        check("[天气预报] 百分比 / 气压 / 降水毫米文案（1012.3→1012 hPa）",
            WeatherMath.PercentText(30) == "30%" && WeatherMath.PercentText(null) == "—"
            && WeatherMath.PressureText(1012.3) == "1012 hPa"
            && WeatherMath.PrecipMmText(0.25) == "0.3 mm" && WeatherMath.PrecipMmText(null) == "—",
            $"{WeatherMath.PressureText(1012.3)} / {WeatherMath.PrecipMmText(0.25)}");
    }

    // ---------------------------------------------------------------- 接口 URL 与城市 Id

    private static void CheckUrlsAndPlaceId(CheckFn check)
    {
        var url = WeatherMath.BuildForecastUrl(39.9042, 116.4074);
        check("[天气预报] 预报 URL：坐标 / current / hourly / daily / timezone=auto / forecast_days=7 全齐",
            url.StartsWith("https://api.open-meteo.com/v1/forecast", StringComparison.Ordinal)
            && url.Contains("latitude=39.9042", StringComparison.Ordinal)
            && url.Contains("longitude=116.4074", StringComparison.Ordinal)
            && url.Contains("current=temperature_2m", StringComparison.Ordinal)
            && url.Contains("hourly=temperature_2m", StringComparison.Ordinal)
            && url.Contains("daily=weather_code", StringComparison.Ordinal)
            && url.Contains("timezone=auto", StringComparison.Ordinal)
            && url.Contains("forecast_days=7", StringComparison.Ordinal),
            url);

        var geoUrl = WeatherMath.BuildGeocodeUrl("北京");
        check("[天气预报] 搜索 URL：name 转义（中文百分号编码）/ count=10 / language=zh / format=json",
            geoUrl.StartsWith("https://geocoding-api.open-meteo.com/v1/search", StringComparison.Ordinal)
            && geoUrl.Contains("name=%E5%8C%97%E4%BA%AC", StringComparison.Ordinal)
            && geoUrl.Contains("count=10", StringComparison.Ordinal)
            && geoUrl.Contains("language=zh", StringComparison.Ordinal)
            && geoUrl.Contains("format=json", StringComparison.Ordinal),
            geoUrl);

        check("[天气预报] 城市 Id 由坐标 4 位小数拼出（31.23,121.47），跨次搜索稳定",
            WeatherMath.MakePlaceId(31.23, 121.47) == "31.23,121.47"
            && WeatherMath.MakePlaceId(39.90421, 116.40739) == "39.9042,116.4074"
            && WeatherMath.MakePlaceId(31.23001, 121.47002) == WeatherMath.MakePlaceId(31.23002, 121.47001),
            WeatherMath.MakePlaceId(39.90421, 116.40739));
    }

    // ---------------------------------------------------------------- 时间与日期文案

    private static void CheckTimeText(CheckFn check)
    {
        check("[天气预报] ShortTimeText：「2026-11-20T06:42」→「06:42」，缺数 / 非时间回「—」",
            WeatherMath.ShortTimeText("2026-11-20T06:42") == "06:42"
            && WeatherMath.ShortTimeText(null) == "—" && WeatherMath.ShortTimeText("垃圾") == "—",
            WeatherMath.ShortTimeText("2026-11-20T06:42"));

        check("[天气预报] TryParseIso：日期 / 带时间都解析，「垃圾」返回 null",
            WeatherMath.TryParseIso("2026-11-20") is { } d && d.Month == 11 && d.Day == 20
            && WeatherMath.TryParseIso("2026-11-20T14:00") is { } t && t.Hour == 14
            && WeatherMath.TryParseIso("垃圾") is null,
            "ok");

        check("[天气预报] 逐小时文案：首格「现在」/「14时」/ 跨天「明天 2时」",
            WeatherMath.HourLabelText("2026-11-20T14:00", "2026-11-20", true) == "现在"
            && WeatherMath.HourLabelText("2026-11-20T14:00", "2026-11-20", false) == "14时"
            && WeatherMath.HourLabelText("2026-11-21T02:00", "2026-11-20", false) == "明天 2时"
            && WeatherMath.HourLabelText("垃圾", "2026-11-20", false) == "—",
            "ok");

        check("[天气预报] 日差：相邻日期差 1 天，同日 0 天",
            WeatherMath.DayOffsetFrom("2026-11-20", "2026-11-21") == 1
            && WeatherMath.DayOffsetFrom("2026-11-20", "2026-11-20") == 0,
            "ok");

        check("[天气预报] 每日文案：0 今天 / 1 明天 / 其余按星期（周四=周四）",
            WeatherMath.DayLabelText("2026-11-20", 0) == "今天"
            && WeatherMath.DayLabelText("2026-11-20", 1) == "明天"
            && WeatherMath.DayLabelText("2026-11-20", 2) == WeatherMath.DayOfWeekText(DateTime.Parse("2026-11-20", System.Globalization.CultureInfo.InvariantCulture).DayOfWeek)
            && WeatherMath.DayOfWeekText(DayOfWeek.Thursday) == "周四",
            WeatherMath.DayLabelText("2026-11-20", 2));
    }

    // ---------------------------------------------------------------- 逐小时切片

    private static void CheckHourSlice(CheckFn check)
    {
        // 整点序列：11-20 00:00 ~ 23:00
        var oneDay = Enumerable.Range(0, 24).Select(h => $"2026-11-20T{h:00}:00").ToList();

        check("[天气预报] 切片起点：锚点 14:00 → 下标 14（该整点起）",
            WeatherMath.PickHourlyStartIndex(oneDay, "2026-11-20T14:00") == 14,
            WeatherMath.PickHourlyStartIndex(oneDay, "2026-11-20T14:00").ToString());

        check("[天气预报] 切片起点：锚点比全部整点晚（数据过期）回退 0；空数组回退 0",
            WeatherMath.PickHourlyStartIndex(oneDay, "2026-11-20T23:30") == 0
            && WeatherMath.PickHourlyStartIndex([], "2026-11-20T00:00") == 0
            && WeatherMath.PickHourlyStartIndex(null, null) == 0,
            "ok");

        // 48 小时序列：11-20 全天 + 11-21 全天
        var forecast = MakeSampleForecast(48);
        var hours = WeatherMath.BuildHours(forecast, "celsius", 24);
        check("[天气预报] BuildHours：从「现在」起取 24 个小时，第一格「现在」、下一格「15时」",
            hours.Count == 24 && hours[0].TimeText == "现在" && hours[1].TimeText == "15时",
            $"{hours.Count} 条 / {hours[0].TimeText} / {hours[1].TimeText}");

        check("[天气预报] BuildHours 跨天：23 点后是「明天 0时」~「明天 13时」",
            hours[10].TimeText == "明天 0时" && hours[^1].TimeText == "明天 13时",
            $"{hours[10].TimeText} / {hours[^1].TimeText}");

        check("[天气预报] BuildHours 夜间图标：凌晨时段用夜间图标（is_day=0）",
            hours[15].Icon != "☀️" && hours[15].Icon != "⛅",
            hours[15].Icon);

        var few = WeatherMath.BuildHours(forecast, "celsius", 12);
        check("[天气预报] 条数选项生效：hourCount=12 只出 12 条",
            few.Count == 12,
            few.Count.ToString());
    }

    // ---------------------------------------------------------------- 展示模型

    private static void CheckDisplays(CheckFn check)
    {
        var forecast = MakeSampleForecast(48);

        var now = WeatherMath.BuildNow(forecast, "北京", "更新于 14:32", "celsius");
        check("[天气预报] BuildNow：温度 / 体感 / 湿度 / 风向东南 / 气压 / 云量 / 降水",
            now.PlaceName == "北京" && now.TemperatureText == "25.3°" && now.FeelsText == "26.1°"
            && now.HumidityText == "60%" && now.WindDirText == "东南风"
            && now.WindText == "12.4 km/h" && now.PressureText == "1012 hPa"
            && now.CloudText == "40%" && now.PrecipText == "0.2 mm",
            $"{now.TemperatureText} / {now.FeelsText} / {now.WindDirText}");

        check("[天气预报] BuildNow：日出日落取今天（06:42 / 17:10），更新时间原样带出",
            now.SunriseText == "06:42" && now.SunsetText == "17:10" && now.UpdatedText == "更新于 14:32",
            $"{now.SunriseText} / {now.SunsetText}");

        var night = MakeSampleForecast(48);
        night.Current!.IsDay = 0;
        check("[天气预报] BuildNow：夜间换夜间图标（is_day=0）",
            WeatherMath.BuildNow(night, "北京", "x", "celsius").Icon == WeatherCodeCatalog.Describe(2, false).Icon,
            WeatherMath.BuildNow(night, "北京", "x", "celsius").Icon);

        var missing = new OpenMeteoForecast
        {
            Current = new OpenMeteoCurrent { Time = "2026-11-20T14:00", WeatherCode = 3 },
        };
        var emptyNow = WeatherMath.BuildNow(missing, "无数据城", "x", "celsius");
        check("[天气预报] BuildNow 缺数：温度 / 湿度 / 风 / 日出日落全是「—」，阴天图标",
            emptyNow.TemperatureText == "—" && emptyNow.HumidityText == "—"
            && emptyNow.WindText == "—" && emptyNow.WindDirText == "—"
            && emptyNow.SunriseText == "—" && emptyNow.ConditionText == "阴",
            $"{emptyNow.TemperatureText} / {emptyNow.ConditionText}");

        var days = WeatherMath.BuildDays(forecast, "celsius");
        check("[天气预报] BuildDays：7 条，0=今天 / 1=明天，日期 11/20 与 11/21",
            days.Count == 7 && days[0].DayText == "今天" && days[1].DayText == "明天"
            && days[0].DateText == "11/20" && days[1].DateText == "11/21",
            $"{days.Count} 天 / {days[0].DateText}");

        check("[天气预报] BuildDays：最高最低温 / 降水概率（80%）/ 无概率时回退降水量（95→95%，null→mm）",
            days[0].MaxText == "12.5°" && days[0].MinText == "3.5°"
            && days[1].PrecipText == "80%"
            && days[5].PrecipText == "2 mm",
            $"{days[0].MaxText}~{days[0].MinText} / {days[1].PrecipText} / {days[5].PrecipText}");

        check("[天气预报] BuildDays 天气文案：中雨 / 大雪 / 雷阵雨各就各位",
            days[1].CondText == "小雨" && days[3].CondText == "阴"
            && days[4].CondText == "雷阵雨" && days[5].CondText == "小雪",
            $"{days[1].CondText} / {days[4].CondText} / {days[5].CondText}");
    }

    // ---------------------------------------------------------------- 请求层（假 handler）

    /// <summary>假 handler：按 URL 前缀返回预置响应，并记录请求（自检断言 UA / URL）。
    /// <see cref="ForceError"/> 打开后返回接口报错体（模拟 Open-Meteo error=true）。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public string? LastUserAgent;
        public string? LastUrl;
        public bool ForceError;
        public string? GeocodeBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();
            LastUserAgent = request.Headers.UserAgent.ToString();

            string body;
            if (ForceError)
            {
                body = """{"error":true,"reason":"Calculated request exceeds bounds"}""";
            }
            else if (LastUrl!.Contains("geocoding-api.open-meteo.com"))
            {
                body = GeocodeBody ?? """
                    {"results":[{"id":1816670,"name":"北京","latitude":39.9042,"longitude":116.4074,"country":"中国","admin1":"北京市","timezone":"Asia/Shanghai","population":11716620},{"id":1810000,"name":"北京县","latitude":39.86,"longitude":116.28,"country":"中国","admin1":"河北省"}],"generationtime_ms":0.5}
                    """;
            }
            else
            {
                body = JsonSerializer.Serialize(MakeSampleForecast(48));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private static void CheckClientWithFakeHandler(CheckFn check)
    {
        var handler = new FakeHandler();
        var client = new OpenMeteoClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

        var places = client.SearchPlacesAsync("北京").GetAwaiter().GetResult();
        check("[天气预报] 地理编码：2 条结果、名称与坐标解析正确",
            places.Count == 2 && places[0].Name == "北京"
            && Math.Abs(places[0].Latitude - 39.9042) < 1e-9 && places[0].Admin1 == "北京市",
            $"{places.Count} 条 / {places[0].Name}({places[0].Latitude})");

        check("[天气预报] 请求头：User-Agent 必须带（YuLiangToolkit-Weather/1.0）、URL 是 geocoding 搜索",
            handler.LastUserAgent == OpenMeteoClient.UserAgent
            && handler.LastUrl!.Contains("geocoding-api.open-meteo.com", StringComparison.Ordinal)
            && handler.LastUrl.Contains("name=", StringComparison.Ordinal)
            && Uri.UnescapeDataString(handler.LastUrl).Contains("name=北京", StringComparison.Ordinal),
            (handler.LastUserAgent ?? "无") + " · " + handler.LastUrl);

        var (forecast, rawJson) = client.GetForecastRawAsync(39.9, 116.4).GetAwaiter().GetResult();
        check("[天气预报] 预报：解析出 current / hourly / daily，原始 JSON 一并带回",
            forecast.Current!.Temperature2m == 25.3
            && forecast.Hourly!.Time!.Count == 48 && forecast.Daily!.Time!.Count == 7
            && !string.IsNullOrEmpty(rawJson),
            $"{forecast.Current!.Temperature2m}°C / {forecast.Hourly!.Time!.Count}h / {forecast.Daily!.Time!.Count}d");

        check("[天气预报] 预报请求走 forecast 接口且 timezone=auto",
            handler.LastUrl!.Contains("api.open-meteo.com/v1/forecast", StringComparison.Ordinal)
            && handler.LastUrl.Contains("timezone=auto", StringComparison.Ordinal),
            handler.LastUrl);

        var errorHandler = new FakeHandler { ForceError = true };
        var errorClient = new OpenMeteoClient(new HttpClient(errorHandler) { Timeout = TimeSpan.FromSeconds(5) });
        check("[天气预报] 接口 error=true → 翻译成带 reason 的 OpenMeteoException",
            CatchMessage(() => errorClient.SearchPlacesAsync("北京").GetAwaiter().GetResult())
                .Contains("Calculated request exceeds bounds", StringComparison.Ordinal),
            CatchMessage(() => errorClient.SearchPlacesAsync("北京").GetAwaiter().GetResult()));

        check("[天气预报] 响应不是合法 JSON → OpenMeteoException（不是 JsonException 直接冒出）",
            CatchMessage(() => client.ParseForecastAsync("这不是JSON")).Contains("响应不是合法 JSON", StringComparison.Ordinal),
            CatchMessage(() => client.ParseForecastAsync("这不是JSON")));

        var emptyHandler = new FakeHandler { GeocodeBody = "{\"generationtime_ms\":1}" };
        var emptyClient = new OpenMeteoClient(new HttpClient(emptyHandler) { Timeout = TimeSpan.FromSeconds(5) });
        var none = emptyClient.SearchPlacesAsync("不存在的地方").GetAwaiter().GetResult();
        check("[天气预报] 无结果（results 字段缺失）→ 空列表而不是 null（返回值本身是 List，不可能为 null）",
            none.Count == 0,
            none.Count.ToString());
    }

    /// <summary>执行并抓异常文案（没抛异常返回空串）。</summary>
    private static string CatchMessage(Action action)
    {
        try
        {
            action();
            return "";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---------------------------------------------------------------- 缓存往返

    private static void CheckCacheRoundTrip(CheckFn check)
    {
        var cache = ToolCache.For(WeatherConfig.ToolId);
        var path = cache.PathOf("last-selfcheck.json");
        try
        {
            var payload = new CachedForecast
            {
                PlaceId = "selfcheck",
                FetchedAtText = "11-20 14:32",
                Json = JsonSerializer.Serialize(MakeSampleForecast(48)),
            };
            cache.EnsureCreated();
            File.WriteAllText(path, JsonSerializer.Serialize(payload, OpenMeteoClient.JsonOptions));

            var reloaded = JsonSerializer.Deserialize<CachedForecast>(File.ReadAllText(path), OpenMeteoClient.JsonOptions);
            check("[天气预报] 缓存往返：PlaceId / 拉取时间 / 原始 JSON 都存得住",
                reloaded is not null && reloaded.PlaceId == "selfcheck"
                && reloaded.FetchedAtText == "11-20 14:32"
                && reloaded.Json.Contains("temperature_2m", StringComparison.Ordinal),
                reloaded?.FetchedAtText ?? "null");

            // 缓存里的 JSON 能原样解析回预报（断网兜底的关键链路）
            var back = new OpenMeteoClient().ParseForecastAsync(reloaded!.Json).GetAwaiter().GetResult();
            check("[天气预报] 缓存 JSON 原样还原成预报（兜底渲染可用）",
                back.Current!.Temperature2m == 25.3 && back.Daily!.Time!.Count == 7,
                $"{back.Current.Temperature2m}°C");

            check("[天气预报] 缓存写在 Cache\\weather\\（主页设置「清理缓存」覆盖）",
                cache.Directory.EndsWith(Path.Combine("Cache", "weather"), StringComparison.OrdinalIgnoreCase),
                cache.Directory);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 临时文件清理失败忽略 */ }
        }
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var defaults = new WeatherConfig();
        check("[天气预报] 配置默认值：摄氏 / km/h / 24 小时 / 不自动刷新 / 无城市",
            defaults.TemperatureUnit == "celsius" && defaults.WindSpeedUnit == "kmh"
            && defaults.HourCount == 24 && defaults.AutoRefreshMinutes == 0
            && defaults.Places.Count == 0 && defaults.CurrentPlace is null,
            $"{defaults.TemperatureUnit} / {defaults.WindSpeedUnit} / {defaults.HourCount} / {defaults.AutoRefreshMinutes}");

        defaults.TemperatureUnit = "乱写的";
        defaults.WindSpeedUnit = "m/s 乱写";
        check("[天气预报] 单位写错时回退默认（celsius / kmh），不会显示成空白",
            defaults.TemperatureUnit == "celsius" && defaults.WindSpeedUnit == "kmh",
            $"{defaults.TemperatureUnit} / {defaults.WindSpeedUnit}");

        defaults.HourCount = 36;
        defaults.AutoRefreshMinutes = 7;
        check("[天气预报] 逐小时条数只允许 12/24/48、自动刷新只允许 0/30/60/180",
            defaults.HourCount == 24 && defaults.AutoRefreshMinutes == 0,
            $"{defaults.HourCount} / {defaults.AutoRefreshMinutes}");

        var store = ToolConfigStore.For(WeatherConfig.ToolId, "selfcheck.json");
        try
        {
            var config = store.Load<WeatherConfig>();
            config.Places.Add(new WeatherPlaceItem
            {
                Id = "39.9042,116.4074",
                Name = "北京",
                SubName = "中国 · 北京市",
                Latitude = 39.9042,
                Longitude = 116.4074,
            });
            config.Places.Add(new WeatherPlaceItem
            {
                Id = "31.2304,121.4737",
                Name = "上海",
                SubName = "中国 · 上海市",
                Latitude = 31.2304,
                Longitude = 121.4737,
            });
            config.CurrentPlaceId = "31.2304,121.4737";
            config.TemperatureUnit = "fahrenheit";
            config.HourCount = 12;

            var reloaded = ToolConfigStore.For(WeatherConfig.ToolId, "selfcheck.json").Load<WeatherConfig>();
            check("[天气预报] 配置往返：城市列表 / 当前城市 / 单位 / 条数都存得住",
                reloaded.Places.Count == 2
                && reloaded.CurrentPlaceId == "31.2304,121.4737"
                && reloaded.CurrentPlace!.Name == "上海"
                && reloaded.TemperatureUnit == "fahrenheit"
                && reloaded.HourCount == 12,
                $"{reloaded.Places.Count} 城 / 当前 {reloaded.CurrentPlace?.Name} / {reloaded.TemperatureUnit}");

            reloaded.CurrentPlaceId = "不存在的";
            check("[天气预报] 当前城市 Id 对不上时回退列表第一个（北京）",
                reloaded.CurrentPlace!.Name == "北京",
                reloaded.CurrentPlace.Name);

            var json = config.ToJson();
            check("[天气预报] 瞬态计算字段不写进配置（CurrentPlace 只读展示）",
                !json.Contains("\"CurrentPlace\"", StringComparison.Ordinal),
                json.Length > 200 ? "配置文件里只有持久化字段" : json);
        }
        finally
        {
            store.DeleteFile();
        }
    }
}
