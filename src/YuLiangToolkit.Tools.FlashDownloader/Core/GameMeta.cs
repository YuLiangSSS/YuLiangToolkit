namespace YuLiangToolkit.Tools.FlashDownloader.Core;

/// <summary>
/// 支持的小游戏站点。对应原始 Python 项目里 <c>HtmlAnalyse.getURLInfo()</c> 返回的
/// <c>'4399' / '7k7k' / '17yy' / None</c>，以及文件名后缀里的 a / b / c / d / e / z 六个字母。
///
/// <para>枚举值按名字进 JSON（配置里的排序等），<b>只能往后加，不能改前面的数字</b>。</para>
/// </summary>
public enum GameSite
{
    /// <summary>其它站点（后缀字母 z）：只能走「自由下载工具」+ 手工建文件夹。</summary>
    Unknown = 0,
    Site4399 = 1,
    Site7k7k = 2,
    Site17yy = 3,

    /// <summary>ArmorGames（后缀字母 d）：<c>armorgames.com/play/编号</c>，swf 在 cache.armorgames.com。</summary>
    SiteArmorGames = 4,

    /// <summary>Kongregate（后缀字母 e）：<c>kongregate.com/games/作者/游戏名</c>，swf 在 chat.kongregate.com。</summary>
    SiteKongregate = 5,
}

/// <summary>下载内容的类型。</summary>
public enum GameContentKind
{
    /// <summary>没识别出来。</summary>
    Unknown = 0,

    /// <summary>直接是一个 swf 文件（Flash 游戏，最理想的情况）。</summary>
    Swf = 1,

    /// <summary>是一层网页（h5 / UnityWebPlayer / 需要网页运行的老游戏）。</summary>
    Htm = 2,
}

/// <summary>站点相关的静态小工具（字母 ↔ 站点 ↔ 显示名 ↔ 支持情况）。</summary>
public static class GameSites
{
    /// <summary>
    /// 站点对应的后缀字母（<c>a</c> = 4399，<c>b</c> = 7k7k，<c>c</c> = 17yy，
    /// <c>d</c> = ArmorGames，<c>e</c> = Kongregate，<c>z</c> = 其它）。
    /// </summary>
    public static char ToLetter(GameSite site) => site switch
    {
        GameSite.Site4399 => 'a',
        GameSite.Site7k7k => 'b',
        GameSite.Site17yy => 'c',
        GameSite.SiteArmorGames => 'd',
        GameSite.SiteKongregate => 'e',
        _ => 'z',
    };

    /// <summary>字母 → 站点（认不出来返回 <see cref="GameSite.Unknown"/>）。</summary>
    public static GameSite FromLetter(char letter) => char.ToLowerInvariant(letter) switch
    {
        'a' => GameSite.Site4399,
        'b' => GameSite.Site7k7k,
        'c' => GameSite.Site17yy,
        'd' => GameSite.SiteArmorGames,
        'e' => GameSite.SiteKongregate,
        _ => GameSite.Unknown,
    };

    /// <summary>界面上显示的站点名。</summary>
    public static string DisplayName(GameSite site) => site switch
    {
        GameSite.Site4399 => "4399",
        GameSite.Site7k7k => "7k7k",
        GameSite.Site17yy => "17yy",
        GameSite.SiteArmorGames => "ArmorGames",
        GameSite.SiteKongregate => "Kongregate",
        _ => "其它站点",
    };

    /// <summary>这个站点能不能靠 URL 自动解析（其它站点只能手动下载）。</summary>
    public static bool IsSupported(GameSite site) => site != GameSite.Unknown;

    /// <summary>站点主页（界面上的“打开站点”用）。</summary>
    public static string HomePage(GameSite site) => site switch
    {
        GameSite.Site4399 => "https://www.4399.com/",
        GameSite.Site7k7k => "https://www.7k7k.com/",
        GameSite.Site17yy => "http://www.17yy.com/",
        GameSite.SiteArmorGames => "https://armorgames.com/",
        GameSite.SiteKongregate => "https://www.kongregate.com/",
        _ => "",
    };

