using System.Collections.ObjectModel;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Calculator.Core.Kin;

namespace YuLiangToolkit.Tools.Calculator.ViewModels;

/// <summary>
/// 辈分计算模式的关系链 + 结果 + 对照表。
///
/// <para>由 <see cref="CalculatorViewModel"/> 持有（同一个工具页的两个模式共用一份配置），
/// 界面只做绑定；推导全部在 <see cref="KinNaming"/> / <see cref="KinPath"/> 里（纯逻辑，自检直接断言）。</para>
/// </summary>
public sealed class KinCalculatorViewModel : ObservableObject
{
    private readonly CalculatorConfig _config;

    /// <summary>当前关系链（未规范化，原样保留用户点出来的顺序，显示才对得上）。</summary>
    private List<KinStep> _path = new();

    private IReadOnlyList<KinStep> _displayPath = Array.Empty<KinStep>();

    public KinCalculatorViewModel(CalculatorConfig config)
    {
        _config = config;
        _path = KinPath.ParseToken(config.KinPath);
        config.PropertyChanged += OnConfigChanged;

        StepCommand = new RelayCommand(parameter => AddStep(parameter as KinStep?), parameter => parameter is KinStep);
        UndoCommand = new RelayCommand(_ => DropLast(), _ => _path.Count > 0);
        ResetCommand = new RelayCommand(_ => Reset(), _ => _path.Count > 0);
        UseTermCommand = new RelayCommand(parameter => UseTerm(parameter as KinTermItem), parameter => parameter is KinTermItem);

        RebuildTable();
        Refresh();
    }

    // ---------- 关系链 ----------

    /// <summary>十个关系按钮（爸爸 / 妈妈 / 哥哥 / 弟弟 / 姐姐 / 妹妹 / 老公 / 老婆 / 儿子 / 女儿）。</summary>
    public IReadOnlyList<KinStepButton> StepButtons { get; } = KinStepInfo.All
        .Select(step => new KinStepButton(step))
        .ToArray();

    /// <summary>点一个按钮往下走一级。</summary>
    public RelayCommand StepCommand { get; }

    /// <summary>退一级。</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>重来（回到「我」）。</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>关系链的显示（「我 → 妈妈 → 的姐姐」由界面拼，这里给中文写法）。</summary>
    public string PathText => KinPath.Describe(_path);

    /// <summary>当前链有几步（0 = 就是我自己）。</summary>
    public int StepCount => _path.Count;

    /// <summary>关系的紧凑写法（界面上不显示，给自检和排错用）。</summary>
    public string PathToken => KinPath.ToToken(_path);

    // ---------- 结果 ----------

    private KinResult _result = KinNaming.Resolve(null, true, true);

    /// <summary>当前结果（标题 / 南北叫法 / 辈分 / 支系 / 反向称谓）。</summary>
    public KinResult Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    /// <summary>地区说明：当前用哪一边的叫法。</summary>
    public string RegionText => _config.PreferNorth ? "当前：北方叫法" : "当前：南方叫法";

    /// <summary>「我」的性别说明。</summary>
    public string GenderText => _config.SelfIsMale ? "我：男性" : "我：女性";

    /// <summary>「叫法地区」单选：北方（与 <see cref="IsSouthRegion"/> 互斥，两个单选按钮各绑一个）。</summary>
    public bool IsNorthRegion
    {
        get => _config.PreferNorth;
        set { if (value) _config.KinRegion = CalculatorConfig.NorthRegion; }
    }

    /// <summary>「叫法地区」单选：南方。</summary>
    public bool IsSouthRegion
    {
        get => !_config.PreferNorth;
        set { if (value) _config.KinRegion = CalculatorConfig.SouthRegion; }
    }

    // ---------- 设置 ----------

    /// <summary>「我」的性别下拉框选项。</summary>
    public IReadOnlyList<string> GenderOptions => CalculatorConfig.GenderOptions;

