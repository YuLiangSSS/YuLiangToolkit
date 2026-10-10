namespace YuLiangToolkit.Tools.FlashDownloader.Core;

/// <summary>
/// 游戏页网址的识别（从 <see cref="GamePageParser"/> 里拆出来的独立文件）。
///
/// <para>为什么要拆：这段逻辑是「拿用户粘的网址 → 认出站点 + 后缀」的第一步，
/// 单独成文件方便自检直接定位，也避免和一大堆正则混在一个大文件里。</para>
///
/// <para><b>规则</b>：先按域名认站点；域名认不出来时按后缀字母兜底（a=4399 / b=7k7k / c=17yy /
/// d=ArmorGames / e=Kongregate），这样镜像站、代理站的地址也能认出老朋友，不会白白丢掉后缀。</para>
/// </summary>
public static class GameUrlInfo
{
    /// <summary>从游戏页 URL 里认出站点（认不出返回 <see cref="GameSite.Unknown"/>）。</summary>
    public static GameSite DetectSite(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return GameSite.Unknown;
        if (url.Contains("4399.com", StringComparison.OrdinalIgnoreCase)) return GameSite.Site4399;
        if (url.Contains("7k7k.com", StringComparison.OrdinalIgnoreCase)) return GameSite.Site7k7k;
        if (url.Contains("17yy.com", StringComparison.OrdinalIgnoreCase)) return GameSite.Site17yy;
        if (url.Contains("armorgames.com", StringComparison.OrdinalIgnoreCase)) return GameSite.SiteArmorGames;
        if (url.Contains("kongregate.com", StringComparison.OrdinalIgnoreCase)) return GameSite.SiteKongregate;
        return GameSite.Unknown;
    }

    /// <summary>站点对应的后缀字母（a / b / c / z）。</summary>
    public static char LetterOf(GameSite site) => GameSites.ToLetter(site);

    /// <summary>按域名认站点、按字母兜底，最后把「后缀 + 站点」一起交出去。</summary>
    public static (string Appendix, GameSite Site) Describe(string? url, string number)
    {
        var site = DetectSite(url);
        var letter = site == GameSite.Unknown ? 'z' : GameSites.ToLetter(site);
        var appendix = number.Length > 0 ? letter + number : "";
        if (site == GameSite.Unknown && number.Length > 0)
        {
            // 域名认不出来，就用后缀字母倒推站点（a → 4399 …）；z 仍然是 Unknown
            var guessed = GameSites.FromLetter(letter);
            site = guessed;
        }
        return (appendix, site);
    }
}
