using YuLiangToolkit.Core.Documents;

namespace YuLiangToolkit.Tools.ExcelViewer.Parsing;

/// <summary>
/// 一个单元格上的超链接（只读模型）。
///
/// <para>Excel 的超链接有两大类：</para>
/// <list type="bullet">
///   <item><b>外部链接</b>：<c>&lt;hyperlink r:id="rId1"/&gt;</c> + 工作表自己的
///         <c>xl/worksheets/_rels/sheetN.xml.rels</c> 里 <c>TargetMode="External"</c> 的 URL
///         （http / https / mailto / file…，打开前还要过一遍 <see cref="ExcelHyperlinks.IsAllowedProtocol"/>）；</item>
///   <item><b>内部链接</b>：<c>&lt;hyperlink location="A1"/&gt;</c>，location 可以是
///         <c>A1</c>、<c>Sheet2!A1</c>、<c>'我的 表'!B2</c>，有的文件还会带前导 <c>#</c>。</item>
/// </list>
///
/// <para><b>Target / Location 各存各的</b>：外部链接把 URL 放 <see cref="Target"/>，
/// 内部链接把 location 放 <see cref="Location"/>；界面 / 状态栏不用再猜哪边有值。</para>
/// </summary>
/// <param name="Target">外部链接的目标 URL（内部链接为空串）。</param>
/// <param name="IsExternal">是不是外部链接（来自 <c>TargetMode="External"</c> 的关系）。</param>
/// <param name="Location">内部链接位置（已去掉 <c>#</c>，保留引号表名的原始写法）。</param>
/// <param name="Display">文件里写的 display 属性（用户看到的链接文字），可能为空。</param>
public sealed record ExcelHyperlink(string Target, bool IsExternal, string Location, string Display)
{
    /// <summary>是不是工作簿内部链接（跳到某张表的某个格）。</summary>
    public bool IsInternal => !IsExternal;

    /// <summary>单元格悬停提示：外部给 URL，内部给 location。</summary>
    public string ToolTipText => IsExternal
        ? (string.IsNullOrEmpty(Display) ? Target : $"{Target}{Environment.NewLine}{Display}")
        : $"内部链接：{Location}";

    /// <summary>状态栏里的一句话，例如「链接：https://…」/「内部链接：Sheet2!A1」。</summary>
    public string StatusText => IsExternal ? $"链接：{Target}" : $"内部链接：{Location}";

    /// <summary>显示文字：有 display 用 display，没有就用 URL / location。</summary>
    public string DisplayText
    {
        get
        {
            if (!string.IsNullOrEmpty(Display)) return Display;
            return IsExternal ? Target : Location;
        }
    }
}

/// <summary>
/// 超链接解析 + 打开策略的<b>纯逻辑</b>：把 worksheet 里的 <c>hyperlinks/hyperlink</c>、
/// 关系文件里的 URL、<c>location</c> 文本解析成 <see cref="ExcelHyperlink"/>；
/// 以及「哪些协议允许用系统默认程序打开」的白名单。
///
/// <para><b>为什么抽成纯函数</b>：解析在后台线程、打开链接在 UI 线程，中间只该传纯数据；
/// 协议白名单是安全边界（<c>file:</c> / <c>javascript:</c> / <c>data:</c> 绝不能交给
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>），
/// 纯函数放这里自检可以直接断言，也避免以后在别处又写一份不一致的判断。</para>
///
/// <para><b>容错约定</b>：缺 rels / 坏 rels / 坏 ref / 非法 location 一律返回 null / false，
/// 不抛异常；一个坏链接不能拖垮整张表。</para>
/// </summary>
public static class ExcelHyperlinks
{
    /// <summary>
    /// 一张工作表最多给多少个格子挂链接（和合并单元格的标记预算同理，防畸形文件里
    /// 一个 <c>ref="A1:XFD1048576"</c> 的超链接把解析拖死）。超过后多余链接直接忽略。
    /// </summary>
    public const int MaxHyperlinkCells = 200_000;

    /// <summary>允许用系统默认程序打开的协议（小写比较）：<b>只放行 http / https / mailto</b>。</summary>
    private static readonly string[] AllowedSchemes = { "http", "https", "mailto" };

    /// <summary>
    /// 判断一个地址能不能「双击打开」：必须是绝对 URI，且协议在 <c>http / https / mailto</c> 白名单里。
    /// <c>file:</c> / <c>javascript:</c> / <c>data:</c> / <c>ftp:</c> 等一律返回 false。
    /// </summary>
    public static bool IsAllowedProtocol(string? target) => TryCreateLaunchUri(target, out _);

    /// <summary>
    /// 把外部链接地址规范成可交给 <c>Process.Start</c> 的绝对 URI（只放行白名单协议）。
    /// 空串 / 相对地址 / 不支持的协议返回 false。
    /// </summary>
    public static bool TryCreateLaunchUri(string? target, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(target)) return false;