    /// <summary>同辈亲戚（堂/表兄弟）的说明：长幼要按实际年龄对号。</summary>
    public string PeerHint =>
        "堂 / 表兄弟、堂 / 表姐妹按「你俩谁大」分哥弟、姐妹：关系链里看不出年龄，"
        + "所以结果写成「堂哥 / 堂弟」这样一对，自己按年龄挑。";

    /// <summary>推导范围的说明（避免用户拿继父母 / 收养去对）。</summary>
    public string ScopeHint =>
        "按血缘与婚姻推导（一夫一妻的常规理解）：继父母、收养、同父异母这类特殊情况不在此范围内。";

    // ---------- 对照表 ----------

    /// <summary>对照表的一行（点一下就把它的关系链装进左边）。</summary>
    public ObservableCollection<KinTermItem> Terms { get; } = new();

    /// <summary>点对照表里的一行：把它的关系链装进来，并算出结果。</summary>
    public RelayCommand UseTermCommand { get; }

    private string _searchText = "";

    /// <summary>对照表的搜索词（按叫法 / 书面称谓 / 关系名过滤）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            RebuildTable();
        }
    }

    /// <summary>有没有过滤出结果（空状态提示用）。</summary>
    public bool HasTerms => Terms.Count > 0;

    /// <summary>对照表的小字统计。</summary>
    public string TermsCountText => $"共 {Terms.Count} 条 · 点一行把它的关系装到左边";

    // ---------- 关系链操作 ----------

    private void AddStep(KinStep? step)
    {
        if (step is null) return;

        // 配偶不能连着点两次（老公的老婆就是你自己）——直接忽略，避免链越点越怪
        if (step.Value.IsSpouse())
        {
            if (_path.Count > 0 && _path[^1].IsSpouse())
            {
                StatusText = "配偶不能连着点两次：老公的老婆就是你自己";
                return;
            }
            if (HasSpouseInPath())
            {
                StatusText = "同一条关系链里只能有一位配偶";
                return;
            }
        }

        _path.Add(step.Value);
        Refresh();
    }

    private bool HasSpouseInPath() => _path.Any(step => step.IsSpouse());

    private void DropLast()
    {
        if (_path.Count == 0) return;
        _path.RemoveAt(_path.Count - 1);
        Refresh();
    }

    private void Reset()
    {
        _path.Clear();
        Refresh();
    }

    private void UseTerm(KinTermItem? item)
    {
        if (item is null) return;
        _path = KinPath.ParseToken(item.Key);
        Refresh();
        StatusText = $"已按「{item.Title}」装好关系：{KinPath.Describe(_path)}";
    }

    // ---------- 状态 ----------

    private string _statusText = "";

    /// <summary>操作反馈（点重来 / 装对照表里的一条时的一句话）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // ---------- 计算与刷新 ----------

    /// <summary>重新算一遍，并把当前链写回配置（切走再回来还停在原处）。</summary>
    private void Refresh()
    {
        _displayPath = _path.ToList();
        Result = KinNaming.Resolve(_displayPath, _config.SelfIsMale, _config.PreferNorth);

        // 只在真的变了才写配置：属性通知会连锁触发 OnConfigChanged，避免来回抖动
        if (_config.KinPath != PathToken) _config.KinPath = PathToken;

        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(StepCount));
        OnPropertyChanged(nameof(PathToken));
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(GenderText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void OnConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CalculatorConfig.KinRegion):
                Result = KinNaming.Resolve(_displayPath, _config.SelfIsMale, _config.PreferNorth);
                OnPropertyChanged(nameof(RegionText));
                OnPropertyChanged(nameof(IsNorthRegion));
                OnPropertyChanged(nameof(IsSouthRegion));
                break;
            case nameof(CalculatorConfig.KinGender):
                Result = KinNaming.Resolve(_displayPath, _config.SelfIsMale, _config.PreferNorth);
                OnPropertyChanged(nameof(GenderText));
                break;
            case nameof(CalculatorConfig.KinPath):
                // 只有外部（例如重建配置）改了才算，自己写的不用回灌，免得把未规范化的链换掉
                if (_config.KinPath != PathToken)
                {
                    _path = KinPath.ParseToken(_config.KinPath);
                    Refresh();
                }
                break;
        }
    }

    private void RebuildTable()
    {
        var keyword = SearchText.Trim();
        Terms.Clear();

        foreach (var term in KinTermTable.All)
        {
            // 「我」那一行（空键）不进对照表：对照表是给人查亲戚叫法的
            if (string.IsNullOrEmpty(term.Key)) continue;

            var item = new KinTermItem(term);
            if (keyword.Length > 0 && !item.Matches(keyword)) continue;
            Terms.Add(item);
        }

        OnPropertyChanged(nameof(HasTerms));
        OnPropertyChanged(nameof(TermsCountText));
    }

    /// <summary>配置对象是缓存复用的，切走时要摘事件。</summary>
    public void Detach() => _config.PropertyChanged -= OnConfigChanged;
}

