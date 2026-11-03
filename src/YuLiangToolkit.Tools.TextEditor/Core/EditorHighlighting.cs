using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace YuLiangToolkit.Tools.TextEditor.Core;

/// <summary>
/// 把 AvalonEdit 内置的语法高亮定义调成「Visual Studio 2022 深色主题」的配色：
/// 内置定义的颜色是<b>为白底设计</b>的（关键字纯蓝、字符串蓝、方法名 MidnightBlue 粗体、
/// 数字 DarkBlue、注释 Green…），直接铺在深色背景上又暗又糊。
///
/// <para><b>配色来源</b>（VS 2022 深色主题的 C# 编辑器）：关键字蓝 <c>#569CD6</c>、
/// 方法名淡黄 <c>#DCDCAA</c>、类型青绿 <c>#4EC9B0</c>、字符串橙 <c>#D69D85</c>、
/// 数字浅绿 <c>#B5CEA8</c>、注释绿 <c>#57A64A</c>、属性 / 字段浅蓝 <c>#9CDCFE</c>、
/// 标点浅灰 <c>#D4D4D4</c>、预处理灰 <c>#9B9B9B</c>。</para>
///
/// <para><b>怎么覆写</b>：按 xshd 里的「颜色名」匹配（不同语言命名不统一，先查
/// <see cref="ExactColors"/> 精确表，再按名字里的词根分类）；<b>没归类的颜色</b>若内置带了前景色
/// （深色底上看不清的深蓝 / 藏青 / 暗红…），统一回退成浅灰 —— 宁可少一种区分，也不留看不清的字。
/// 同时清掉内置背景色（如 Markdown LineBreak 的浅灰底）并把字重归到正常（VS 的关键字 / 方法名不加粗）。</para>
///
/// <para>定义实例是 <see cref="HighlightingManager"/> 的全局缓存，同一份定义只覆写一次（幂等）；
/// 覆写失败（以后版本改名等）静默跳过——语法高亮是锦上添花，不能因为它把编辑器搞挂。</para>
/// </summary>
public static class EditorHighlighting
{
    // ---------------- Visual Studio 2022 深色主题色值（自检逐条断言） ----------------

    /// <summary>注释绿。</summary>
    public const string CommentHex = "#57A64A";

    /// <summary>字符串 / 字符 / 日期字面量（沙橙）。</summary>
    public const string StringHex = "#D69D85";

    /// <summary>关键字 / 修饰符 / 语句 / 字面量（VS 关键字蓝）。</summary>
    public const string KeywordHex = "#569CD6";

    /// <summary>类型 / 类 / 结构 / 枚举（青绿）。</summary>
    public const string TypeHex = "#4EC9B0";

    /// <summary>方法名 / 函数调用（淡黄，用户点名要的 VS 方法色）。</summary>
    public const string MethodHex = "#DCDCAA";

    /// <summary>数字字面量（浅绿）。</summary>
    public const string NumberHex = "#B5CEA8";

    /// <summary>预处理指令（灰）。</summary>
    public const string PreprocessorHex = "#9B9B9B";

    /// <summary>标点 / 运算符（浅灰）。</summary>
    public const string PunctuationHex = "#D4D4D4";

    /// <summary>属性 / 字段 / 参数 / 变量（浅蓝）。</summary>
    public const string PropertyHex = "#9CDCFE";

    /// <summary>正则字面量 / 删除行（偏红）。</summary>
    public const string RegexHex = "#D16969";

    /// <summary>HTML / XML 标签（与关键字同蓝）。</summary>
    public const string TagHex = "#569CD6";

    /// <summary>没归类的颜色统一回退的浅灰（保证深色底上永远看得清）。</summary>
    public const string FallbackHex = "#D4D4D4";

    private static readonly HashSet<IHighlightingDefinition> Processed = new();
    private static bool _docCommentPaletteApplied;

