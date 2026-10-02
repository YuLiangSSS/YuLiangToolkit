using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.MapDownloader;

/// <summary>
/// 「osu! 下图器」的强类型配置 → <c>Config\map-downloader\config.json</c>。
///
/// <para>原版是个 WinForms 小程序，凭据存在 <c>Properties.Settings</c> 里、token 另写一个
/// <c>token_expiry.dat</c> 放在 exe 旁边；移植后统一走本工具集的配置体系（改一下就自动落盘），
/// 所以这里既有 API 凭据，也有搜索条件和下载目录。</para>
///
/// <para>⚠ 凭据（Client Secret / Access Token）是明文存在这个 JSON 里的，跟原版一样只适合本机自用；
/// 这个文件在 <c>Config\</c> 下、已被 <c>.gitignore</c> 忽略，别把它拷给别人。</para>
/// </summary>
public sealed class MapDownloaderConfig : ToolConfig
{
    public const string ToolId = "map-downloader";

    // ---------- API 凭据 ----------

    /// <summary>osu! OAuth 应用的 Client ID（原版是文本框，这里就按文本存，用的时候再解析成数字）。</summary>
    public string ClientId { get; set; } = "";

    /// <summary>osu! OAuth 应用的 Client Secret。</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>打开工具时自动换一次 Token（凭据齐全且缓存已过期时）。</summary>
    public bool AutoGetToken { get; set; } = true;

    /// <summary>换到的 Access Token（client_credentials 的公共 token，有效期约 24 小时）。</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Token 过期时间（UTC）。原版把它单独写进 token_expiry.dat，这里直接进配置。</summary>
    public DateTime TokenExpiresAtUtc { get; set; }

    // ---------- 搜索条件（下次打开还是上次那套） ----------

    public string LastQuery { get; set; } = "";

    /// <summary>模式：空 = 不限，否则 "0"~"3"（osu / taiko / catch / mania）。</summary>
    public string Mode { get; set; } = "";

    /// <summary>状态：空 = 不限，否则 ranked / loved / qualified / pending / wip / graveyard。</summary>
    public string Status { get; set; } = "";

    /// <summary>排序：空 = 相关性，否则 title_desc / artist_desc / …。</summary>
    public string Sort { get; set; } = "";

    /// <summary>Key 数（mania 专用）：空 = 不限，否则 "4"~"10"，"10+" = 10 键以上。</summary>
    public string KeyCount { get; set; } = "";

    /// <summary>是否包含 NSFW 谱面。</summary>
    public bool IncludeNsfw { get; set; }

    /// <summary>日期筛选下界（本地筛选，留空 = 不限）。</summary>
    public string DateFrom { get; set; } = "";

    /// <summary>日期筛选上界（本地筛选，留空 = 不限）。</summary>
    public string DateTo { get; set; } = "";

    // ---------- 下载 ----------

    /// <summary>下载目录（空 = 用 <see cref="DefaultDownloadFolder"/>）。</summary>
    public string DownloadFolder { get; set; } = "";

    /// <summary>同时下载几个（原版写死 5）。</summary>
    public int ParallelDownloads { get; set; } = 5;

    /// <summary>默认下载目录（<c>Data\map-downloader\downloads</c>），只读、不会被写进 JSON。</summary>
    [JsonIgnore]
    public string DefaultDownloadFolder { get; set; } = "";

    /// <summary>实际使用的下载目录：配了就用配的，没配就用默认的。只读，不会被写进 JSON。</summary>
    [JsonIgnore]
    public string EffectiveDownloadFolder => string.IsNullOrWhiteSpace(DownloadFolder) ? DefaultDownloadFolder : DownloadFolder;

    /// <summary>已换到的 Token 还能用吗（留 60 秒余量，和原版一致）。</summary>
    [JsonIgnore]
    public bool HasValidToken => !string.IsNullOrWhiteSpace(AccessToken) && DateTime.UtcNow < TokenExpiresAtUtc;

    /// <summary>凭据填齐了没（Client ID 必须能解析成数字）。</summary>
    [JsonIgnore]
    public bool HasCredentials => int.TryParse(ClientId.Trim(), out _) && !string.IsNullOrWhiteSpace(ClientSecret);
}