/// <summary>十个关系按钮里的一枚：把枚举包成可绑定的按钮数据。</summary>
public sealed class KinStepButton
{
    public KinStepButton(KinStep step)
    {
        Step = step;
    }

    /// <summary>这一步关系（按钮的 <c>CommandParameter</c>）。</summary>
    public KinStep Step { get; }

    /// <summary>按钮上的字：「爸爸」「哥哥」…。</summary>
    public string Label => Step.Label();

    /// <summary>悬停提示：这一步是往上 / 往下 / 同辈。</summary>
    public string ToolTipText => Step.GenerationDelta() switch
    {
        > 0 => $"{Label}：长一辈",
        < 0 => $"{Label}：小一辈",
        _ => Step.IsSpouse() ? $"{Label}：配偶" : $"{Label}：同辈",
    };
}

/// <summary>对照表里的一行：一条称谓 + 它的关系链。</summary>
public sealed class KinTermItem
{
    public KinTermItem(KinTerm term)
    {
        Term = term;
        Path = KinPath.ParseToken(term.Key);
        PathText = KinPath.Describe(Path);
        GenerationText = KinPath.DescribeGeneration(KinPath.GenerationDelta(Path));
    }

    /// <summary>原始称谓数据。</summary>
    public KinTerm Term { get; }

    /// <summary>关系链（紧凑写法，装回关系链用）。</summary>
    public string Key => Term.Key;

    /// <summary>展开后的关系链。</summary>
    public IReadOnlyList<KinStep> Path { get; }

    /// <summary>关系链的中文写法：「爸爸的哥哥」。</summary>
    public string PathText { get; }

    /// <summary>辈分：「长一辈」。</summary>
    public string GenerationText { get; }

    /// <summary>北方叫法。</summary>
    public string North => Term.North;

    /// <summary>南方叫法。</summary>
    public string South => Term.South;

    /// <summary>书面称谓（南北一样时界面上当主标题）。</summary>
    public string Formal => Term.Formal;

    /// <summary>主标题：南北叫法一致就用它，否则用书面称谓（两列各显示南北）。</summary>
    public string Title => Term.IsSameNationwide ? Term.North : Term.Formal;

    /// <summary>南北叫法不一样时注意一下（界面上用来加粗提示）。</summary>
    public bool HasRegionDifference => !Term.IsSameNationwide;

    /// <summary>悬停提示：关系 + 白话说明。</summary>
    public string ToolTipText => $"{PathText}（{GenerationText}）\n{Term.Note}";

    /// <summary>搜索匹配：叫法 / 书面称谓 / 关系名 / 说明。</summary>
    public bool Matches(string keyword)
        => Term.North.Contains(keyword, StringComparison.Ordinal)
        || Term.South.Contains(keyword, StringComparison.Ordinal)
        || Term.Formal.Contains(keyword, StringComparison.Ordinal)
        || PathText.Contains(keyword, StringComparison.Ordinal)
        || Term.Note.Contains(keyword, StringComparison.Ordinal);
}
