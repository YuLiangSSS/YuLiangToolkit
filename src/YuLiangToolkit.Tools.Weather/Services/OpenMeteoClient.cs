using System.Net.Http;
using System.Text.Json;
using YuLiangToolkit.Tools.Weather.Services;

namespace YuLiangToolkit.Tools.Weather.Services;

/// <summary>
/// Open-Meteo 客户端：地名搜索（Geocoding）+ 天气预报（Forecast）。
///
/// <para>两个接口都<b>免费、无需 API Key</b>。出错时 HTTP 仍是 200、错误在响应体的
/// <c>error/reason</c> 字段里（见 <see cref="EnsureNotError"/>），所以解析统一走一个入口。</para>
///
/// <para>构造时可注入 <see cref="HttpClient"/>（自检用假 handler 断言请求长什么样）；
/// 默认共享一个实例，必须显式带 User-Agent（不少网关会挡无 UA 请求，这条 MailSender 踩过）。</para>
/// </summary>
public sealed class OpenMeteoClient
{
    /// <summary>请求标识（网关侧好认；别省略 UA）。</summary>
    public const string UserAgent = "YuLiangToolkit-Weather/1.0";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _http;

    /// <summary>响应解析选项（字段名已用 <c>JsonPropertyName</c> 显式标注，这里只管宽松容错）。
    /// 缓存文件也用同一套选项序列化 / 反序列化，自检同样要读，所以公开。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public OpenMeteoClient(HttpClient? http = null) => _http = http ?? SharedHttpClient;

    /// <summary>按地名搜索城市（中文搜索返回中文名；空结果返回空列表）。</summary>
    public async Task<List<OpenMeteoPlace>> SearchPlacesAsync(string query, int count = 10, CancellationToken ct = default)
    {
        var url = WeatherMath.BuildGeocodeUrl(query, count);
        var response = Parse<OpenMeteoGeocodeResponse>(await ReadAsync(url, ct).ConfigureAwait(false));
        EnsureNotError(response.Error, response.Reason, url);
        return response.Results ?? [];
    }

    /// <summary>拉天气预报，同时带回<b>原始响应 JSON</b>（缓存文件存原文，断网兜底时逐字还原）。</summary>
    public async Task<(OpenMeteoForecast Forecast, string RawJson)> GetForecastRawAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        var url = WeatherMath.BuildForecastUrl(latitude, longitude);
        var json = await ReadAsync(url, ct).ConfigureAwait(false);
        var response = Parse<OpenMeteoForecast>(json);
        EnsureNotError(response.Error, response.Reason, url);
        return (response, json);
    }

    /// <summary>拉天气预报（原始 JSON 交给调用方组装 / 落缓存）。</summary>
    public async Task<OpenMeteoForecast> GetForecastAsync(double latitude, double longitude, CancellationToken ct = default)
        => (await GetForecastRawAsync(latitude, longitude, ct).ConfigureAwait(false)).Forecast;

    /// <summary>解析一份原始响应 JSON（缓存兜底 / 自检造数据用）。</summary>
    public Task<OpenMeteoForecast> ParseForecastAsync(string json) => Task.FromResult(Parse<OpenMeteoForecast>(json));

    /// <summary>GET 并返回文本（HTTP 错误抛 <see cref="HttpRequestException"/>，由 ViewModel 统一提示）。</summary>
    private async Task<string> ReadAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>把响应文本反序列化成 DTO；解析失败统一翻译成 <see cref="OpenMeteoException"/>。</summary>
    private static T Parse<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                   ?? throw new OpenMeteoException("接口返回了空内容");
        }
        catch (JsonException ex)
        {
            throw new OpenMeteoException($"响应不是合法 JSON：{ex.Message}");
        }
    }

    /// <summary>Open-Meteo 出错时 HTTP 200 但 body 里 error=true：翻译成异常让上层统一提示。</summary>
    private static void EnsureNotError(bool error, string? reason, string url)
    {
        if (error) throw new OpenMeteoException($"接口报错：{reason ?? "未知原因"}（{url}）");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}

/// <summary>Open-Meteo 业务错误（error=true / 响应解析失败），文案已翻译好可直接展示。</summary>
public sealed class OpenMeteoException(string message) : Exception(message);
