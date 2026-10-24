using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.FlashDownloader.Core;

/// <summary>
/// 游戏目录里的一个游戏（一个文件夹，或者一个散放的 <c>.swf</c>）。
/// 对应原始 Python 项目的 <c>InfoItem</c>。
///
/// <para><b>命名规则</b>（想手工放游戏就得按这个来）：</para>
/// <list type="bullet">
///   <item>散放：<c>游戏名_a123.swf</c>（字母 = 站点，数字 = 站点里的编号）；</item>
///   <item>文件夹：<c>游戏名_a123s</c>（s = 用 Flash 播放器打开）/ <c>游戏名_a123h</c>（h = 用网页打开），
///         里面是 <c>游戏名_a123.swf</c> 或 <c>游戏名_a123.htm</c>，外加 <c>SWFNetPath.ini</c>。</item>
/// </list>
/// </summary>
public sealed partial class GameEntry
{
    /// <summary>文件名或文件夹名（含后缀字母，例如 <c>黄金矿工_a123s</c>）。</summary>
    public required string FileOrFolderName { get; init; }

    /// <summary>是不是文件夹。</summary>
    public required bool IsFolder { get; init; }

    /// <summary>去掉后缀字母的游戏名 / 文件名去掉 .swf。</summary>
    public required string GameName { get; init; }

    /// <summary>文件夹的扩展名含义：<c>.swf</c> / <c>.htm</c> / <c>.unknown</c>（散放的 swf 条目恒为 <c>.swf</c>）。</summary>
    public required string FolderExt { get; init; }

    /// <summary>游戏后缀，形如 <c>a123</c>；识别不到就是空字符串。</summary>
    public string Appendix { get; init; } = "";

    /// <summary>完整路径。</summary>
    public required string FullPath { get; init; }

    /// <summary>文件夹名是旧格式（<c>xxx_a123</c>，缺 s / h），需要用户手工改名。</summary>
    public bool IsLegacyFolderName { get; init; }

    /// <summary>文件或文件夹大小（文件夹 = 里面全部文件之和）；拿不到返回 -1。</summary>
    public long SizeBytes { get; init; } = -1;

    /// <summary>ini 的完整路径（文件夹条目才有意义）。</summary>
    public string IniFullPath => Path.Combine(FullPath, "SWFNetPath.ini");

    /// <summary>
    /// 游戏本体（文件夹里那个 swf / htm）的完整路径。
    /// 散放的 swf 条目本身就是本体，所以直接用 <see cref="FullPath"/>
    /// （这里踩过一次：写成 <c>FullPath 里再拼一层</c>，于是列表里每个散放 swf 都报“找不到本体”）。
    /// </summary>
    public string GameFullPath => IsFolder ? Path.Combine(FullPath, GameName + FolderExt) : FullPath;

    /// <summary>要播放的内容在不在。</summary>
    public bool HasGameFile => GameFullPath.Length > 0 && File.Exists(GameFullPath);

    /// <summary>列表里显示的名字（文件夹带一个尾斜杠，一眼区分）。</summary>
    public string DisplayText => IsFolder ? FileOrFolderName + "/" : FileOrFolderName;

    /// <summary>“文件”还是“文件夹”。</summary>
    public string TypeText => IsFolder ? "文件夹" : "SWF 文件";

    /// <summary>用哪种方式打开（文件夹条目）。</summary>
    public string OpenModeText => !IsFolder ? "Flash 播放器"
        : FolderExt == ".htm" ? "网页"
        : FolderExt == ".swf" ? "Flash 播放器"
        : "未知";

    /// <summary>建议的打开方式能不能用。</summary>
    public bool CanPlay => !IsFolder || FolderExt is ".swf" or ".htm";

    /// <summary>能不能「装入文件夹」（只有散放的 swf 可以）。</summary>
    public bool CanMoveIntoFolder => !IsFolder && GameName.Length > 0;

    /// <summary>能不能「改为 htm 打开」（只有 s 文件夹可以）。</summary>
    public bool CanConvertToHtm => IsFolder && FolderExt == ".swf" && !IsLegacyFolderName;

    /// <summary>能不能用「关联文件下载器」（文件夹才行）。</summary>
    public bool CanDownloadRelated => IsFolder;

    /// <summary>站点（从后缀字母猜）。</summary>
    public GameSite Site => Appendix.Length > 0 ? GameSites.FromLetter(Appendix[0]) : GameSite.Unknown;

    /// <summary>站点显示名。</summary>
    public string SiteText => Appendix.Length > 0 ? GameSites.DisplayName(Site) : "—";

    /// <summary>
    /// 站点排序键：4399 → 7k7k → 17yy → ArmorGames → Kongregate → 其它。
    /// 不能拿 <see cref="SiteText"/> 直接排——那会按中文字符串比，
    /// 「7k7k」会跑到「4399」前面（自检里就是这么发现的）。
    /// </summary>
    public int SiteSortKey => Site switch
    {
        GameSite.Site4399 => 1,
        GameSite.Site7k7k => 2,
        GameSite.Site17yy => 3,
        GameSite.SiteArmorGames => 4,
        GameSite.SiteKongregate => 5,
        _ => 9,
    };