    /// <summary>
    /// 颜色名精确对照表：先把语言里那些「按词根猜不出来」的名字钉死
    /// （例如 <c>MethodCall</c>、<c>ThisOrBaseReference</c>、<c>GetSetAddRemove</c>、补丁的 <c>AddedText</c>…）。
    /// </summary>
    private static readonly Dictionary<string, string> ExactColors = new(StringComparer.OrdinalIgnoreCase)
    {
        // 注释 / 文档
        ["Comment"] = CommentHex,
        ["BlockQuote"] = CommentHex,
        ["DocComment"] = CommentHex,
        ["KnownDocTags"] = CommentHex,
        ["XmlString"] = CommentHex,
        ["LineBreak"] = PreprocessorHex,

        // 字符串 / 字符 / 日期 / 内联代码
        ["String"] = StringHex,
        ["Char"] = StringHex,
        ["Character"] = StringHex,
        ["StringInterpolation"] = StringHex,
        ["DateLiteral"] = StringHex,
        ["Code"] = StringHex,
        ["Value"] = StringHex,

        // 正则 / 删除行
        ["Regex"] = RegexHex,
        ["RemovedText"] = RegexHex,

        // 数字
        ["NumberLiteral"] = NumberHex,
        ["Number"] = NumberHex,
        ["Digits"] = NumberHex,

        // 方法 / 函数 / 命令
        ["MethodCall"] = MethodHex,
        ["MethodName"] = MethodHex,
        ["FunctionCall"] = MethodHex,
        ["Command"] = MethodHex,
        ["Selector"] = MethodHex,
        ["JavaScriptGlobalFunctions"] = MethodHex,
        ["FileName"] = MethodHex,

        // 预处理 / 补丁位置
        ["Preprocessor"] = PreprocessorHex,
        ["Position"] = PreprocessorHex,

        // 标点 / 运算符
        ["Punctuation"] = PunctuationHex,
        ["Slash"] = PunctuationHex,
        ["Colon"] = PunctuationHex,
        ["CurlyBraces"] = PunctuationHex,
        ["XmlPunctuation"] = PunctuationHex,
        ["Assignment"] = PunctuationHex,
        ["Operators"] = PunctuationHex,
        ["Operator"] = PunctuationHex,

        // 属性 / 字段 / 参数 / 变量 / 特性
        ["Property"] = PropertyHex,
        ["FieldName"] = PropertyHex,
        ["Variable"] = PropertyHex,
        ["Parameter"] = PropertyHex,
        ["Attribute"] = PropertyHex,
        ["Attributes"] = PropertyHex,
        ["UnknownAttribute"] = PropertyHex,
        ["JavaScriptIntrinsics"] = PropertyHex,

        // 类型 / 类 / 实体 / 图片
        ["Type"] = TypeHex,
        ["Class"] = TypeHex,
        ["ValueTypes"] = TypeHex,
        ["ReferenceTypes"] = TypeHex,
        ["DataTypes"] = TypeHex,
        ["OtherTypes"] = TypeHex,
        ["EntityReference"] = TypeHex,
        ["Entities"] = TypeHex,
        ["Image"] = TypeHex,

        // 关键字 / 字面量 / 补丁
        ["Keyword"] = KeywordHex,
        ["Keywords"] = KeywordHex,
        ["TrueFalse"] = KeywordHex,
        ["Bool"] = KeywordHex,
        ["Null"] = KeywordHex,
        ["Void"] = KeywordHex,
        ["ThisOrBaseReference"] = KeywordHex,
        ["GetSetAddRemove"] = KeywordHex,
        ["Friend"] = KeywordHex,
        ["Package"] = KeywordHex,
        ["Literals"] = KeywordHex,
        ["Constants"] = KeywordHex,
        ["ControlFlow"] = KeywordHex,
        ["AddedText"] = CommentHex,
        ["Header"] = KeywordHex,
        ["ASPSection"] = KeywordHex,
        ["Link"] = KeywordHex,

        // HTML / XML 标签
        ["Tag"] = TagHex,
        ["Tags"] = TagHex,
        ["HtmlTag"] = TagHex,
        ["ScriptTag"] = TagHex,
        ["JavaScriptTag"] = TagHex,
        ["JScriptTag"] = TagHex,
        ["VBScriptTag"] = TagHex,
        ["UnknownScriptTag"] = TagHex,
    };

