using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Tools.FlashDownloader.Core;

namespace YuLiangToolkit.Tools.FlashDownloader;

/// <summary>
/// 「Flash 下载器」的选项 → <c>Config\flash-downloader\config.json</c>。
/// 属性一改就自动落盘（最多 400ms），界面直接双向绑定，没有“保存”按钮。
/// </summary>
public sealed class FlashDownloaderConfig : ToolConfig
{
    /// <summary>功能 Id（同时是 <c>Data\</c> / <c>Config\</c> 下的小文件夹名）——定了就别改。</summary>
    public const string ToolId = "flash-downloader";

    /// <summary>
    /// 游戏存放目录。留空 = 用功能数据目录下的 <c>games\</c>（<see cref="DefaultGamesDirectory"/>）。
    /// 也可以在界面上点「更改…」选一个外部目录（不支持 <c>..</c> 相对路径）。
    /// </summary>
    public string GamesFolder { get; set; } = "";

    /// <summary>同时发起的连接数（关联文件批量下载 / 自动下载用）。1~16。</summary>
    public int MaxParallel { get; set; } = 4;

    /// <summary>连接超时（秒），对应原始工具的 <c>timeout=(3, 10)</c> 的前半段。</summary>
    public int ConnectTimeoutSeconds { get; set; } = 8;

    /// <summary>读取超时（秒）。</summary>
    public int ReadTimeoutSeconds { get; set; } = 30;

    /// <summary>下载 4399 的 h5 游戏时，把官方 h5api 换成内置的替代脚本（否则广告点不动、卡登录）。</summary>
    public bool Replace4399H5Api { get; set; } = true;

    /// <summary>用本地服务器（http://localhost:端口/）打开 htm 游戏；关掉则退回“直接双击文件”的旧方式。</summary>
    public bool UseLocalServerForHtm { get; set; } = true;

    /// <summary>打开工具时自动启动本地 HTTP 服务器。</summary>
    public bool AutoStartServer { get; set; } = true;

    /// <summary>本地服务器起始端口（被占用就依次往后试 100 个）。</summary>
    public int ServerStartPort { get; set; } = 1055;

    /// <summary>批量下载时跳过已经存在的文件（原始工具界面上那个“不覆盖已有文件”）。</summary>
    public bool SkipExistingFiles { get; set; } = true;

    /// <summary>Flash 独立播放器（FlashPlayer.exe）路径；留空 = 自动寻找（见 <see cref="Engine.FlashPlayerLocator"/>）。</summary>
    public string FlashPlayerPath { get; set; } = "";

    /// <summary>上次输入的“自由下载工具”参数（省得每次重填）。</summary>
    public string LastFreeUrl { get; set; } = "";
    public string LastFreeReferer { get; set; } = "";
    public string LastFreeName { get; set; } = "";

    /// <summary>上一次输入的游戏 URL（下次打开工具直接带出来）。</summary>
    public string LastGameUrl { get; set; } = "";

    /// <summary>游戏列表的排序方式（默认 / 上次游玩 / 名字 / 大小 / 站点）。</summary>
    public GameSortMode GameSortMode { get; set; } = GameSortMode.LastPlayed;

    /// <summary>游戏列表是否反序。</summary>
    public bool GameSortDescending { get; set; }

    /// <summary>
    /// 每个游戏「上次游玩时间」（键 = 文件名或文件夹名）。
    /// 「按上次游玩排序」靠它 —— 与外壳的 <c>ShellConfig.LastUsed</c> 一个套路：
    /// 字典原地增删改照样会落盘（自动保存靠内容比对，不依赖变更通知）。
    /// </summary>
    public Dictionary<string, DateTime> LastPlayedAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认游戏目录（功能数据目录下），只读计算属性 → 不写进 JSON。</summary>
    [JsonIgnore]
    public string DefaultGamesDirectory { get; set; } = "";

    /// <summary>最终生效的游戏目录：用户选过就用用户的，否则用默认的。</summary>
    [JsonIgnore]
    public string EffectiveGamesDirectory
        => string.IsNullOrWhiteSpace(GamesFolder) ? DefaultGamesDirectory : GamesFolder.Trim();

    /// <summary>界面上是否显示“恢复默认目录”。</summary>
    [JsonIgnore]
    public bool HasCustomFolder => !string.IsNullOrWhiteSpace(GamesFolder);
}