        var text = target.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var candidate)) return false;
        if (!IsAllowedScheme(candidate.Scheme)) return false;

        uri = candidate;
        return true;
    }

    /// <summary>
    /// 取地址里的协议名（小写），非法 / 没有协议时返回空串。
    /// 状态栏「当前协议 file 不允许」这类提示要用它，别把整个 URL 回显给用户。
    /// </summary>
    public static string GetProtocol(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "";

        var text = target.Trim();
        var colon = text.IndexOf(':');
        if (colon <= 0) return "";

        var scheme = text[..colon];
        return scheme.All(char.IsAsciiLetterOrDigit) ? scheme.ToLowerInvariant() : "";
    }

    /// <summary>
    /// 把 worksheet 里的一条超链接 + 它可能引用到的关系，解析成只读模型。
    /// 解析不出任何可用目标（既没有 location，也没有外部 URL）时返回 null。
    /// </summary>
    /// <param name="location">XML 的 <c>location</c> 属性（内部链接位置）。</param>
    /// <param name="display">XML 的 <c>display</c> 属性（显示文字）。</param>
    /// <param name="relationship">按 <c>r:id</c> 查到的关系；没有 / 坏 rels 时为 null。</param>
    public static ExcelHyperlink? Resolve(string? location, string? display, OoxmlRelationship? relationship)
    {
        var normalizedLocation = NormalizeLocation(location);
        var normalizedDisplay = display?.Trim() ?? "";

        if (relationship is not null)
        {
            if (relationship.IsExternal && !string.IsNullOrWhiteSpace(relationship.Target))
            {
                return new ExcelHyperlink(relationship.Target.Trim(), true, normalizedLocation, normalizedDisplay);
            }

            // 极少数文件把内部链接写成「内部关系 Target="#Sheet2!A1"」而不是 location；
            // 这里兜一下（ReadRelationships 会把包内相对路径归一化，所以只认以 # 开头的）。
            if (!relationship.IsExternal
                && normalizedLocation.Length == 0
                && relationship.Target.StartsWith('#'))
            {
                normalizedLocation = NormalizeLocation(relationship.Target);
            }
        }

        if (normalizedLocation.Length == 0) return null;

        return new ExcelHyperlink("", false, normalizedLocation, normalizedDisplay);
    }

    /// <summary>
    /// 解析内部链接的 location，支持以下写法（可带前导 <c>#</c>、<c>$</c> 绝对引用、单元格区域取左上角）：
    /// <list type="bullet">
    ///   <item><c>A1</c>、<c>$B$2</c> → 当前工作表的格子；</item>
    ///   <item><c>Sheet2!A1</c> → 指定工作表；</item>
    ///   <item><c>'我的 表'!B2</c> → 引号包住含空格的表名（Excel 用两个单引号转义表名里的单引号）。</item>
    /// </list>
    /// 格式非法 / 目标看起来是跨工作簿（<c>[Book.xlsx]Sheet!A1</c>）时返回 false（行列输出 0）。
    /// </summary>
    public static bool TryParseLocation(string? location, out string sheetName, out int row, out int column)
    {
        sheetName = "";
        row = 0;
        column = 0;

        var text = NormalizeLocation(location);
        if (text.Length == 0) return false;

        // 表名里理论上不会出现 '!'（Excel 不允许），但为了容忍脏数据按最后一个 '!' 拆
        var bang = text.LastIndexOf('!');
        var cellText = text;
        if (bang >= 0)
        {
            var sheetPart = text[..bang].Trim();
            cellText = text[(bang + 1)..].Trim();
            if (sheetPart.Length == 0) return false;

            sheetName = UnquoteSheetName(sheetPart);
            if (sheetName.Length == 0) return false;
        }

        // 区域链接（A1:B2）跳到左上角即可；这里只要「能定位到一格」
        var rangeSeparator = cellText.IndexOf(':');
        if (rangeSeparator > 0) cellText = cellText[..rangeSeparator].Trim();

        if (!ExcelAddresses.TryParseCellReference(cellText, out row, out column))
        {
            sheetName = "";
            row = 0;
            column = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 去掉 location 首尾空白与前导 <c>#</c>（Excel 内部链接两种写法都有）。
    /// </summary>
    public static string NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "";

        var text = location.Trim();
        while (text.StartsWith('#')) text = text[1..].TrimStart();
        return text;
    }

    /// <summary>去掉引号表名的单引号，并把 Excel 的 <c>''</c> 转义还原成 <c>'</c>。</summary>
    private static string UnquoteSheetName(string raw)
    {
        var text = raw.Trim();
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            text = text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return text.Trim();
    }

    /// <summary>协议是否在白名单里（大小写不敏感）。</summary>
    private static bool IsAllowedScheme(string? scheme)
    {
        if (string.IsNullOrEmpty(scheme)) return false;

        foreach (var allowed in AllowedSchemes)
        {
            if (string.Equals(scheme, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
