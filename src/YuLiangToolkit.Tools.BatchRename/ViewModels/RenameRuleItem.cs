using System.ComponentModel;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.BatchRename.ViewModels;

/// <summary>
/// 规则列表里的一行：把 <see cref="RenameRule"/>（要持久化的数据）和<b>只属于界面的状态</b>
/// （展开/收起、拖动时的插入指示线）装在一起；顺带提供卡片里那几个下拉框的选项列表。
///
/// <para>分开的理由：<see cref="RenameRule"/> 会被整条序列化进 config.json，
/// 而 <see cref="IsExpanded"/> 这种纯界面状态不该写进配置文件。</para>
///
/// <para>它同时把规则内部的变更<b>转发</b>出来，这样 ViewModel 只要订阅每一行，
/// 就能在“任何参数被改动”时重算预览。</para>
/// </summary>
public sealed class RenameRuleItem : ObservableObject
{
    private bool _isExpanded;
    private bool _showDropTop;
    private bool _showDropBottom;

    public RenameRuleItem(RenameRule rule)
    {
        Rule = rule;
        Rule.PropertyChanged += OnRulePropertyChanged;
    }

    /// <summary>这条规则本身（界面上绑 <c>Rule.Xxx</c>）。</summary>
    public RenameRule Rule { get; }

    /// <summary>是否展开参数编辑区（点标题行切换；同一时刻只展开一条）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>拖动时：插入位置在这条规则<b>上方</b>（界面画一条指示线）。</summary>
    public bool ShowDropTop
    {
        get => _showDropTop;
        set => Set(ref _showDropTop, value);
    }

    /// <summary>拖动时：插入位置在这条规则<b>下方</b>。</summary>
    public bool ShowDropBottom
    {
        get => _showDropBottom;
        set => Set(ref _showDropBottom, value);
    }

    // ---------- 卡片里几个下拉框的选项（所有规则共用同一份） ----------

    /// <summary>大小写选项。</summary>
    public IReadOnlyList<OptionItem<TextCaseMode>> CaseOptions => SharedOptions.Cases;

    /// <summary>序号位置选项。</summary>
    public IReadOnlyList<OptionItem<SequencePlacement>> SequencePlacementOptions => SharedOptions.SequencePlacements;

    /// <summary>扩展名模式选项。</summary>
    public IReadOnlyList<OptionItem<ExtensionMode>> ExtensionModeOptions => SharedOptions.ExtensionModes;

    /// <summary>作用范围选项。</summary>
    public IReadOnlyList<OptionItem<RuleScope>> ScopeOptions => SharedOptions.Scopes;

    /// <summary>撤销订阅（列表项被移除 / 工具关闭时调用，避免旧对象被规则对象一直引用着）。</summary>
    public void Detach() => Rule.PropertyChanged -= OnRulePropertyChanged;

    /// <summary>
    /// 给 UI 自动化 / 读屏软件用的名字。不覆写的话会显示成类型全名
    /// （<c>YuLiangToolkit.Tools.BatchRename.ViewModels.RenameRuleItem</c>），排查问题时很难看。
    /// </summary>
    public override string ToString() => $"{Rule.KindName}：{Rule.Summary}";

    /// <summary>
    /// 规则内部一改就转发一次：界面上绑 <c>Rule.Xxx</c> 的控件自己会刷新（<see cref="RenameRule"/>
    /// 也是可观察对象），这里转发主要是给 ViewModel 一个“该重算预览了”的信号。
    /// </summary>
    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(e.PropertyName);

    /// <summary>所有规则卡片共用的一份下拉选项（放静态类里避免每条规则各造一份数组）。</summary>
    private static class SharedOptions
    {
        public static readonly IReadOnlyList<OptionItem<TextCaseMode>> Cases = OptionItem.From(
            (TextCaseMode.Keep, "不改变"),
            (TextCaseMode.Lower, "全部小写 abc"),
            (TextCaseMode.Upper, "全部大写 ABC"),
            (TextCaseMode.TitleCase, "每个单词首字母大写"),
            (TextCaseMode.SentenceCase, "仅首字母大写"));

        public static readonly IReadOnlyList<OptionItem<SequencePlacement>> SequencePlacements = OptionItem.From(
            (SequencePlacement.Prefix, "序号在前（01_名字）"),
            (SequencePlacement.Suffix, "序号在后（名字_01）"),
            (SequencePlacement.Replace, "只用序号（01）"));

        public static readonly IReadOnlyList<OptionItem<ExtensionMode>> ExtensionModes = OptionItem.From(
            (ExtensionMode.Keep, "保持原样"),
            (ExtensionMode.Lower, "转小写（.JPG → .jpg）"),
            (ExtensionMode.Upper, "转大写"),
            (ExtensionMode.Replace, "统一换成…"),
            (ExtensionMode.Remove, "去掉扩展名"));

        public static readonly IReadOnlyList<OptionItem<RuleScope>> Scopes = OptionItem.From(
            (RuleScope.Stem, "主名（不含扩展名）"),
            (RuleScope.Extension, "扩展名（含点）"),
            (RuleScope.FullName, "整个文件名"));
    }
}
