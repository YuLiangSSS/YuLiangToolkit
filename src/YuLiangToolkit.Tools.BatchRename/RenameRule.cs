using System.Runtime.CompilerServices;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.BatchRename;

/// <summary>规则的种类。界面上「新建规则」下拉框里就是这几项。</summary>
public enum RenameRuleKind
{
    /// <summary>查找替换（可选正则）。</summary>
    Replace,

    /// <summary>清理：去首尾空格 / 合并连续空格 / 删掉指定字符。</summary>
    Cleanup,

    /// <summary>截取 / 删除：按标记区间删，或从头/从尾按字数删。</summary>
    DeleteRange,

    /// <summary>大小写转换。</summary>
    Case,

    /// <summary>加前缀 / 后缀。</summary>
    Affix,

    /// <summary>自动编号（按列表顺序）。</summary>
    Sequence,

    /// <summary>扩展名处理（这条规则不看「作用于」，它天生只动扩展名）。</summary>
    Extension,
}

/// <summary>一条规则作用在文件名的哪一部分上。</summary>
public enum RuleScope
{
    /// <summary>主名（不含扩展名）—— 默认，最不容易误伤。</summary>
    Stem,

    /// <summary>扩展名（含前面的点，例如 <c>.JPG</c>）。</summary>
    Extension,

    /// <summary>整个文件名（改完会重新拆成主名 + 扩展名，供后面的规则继续处理）。</summary>
    FullName,
}

/// <summary>大小写转换方式。</summary>
public enum TextCaseMode
{
    /// <summary>保持原样。</summary>
    Keep,

    /// <summary>全部小写。</summary>
    Lower,

    /// <summary>全部大写。</summary>
    Upper,

    /// <summary>每个单词首字母大写（其余小写），例如 <c>my photo</c> → <c>My Photo</c>。</summary>
    TitleCase,

    /// <summary>仅首字母大写（其余保持原样）。</summary>
    SentenceCase,
}

/// <summary>序号放在名字的哪个位置。</summary>
public enum SequencePlacement
{
    /// <summary>序号在前，例如 <c>01_照片.jpg</c>。</summary>
    Prefix,

    /// <summary>序号在后，例如 <c>照片_01.jpg</c>。</summary>
    Suffix,

    /// <summary>整个名字就是序号，例如 <c>01.jpg</c>（原来那部分被丢掉）。</summary>
    Replace,
}

/// <summary>扩展名处理方式。</summary>
public enum ExtensionMode
{
    /// <summary>保持原样。</summary>
    Keep,

    /// <summary>转小写，例如 <c>.JPG</c> → <c>.jpg</c>（顺带去掉重复的点）。</summary>
    Lower,

    /// <summary>转大写。</summary>
    Upper,

    /// <summary>统一换成指定扩展名（见 <see cref="RenameRule.NewExtension"/>）。</summary>
    Replace,

    /// <summary>去掉扩展名。</summary>
    Remove,
}

/// <summary>
/// <b>规则列表里的一条规则</b>：种类 + 作用范围 + 该种类的参数。整条规则会被序列化进
/// <c>Config\batch-rename\config.json</c> 的 <c>rules</c> 数组，<b>数组顺序 = 执行顺序</b>。
///
/// <para><b>为什么所有参数挤在一个类里</b>：这样 JSON 结构扁平静态、手改方便、加减规则种类不会
/// 影响老配置读取；界面上只显示当前 <see cref="Kind"/> 用得上的那几个字段。</para>
///
/// <para><b>为什么继承 <see cref="ObservableObject"/> 并且都走 <see cref="SetAndRefresh"/></b>：
/// 规则一改，界面上的摘要文案 <see cref="Summary"/> 和上面那份预览列表都要立刻跟着变。</para>
/// </summary>
public sealed class RenameRule : ObservableObject
{
    // ================= 通用 =================

    private RenameRuleKind _kind = RenameRuleKind.Replace;
    /// <summary>规则种类。</summary>
    public RenameRuleKind Kind
    {
        get => _kind;
        set
        {
            if (!SetAndRefresh(ref _kind, value)) return;
            OnPropertyChanged(nameof(KindName));
            OnPropertyChanged(nameof(IsReplace));
            OnPropertyChanged(nameof(IsCleanup));
            OnPropertyChanged(nameof(IsDeleteRange));
            OnPropertyChanged(nameof(IsCase));
            OnPropertyChanged(nameof(IsAffix));
            OnPropertyChanged(nameof(IsSequence));
            OnPropertyChanged(nameof(IsExtension));
            OnPropertyChanged(nameof(ShowScope));
            OnPropertyChanged(nameof(NeedsNewExtension));
        }
    }

