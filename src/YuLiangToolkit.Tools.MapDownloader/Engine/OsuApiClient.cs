using System.Net;
using System.Net.Http;
using System.Text.Json;
using YuLiangToolkit.Tools.MapDownloader.Core;

namespace YuLiangToolkit.Tools.MapDownloader.Engine;

/// <summary>一次 API 调用的结果：成功给 <see cref="Value"/>，失败给 <see cref="Message"/> + HTTP 状态码。</summary>
/// <param name="Ok">成没成。</param>
/// <param name="Value">成功时的返回体。</param>
/// <param name="StatusCode">HTTP 状态码（网络层直接失败时是 0）。</param>
/// <param name="Message">人话错误说明。</param>
public readonly record struct OsuApiResult<T>(bool Ok, T? Value, int StatusCode, string Message)
{
    public static OsuApiResult<T> Success(T value, int statusCode = 200) => new(true, value, statusCode, "");
    public static OsuApiResult<T> Failure(string message, int statusCode = 0) => new(false, default, statusCode, message);
}

/// <summary>
/// osu! API v2 的客户端：换 Token（client_credentials） + 搜谱面集。
///
/// <para><b>和原版的区别</b>：原版把 Token 存在客户端实例里（<c>IsAuthenticated</c> / <c>AccessToken</c>），
/// 这里改成<b>无状态</b> —— Token 由调用方（配置对象）拿着、每次调用传进来。
/// 好处是"配置里那份 Token 才是唯一真相"，界面刷新 / 重开工具都不会出现两份不一致的状态。</para>
///
/// <para>失败**不抛异常**，统一返回 <see cref="OsuApiResult{T}"/>：401 凭据过期、404 找不到、
/// 超时、DNS 不通都能被界面直接显示成人话（异常只留给真正写错的代码）。</para>
/// </summary>
public sealed class OsuApiClient : IDisposable
{
    private const string UserAgent = "YuLiangToolkit-MapDownloader/1.0 (WPF; +https://osu.ppy.sh/)";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public OsuApiClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>换一个 Access Token（client_credentials，scope=public，有效期约 24 小时）。</summary>
    public async Task<OsuAuthResult> GetTokenAsync(int clientId, string clientSecret, CancellationToken ct = default)
    {
        try
        {
            using var form = new FormUrlEncodedContent(OsuQuery.BuildTokenForm(clientId, clientSecret));
            using var response = await _http.PostAsync(OsuQuery.TokenUrl, form, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = TryParse<OsuErrorResponse>(body);
                return OsuAuthResult.Fail($"HTTP {(int)response.StatusCode}：{error?.Describe() ?? Trim(body)}");
            }

            var token = TryParse<OsuTokenResponse>(body);
            if (token?.AccessToken is not { Length: > 0 } value)
                return OsuAuthResult.Fail("返回体里没有 access_token（多半是 client_id / client_secret 不对）");

            return OsuAuthResult.Ok(value, token.ExpiresIn, token.TokenType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OsuAuthResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            return OsuAuthResult.Fail(OsuHttp.Describe(ex));
        }
    }

    /// <summary>搜谱面集（<c>GET /beatmapsets/search</c>）。</summary>
    public async Task<OsuApiResult<BeatmapSetSearchResult>> SearchAsync(
        string accessToken, OsuSearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return OsuApiResult<BeatmapSetSearchResult>.Failure("还没有 Access Token，先获取一个", 401);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, OsuQuery.BuildSearchUrl(request));
            message.Headers.Add("Authorization", "Bearer " + accessToken);
            message.Headers.Add("Accept", "application/json");

            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = TryParse<OsuErrorResponse>(body);
                var hint = (int)response.StatusCode == 401 ? "（Token 可能已过期，重新获取一个）" : "";
                return OsuApiResult<BeatmapSetSearchResult>.Failure(
                    $"搜索失败 [{ (int)response.StatusCode }]：{error?.Describe() ?? Trim(body)}{hint}",
                    (int)response.StatusCode);
            }

            var result = TryParse<BeatmapSetSearchResult>(body);
            return result is null
                ? OsuApiResult<BeatmapSetSearchResult>.Failure("返回体解析失败（不是预期的 JSON）", (int)response.StatusCode)
                : OsuApiResult<BeatmapSetSearchResult>.Success(result, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OsuApiResult<BeatmapSetSearchResult>.Failure("已取消");
        }
        catch (Exception ex)
        {
            return OsuApiResult<BeatmapSetSearchResult>.Failure(OsuHttp.Describe(ex));
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private static T? TryParse<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    private static string Trim(string body)
        => body.Length <= 200 ? body : body[..200] + "…";
}

/// <summary>把网络异常翻成人话（和联机聊天室的 <c>ChatLink.Describe</c> 一个套路）。</summary>
public static class OsuHttp
{
    public static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "请求超时或被取消",
        HttpRequestException { StatusCode: { } code } => $"网络错误（HTTP {(int)code}）",
        HttpRequestException http => $"网络不通：{http.Message}",
        JsonException => "返回的内容不是合法 JSON",
        _ => $"{ex.GetType().Name}：{ex.Message}",
    };

    /// <summary>给下载器用：这个状态码值不值得重试/能不能算"网上没有"。</summary>
    public static bool IsNotFound(HttpStatusCode code) => code == HttpStatusCode.NotFound;
}
