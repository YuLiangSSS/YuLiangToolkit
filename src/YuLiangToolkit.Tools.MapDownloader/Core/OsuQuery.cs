namespace YuLiangToolkit.Tools.MapDownloader.Core;

/// <summary>
/// osu! API 的 URL / 表单构造。**全是纯函数**，自检直接断言，不用联网。
///
/// <para>参数规则照搬原版 <c>OsuApiClient</c>：<c>q</c>（关键词）、<c>m</c>（模式）、<c>s</c>（状态）、
/// <c>sort</c>（排序）、<c>nsfw=true</c>、<c>cursor_string</c>（翻页游标）。
/// 只有 <c>q</c> 和 <c>cursor_string</c> 需要转义 —— 游标是 API 给的一长串 base64 样的东西，
/// 里面带 <c>+ / =</c> 这类字符，不转义会拼出坏 URL（原版这里是对的，改写时别改掉）。</para>
/// </summary>
public static class OsuQuery
{
    /// <summary>换 Token 的地址。</summary>
    public const string TokenUrl = "https://osu.ppy.sh/oauth/token";

    /// <summary>API v2 根地址。</summary>
    public const string ApiBaseUrl = "https://osu.ppy.sh/api/v2";

    /// <summary>给用户点的 OAuth 应用注册页（原版那个 LinkLabel 的目标）。</summary>
    public const string OAuthAppUrl = "https://osu.ppy.sh/home/account/edit#oauth";

    /// <summary>
    /// 拼搜索用的查询串（不含开头的 <c>?</c>；什么都没填就返回空串）。
    /// </summary>
    public static string BuildSearchQuery(OsuSearchRequest request)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Query))
            parts.Add("q=" + Uri.EscapeDataString(request.Query.Trim()));

        if (request.Mode.HasValue)
            parts.Add("m=" + request.Mode.Value);

        if (!string.IsNullOrWhiteSpace(request.Status))
            parts.Add("s=" + request.Status);

        if (!string.IsNullOrWhiteSpace(request.Sort))
            parts.Add("sort=" + request.Sort);

        if (request.IncludeNsfw)
            parts.Add("nsfw=true");

        if (!string.IsNullOrWhiteSpace(request.CursorString))
            parts.Add("cursor_string=" + Uri.EscapeDataString(request.CursorString));

        return string.Join("&", parts);
    }

    /// <summary>拼搜索地址（<c>ApiBaseUrl + /beatmapsets/search + ?查询串</c>）。</summary>
    public static string BuildSearchUrl(OsuSearchRequest request, string baseUrl = ApiBaseUrl)
    {
        var query = BuildSearchQuery(request);
        return query.Length == 0 ? baseUrl + "/beatmapsets/search" : baseUrl + "/beatmapsets/search?" + query;
    }

    /// <summary>
    /// 换 Token 的表单字段（client_credentials 授权，scope=public）。
    /// 顺序与原版一致，方便自检逐项断言。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> BuildTokenForm(int clientId, string clientSecret) =>
    [
        new("client_id", clientId.ToString()),
        new("client_secret", clientSecret),
        new("grant_type", "client_credentials"),
        new("scope", "public"),
    ];
}
