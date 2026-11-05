using System.Text;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.SoftwareUpdater.Winget;

/// <summary>
/// 手写解析 <c>winget list</c> / <c>winget upgrade</c> 的表格输出（不引第三方包、不依赖 PowerShell 模块）。
///
/// <para><b>为什么不是简单 Split(' ')</b>：winget 的表头是中文（"名称 / ID / 版本 / 可用 / 源"），
/// 中文字符在终端里占 2 列，而 .NET 的 string 索引按 UTF-16 码元算 1；应用名里也常带空格和中文
/// （例如 <c>BitComet(比特彗星) 2.19</c>），按空格切必然错列。这里的做法是：</para>
/// <list type="number">
///   <item>先按「显示列宽」（CJK / 全角算 2）算出表头每个词的起始列；</item>
///   <item>数据行用同样的显示列宽切到对应区间，再 Trim；</item>
///   <item>统计行、分隔线、进度动画、ANSI 转义、<c>\r</c> 覆盖的旧帧全部过滤掉。</item>
/// </list>
///
/// <para>列角色同时认中文与英文表头；winget 版本之间列宽变化（宽终端 / 重定向）都不影响，
/// 因为列起点是从当次输出的表头现算的。</para>
/// </summary>
public static class WingetOutputParser
{
    private static readonly Regex AnsiPattern = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private enum Role { None, Name, Id, Version, Available, Source, Ignore }

    private sealed record Column(Role Role, int Start, int End);

    /// <summary>
    /// 清洗一行原始输出：取 <c>\r</c> 之后的最后一帧（进度动画用回车覆盖）、去 ANSI 颜色 / 光标控制、
    /// 去其余控制字符。表格本身左对齐，所以只 TrimEnd、保留前导空格。
    /// </summary>
    public static string CleanLine(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var line = raw;
        var cut = line.LastIndexOf('\r');
        if (cut >= 0) line = line[(cut + 1)..];
        line = AnsiPattern.Replace(line, "");

        var sb = new StringBuilder(line.Length);
        foreach (var ch in line)
        {
            if (ch == '\t') sb.Append(' ');
            else if (ch >= ' ' && ch != '\u007F') sb.Append(ch);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>表格分隔线（纯 '-' / '─' / '=' 组成）。</summary>
    public static bool IsSeparatorLine(string line)
    {
        var text = line.Trim();
        if (text.Length < 3) return false;
        foreach (var ch in text)
        {
            if (ch is not ('-' or '=' or '─' or '—' or '═')) return false;
        }
        return true;
    }

    /// <summary>winget 末尾的统计行（"41 升级可用。" / "41 upgrades available."）。</summary>
    public static bool IsSummaryLine(string line)
        => line.Contains("升级可用", StringComparison.Ordinal)
           || line.Contains("upgrades available", StringComparison.OrdinalIgnoreCase)
           || line.Contains("已找到", StringComparison.Ordinal);

    /// <summary>是不是表格表头行（中文或英文）。</summary>
    public static bool IsTableHeader(string line)
    {
        if (line.Length == 0 || IsSeparatorLine(line)) return false;
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hasId = words.Any(word => word.Equals("ID", StringComparison.OrdinalIgnoreCase));
        var hasName = words.Any(word => word.Contains("名称", StringComparison.Ordinal)
                                        || word.Equals("Name", StringComparison.OrdinalIgnoreCase));
        var hasVersion = words.Any(word => word.Contains("版本", StringComparison.Ordinal)
                                           || word.Contains("Version", StringComparison.OrdinalIgnoreCase));
        return hasId && hasName && hasVersion;
    }

    /// <summary>
    /// 把 winget 的完整输出解析成应用列表。找不到表头 / 输出为空时返回空列表（不抛异常，
    /// 由调用方提示「没读到表格，可能是 winget 缺失或源未就绪」）。
    /// </summary>
    public static IReadOnlyList<WingetPackage> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<WingetPackage>();

        var lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(CleanLine)
            .ToList();

        var headerIndex = lines.FindIndex(IsTableHeader);
        if (headerIndex < 0) return Array.Empty<WingetPackage>();

        var columns = BuildColumns(lines[headerIndex]);
        var byId = new Dictionary<string, WingetPackage>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || IsSeparatorLine(line) || IsTableHeader(line) || IsSummaryLine(line)) continue;

            var fields = new Dictionary<Role, string>();
            foreach (var column in columns)
            {
                if (column.Role is Role.None or Role.Ignore) continue;
                fields[column.Role] = FieldAt(line, column.Start, column.End);
            }

            var id = fields.GetValueOrDefault(Role.Id) ?? "";
            if (id.Length == 0 || id.Equals("ID", StringComparison.OrdinalIgnoreCase)) continue;

            var name = fields.GetValueOrDefault(Role.Name) ?? "";
            if (name.Length == 0) name = id;

            var package = new WingetPackage(
                name,
                id,
                fields.GetValueOrDefault(Role.Version) ?? "",
                fields.GetValueOrDefault(Role.Available) ?? "",
                fields.GetValueOrDefault(Role.Source) ?? "");

            var key = package.Source + "|" + package.Id;
            if (!byId.TryGetValue(key, out var existing))
            {
                byId[key] = package;
                order.Add(key);
            }
            else if (!existing.HasUpdate && package.HasUpdate)
            {
                // 同一个包被列了两次（例如 winget 源 + ARP 源）时，保留有可用版本的那条。
                byId[key] = package;
            }
        }

