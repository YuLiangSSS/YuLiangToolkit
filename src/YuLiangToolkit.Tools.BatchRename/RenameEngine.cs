using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.BatchRename;

/// <summary>一个文件在“预览计划”里的状态。</summary>
public enum RenameStatus
{
    /// <summary>会改名（新名合法且不冲突）。</summary>
    Ok,

    /// <summary>按当前规则算出来和原名一样，不用改。</summary>
    Unchanged,

    /// <summary>用户没勾选，本次跳过。</summary>
    Skipped,

    /// <summary>和别的文件（或磁盘上已有文件）撞名了。</summary>
    Conflict,

    /// <summary>新名非法（空、含非法字符、系统保留名…）或规则本身有问题。</summary>
    Error,

    /// <summary>已经改名成功（执行后的状态）。</summary>
    Done,

    /// <summary>改名失败（文件被占用、没权限…）。</summary>
    Failed,

    /// <summary>已被撤销（撤销后的状态）。</summary>
    Undone,
}

/// <summary>
/// 一个文件的改名计划：原路径 + 算出来的新名 + 状态。
/// <see cref="RenameEngine.BuildPlan"/> 产出，界面直接绑它。
/// </summary>
public sealed class RenamePlanEntry
{
    /// <summary>源文件完整路径。</summary>
    public required string SourcePath { get; init; }

    /// <summary>源文件所在目录（改名不会换目录）。</summary>
    public required string Directory { get; init; }

    /// <summary>原文件名。</summary>
    public required string OriginalName { get; init; }

    /// <summary>按规则算出来的新文件名（出错时也保留算出来的值，方便用户看出问题）。</summary>
    public string NewName { get; set; } = "";

    /// <summary>状态。</summary>
    public RenameStatus Status { get; set; } = RenameStatus.Unchanged;

    /// <summary>状态说明（冲突原因 / 失败原因 / 自动避让提示…）。</summary>
    public string Message { get; set; } = "";

    /// <summary>目标完整路径（<see cref="NewName"/> 为空时返回空串）。</summary>
    public string TargetPath => NewName.Length == 0 ? "" : Path.Combine(Directory, NewName);

    /// <summary>这个名字是否真的会被改。</summary>
    public bool WillRename => Status == RenameStatus.Ok;
}