    private bool _enabled = true;
    /// <summary>这条规则是否生效（不勾选就直接跳过，不用删掉它）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set { if (SetAndRefresh(ref _enabled, value)) OnPropertyChanged(nameof(Summary)); }
    }

    private RuleScope _scope = RuleScope.Stem;
    /// <summary>作用于主名 / 扩展名 / 整个文件名。</summary>
    public RuleScope Scope { get => _scope; set => SetAndRefresh(ref _scope, value); }

    // ================= 查找替换 =================

    private string _findText = "";
    /// <summary>要查找的文本；<see cref="UseRegex"/> 打开时按正则解释。</summary>
    public string FindText { get => _findText; set => SetAndRefresh(ref _findText, value); }

    private string _replaceText = "";
    /// <summary>替换成什么；正则模式下可以用 <c>$1</c> 引用捕获组。</summary>
    public string ReplaceText { get => _replaceText; set => SetAndRefresh(ref _replaceText, value); }

    private bool _useRegex;
    /// <summary>把 <see cref="FindText"/> 当正则表达式。</summary>
    public bool UseRegex { get => _useRegex; set => SetAndRefresh(ref _useRegex, value); }

    private bool _caseSensitive;
    /// <summary>查找时区分大小写。</summary>
    public bool CaseSensitive { get => _caseSensitive; set => SetAndRefresh(ref _caseSensitive, value); }

    private bool _replaceAll = true;
    /// <summary>替换所有出现的位置；不勾则只替换第一处。</summary>
    public bool ReplaceAll { get => _replaceAll; set => SetAndRefresh(ref _replaceAll, value); }

    // ================= 清理 =================

    private bool _trimWhitespace = true;
    /// <summary>去掉首尾空白。</summary>
    public bool TrimWhitespace { get => _trimWhitespace; set => SetAndRefresh(ref _trimWhitespace, value); }

    private bool _collapseSpaces;
    /// <summary>把连续的多个空格合并成一个。</summary>
    public bool CollapseSpaces { get => _collapseSpaces; set => SetAndRefresh(ref _collapseSpaces, value); }

    private string _removeChars = "";
    /// <summary>删掉这些字符（逐个字符匹配，不是子串），例如 <c>_-</c>。</summary>
    public string RemoveChars { get => _removeChars; set => SetAndRefresh(ref _removeChars, value); }

    // ================= 截取 / 删除 =================

    private string _deleteBetweenStart = "";
    /// <summary>删除区间的起始标记（含本身）。和 <see cref="DeleteBetweenEnd"/> 都填了才生效。</summary>
    public string DeleteBetweenStart
    {
        get => _deleteBetweenStart;
        set => SetAndRefresh(ref _deleteBetweenStart, value);
    }

    private string _deleteBetweenEnd = "";
    /// <summary>删除区间的结束标记（含本身）。</summary>
    public string DeleteBetweenEnd { get => _deleteBetweenEnd; set => SetAndRefresh(ref _deleteBetweenEnd, value); }

    private bool _deleteBetweenKeepContent;
    /// <summary>只删两个标记本身，保留中间的内容（提取用）。</summary>
    public bool DeleteBetweenKeepContent
    {
        get => _deleteBetweenKeepContent;
        set => SetAndRefresh(ref _deleteBetweenKeepContent, value);
    }

    private int _deleteFromStart;
    /// <summary>从开头删掉 N 个字符（0 = 不删）。</summary>
    public int DeleteFromStart { get => _deleteFromStart; set => SetAndRefresh(ref _deleteFromStart, value); }

    private int _deleteFromEnd;
    /// <summary>从末尾删掉 N 个字符（0 = 不删）。</summary>
    public int DeleteFromEnd { get => _deleteFromEnd; set => SetAndRefresh(ref _deleteFromEnd, value); }

    // ================= 大小写 =================

    private TextCaseMode _caseMode = TextCaseMode.Lower;
    /// <summary>大小写转换方式（默认小写，因为「不改变」的规则没有必要加）。</summary>
    public TextCaseMode CaseMode { get => _caseMode; set => SetAndRefresh(ref _caseMode, value); }

    // ================= 前后缀 =================

    private string _prefix = "";
    /// <summary>加在最前面的文本。</summary>
    public string Prefix { get => _prefix; set => SetAndRefresh(ref _prefix, value); }

    private string _suffix = "";
    /// <summary>加在最后面的文本。</summary>
    public string Suffix { get => _suffix; set => SetAndRefresh(ref _suffix, value); }

    // ================= 序号 =================

    private int _sequenceStart = 1;
    /// <summary>起始编号。</summary>
    public int SequenceStart { get => _sequenceStart; set => SetAndRefresh(ref _sequenceStart, value); }

    private int _sequenceStep = 1;
    /// <summary>每次递增多少（可以是负数）。</summary>
    public int SequenceStep { get => _sequenceStep; set => SetAndRefresh(ref _sequenceStep, value); }

    private int _sequenceDigits = 2;
    /// <summary>位数，不足前面补 0（1 ~ 12）。</summary>
    public int SequenceDigits { get => _sequenceDigits; set => SetAndRefresh(ref _sequenceDigits, value); }

    private SequencePlacement _sequencePlacement = SequencePlacement.Suffix;
    /// <summary>序号放在前面 / 后面 / 直接当整个名字。</summary>
    public SequencePlacement SequencePlacement
    {
        get => _sequencePlacement;
        set => SetAndRefresh(ref _sequencePlacement, value);
    }

    private string _sequenceSeparator = "_";
    /// <summary>序号与原名字之间的分隔符（可为空）。</summary>
    public string SequenceSeparator
    {
        get => _sequenceSeparator;
        set => SetAndRefresh(ref _sequenceSeparator, value);
    }

    // ================= 扩展名 =================

    private ExtensionMode _extensionMode = ExtensionMode.Lower;
    /// <summary>扩展名处理方式（默认转小写，这是最常见需求）。</summary>
    public ExtensionMode ExtensionMode
    {
        get => _extensionMode;
        set
        {
            if (!SetAndRefresh(ref _extensionMode, value)) return;
            OnPropertyChanged(nameof(NeedsNewExtension));
        }
    }

    private string _newExtension = "";
    /// <summary>统一换成这个扩展名（可带点也可不带，例如 <c>jpg</c> 或 <c>.jpg</c>）。</summary>
    public string NewExtension { get => _newExtension; set => SetAndRefresh(ref _newExtension, value); }

    // ================= 只读计算属性（不会写进 JSON） =================

    /// <summary>种类的中文名（界面标题用）。</summary>
    public string KindName => Kind switch
    {
        RenameRuleKind.Replace => "查找替换",
        RenameRuleKind.Cleanup => "清理",
        RenameRuleKind.DeleteRange => "截取 / 删除",
        RenameRuleKind.Case => "大小写",
        RenameRuleKind.Affix => "前后缀",
        RenameRuleKind.Sequence => "自动编号",
        RenameRuleKind.Extension => "扩展名",
        _ => Kind.ToString(),
    };

    /// <summary>扩展名规则不需要选「作用于」——它天生只动扩展名。</summary>
    public bool ShowScope => Kind != RenameRuleKind.Extension;

    /// <summary>是不是「统一换成…」模式（决定「新扩展名」输入框能不能填）。</summary>
    public bool NeedsNewExtension => Kind == RenameRuleKind.Extension && ExtensionMode == ExtensionMode.Replace;

    public bool IsReplace => Kind == RenameRuleKind.Replace;
    public bool IsCleanup => Kind == RenameRuleKind.Cleanup;
    public bool IsDeleteRange => Kind == RenameRuleKind.DeleteRange;
    public bool IsCase => Kind == RenameRuleKind.Case;
    public bool IsAffix => Kind == RenameRuleKind.Affix;
    public bool IsSequence => Kind == RenameRuleKind.Sequence;
    public bool IsExtension => Kind == RenameRuleKind.Extension;

    /// <summary>作用范围的中文名。</summary>
    public string ScopeName => Scope switch
    {
        RuleScope.Extension => "扩展名",
        RuleScope.FullName => "整个文件名",
        _ => "主名",
    };

    /// <summary>一行话说明这条规则在干什么（列表收起时显示）。</summary>
    public string Summary
    {
        get
        {
            var body = Kind switch
            {
                RenameRuleKind.Replace => SummarizeReplace(),
                RenameRuleKind.Cleanup => SummarizeCleanup(),
                RenameRuleKind.DeleteRange => SummarizeDeleteRange(),
                RenameRuleKind.Case => "改成 " + (CaseMode switch
                {
                    TextCaseMode.Lower => "全部小写",
                    TextCaseMode.Upper => "全部大写",
                    TextCaseMode.TitleCase => "每个单词首字母大写",
                    TextCaseMode.SentenceCase => "仅首字母大写",
                    _ => "不改变",
                }),
                RenameRuleKind.Affix => SummarizeAffix(),
                RenameRuleKind.Sequence => SummarizeSequence(),
                RenameRuleKind.Extension => SummarizeExtension(),
                _ => KindName,
            };

            if (Kind == RenameRuleKind.Extension || Scope == RuleScope.Stem) return body;
            return $"（作用于{ScopeName}）{body}";
        }
    }

    private string SummarizeReplace()
    {
        if (FindText.Length == 0) return "还没填要查找的内容";
        var arrow = UseRegex ? "正则 " : "";
        var flags = (CaseSensitive ? " · 区分大小写" : "") + (ReplaceAll ? "" : " · 只替换第一处");
        return $"{arrow}「{FindText}」→「{ReplaceText}」{flags}";
    }

    private string SummarizeCleanup()
    {
        var parts = new List<string>();
        if (TrimWhitespace) parts.Add("去首尾空格");
        if (CollapseSpaces) parts.Add("连续空格合并");
        if (RemoveChars.Length > 0) parts.Add($"删字符「{RemoveChars}」");
        return parts.Count == 0 ? "（没勾任何清理项，等于没这条规则）" : string.Join(" · ", parts);
    }

    private string SummarizeDeleteRange()
    {
        var parts = new List<string>();
        if (DeleteBetweenStart.Length > 0 && DeleteBetweenEnd.Length > 0)
            parts.Add(DeleteBetweenKeepContent
                ? $"只删标记「{DeleteBetweenStart}」「{DeleteBetweenEnd}」"
                : $"删「{DeleteBetweenStart}」…「{DeleteBetweenEnd}」");
        if (DeleteFromStart > 0) parts.Add($"从头删 {DeleteFromStart}");
        if (DeleteFromEnd > 0) parts.Add($"从尾删 {DeleteFromEnd}");
        return parts.Count == 0 ? "（还没设删除区间）" : string.Join(" · ", parts);
    }

    private string SummarizeAffix()
    {
        var parts = new List<string>();
        if (Prefix.Length > 0) parts.Add($"前缀「{Prefix}」");
        if (Suffix.Length > 0) parts.Add($"后缀「{Suffix}」");
        return parts.Count == 0 ? "（前后缀都空着）" : string.Join(" · ", parts);
    }

    private string SummarizeSequence()
    {
        var placement = SequencePlacement switch
        {
            SequencePlacement.Prefix => "在前",
            SequencePlacement.Suffix => "在后",
            _ => "只用序号",
        };
        var digits = Math.Clamp(SequenceDigits, 1, 12);
        var separator = SequenceSeparator.Length == 0 ? "无分隔" : $"分隔「{SequenceSeparator}」";
        return $"{SequenceStart} 起 · 步长 {SequenceStep} · {digits} 位补零 · {placement} · {separator}";
    }

    private string SummarizeExtension() => ExtensionMode switch
    {
        ExtensionMode.Lower => "转小写",
        ExtensionMode.Upper => "转大写",
        ExtensionMode.Remove => "去掉扩展名",
        ExtensionMode.Replace => NewExtension.Trim().Length == 0 ? "换成（还没填新扩展名）" : $"换成「{NewExtension.Trim()}」",
        _ => "保持原样",
    };

    // ================= 构造 =================

    /// <summary>新建一条某种类的规则（默认值就写在上面各属性上，这里只切种类）。</summary>
    public static RenameRule Create(RenameRuleKind kind) => new() { Kind = kind };

    /// <summary>链式设置作用范围，写起来短一点：<c>new RenameRule{…}.WithScope(RuleScope.Extension)</c>。</summary>
    public RenameRule WithScope(RuleScope scope)
    {
        Scope = scope;
        return this;
    }

    /// <summary>赋值 + 顺带通知 <see cref="Summary"/>（一条规则改任何一个参数都会影响摘要文案）。</summary>
    private bool SetAndRefresh<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!Set(ref field, value, propertyName)) return false;
        OnPropertyChanged(nameof(Summary));
        return true;
    }
}