    /// <summary>按文件路径取高亮定义（识别不了返回 <c>null</c> = 纯文本）并做 VS 深色适配。</summary>
    public static IHighlightingDefinition? ForFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var definition = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
            ApplyDarkPalette(definition);
            ApplyDocCommentPalette();
            return definition;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把一份定义的颜色调成 VS 深色主题配色（幂等；null 安全）。</summary>
    public static void ApplyDarkPalette(IHighlightingDefinition? definition)
    {
        if (definition is null || !Processed.Add(definition)) return;

        foreach (var color in definition.NamedHighlightingColors)
        {
            // 没归类、但内置又带了前景色的（内置是按白底设计的深蓝 / 藏青 / 暗红…）：
            // 统一回退成浅灰，保证深色底上不会留下看不清的深色。
            var target = MapColor(color.Name)
                         ?? (color.Foreground is null ? null : FromHex(FallbackHex));
            if (target is null) continue;

            try
            {
                color.Foreground = new SimpleHighlightingBrush(target.Value);
                color.Background = null;                 // 清掉内置的浅色底块（如 Markdown LineBreak）
                color.FontWeight = FontWeights.Normal;   // VS 风格：关键字 / 方法名不加粗
            }
            catch
            {
                // 单个颜色覆写失败不影响其它颜色
            }
        }
    }

    /// <summary>
    /// 颜色名 → VS 深色主题色。先查精确表，再按名字里的词根分类；
    /// 返回 <c>null</c> = 不认识（由 <see cref="ApplyDarkPalette"/> 决定是否回退浅灰）。
    /// </summary>
    public static Color? MapColor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();

        if (ExactColors.TryGetValue(n, out var exact)) return FromHex(exact);

        // 顺序有讲究：越具体的判断越靠前（关键字类里也有带 Type / Attribute 字样的名字）
        // 关键字 / 语句 / 修饰符 / 字面量 → VS 关键字蓝
        if (Has(n, "keyword", "statement", "modifier", "visibility", "namespace", "package",
                "constant", "literal", "heading", "control", "jump", "loop", "friend",
                "truefalse", "semantic", "getset", "thisorbase", "nullorvalue", "checked"))
            return FromHex(KeywordHex);

        // 注释 / 文档 / 引用块
        if (Has(n, "comment", "blockquote", "doc")) return FromHex(CommentHex);

        // 字符串 / 字符 / 日期
        if (Has(n, "string", "char", "date")) return FromHex(StringHex);

        // 数字
        if (Has(n, "number", "digit")) return FromHex(NumberHex);

        // 方法 / 函数 / 调用
        if (Has(n, "method", "function", "call", "command")) return FromHex(MethodHex);

        // 类型 / 类 / 结构 / 枚举
        if (Has(n, "type", "class", "enum", "interface", "struct")) return FromHex(TypeHex);

        // 属性 / 字段 / 参数 / 变量 / 特性
        if (Has(n, "attribute", "property", "field", "variable", "parameter")) return FromHex(PropertyHex);

        // HTML / XML 标签
        if (Has(n, "tag")) return FromHex(TagHex);

        // 实体（&nbsp; 之类）
        if (Has(n, "entity")) return FromHex(TypeHex);

        // 标点 / 运算符
        if (Has(n, "punctuation", "operator", "slash", "colon", "brace", "assignment"))
            return FromHex(PunctuationHex);

        // 正则字面量
        if (Has(n, "regex")) return FromHex(RegexHex);

        return null;
    }

    /// <summary>
    /// C# 的 <c>///</c> 文档注释颜色来自单独注册的 <c>XmlDoc</c> 定义（被 C# 定义 Import），
    /// 是另一组颜色对象：只调 C# 定义调不到它们，这里补一次（全局只做一次）。
    /// </summary>
    private static void ApplyDocCommentPalette()
    {
        if (_docCommentPaletteApplied) return;
        _docCommentPaletteApplied = true;
        try
        {
            ApplyDarkPalette(HighlightingManager.Instance.GetDefinition("XmlDoc"));
        }
        catch
        {
            // 没有这份定义就算了，文档注释保持默认色
        }
    }

    private static bool Has(string name, params string[] parts)
        => parts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

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