    /// <summary>给用户看的后缀文本。</summary>
    public string AppendixText => Appendix.Length > 0 ? Appendix : "—";

    /// <summary>大小文本。</summary>
    public string SizeText => SizeBytes < 0 ? "—"
        : SizeBytes < 1024 ? $"{SizeBytes} B"
        : SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024.0:0.#} KB"
        : $"{SizeBytes / 1024.0 / 1024.0:0.##} MB";

    /// <summary>列表中“说明”那一列。</summary>
    public string DetailText
        => IsLegacyFolderName ? "早期版本命名（请在名字后加 s 或 h）"
        : $"{OpenModeText} · {SizeText}";

    /// <summary>从文件夹构造。</summary>
    public static GameEntry FromFolder(string fullPath, string folderName)
    {
        var ext = FolderExtOf(folderName);
        return new GameEntry
        {
            FileOrFolderName = folderName,
            IsFolder = true,
            GameName = GameNameOfFolder(folderName),
            FolderExt = ext,
            Appendix = AppendixOfFolder(folderName),
            FullPath = fullPath,
            IsLegacyFolderName = IsLegacy(folderName),
            SizeBytes = DirectorySize(fullPath),
        };
    }

    /// <summary>从散放的 swf 文件构造。</summary>
    public static GameEntry FromSwfFile(string fullPath, string fileName)
    {
        var length = -1L;
        try { length = new FileInfo(fullPath).Length; } catch { }
        return new GameEntry
        {
            FileOrFolderName = fileName,
            IsFolder = false,
            GameName = fileName.Length > 4 ? fileName[..^4] : fileName,
            FolderExt = ".swf",
            Appendix = AppendixOfSwfFile(fileName),
            FullPath = fullPath,
            IsLegacyFolderName = false,
            SizeBytes = length,
        };
    }

    /// <summary>文件夹名的尾字母 → 扩展名含义。</summary>
    public static string FolderExtOf(string folderName)
        => FolderNewPattern().Match(folderName) is { Success: true } m
            ? (char.ToLowerInvariant(m.Groups[3].Value[0]) == 's' ? ".swf" : ".htm")
            : ".unknown";

    /// <summary>
    /// 文件夹名 → 游戏名（<c>黄金矿工_a12345s</c> → <c>黄金矿工_a12345</c>）。
    ///
    /// <para>⚠ 只去掉最后那一个 s / h：文件夹名是「游戏名 + 站点后缀 + 打开方式字母」三位一体，
    /// 去掉 s / h 之后剩下的才是里面 swf / htm 该叫的名字
    /// （<c>黄金矿工_a12345s/黄金矿工_a12345.swf</c>）。多去掉一点就会把站点后缀一起吃掉，
    /// 于是 <c>GameFullPath</c> 指到不存在的文件上（自检里踩过）。</para>
    /// </summary>
    public static string GameNameOfFolder(string folderName)
    {
        if (FolderNewPattern().IsMatch(folderName)) return folderName[..^1];
        return FolderOldPattern().IsMatch(folderName) ? folderName : "";
    }

    /// <summary>文件夹名 → 站点后缀（<c>黄金矿工_a12345s</c> → <c>a12345</c>）。</summary>
    public static string AppendixOfFolder(string folderName)
    {
        var match = FolderNewPattern().Match(folderName);
        if (match.Success) return match.Groups[2].Value;
        match = FolderOldPattern().Match(folderName);
        return match.Success ? match.Groups[2].Value : "";
    }

    /// <summary>swf 文件名 → 后缀（<c>a123</c>）。</summary>
    public static string AppendixOfSwfFile(string fileName)
    {
        var match = SwfAppendixPattern().Match(fileName);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>旧命名（<c>xxx_a123</c>，缺 s / h）——需要用户手工改名才能在早期版本工具里识别。</summary>
    public static bool IsLegacy(string folderName)
        => !FolderNewPattern().IsMatch(folderName) && FolderOldPattern().IsMatch(folderName);

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return -1; }
    }

    public override string ToString() => DisplayText;

    /// <summary>新命名：<c>游戏名</c> + <c>_站点字母+编号</c> + <c>s/h</c>。</summary>
    [GeneratedRegex(@"^(.+)_([a-z][0-9]+)([sh])$", RegexOptions.IgnoreCase)]
    private static partial Regex FolderNewPattern();

    /// <summary>旧命名：<c>游戏名_a123</c>（没有 s / h）。</summary>
    [GeneratedRegex(@"^(.+)_([a-z][0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FolderOldPattern();

    /// <summary>散放的 swf：<c>游戏名_a123.swf</c>。</summary>
    [GeneratedRegex(@"_([a-z][0-9]+)\.swf$", RegexOptions.IgnoreCase)]
    private static partial Regex SwfAppendixPattern();
}