    /// <summary>
    /// 后缀里的编号能不能<b>反推</b>回游戏页网址。
    ///
    /// <para>4399 / 7k7k / 17yy / ArmorGames 的游戏页网址里就带着编号，能直接拼回去；
    /// Kongregate 不行 —— 它的页面地址是「作者 / 游戏名」（<c>…/games/作者/游戏名</c>），
    /// 编号只藏在网页正文里，站点也不提供「编号 → 页面」的跳转（试过多个写法都是 404）。
    /// 所以 <c>e12345</c> 这种后缀只能认出站点，不能自动找回页面。</para>
    /// </summary>
    public static bool CanResolveFromAppendix(GameSite site) => site switch
    {
        GameSite.Site4399 or GameSite.Site7k7k or GameSite.Site17yy or GameSite.SiteArmorGames => true,
        _ => false,
    };
}

/// <summary>
/// 一个游戏在网上的信息（解析游戏页得到的结果）。对应原始 Python 项目的 <c>DownloadResult</c>。
///
/// <para><b>命名约定</b>（三位一体，缺一不可）：</para>
/// <list type="bullet">
///   <item><see cref="CleanName"/> 是清洗过的游戏名（去掉非法字符、下划线换连字符、≤100 字）；</item>
///   <item><see cref="Appendix"/> 形如 <c>a12345</c>，字母是站点、数字是站点内的游戏编号；</item>
///   <item>单个 swf 存成 <c>游戏名_a12345.swf</c>；放进文件夹时目录名是 <c>游戏名_a12345s</c>
///         （s = 用 Flash 播放器打开）或 <c>游戏名_a12345h</c>（h = 用网页打开），
///         与文件夹里的 <c>SWFNetPath.ini</c> 一一对应。</item>
/// </list>
/// </summary>
public sealed record GameMeta
{
    /// <summary>用户在界面上粘贴的原始网址。</summary>
    public string OriginUrl { get; init; } = "";

    /// <summary>清洗后的游戏名（可能为空字符串——表示没解析到名字）。</summary>
    public string CleanName { get; init; } = "";

    /// <summary>游戏页后缀，形如 <c>a12345</c>；解析不到就是空字符串。</summary>
    public string Appendix { get; init; } = "";

    /// <summary>识别出来的站点。</summary>
    public GameSite Site { get; init; } = GameSite.Unknown;

    /// <summary>游戏宽度；&lt;= 0 表示未知。</summary>
    public int Width { get; init; } = -1;

    /// <summary>游戏高度；&lt;= 0 表示未知。</summary>
    public int Height { get; init; } = -1;

    /// <summary>是直接指向 swf 还是指向一层网页（h5 / 多文件 / 需要网页环境的游戏）。</summary>
    public GameContentKind Kind { get; init; } = GameContentKind.Unknown;

    /// <summary>要下载/访问的内容地址：swf 文件地址，或网页地址。</summary>
    public string ContentUrl { get; init; } = "";

    /// <summary>swf 是不是藏在一层新网页后面（决定批量下载时的 Referer 与新页记录）。</summary>
    public bool IsNewPage { get; init; }

    /// <summary>新网页地址（<see cref="IsNewPage"/> 为 true 时才有意义）。</summary>
    public string NewPageUrl { get; init; } = "";

    /// <summary>网页游戏的整页内容（htm 类型时随解析一起拿到，省一次请求）。</summary>
    public byte[]? PageContent { get; init; }

    /// <summary>下载这个游戏时该用的 Referer（swf 的上一级页面）。</summary>
    public string Referer => IsNewPage && NewPageUrl.Length > 0 ? NewPageUrl : OriginUrl;

    /// <summary>解析成功了没有。</summary>
    public bool IsValid => Kind != GameContentKind.Unknown && ContentUrl.Length > 0;

    /// <summary>解析到游戏名了没有（没解析到就用站点编号兜底命名）。</summary>
    public bool HasName => CleanName.Length > 0;

    /// <summary>给用户看的宽高文本。</summary>
    public string SizeText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "尺寸未知";

    /// <summary>拿不到名字时用“站点+编号”兜底，别让用户看到空标题。</summary>
    public string DisplayNameOrFallback()
        => HasName ? CleanName : (Appendix.Length > 0 ? $"未命名-{Appendix}" : "未命名游戏");

    /// <summary>一行摘要（日志 / 提示用）。</summary>
    public string Describe()
        => $"[{GameSites.DisplayName(Site)}] {DisplayNameOrFallback()} · {Kind} · {SizeText} · {ContentUrl}";
}
