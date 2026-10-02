using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.MapDownloader.Core;

// ===== osu! API 数据模型 =====
// 全部照搬原版 MapDownloader 的字段（属性名用 [JsonPropertyName] 对上 API 的 snake_case），
// 只是补上了可空标注 —— 原版 <Nullable>disable</Nullable>，API 少给一个字段就会 NRE。

/// <summary>换 Token 的返回体（<c>POST /oauth/token</c>）。</summary>
public sealed class OsuTokenResponse
{
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
}

/// <summary>osu! 的错误返回体（换 Token / 调 API 失败时都是这个形状）。</summary>
public sealed class OsuErrorResponse
{
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    [JsonPropertyName("hint")] public string? Hint { get; set; }

    /// <summary>拼成一句能看的错误说明。</summary>
    public string Describe()
    {
        var parts = new[] { Error, ErrorDescription, Hint }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        return parts.Length > 0 ? string.Join(" - ", parts) : "未知错误";
    }
}

/// <summary>换 Token 的结果（原版的 OsuAuthResult）。</summary>
public sealed record OsuAuthResult(bool Success, string? AccessToken, int ExpiresIn, string? TokenType, string? ErrorMessage)
{
    public static OsuAuthResult Ok(string token, int expiresIn, string? tokenType)
        => new(true, token, expiresIn, tokenType, null);

    public static OsuAuthResult Fail(string message) => new(false, null, 0, null, message);
}

/// <summary>谱面集搜索的返回体（<c>GET /beatmapsets/search</c>）。</summary>
public sealed class BeatmapSetSearchResult
{
    [JsonPropertyName("beatmapsets")] public List<BeatmapSet>? BeatmapSets { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("cursor_string")] public string? CursorString { get; set; }
}

/// <summary>一个谱面集（一张图）。</summary>
public sealed class BeatmapSet
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("artist")] public string? Artist { get; set; }
    [JsonPropertyName("creator")] public string? Creator { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("ranked_date")] public string? RankedDate { get; set; }
    [JsonPropertyName("favourite_count")] public int FavouriteCount { get; set; }
    [JsonPropertyName("play_count")] public int PlayCount { get; set; }
    [JsonPropertyName("beatmaps")] public List<BeatmapInfo>? Beatmaps { get; set; }
}

/// <summary>谱面集里的一个难度。</summary>
public sealed class BeatmapInfo
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("difficulty_rating")] public double DifficultyRating { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("mode_int")] public int ModeInt { get; set; }
    [JsonPropertyName("cs")] public double CS { get; set; }
}

/// <summary>一次搜索请求（界面上的筛选条件 → 这里 → URL）。</summary>
/// <param name="Query">关键词（标题 / 艺术家 / 作者）。</param>
/// <param name="Mode">0=osu 1=taiko 2=catch 3=mania；null = 不限。</param>
/// <param name="Status">ranked / loved / qualified / pending / wip / graveyard；null = 不限。</param>
/// <param name="Sort">title_desc / artist_desc / difficulty_desc / ranked_desc / rating_desc / plays_desc / favourites_desc；null = 相关性。</param>
/// <param name="IncludeNsfw">是否带上 NSFW。</param>
/// <param name="CursorString">翻页游标（首页为 null）。</param>
public sealed record OsuSearchRequest(
    string? Query = null,
    int? Mode = null,
    string? Status = null,
    string? Sort = null,
    bool IncludeNsfw = false,
    string? CursorString = null);

/// <summary>要下载的一行：谱面集 Id + 标题（标题只用来当文件名）。</summary>
public readonly record struct OszItem(int SetId, string Title);
