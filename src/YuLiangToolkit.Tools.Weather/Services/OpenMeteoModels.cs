using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.Weather.Services;

/// <summary>
/// Open-Meteo 接口的响应 DTO（只声明本工具用到的字段）。
/// 字段名与接口原文一致（snake_case），所以全部显式标 <see cref="JsonPropertyNameAttribute"/>；
/// 数值都可能缺（部分时段 / 高海拔站点没有降水概率等），统一用可空类型。
/// </summary>
public sealed class OpenMeteoGeocodeResponse
{
    /// <summary>搜索结果（无结果时接口直接不给这个字段）。</summary>
    [JsonPropertyName("results")]
    public List<OpenMeteoPlace>? Results { get; set; }

    /// <summary>接口报错标记（Open-Meteo 出错时 HTTP 仍是 200，靠这个字段识别）。</summary>
    [JsonPropertyName("error")]
    public bool Error { get; set; }

    /// <summary>报错原因。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>地名搜索结果（一座城市 / 地点）。</summary>
public sealed class OpenMeteoPlace
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>地名（按搜索语言返回，中文搜索给中文名）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    /// <summary>国家（如「中国」）。</summary>
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    /// <summary>一级行政区（如「北京市」）。</summary>
    [JsonPropertyName("admin1")]
    public string? Admin1 { get; set; }

    /// <summary>二级行政区（如「朝阳区」）。</summary>
    [JsonPropertyName("admin2")]
    public string? Admin2 { get; set; }

    /// <summary>IANA 时区名（记录下来便于排查，界面不用）。</summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    /// <summary>人口（用于同名列「优先大城市」排序，可为空）。</summary>
    [JsonPropertyName("population")]
    public long? Population { get; set; }
}

/// <summary>天气预报响应（current + hourly + daily 三块）。</summary>
public sealed class OpenMeteoForecast
{
    [JsonPropertyName("error")]
    public bool Error { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>目标位置的 IANA 时区（timezone=auto 由坐标解析）。</summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("current")]
    public OpenMeteoCurrent? Current { get; set; }

    [JsonPropertyName("hourly")]
    public OpenMeteoHourly? Hourly { get; set; }

    [JsonPropertyName("daily")]
    public OpenMeteoDaily? Daily { get; set; }
}

/// <summary>实时天气块。</summary>
public sealed class OpenMeteoCurrent
{
    /// <summary>当地时间（ISO8601 无时区后缀，如「2026-11-20T14:00」）。</summary>
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("temperature_2m")]
    public double? Temperature2m { get; set; }

    /// <summary>体感温度。</summary>
    [JsonPropertyName("apparent_temperature")]
    public double? ApparentTemperature { get; set; }

    /// <summary>相对湿度（%）。</summary>
    [JsonPropertyName("relative_humidity_2m")]
    public int? RelativeHumidity2m { get; set; }

    /// <summary>1 = 白天，0 = 夜间。</summary>
    [JsonPropertyName("is_day")]
    public int? IsDay { get; set; }

    /// <summary>当前一小时降水量（mm）。</summary>
    [JsonPropertyName("precipitation")]
    public double? Precipitation { get; set; }

    /// <summary>WMO 天气代码（0 晴 / 3 阴 / 61 雨 / 71 雪 / 95 雷阵雨…）。</summary>
    [JsonPropertyName("weather_code")]
    public int? WeatherCode { get; set; }

    /// <summary>云量（%）。</summary>
    [JsonPropertyName("cloud_cover")]
    public int? CloudCover { get; set; }

    /// <summary>海平面气压（hPa）。</summary>
    [JsonPropertyName("pressure_msl")]
    public double? PressureMsl { get; set; }

    /// <summary>10 米高风速（km/h，固定用公制拉取，展示时再换算）。</summary>
    [JsonPropertyName("wind_speed_10m")]
    public double? WindSpeed10m { get; set; }

    /// <summary>风向（度，0 = 北）。</summary>
    [JsonPropertyName("wind_direction_10m")]
    public int? WindDirection10m { get; set; }
}

/// <summary>逐小时预报块（数组下标一一对应）。</summary>
public sealed class OpenMeteoHourly
{
    /// <summary>当地时间（ISO8601 无时区后缀）。</summary>
    [JsonPropertyName("time")]
    public List<string>? Time { get; set; }

    [JsonPropertyName("temperature_2m")]
    public List<double?>? Temperature2m { get; set; }

    /// <summary>降水概率（%），部分时段可能缺。</summary>
    [JsonPropertyName("precipitation_probability")]
    public List<int?>? PrecipitationProbability { get; set; }

    [JsonPropertyName("weather_code")]
    public List<int?>? WeatherCode { get; set; }

    /// <summary>1 = 白天，0 = 夜间（与 time 下标对应）。</summary>
    [JsonPropertyName("is_day")]
    public List<int?>? IsDay { get; set; }
}

/// <summary>每日预报块（forecast_days=7，下标 0 是今天）。</summary>
public sealed class OpenMeteoDaily
{
    /// <summary>日期（「2026-11-20」）。</summary>
    [JsonPropertyName("time")]
    public List<string>? Time { get; set; }

    [JsonPropertyName("weather_code")]
    public List<int?>? WeatherCode { get; set; }

    [JsonPropertyName("temperature_2m_max")]
    public List<double?>? Temperature2mMax { get; set; }

    [JsonPropertyName("temperature_2m_min")]
    public List<double?>? Temperature2mMin { get; set; }

    /// <summary>日出时间（ISO8601）。</summary>
    [JsonPropertyName("sunrise")]
    public List<string>? Sunrise { get; set; }

    /// <summary>日落时间（ISO8601）。</summary>
    [JsonPropertyName("sunset")]
    public List<string>? Sunset { get; set; }

    /// <summary>全天降水量（mm）。</summary>
    [JsonPropertyName("precipitation_sum")]
    public List<double?>? PrecipitationSum { get; set; }

    /// <summary>全天最大降水概率（%）。</summary>
    [JsonPropertyName("precipitation_probability_max")]
    public List<int?>? PrecipitationProbabilityMax { get; set; }
}

/// <summary>缓存文件（<c>Cache\weather\last-{PlaceId}.json</c>）的反序列化目标：包一层原始响应与拉取时间。</summary>
public sealed class CachedForecast
{
    /// <summary>城市标识（与文件名一致）。</summary>
    [JsonPropertyName("placeId")]
    public string PlaceId { get; set; } = "";

    /// <summary>拉取成功的本地时间（展示「缓存数据 · …」用）。</summary>
    [JsonPropertyName("fetchedAt")]
    public string FetchedAtText { get; set; } = "";

    /// <summary>Open-Meteo 的原始响应 JSON。</summary>
    [JsonPropertyName("json")]
    public string Json { get; set; } = "";
}