/// <summary>
/// 批量重命名的<b>纯逻辑</b>：把「一组文件路径 + 一张规则清单」算成「每个文件的新名字 + 冲突情况」。
/// 不碰界面、不落盘，所以能被自检程序直接断言（见 <c>tests\YuLiangToolkit.FrameworkSelfCheck</c>）。
///
/// <para><b>规则是怎么套用的</b>：<see cref="BuildName"/> 先把文件名拆成「主名 + 扩展名」，
/// 然后<b>按清单顺序</b>逐条套用；每条规则只动它 <see cref="RenameRule.Scope"/> 指定的那一部分
/// （主名 / 扩展名 / 整个文件名）。同一种规则可以出现多次，顺序不同结果就不同。</para>
///
/// <para>执行改名的是 <see cref="RenameExecutor"/>，本类只管“算出名字对不对”。</para>
/// </summary>
public static class RenameEngine
{
    /// <summary>Windows 保留设备名（不能当文件名，即使带扩展名也不行）。</summary>
    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    private static HashSet<string> BuildReservedNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var i = 1; i <= 9; i++)
        {
            set.Add("COM" + i);
            set.Add("LPT" + i);
        }
        return set;
    }

    // =====================================================================
    //  1. 按规则清单算一个文件名
    // =====================================================================

    /// <summary>
    /// 按规则清单把一个文件名算成新文件名（只算名字，不判断冲突、不碰磁盘）。
    /// 结果为空字符串表示“规则把名字整个删没了”，调用方应判为错误。
    /// </summary>
    /// <param name="fileName">原文件名（可以带扩展名）。</param>
    /// <param name="sequenceIndex">它在列表里的<b>下标</b>（0 开始），用来算序号。</param>
    /// <param name="rules">规则清单，<b>顺序即执行顺序</b>；null / 空 = 不改名。</param>
    public static string BuildName(string fileName, int sequenceIndex, IReadOnlyList<RenameRule>? rules)
    {
        // 拆成主名 + 扩展名，之后每条规则只动自己负责的那一块
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        if (rules is not null)
        {
            foreach (var rule in rules)
            {
                if (rule is null || !rule.Enabled) continue;

                // 扩展名规则天生只动扩展名，不看 Scope
                if (rule.Kind == RenameRuleKind.Extension)
                {
                    extension = ApplyExtensionMode(extension, rule);
                    continue;
                }

                switch (rule.Scope)
                {
                    case RuleScope.Extension:
                        extension = ApplyRule(rule, extension, sequenceIndex);
                        break;

                    case RuleScope.FullName:
                    {
                        // 整名处理完要重新拆开，后面的规则才知道哪一段是扩展名
                        var full = ApplyRule(rule, stem + extension, sequenceIndex);
                        stem = Path.GetFileNameWithoutExtension(full);
                        extension = Path.GetExtension(full);
                        break;
                    }

                    default:
                        stem = ApplyRule(rule, stem, sequenceIndex);
                        break;
                }
            }
        }

        // 收尾：整体去首尾空白。Windows 会悄悄吃掉文件名结尾的空格和点，
        // 这里先统一处理掉，免得出现“预览里是一个名字、磁盘上是另一个名字”。
        return (stem + extension).Trim();
    }

    /// <summary>一条规则作用在一段文本上（<see cref="RenameRuleKind.Extension"/> 不走这里）。</summary>
    private static string ApplyRule(RenameRule rule, string text, int sequenceIndex) => rule.Kind switch
    {
        RenameRuleKind.Replace => ApplyReplace(text, rule),
        RenameRuleKind.Cleanup => ApplyCleanup(text, rule),
        RenameRuleKind.DeleteRange => ApplyDeletions(text, rule),
        RenameRuleKind.Case => ApplyCase(text, rule.CaseMode),
        RenameRuleKind.Affix => rule.Prefix + text + rule.Suffix,
        RenameRuleKind.Sequence => ApplySequence(text, sequenceIndex, rule),
        _ => text,
    };

    /// <summary>规则清单里有没有明显写错的地方（正则非法、新扩展名带非法字符…）；没问题返回 null。</summary>
    public static string? Validate(BatchRenameConfig cfg)
    {
        if (cfg.Rules is null || cfg.Rules.Count == 0) return null;

        for (var i = 0; i < cfg.Rules.Count; i++)
        {
            var rule = cfg.Rules[i];
            if (rule is null || !rule.Enabled) continue;
            var error = ValidateRule(rule);
            if (error is not null) return $"第 {i + 1} 条规则（{rule.KindName}）：{error}";
        }
        return null;
    }

    /// <summary>单条规则自身有没有写错的地方；没问题返回 null。</summary>
    public static string? ValidateRule(RenameRule rule)
    {
        if (rule.Kind == RenameRuleKind.Replace && rule.UseRegex && rule.FindText.Length > 0)
        {
            try
            {
                _ = new Regex(rule.FindText);
            }
            catch (ArgumentException ex)
            {
                return $"正则表达式无效：{ex.Message}";
            }
        }

        if (rule.Kind == RenameRuleKind.Extension
            && rule.ExtensionMode == ExtensionMode.Replace
            && rule.NewExtension.Trim().Length > 0
            && NormalizeExtension(rule.NewExtension).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return $"新扩展名含非法字符：{rule.NewExtension}";
        }

        return null;
    }

    /// <summary>
    /// 把界面上输入的扩展名过滤串解析成通配符数组：
    /// <c>"jpg; png、.bmp"</c> → <c>["*.jpg", "*.png", "*.bmp"]</c>（去重、忽略大小写、保留输入顺序）。
    /// 空输入返回空数组 = 不过滤。
    /// </summary>
    public static IReadOnlyList<string> ParseExtensionFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return Array.Empty<string>();
        return filter
            .Split(new[] { ';', ',', '，', '、', ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.TrimStart('*', '.'))
            .Where(token => token.Length > 0)
            .Select(token => "*." + token)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // =====================================================================
    //  2. 算整份计划（含冲突检测）
    // =====================================================================

    /// <summary>
    /// 把一组源文件算成完整的改名计划。
    ///
    /// <para>冲突判定规则：</para>
    /// <list type="bullet">
    ///   <item><b>互相撞名</b>：两个文件算出了同一个目标路径 → 都标 <see cref="RenameStatus.Conflict"/>；</item>
    ///   <item><b>位置被占</b>：目标路径上已经有文件，而且那个文件<b>不会在本次被移走</b>
    ///         （它没勾选 / 名字没变 / 自己也是冲突或错误）→ 冲突；</item>
    ///   <item>目标路径上恰好是名单里<b>另一个会被改名的文件</b>（例如 A→B、B→A 互换）→ <b>不算冲突</b>，
    ///         执行时用两阶段改名处理；</item>
    ///   <item>勾了 <see cref="BatchRenameConfig.AutoResolveConflicts"/> 则自动加 <c> (1)</c>、<c> (2)</c> 避让。</item>
    /// </list>
    /// <para>“谁会被移走”这件事本身取决于冲突判定结果，所以判定要迭代到稳定为止（见 <see cref="MarkConflicts"/>）。</para>
    /// </summary>
    /// <param name="sourcePaths">源文件路径（顺序 = 列表顺序 = 序号顺序）。</param>
    /// <param name="cfg">规则与选项。</param>
    /// <param name="include">
    /// 每个文件是否参与本次改名（对应界面上的勾选框）；传 null 表示全部参与。
    /// 未参与的文件不会被改名，所以也就<b>不能</b>把它现在占着的位置让给别人。
    /// </param>
    public static IReadOnlyList<RenamePlanEntry> BuildPlan(
        IReadOnlyList<string> sourcePaths,
        BatchRenameConfig cfg,
        IReadOnlyList<bool>? include = null)
    {
        var entries = new List<RenamePlanEntry>(sourcePaths.Count);
        var optionError = Validate(cfg);
        var rules = cfg.Rules ?? new List<RenameRule>();

        for (var i = 0; i < sourcePaths.Count; i++)
        {
            var source = sourcePaths[i];
            var directory = Path.GetDirectoryName(source) ?? "";
            var original = Path.GetFileName(source);
            var entry = new RenamePlanEntry
            {
                SourcePath = source,
                Directory = directory,
                OriginalName = original,
            };
            entries.Add(entry);

            if (optionError is not null)
            {
                entry.NewName = original;
                entry.Status = RenameStatus.Error;
                entry.Message = optionError;
                continue;
            }

            if (!IsIncluded(include, i))
            {
                entry.NewName = original;
                entry.Status = RenameStatus.Skipped;
                entry.Message = "未勾选";
                continue;
            }

            if (!File.Exists(source))
            {
                entry.NewName = original;
                entry.Status = RenameStatus.Error;
                entry.Message = "文件不存在或已被移走";
                continue;
            }

            string newName;
            try
            {
                newName = BuildName(original, i, rules);
            }
            catch (Exception ex)
            {
                entry.NewName = original;
                entry.Status = RenameStatus.Error;
                entry.Message = "规则计算失败：" + ex.Message;
                continue;
            }

            entry.NewName = newName;

            if (newName.Length == 0)
            {
                entry.Status = RenameStatus.Error;
                entry.Message = "新名为空（规则把名字全删掉了）";
                continue;
            }

            if (string.Equals(newName, original, StringComparison.Ordinal))
            {
                entry.Status = RenameStatus.Unchanged;
                entry.Message = "名称不变";
                continue;
            }

            var invalidIndex = newName.IndexOfAny(Path.GetInvalidFileNameChars());
            if (invalidIndex >= 0)
            {
                entry.Status = RenameStatus.Error;
                entry.Message = $"新名含非法字符「{newName[invalidIndex]}」";
                continue;
            }

            if (newName.Length > 200)
            {
                entry.Status = RenameStatus.Error;
                entry.Message = "新名过长（超过 200 个字符）";
                continue;
            }

            if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(newName)))
            {
                entry.Status = RenameStatus.Error;
                entry.Message = "新名是 Windows 保留设备名";
                continue;
            }

            entry.Status = RenameStatus.Ok;
        }

        MarkConflicts(entries);

        if (cfg.AutoResolveConflicts) ResolveConflicts(entries);

        return entries;
    }

    /// <summary>检查下标是否参与本次改名（<paramref name="include"/> 为 null 表示全参与）。</summary>
    private static bool IsIncluded(IReadOnlyList<bool>? include, int index)
        => include is null || (index >= 0 && index < include.Count && include[index]);

    /// <summary>
    /// 标记冲突：① 互相撞名 ② 目标位置被“本次不会移走的文件”占着。
    ///
    /// <para>“谁会真的被移走”= 当前状态为 <see cref="RenameStatus.Ok"/> 的那些文件，
    /// 而冲突判定又会把一些项从 Ok 打成 Conflict（于是它也不再让位），所以这里要迭代到不再变化为止。
    /// 每轮至少淘汰一项，实际最多几轮就稳定，这里再兜一个 8 轮上限。</para>
    /// </summary>
    private static void MarkConflicts(List<RenamePlanEntry> entries)
    {
        for (var round = 0; round < 8; round++)
        {
            var movable = new HashSet<string>(
                entries.Where(e => e.Status == RenameStatus.Ok).Select(e => e.SourcePath),
                StringComparer.OrdinalIgnoreCase);

            var byTarget = new Dictionary<string, List<RenamePlanEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.Status != RenameStatus.Ok) continue;
                var target = entry.TargetPath;
                if (!byTarget.TryGetValue(target, out var list))
                    byTarget[target] = list = new List<RenamePlanEntry>();
                list.Add(entry);
            }

            var changed = false;
            foreach (var (target, list) in byTarget)
            {
                if (list.Count > 1)
                {
                    foreach (var entry in list)
                    {
                        entry.Status = RenameStatus.Conflict;
                        entry.Message = $"与其他 {list.Count - 1} 个文件的新名重复";
                    }
                    changed = true;
                    continue;
                }

                var only = list[0];
                if (File.Exists(target) && !movable.Contains(target))
                {
                    only.Status = RenameStatus.Conflict;
                    only.Message = "目标位置已有同名文件（且它不在本次改名名单里）";
                    changed = true;
                }
            }

            if (!changed) break;
        }
    }

    /// <summary>勾了「重名自动加序号」时，按列表顺序给冲突的项找一个不撞车的名字。</summary>
    private static void ResolveConflicts(List<RenamePlanEntry> entries)
    {
        var conflicts = entries.Where(e => e.Status == RenameStatus.Conflict).ToList();
        if (conflicts.Count == 0) return;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Status == RenameStatus.Ok)
            {
                used.Add(entry.TargetPath);
                movable.Add(entry.SourcePath);
            }
        }

        // 目标位置可用 = 本次没被别的项占用，且磁盘上要么没这个文件、要么它是本次会被移走的源文件
        bool Usable(string candidate)
            => !used.Contains(candidate) && !(File.Exists(candidate) && !movable.Contains(candidate));

        foreach (var entry in conflicts)
        {
            // 撞名的一方保留原名（列表里靠前的先占位），其余的往后加 (1)、(2)…
            if (Usable(entry.TargetPath))
            {
                entry.Status = RenameStatus.Ok;
                entry.Message = "重名：保留了原目标名";
                used.Add(entry.TargetPath);
                movable.Add(entry.SourcePath);
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(entry.NewName);
            var ext = Path.GetExtension(entry.NewName);
            var resolved = false;
            for (var n = 1; n < 10000; n++)
            {
                var candidateName = $"{stem} ({n}){ext}";
                var candidate = Path.Combine(entry.Directory, candidateName);
                if (!Usable(candidate)) continue;

                entry.NewName = candidateName;
                entry.Status = RenameStatus.Ok;
                entry.Message = $"重名：自动改为 {candidateName}";
                used.Add(candidate);
                movable.Add(entry.SourcePath);
                resolved = true;
                break;
            }

            if (!resolved)
            {
                entry.Status = RenameStatus.Error;
                entry.Message = "重名且找不到可用的替代名字";
            }
        }
    }

    // =====================================================================
    //  3. 每种规则的具体实现（想加新的规则种类，在 ApplyRule 里加一个 case）
    // =====================================================================

    private static string ApplyCleanup(string text, RenameRule rule)
    {
        if (rule.TrimWhitespace) text = text.Trim();
        if (rule.CollapseSpaces) text = CollapseSpaces(text);
        if (rule.RemoveChars.Length > 0) text = RemoveChars(text, rule.RemoveChars);
        return text;
    }

    private static string CollapseSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace && lastWasSpace) continue;
            builder.Append(isSpace ? ' ' : ch);
            lastWasSpace = isSpace;
        }
        return builder.ToString();
    }

    private static string RemoveChars(string text, string chars)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (!chars.Contains(ch)) builder.Append(ch);
        return builder.ToString();
    }

    private static string ApplyReplace(string text, RenameRule rule)
    {
        if (rule.UseRegex)
        {
            var options = RegexOptions.None;
            if (!rule.CaseSensitive) options |= RegexOptions.IgnoreCase;
            var regex = new Regex(rule.FindText, options, TimeSpan.FromSeconds(1));
            try
            {
                return regex.Replace(text, rule.ReplaceText, rule.ReplaceAll ? int.MaxValue : 1);
            }
            catch (RegexMatchTimeoutException)
            {
                return text;
            }
        }

        var comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!rule.ReplaceAll)
        {
            var index = text.IndexOf(rule.FindText, comparison);
            return index < 0
                ? text
                : text[..index] + rule.ReplaceText + text[(index + rule.FindText.Length)..];
        }

        // 手写扫描而不是 string.Replace：替换文本里含查找文本时也不会死循环
        var builder = new StringBuilder(text.Length);
        var position = 0;
        while (true)
        {
            var index = text.IndexOf(rule.FindText, position, comparison);
            if (index < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }
            builder.Append(text, position, index - position).Append(rule.ReplaceText);
            position = index + rule.FindText.Length;
        }
        return builder.ToString();
    }

    private static string ApplyDeletions(string text, RenameRule rule)
    {
        var startMarker = rule.DeleteBetweenStart;
        var endMarker = rule.DeleteBetweenEnd;
        if (startMarker.Length > 0 && endMarker.Length > 0)
        {
            var start = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.OrdinalIgnoreCase);
                if (end >= 0)
                {
                    text = rule.DeleteBetweenKeepContent
                        ? text.Remove(end, endMarker.Length).Remove(start, startMarker.Length)
                        : text.Remove(start, end + endMarker.Length - start);
                }
            }
        }

        var fromStart = Math.Clamp(rule.DeleteFromStart, 0, 512);
        if (fromStart > 0 && text.Length > 0)
            text = text[Math.Min(fromStart, text.Length)..];

        var fromEnd = Math.Clamp(rule.DeleteFromEnd, 0, 512);
        if (fromEnd > 0 && text.Length > 0)
            text = text[..Math.Max(0, text.Length - fromEnd)];

        return text;
    }

    private static string ApplyCase(string text, TextCaseMode mode) => mode switch
    {
        TextCaseMode.Lower => text.ToLowerInvariant(),
        TextCaseMode.Upper => text.ToUpperInvariant(),
        // ToTitleCase 只认小写单词的词首，所以先整体转小写，中文不受影响
        TextCaseMode.TitleCase => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
        TextCaseMode.SentenceCase => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..],
        _ => text,
    };

    private static string ApplySequence(string text, int index, RenameRule rule)
    {
        var number = FormatSequence(index, rule);
        return rule.SequencePlacement switch
        {
            SequencePlacement.Prefix => text.Length == 0 ? number : number + rule.SequenceSeparator + text,
            SequencePlacement.Suffix => text.Length == 0 ? number : text + rule.SequenceSeparator + number,
            _ => number, // Replace：这段文本整个变成序号
        };
    }

    private static string FormatSequence(int index, RenameRule rule)
    {
        var digits = Math.Clamp(rule.SequenceDigits, 1, 12);
        var value = rule.SequenceStart + (long)index * rule.SequenceStep;
        return value.ToString("D" + digits, CultureInfo.InvariantCulture);
    }

    private static string NormalizeExtension(string raw)
    {
        var ext = raw.Trim();
        if (ext.Length == 0) return "";
        return ext.StartsWith('.') ? ext : "." + ext;
    }

    private static string ApplyExtensionMode(string originalExtension, RenameRule rule) => rule.ExtensionMode switch
    {
        ExtensionMode.Lower => originalExtension.ToLowerInvariant(),
        ExtensionMode.Upper => originalExtension.ToUpperInvariant(),
        ExtensionMode.Remove => "",
        ExtensionMode.Replace => NormalizeExtension(rule.NewExtension),
        _ => originalExtension,
    };
}
