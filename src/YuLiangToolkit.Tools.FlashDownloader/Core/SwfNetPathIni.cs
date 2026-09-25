namespace YuLiangToolkit.Tools.FlashDownloader.Core;

/// <summary>文件夹标记的类型（<c>SWFNetPath.ini</c> 第二行）。</summary>
public enum GameFolderKind
{
    /// <summary>用 Flash 播放器打开（目录名以 <c>s</c> 结尾）。</summary>
    Swf = 0,

    /// <summary>用网页打开（目录名以 <c>h</c> 结尾）。</summary>
    Htm = 1,

    /// <summary>认不出来（目录名后缀不是 s / h）。</summary>
    Unknown = 2,
}

/// <summary>
/// 游戏文件夹里的 <c>SWFNetPath.ini</c>（纯逻辑 + 文本读写，不依赖 WPF）。
///
/// <para><b>为什么需要这个文件</b>：批量下载要记住 swf 在网上的位置，网页打开还要记宽高，
/// 所以每个游戏文件夹里放一个纯文本 ini，人也可以直接改（记事本能打开）。</para>
///
/// <para><b>格式</b>（与原始 Python 项目完全一致）：</para>
/// <code>
/// 第 1 行：网络路径（s 文件夹放 swf 地址；h 文件夹放 htm 地址，由无新页面的 swf 转来的也放 swf 地址）
/// 第 2 行：必须为 swf / htm / unknown
/// swf  还多三行：游戏宽度（正整数或 Unknown）/ 游戏高度 / 是否有新页面（True / False / Unknown）
///                第五行若是 True，再补第 6 行：新页面网址
/// htm  还多两行：游戏宽度 / 游戏高度
/// </code>
/// </summary>
public sealed class SwfNetPathIni
{
    /// <summary>文件类型行：swf。</summary>
    public const string TypeSwf = "swf";

    /// <summary>文件类型行：htm。</summary>
    public const string TypeHtm = "htm";

    /// <summary>文件类型行：unknown。</summary>
    public const string TypeUnknown = "unknown";

    /// <summary>宽度 / 高度未知。</summary>
    public const int UnknownSize = -1;

    /// <summary>「是否有新页面」的三种取值。</summary>
    public const int NewPageUnknown = -1;
    public const int NewPageFalse = 0;
    public const int NewPageTrue = 1;

    /// <summary>ini 的完整路径。</summary>
    public string FilePath { get; set; } = "";

    /// <summary>文件夹名后缀决定的类型（<c>.swf</c> / <c>.htm</c> / <c>.unknown</c>）。</summary>
    public string FolderExt { get; set; } = ".unknown";

    /// <summary>第一行：网络路径（批量下载的基地址）。</summary>
    public string NetPath { get; set; } = "";

    /// <summary>第二行：类型。</summary>
    public GameFolderKind Kind { get; set; } = GameFolderKind.Swf;

    /// <summary>游戏宽度（&lt;= 0 = 未知）。</summary>
    public int Width { get; set; } = UnknownSize;

    /// <summary>游戏高度（&lt;= 0 = 未知）。</summary>
    public int Height { get; set; } = UnknownSize;

    /// <summary>swf 是否藏在新页面后面。</summary>
    public int IsNewPage { get; set; } = NewPageUnknown;

    /// <summary>新页面网址（仅 <see cref="Kind"/> 为 swf 且 <see cref="IsNewPage"/> 为
    /// <see cref="NewPageTrue"/> 时有意义）。</summary>
    public string NewPageUrl { get; set; } = "";

    /// <summary>文件是否存在。</summary>
    public bool Exists => FilePath.Length > 0 && File.Exists(FilePath);

    /// <summary>读取过程中攒下的问题描述（空 = 一切正常）。</summary>
    public string ErrorText { get; private set; } = "";

    /// <summary>有没有需要用户注意的问题。</summary>
    public bool HasProblem => ErrorText.Length > 0;

    /// <summary>类型行的文本。</summary>
    public string KindText => Kind switch
    {
        GameFolderKind.Swf => TypeSwf,
        GameFolderKind.Htm => TypeHtm,
        _ => TypeUnknown,
    };

    /// <summary>磁盘上真实存在但不在本类覆盖范围的情况：文件根本不在了。</summary>
    public bool IsMissing { get; private set; }

    /// <summary>按文件夹扩展名推导期望的类型。</summary>
    public static GameFolderKind KindFromFolderExt(string? folderExt) => (folderExt ?? "").ToLowerInvariant() switch
    {
        ".swf" => GameFolderKind.Swf,
        ".htm" => GameFolderKind.Htm,
        _ => GameFolderKind.Unknown,
    };

    /// <summary>界面上显示用的一段摘要。</summary>
    public string Describe()
    {
        var size = Width > 0 && Height > 0 ? $"{Width}×{Height}" : "尺寸未知";
        return Kind switch
        {
            GameFolderKind.Swf when IsNewPage == NewPageTrue => $"{KindText} · {size} · 新页面：{NewPageUrl}",
            GameFolderKind.Swf when IsNewPage == NewPageFalse => $"{KindText} · {size} · 无新页面",
            GameFolderKind.Swf => $"{KindText} · {size} · 新页面：未知",
            GameFolderKind.Htm => $"{KindText} · {size}",
            _ => KindText,
        };
    }

