using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// 「网页浏览器」的配置（<c>Config\browser\config.json</c>，改一下自动落盘）。
/// </summary>
public sealed class BrowserConfig : ToolConfig
{
    /// <summary>功能 Id：同时是 <c>Config\browser</c> / <c>Cache\browser</c> 的文件夹名。</summary>
    public const string ToolId = "browser";

    /// <summary>默认主页：点 ⌂ 按钮 / 第一次打开浏览器时去的地址（v0.63.0 起改用 Google）。</summary>
    public const string DefaultHomePage = "https://www.google.com/";

    /// <summary>默认搜索地址模板（<c>{0}</c> 会被替换成转义后的关键词；v0.63.0 起改用 Google）。</summary>
    public const string DefaultSearchUrl = "https://www.google.com/search?q={0}";

    /// <summary>v0.59.0 ~ v0.62.x 的旧默认主页（读配置时自动迁移成 Google）。</summary>
    public const string LegacyBingHomePage = "https://www.bing.com/";

    /// <summary>v0.59.0 ~ v0.62.x 的旧默认搜索模板（读配置时自动迁移成 Google）。</summary>
    public const string LegacyBingSearchUrl = "https://www.bing.com/search?q={0}";

    /// <summary>主页。</summary>
    public string HomePage { get; set; } = DefaultHomePage;

    /// <summary>地址栏里输入的不是网址时，用这个模板搜索（<c>{0}</c> = 关键词）。</summary>
    public string SearchUrl { get; set; } = DefaultSearchUrl;

    /// <summary>
    /// 启动时恢复上次关闭时打开的一组标签页（默认开）。关掉后每次打开只进主页；
    /// 恢复时<b>只恢复标签元数据</b>，点哪个才加载哪个（懒加载，见 <c>BrowserViewModel.RestoreSession</c>）。
    /// </summary>
    public bool RememberLastPage { get; set; } = true;

    /// <summary>上次访问的页面（兼容保留；没有可恢复会话时用它当起始页）。</summary>
    public string LastPage { get; set; } = "";

    /// <summary>标签栏位置（<see cref="BrowserTabBar.TopPosition"/> / <see cref="BrowserTabBar.LeftPosition"/>；
    /// 读进来会经 <see cref="BrowserTabBar.NormalizePosition"/> 归一化，写坏回退上方）。</summary>
    public string TabBarPosition { get; set; } = BrowserTabBar.TopPosition;

    /// <summary>
    /// 关闭时打开着的一组标签页快照（恢复会话用；<see cref="RememberLastPage"/> 关着时不恢复，
    /// 但仍会随手记录，重新打开开关后能接上）。写入前经 <see cref="BrowserTabBar.SanitizeSession"/> 清洗。
    /// </summary>
    public List<BrowserSessionTab> SessionTabs { get; set; } = new();

    /// <summary>恢复会话时激活第几个标签（越界由 <see cref="BrowserTabBar.ClampActiveIndex"/> 夹回）。</summary>
    public int ActiveTabIndex { get; set; }

    /// <summary>
    /// 旧版本（默认 Bing）的配置迁移到新默认（Google）：
    /// 只有「值恰好还是旧默认」才改，用户自己设过的主页 / 搜索模板一个字都不动。
    /// </summary>
    /// <returns>是否改了配置（调用方不用手动保存，配置会自动落盘）。</returns>
    public static bool MigrateLegacyDefaults(BrowserConfig config)
    {
        if (config is null) return false;
        var changed = false;

        if (string.Equals(config.HomePage?.Trim(), LegacyBingHomePage, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(config.HomePage))
        {
            config.HomePage = DefaultHomePage;
            changed = true;
        }

        if (string.Equals(config.SearchUrl?.Trim(), LegacyBingSearchUrl, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(config.SearchUrl))
        {
            config.SearchUrl = DefaultSearchUrl;
            changed = true;
        }

        // 旧版本调试 / 测试留下的 data: / about: 页面不当作「上次页面」恢复（否则一打开就进测试页）
        if (!string.IsNullOrWhiteSpace(config.LastPage))
        {
            var last = config.LastPage.Trim();
            if (last.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || last.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                || last.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                config.LastPage = "";
                changed = true;
            }
        }

        return changed;
    }
}
