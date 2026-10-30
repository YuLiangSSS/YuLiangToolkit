using System.Net.Http;
using System.Text;

namespace YuLiangToolkit.Tools.SpugCaptcha;

/// <summary>
/// Spug 推送助手的 HTTP 调用器：短信走 GET 查询参数、邮箱走 POST JSON（与官方文档 / 控制台生成器的调用方式一致）。
///
/// <para>构造时可注入 <see cref="HttpClient"/>（自检用假 handler 验证请求长什么样），
/// 默认用带 UA 和 20 秒超时的共享实例——不引第三方库，也避免每次发送 new HttpClient。</para>
///
/// <para>网络层错误（超时 / DNS / TLS）不抛给界面，统一转成 <see cref="SpugSendResult"/> 里的一条中文说明，
/// 由 ViewModel 原样显示到状态和日志里；HTTP 4xx/5xx 也走响应体解析，真正结果看 JSON 的 code。</para>
/// </summary>
public sealed class SpugSender
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _http;

    public SpugSender(HttpClient? http = null) => _http = http ?? SharedHttpClient;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YuLiangToolkit-SpugCaptcha/1.0");
        return client;
    }

    /// <summary>发送短信验证码：GET &lt;URL&gt;?to=手机号&amp;code=验证码[&amp;number=有效分钟数]。</summary>
    public async Task<SpugSendResult> SendSmsAsync(
        string url, string phone, string code, string? minutes, CancellationToken cancellationToken = default)
    {
        try
        {
            var requestUrl = SpugApi.BuildSmsRequestUrl(url, phone, code, minutes);
            using var response = await _http.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return SpugApi.ParseResponse(body, (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            return SpugSendResult.NetworkFailure("请求超时（20 秒）或已取消，请检查网络后重试");
        }
        catch (HttpRequestException ex)
        {
            return SpugSendResult.NetworkFailure($"网络请求失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return SpugSendResult.NetworkFailure($"发送失败：{ex.Message}");
        }
    }

    /// <summary>发送邮箱验证码：POST &lt;URL&gt; + JSON {to, scene, code, minute}。</summary>
    public async Task<SpugSendResult> SendMailAsync(
        string url, string email, string scene, string code, string minute, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = SpugApi.BuildMailJson(email, scene, code, minute);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return SpugApi.ParseResponse(body, (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            return SpugSendResult.NetworkFailure("请求超时（20 秒）或已取消，请检查网络后重试");
        }
        catch (HttpRequestException ex)
        {
            return SpugSendResult.NetworkFailure($"网络请求失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return SpugSendResult.NetworkFailure($"发送失败：{ex.Message}");
        }
    }
}
