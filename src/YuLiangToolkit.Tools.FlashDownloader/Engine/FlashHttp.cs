using System.Net;
using System.Net.Http;
using YuLiangToolkit.Tools.FlashDownloader.Core;

namespace YuLiangToolkit.Tools.FlashDownloader.Engine;

/// <summary>一次 HTTP 下载的结果。</summary>
public enum FetchStatus
{
    /// <summary>拿到内容了。</summary>
    Ok = 0,

    /// <summary>服务器说没有这个文件（404 等）。</summary>
    NotFound = 1,

    /// <summary>网络错误 / 超时 / 其它状态码。</summary>
    Failed = 2,
}

/// <summary>下载结果：状态 + 内容 + 给用户看的原因。</summary>
/// <param name="Status">结果状态。</param>
/// <param name="Content">响应体（失败时为 null）。</param>
/// <param name="Message">失败原因 / 附加说明。</param>
/// <param name="ContentType">响应头里的 Content-Type（可能是 null）。</param>
public sealed record FetchResult(FetchStatus Status, byte[]? Content, string Message, string? ContentType = null)
{
    public bool Ok => Status == FetchStatus.Ok;

    public static FetchResult FromBytes(byte[] content, string? contentType = null)
        => new(FetchStatus.Ok, content, "", contentType);

    public static FetchResult NotFound(string message) => new(FetchStatus.NotFound, null, message);

    public static FetchResult Failed(string message) => new(FetchStatus.Failed, null, message);
}

/// <summary>
/// 统一的 HTTP 访问层：带 Referer / UA、超时、把「404」与「网络错误」分开报告。
///
/// <para>对应原始 Python 项目里到处出现的
/// <c>requests.get(url, headers=self.headerX, timeout=(3,10))</c>。</para>
/// </summary>
public sealed class FlashHttp : IDisposable
{
    private readonly HttpClient _client;

    public FlashHttp(int connectTimeoutSeconds = 8, int readTimeoutSeconds = 30)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(connectTimeoutSeconds, 1, 120)),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(readTimeoutSeconds, 1, 600)),
        };
    }

    /// <summary>底层 HttpClient（自检里要另配 handler 时用）。</summary>
    public HttpClient Client => _client;

    /// <summary>GET 一个资源（拿到的是原始字节，编码自己解）。</summary>
    /// <param name="url">完整网址。</param>
    /// <param name="referer">上一级网址（很多游戏站用 Referer 防盗链，必须给）。</param>
    public async Task<FetchResult> GetBytesAsync(string url, string? referer = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return FetchResult.Failed("网址为空");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", GamePageParser.DefaultUserAgent);
            if (!string.IsNullOrWhiteSpace(referer))
                request.Headers.TryAddWithoutValidation("Referer", referer.Trim());
            request.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return FetchResult.NotFound($"HTTP {(int)response.StatusCode}");

            if (!response.IsSuccessStatusCode)
                return FetchResult.Failed($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString();
            return FetchResult.FromBytes(bytes, contentType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return FetchResult.Failed("请求超时");
        }
        catch (Exception ex)
        {
            return FetchResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 下载一个文件到本地（自动建目录）。404 与网络错误分开报告。
    /// </summary>
    /// <param name="url">完整网址。</param>
    /// <param name="localFullPath">本地绝对路径。</param>
    /// <param name="referer">上一级网址。</param>
    /// <param name="checkNotFoundPage">
    /// 拿到 200 之后是否还检查正文像不像「404 页」（原始工具的自动下载靠它决定删文件）。
    /// </param>
    public async Task<FetchResult> DownloadToFileAsync(
        string url,
        string localFullPath,
        string? referer = null,
        bool checkNotFoundPage = false,
        CancellationToken ct = default)
    {
        var result = await GetBytesAsync(url, referer, ct).ConfigureAwait(false);
        if (!result.Ok || result.Content is null) return result;

        if (checkNotFoundPage && GamePageParser.LooksLikeNotFoundPage(result.Content))
            return new FetchResult(FetchStatus.NotFound, result.Content, "内容是 404 页面");

        try
        {
            var dir = Path.GetDirectoryName(localFullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(localFullPath, result.Content, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            return FetchResult.Failed($"写入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// POST 一个表单并解析 JSON（17yy 用它问游戏路径：<c>vip_ajax.php</c>）。
    /// 返回可空字典，失败返回 null 并给出原因。
    /// </summary>
    public async Task<(Dictionary<string, object?>? Data, string Error)> PostFormForJsonAsync(
        string url,
        IReadOnlyDictionary<string, string> form,
        string? referer = null,
        CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url.Trim())
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", GamePageParser.DefaultUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            if (!string.IsNullOrWhiteSpace(referer))
                request.Headers.TryAddWithoutValidation("Referer", referer.Trim());

            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"php 请求失败，状态码 {(int)response.StatusCode}");

            var text = GamePageParser.DecodeWebContent(bytes).Text;
            var node = System.Text.Json.Nodes.JsonNode.Parse(text);
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in obj)
                    dict[key] = ToPlain(value);
                return (dict, "");
            }
            return (null, "php 返回的不是 JSON 对象");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>JSON 节点 → 纯 CLR 值（字典 / 列表 / bool / long / double / string）。</summary>
    private static object? ToPlain(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case System.Text.Json.Nodes.JsonObject obj:
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in obj) dict[key] = ToPlain(value);
                return dict;
            }
            case System.Text.Json.Nodes.JsonArray array:
                return array.Select(ToPlain).ToList();
            case System.Text.Json.Nodes.JsonValue value:
                if (value.TryGetValue<bool>(out var b)) return b;
                if (value.TryGetValue<string>(out var s)) return s;
                if (value.TryGetValue<long>(out var l)) return l;
                if (value.TryGetValue<double>(out var d)) return d;
                return value.ToJsonString();
            default:
                return node.ToJsonString();
        }
    }

    /// <summary>按「a.b.c」取嵌套字典里的值（php 接口返回 <c>{data:{game_path:"…"}}</c> 这种）。</summary>
    public static object? Dig(object? root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current is Dictionary<string, object?> dict)
            {
                if (!dict.TryGetValue(key, out current)) return null;
            }
            else return null;
        }
        return current;
    }

    /// <summary>按路径取字符串（取不到返回 <paramref name="fallback"/>）。</summary>
    public static string DigString(object? root, string fallback, params string[] path)
        => Dig(root, path) as string ?? fallback;
}
