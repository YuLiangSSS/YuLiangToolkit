using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace YuLiangToolkit.Tools.TextEditor.Core;

/// <summary>
/// 把 AvalonEdit 内置的语法高亮定义调成「暗色友好」：内置定义的颜色是为白底设计的
/// （关键字纯蓝、注释纯绿），直接铺在深色背景上偏暗难读。这里按颜色名把常见类别
/// （关键字 / 字符串 / 注释 / 数字 / 类型名…）覆写成 VSCode 暗色主题的同款色系。
///
/// <para>定义实例是 <see cref="HighlightingManager"/> 的全局缓存，同一份定义只覆写一次；
/// 覆写失败（以后版本改名等）静默跳过——语法高亮是锦上添花，不能因为它把编辑器搞挂。</para>
/// </summary>
public static class EditorHighlighting
{
    private static readonly HashSet<IHighlightingDefinition> Processed = new();

    /// <summary>按文件路径取高亮定义（识别不了返回 <c>null</c> = 纯文本）并做暗色适配。</summary>
    public static IHighlightingDefinition? ForFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var definition = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
            ApplyDarkPalette(definition);
            return definition;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把一份定义的颜色调成暗色友好（幂等；null 安全）。</summary>
    public static void ApplyDarkPalette(IHighlightingDefinition? definition)
    {
        if (definition is null || !Processed.Add(definition)) return;

        foreach (var color in definition.NamedHighlightingColors)
        {
            var target = MapColor(color.Name);
            if (target is null) continue;
            try
            {
                color.Foreground = new SimpleHighlightingBrush(target.Value);
                color.Background = null;   // 内置定义若带背景块（浅色底）直接清掉，统一用编辑区背景
            }
            catch
            {
                // 单个颜色覆写失败不影响其它颜色
            }
        }
    }

    /// <summary>
    /// 颜色名 → 暗色友好色。按名字包含关系匹配（不同语言的 xshd 命名不一致），
    /// 返回 <c>null</c> = 保持默认（用编辑区前景色）。
    /// </summary>
    public static Color? MapColor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();

        // 顺序有讲究：越具体的判断越靠前
        if (n.Contains("Comment", StringComparison.OrdinalIgnoreCase)) return FromHex("#6A9955");
        if (n.Contains("String", StringComparison.OrdinalIgnoreCase) || n.Contains("Char", StringComparison.OrdinalIgnoreCase))
            return FromHex("#CE9178");
        if (n.Contains("Number", StringComparison.OrdinalIgnoreCase) || n.Contains("Digit", StringComparison.OrdinalIgnoreCase))
            return FromHex("#B5CEA8");
        if (n.Contains("Preprocessor", StringComparison.OrdinalIgnoreCase)) return FromHex("#9B9B9B");
        if (n.Contains("Attribute", StringComparison.OrdinalIgnoreCase)) return FromHex("#DCDCAA");
        if (n.Contains("Type", StringComparison.OrdinalIgnoreCase) || n.Contains("Class", StringComparison.OrdinalIgnoreCase))
            return FromHex("#4EC9B0");
        if (n.Contains("Keyword", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Visibility", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Modifier", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Statement", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Tag", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Key", StringComparison.OrdinalIgnoreCase))
            return FromHex("#569CD6");
        if (n.Contains("Property", StringComparison.OrdinalIgnoreCase) || n.Contains("Parameter", StringComparison.OrdinalIgnoreCase))
            return FromHex("#9CDCFE");
        if (n.Contains("Markup", StringComparison.OrdinalIgnoreCase) || n.Contains("Doc", StringComparison.OrdinalIgnoreCase))
            return FromHex("#808080");
        if (n.Contains("Operator", StringComparison.OrdinalIgnoreCase) || n.Contains("Punctuation", StringComparison.OrdinalIgnoreCase))
            return FromHex("#D4D4D4");
        return null;
    }

    private static Color? FromHex(string hex)
    {
        try
        {
            return (Color?)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return null;
        }
    }
}
