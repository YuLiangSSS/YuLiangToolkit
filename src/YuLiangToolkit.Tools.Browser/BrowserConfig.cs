using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// 「网页浏览器」的配置（<c>Config\browser\config.json</c>，改一下自动落盘）。
/// </summary>
public sealed class BrowserConfig : ToolConfig
{
    /// <summary>功能 Id：同时是 <c>Config\browser</c> / <c>Cache\browser</c> 的文件夹名。</summary>
    public const string ToolId = "browser";

    /// <summary>默认主页：点 ⌂ 按钮 / 第一次打开浏览器时去的地址。</summary>
    public const string DefaultHomePage = "https://www.bing.com/";

    /// <summary>默认搜索地址模板（<c>{0}</c> 会被替换成转义后的关键词）。</summary>
    public const string DefaultSearchUrl = "https://www.bing.com/search?q={0}";

    /// <summary>主页。</summary>
    public string HomePage { get; set; } = DefaultHomePage;

    /// <summary>地址栏里输入的不是网址时，用这个模板搜索（<c>{0}</c> = 关键词）。</summary>
    public string SearchUrl { get; set; } = DefaultSearchUrl;

    /// <summary>是否记住上次关闭时打开的页面（下次打开浏览器接着看）。</summary>
    public bool RememberLastPage { get; set; } = true;

    /// <summary>上次访问的页面（<see cref="RememberLastPage"/> 打开时才用；空 = 用主页）。</summary>
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
}
