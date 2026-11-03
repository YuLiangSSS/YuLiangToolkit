namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// 标签栏的纯逻辑（<b>不碰界面 / 不碰 WebView2</b>，自检 <c>browser</c> 组直接断言）：
/// 标签栏位置归一化、会话标签列表清洗、关闭标签后「谁变成激活」。
/// ViewModel（<c>BrowserViewModel</c>）和配置读写都走这里，规则改一处即可。
/// </summary>
public static class BrowserTabBar
{
    /// <summary>标签栏在上方（工具条上面一行，横排）。</summary>
    public const string TopPosition = "Top";

    /// <summary>标签栏在左侧（竖排一列，像浏览器侧边栏）。</summary>
    public const string LeftPosition = "Left";

    /// <summary>会话最多记住多少个标签（防止标签越开越多把 config.json 撑大；超出部分不恢复）。</summary>
    public const int MaxSessionTabs = 20;

    /// <summary>
    /// 把配置里读到的标签栏位置归一化成 <see cref="TopPosition"/> / <see cref="LeftPosition"/>：
    /// 大小写 / 首尾空白都容忍，写坏（或老版本没有这个键）一律回退上方。
    /// </summary>
    public static string NormalizePosition(string? value)
        => string.Equals(value?.Trim(), LeftPosition, StringComparison.OrdinalIgnoreCase)
            ? LeftPosition
            : TopPosition;

    /// <summary>
    /// 清洗要写进配置 / 刚从配置读出来的会话标签列表：
    /// 去掉空地址、去掉首尾空白、封顶 <see cref="MaxSessionTabs"/> 个。
    /// <b>不去重</b>——两个标签页开同一个网址是用户的真实状态，得原样恢复。
    /// </summary>
    public static List<BrowserSessionTab> SanitizeSession(IEnumerable<BrowserSessionTab>? tabs)
    {
        var result = new List<BrowserSessionTab>();
        if (tabs is null) return result;

        foreach (var tab in tabs)
        {
            if (tab is null) continue;
            var url = tab.Url?.Trim() ?? "";
            if (url.Length == 0) continue;
            result.Add(new BrowserSessionTab { Url = url, Title = tab.Title?.Trim() ?? "" });
            if (result.Count >= MaxSessionTabs) break;
        }
        return result;
    }

    /// <summary>把「激活第几个标签」夹回合法范围（没有标签时返回 0）。</summary>
    public static int ClampActiveIndex(int index, int count)
        => count <= 0 ? 0 : Math.Clamp(index, 0, count - 1);

    /// <summary>
    /// 关掉 <paramref name="removedIndex"/> 位置的标签后，哪个位置变成激活：
    /// 关的是激活标签 → 原地补位（最右就退一格）；关的是别的标签 → 激活位置不动。
    /// 只剩 0 个时返回 <c>-1</c>（调用方负责开一个新标签兜底）。
    /// </summary>
    /// <param name="countBefore">关闭前的标签总数（含被关的那个）。</param>
    /// <param name="removedIndex">被关标签的位置。</param>
    /// <param name="activeIndex">关闭前激活标签的位置。</param>
    public static int NextActiveAfterRemove(int countBefore, int removedIndex, int activeIndex)
    {
        var countAfter = countBefore - 1;
        if (countAfter <= 0) return -1;
        if (removedIndex < 0 || removedIndex >= countBefore) return ClampActiveIndex(activeIndex, countAfter);
        if (activeIndex < removedIndex) return ClampActiveIndex(activeIndex, countAfter);
        if (activeIndex > removedIndex) return ClampActiveIndex(activeIndex - 1, countAfter);
        return ClampActiveIndex(removedIndex, countAfter); // 关的就是激活标签：原地补位
    }
}

/// <summary>会话里的一个标签页快照（<c>Config\browser\config.json</c> 的 <c>SessionTabs</c> 数组元素）。</summary>
public sealed class BrowserSessionTab
{
    /// <summary>标签页地址（关掉时的真实 URL，恢复时原样导航）。</summary>
    public string Url { get; set; } = "";

    /// <summary>标签页标题（恢复时先显示它，页面加载完会被真实标题覆盖）。</summary>
    public string Title { get; set; } = "";
}