    /// <summary>按类型生成文件内容（不落盘，方便断言）。</summary>
    public string BuildContent()
    {
        var lines = new List<string> { NetPath, KindText };
        switch (Kind)
        {
            case GameFolderKind.Swf:
                lines.Add(Width > 0 ? Width.ToString() : "Unknown");
                lines.Add(Height > 0 ? Height.ToString() : "Unknown");
                lines.Add(IsNewPage switch
                {
                    NewPageTrue => "True",
                    NewPageFalse => "False",
                    _ => "Unknown",
                });
                if (IsNewPage == NewPageTrue) lines.Add(NewPageUrl);
                break;
            case GameFolderKind.Htm:
                lines.Add(Width > 0 ? Width.ToString() : "Unknown");
                lines.Add(Height > 0 ? Height.ToString() : "Unknown");
                break;
            default:
                throw new InvalidOperationException($"未知的 ini 类型：{KindText}（只能是 swf / htm / unknown）");
        }
        return string.Join("\n", lines);
    }

    /// <summary>写盘。失败抛异常（调用方提示用户）。</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, BuildContent());
        ErrorText = "";
        IsMissing = false;
    }

    /// <summary>
    /// 解析文本内容（纯函数，对应 Python 的 <c>IniResult.readFile</c>）。
    /// <paramref name="folderExt"/> 决定“应该是什么类型”，类型行对不上时会记进
    /// <see cref="ErrorText"/> 并按 <paramref name="folderExt"/> 强制归一。
    /// </summary>
    public static SwfNetPathIni Parse(string? content, string folderExt)
    {
        var ini = new SwfNetPathIni { FolderExt = folderExt, Kind = KindFromFolderExt(folderExt) };
        var lines = (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string Line(int index) => index < lines.Length ? lines[index].Trim() : "";

        ini.NetPath = Line(0);
        var typeLine = Line(1);
        var expected = ini.Kind;

        switch (expected)
        {
            case GameFolderKind.Swf:
                if (typeLine.Length == 0)
                {
                    // 老版本的 ini（没有类型行）：按旧格式补齐并提醒用户
                    ini.ErrorText += "版本更新，旧的 SWFNetPath.ini 需要更改（缺第二行的类型）。";
                }
                else if (!typeLine.Equals(TypeSwf, StringComparison.OrdinalIgnoreCase))
                {
                    ini.ErrorText += $"类型不匹配，应为 swf，实为 {typeLine}。";
                }
                ini.Width = ParseSize(Line(2), ini, 2);
                ini.Height = ParseSize(Line(3), ini, 3);
                var newPageLine = Line(4);
                if (typeLine.Length > 0 || newPageLine.Length > 0)
                {
                    switch (newPageLine)
                    {
                        case "True": ini.IsNewPage = NewPageTrue; break;
                        case "False": ini.IsNewPage = NewPageFalse; break;
                        case "Unknown": ini.IsNewPage = NewPageUnknown; break;
                        default:
                            ini.IsNewPage = NewPageUnknown;
                            if (!newPageLine.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                                ini.ErrorText += $"第五行只能为 True / False / Unknown，而非 {newPageLine}。";
                            break;
                    }
                    if (ini.IsNewPage == NewPageTrue) ini.NewPageUrl = Line(5);
                }
                break;

            case GameFolderKind.Htm:
                if (!typeLine.Equals(TypeHtm, StringComparison.OrdinalIgnoreCase))
                    ini.ErrorText += $"类型不匹配，应为 htm，实为 {typeLine}。";
                ini.Width = ParseSize(Line(2), ini, 2);
                ini.Height = ParseSize(Line(3), ini, 3);
                break;

            default:
                if (typeLine.Length > 0 && !typeLine.Equals(TypeUnknown, StringComparison.OrdinalIgnoreCase))
                    ini.ErrorText += $"类型不匹配，应为 unknown，实为 {typeLine}。";
                break;
        }

        return ini;
    }

    /// <summary>从磁盘读（文件不存在也不抛异常，只把 <see cref="IsMissing"/> 立起来）。</summary>
    public void Load()
    {
        ErrorText = "";
        NewPageUrl = "";
        IsNewPage = NewPageUnknown;
        Width = UnknownSize;
        Height = UnknownSize;

        if (FilePath.Length == 0 || !File.Exists(FilePath))
        {
            IsMissing = true;
            Kind = KindFromFolderExt(FolderExt);
            ErrorText = "文件不存在。";
            return;
        }

        var parsed = Parse(File.ReadAllText(FilePath), FolderExt);
        NetPath = parsed.NetPath;
        Kind = parsed.Kind;
        Width = parsed.Width;
        Height = parsed.Height;
        IsNewPage = parsed.IsNewPage;
        NewPageUrl = parsed.NewPageUrl;
        ErrorText = parsed.ErrorText;
        IsMissing = false;
    }

    /// <summary>从磁盘读的便捷方法。</summary>
    public static SwfNetPathIni FromFile(string filePath, string folderExt)
    {
        var ini = new SwfNetPathIni { FilePath = filePath, FolderExt = folderExt };
        ini.Load();
        return ini;
    }

    /// <summary>第三 / 第四行：正整数或 <c>Unknown</c>，其它值记问题并返回 -1。</summary>
    private static int ParseSize(string text, SwfNetPathIni ini, int lineNumber)
    {
        if (text.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return UnknownSize;
        if (text.Length == 0) return UnknownSize;
        if (int.TryParse(text, out var value) && value > 0) return value;
        ini.ErrorText += $"第{lineNumber + 1}行应为正整数 | Unknown，而非 {text}。";
        return UnknownSize;
    }
}