        return order.Select(key => byId[key]).ToList();
    }

    /// <summary>取尾部统计文字（给状态栏 / 日志用）；没有就返回空串。</summary>
    public static string FindSummary(string? output)
    {
        if (string.IsNullOrEmpty(output)) return "";
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = lines.Length - 1; i >= 0 && i >= lines.Length - 6; i--)
        {
            var line = CleanLine(lines[i]).Trim();
            if (IsSummaryLine(line)) return line;
        }
        return "";
    }

    // ---------------------------------------------------------------- 列布局

    /// <summary>按显示列宽把表头切成「词 + 起始列」。</summary>
    private static List<(string Text, int Start)> Tokenize(string line)
    {
        var result = new List<(string, int)>();
        var column = 0;
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                column += CharWidth(line[i]);
                i++;
            }
            if (i >= line.Length) break;

            var start = column;
            var word = new StringBuilder();
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                word.Append(line[i]);
                column += CharWidth(line[i]);
                i++;
            }
            result.Add((word.ToString(), start));
        }
        return result;
    }

    private static List<Column> BuildColumns(string header)
    {
        var tokens = Tokenize(header);
        var roles = tokens.Select(token => Classify(token.Text)).ToList();

        // 英文表头可能是 "Available Version" 两个词：把 Version 并进 Available 列，
        // 否则可用版本会落在被忽略的那一格里。
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (roles[i] == Role.Available
                && tokens[i + 1].Text.Contains("Version", StringComparison.OrdinalIgnoreCase))
            {
                roles[i + 1] = Role.Ignore;
            }
        }

        // 重复角色（例如上面的 Version 出现在两个位置）只保留第一个。
        var seen = new HashSet<Role>();
        for (var i = 0; i < roles.Count; i++)
        {
            if (roles[i] is Role.None or Role.Ignore) continue;
            if (!seen.Add(roles[i])) roles[i] = Role.Ignore;
        }

        // 表头认不出来时按 winget 的固定列序兜底：名称 / ID / 版本 / 可用 / 源。
        if (!seen.Contains(Role.Id) || !seen.Contains(Role.Name) || !seen.Contains(Role.Available))
        {
            var fallback = new[] { Role.Name, Role.Id, Role.Version, Role.Available, Role.Source };
            roles = tokens.Select((_, index) => index < fallback.Length ? fallback[index] : Role.None).ToList();
        }

        // 合并连续列：Ignore 的列不产出字段，但它的起点仍是下一列的边界，
        // 所以这里直接把「有效列」的 End 设成下一个有效列的 Start，中间列的内容被丢掉。
        var effective = new List<(Role Role, int Start)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (roles[i] is Role.None or Role.Ignore) continue;
            effective.Add((roles[i], tokens[i].Start));
        }

        var columns = new List<Column>(effective.Count);
        for (var i = 0; i < effective.Count; i++)
        {
            var end = i + 1 < effective.Count ? effective[i + 1].Start : int.MaxValue;
            columns.Add(new Column(effective[i].Role, effective[i].Start, end));
        }
        return columns;
    }

    private static Role Classify(string word)
    {
        if (word.Contains("名称", StringComparison.Ordinal)
            || word.Equals("Name", StringComparison.OrdinalIgnoreCase)) return Role.Name;
        if (word.Equals("ID", StringComparison.OrdinalIgnoreCase)) return Role.Id;
        if (word.Contains("可用", StringComparison.Ordinal)
            || word.Contains("Available", StringComparison.OrdinalIgnoreCase)) return Role.Available;
        if (word.Contains("版本", StringComparison.Ordinal)
            || word.Contains("Version", StringComparison.OrdinalIgnoreCase)) return Role.Version;
        if (word.Contains("源", StringComparison.Ordinal)
            || word.Equals("Source", StringComparison.OrdinalIgnoreCase)) return Role.Source;
        return Role.None;
    }

    /// <summary>取一行里落在 [start, end) 显示列区间的文字（按显示列宽切，中文不会错位）。</summary>
    private static string FieldAt(string line, int start, int end)
    {
        var sb = new StringBuilder();
        var column = 0;
        foreach (var ch in line)
        {
            if (column >= start && column < end) sb.Append(ch);
            column += CharWidth(ch);
            if (column >= end && end != int.MaxValue) break;
        }
        return sb.ToString().Trim();
    }

    /// <summary>终端显示宽度：CJK / 全角 / 假名 / 韩文算 2，其余算 1，控制字符算 0。</summary>
    private static int CharWidth(char ch)
    {
        if (char.IsControl(ch)) return 0;
        if (ch < 0x7F) return 1;
        if (ch is >= '\u0300' and <= '\u036F') return 0; // 组合音标不占列
        if (ch is >= '\u1100' and <= '\u115F') return 2; // 韩文字母
        if (ch is >= '\u2E80' and <= '\uA4CF') return 2; // CJK 部首 ~ 彝文
        if (ch is >= '\uAC00' and <= '\uD7A3') return 2; // 韩文音节
        if (ch is >= '\uF900' and <= '\uFAFF') return 2; // 兼容汉字
        if (ch is >= '\uFE10' and <= '\uFE6F') return 2; // 全角标点
        if (ch is >= '\uFF00' and <= '\uFF60') return 2; // 全角 ASCII
        if (ch is >= '\uFFE0' and <= '\uFFE6') return 2; // 全角符号
        return 1; // 代理对的两个 char 各算 1，合起来正好 2
    }
}
