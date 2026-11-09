using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.RandomNumber.Core;

namespace YuLiangToolkit.Tools.RandomNumber.ViewModels;

/// <summary>
/// 「随机数生成器」的 ViewModel：把配置里的十进制文本喂给纯逻辑 <see cref="ExactRandom"/>，
/// 生成精确结果并展示 / 复制。业务（网格换算 / 无偏采样 / 排序 / 拼接）全在 Core 里，界面只绑定。
///
/// <para>配置一改就自动落盘（无保存按钮）；参数变化不自动重算，点「生成」或按 Enter 才出结果，
/// 免得每敲一个字符都在算 1e100 级别的网格。</para>
/// </summary>
public sealed class RandomNumberViewModel : ToolViewModelBase
{
    public RandomNumberViewModel() : base(RandomNumberConfig.ToolId)
    {
        Cfg = LoadConfig<RandomNumberConfig>();
        NormalizeConfig();

        GenerateCommand = new RelayCommand(_ => Generate());
        ClearCommand = new RelayCommand(_ => ClearResults());
        CopyCommand = new RelayCommand(_ => CopyResults(), _ => Results.Count > 0);
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());

        Cfg.PropertyChanged += OnConfigChanged;
    }

    // ---------- 配置 ----------

    /// <summary>强类型配置（→ <c>Config\random-number\config.json</c>）。</summary>
    public RandomNumberConfig Cfg { get; }

    /// <summary>分隔符下拉框选项。</summary>
    public IReadOnlyList<string> SeparatorOptions => ExactRandom.SeparatorOptions;

    /// <summary>精确度常用步长（点一下填进输入框；也可以自己手打任意值）。</summary>
    public IReadOnlyList<string> StepPresets { get; } = new[] { "1", "0.1", "0.01", "0.001", "0.0001" };

    /// <summary>预设步长按钮统一入口。</summary>
    public RelayCommand ApplyStepPresetCommand => _applyStepPresetCommand ??= new RelayCommand(p => ApplyStepPreset(p as string));
    private RelayCommand? _applyStepPresetCommand;

    // ---------- 结果 ----------

    /// <summary>生成的结果（每个都是完整十进制文本，不丢位）。</summary>
    public ObservableCollection<string> Results { get; } = new();

    private string _resultsText = "";

    /// <summary>结果区 / 复制用的整段文本。</summary>
    public string ResultsText
    {
        get => _resultsText;
        private set => Set(ref _resultsText, value);
    }

    private string _errorText = "";

    /// <summary>参数错误提示（中文小字，不弹窗）。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    private string _rangeHint = "";

    /// <summary>网格信息（刻度总数 / 对齐后的上限 / 点数）。</summary>
    public string RangeHint
    {
        get => _rangeHint;
        private set => Set(ref _rangeHint, value);
    }

    /// <summary>有没有结果（空状态提示 / 按钮可用性）。</summary>
    public bool HasResults => Results.Count > 0;

    // ---------- 命令 ----------

    public RelayCommand GenerateCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }

    // ---------- 实现 ----------

    private void Generate()
    {
        var result = ExactRandom.Generate(
            Cfg.Lower, Cfg.Upper, Cfg.Step,
            Cfg.SafeCount, Cfg.AllowDuplicates, Cfg.SortResults, Cfg.SafeSeparator);

        if (!result.Success)
        {
            ClearResultOnly();
            ErrorText = result.Error ?? "参数不合法";
            RangeHint = "";
            StatusText = "参数有误，请检查下限 / 上限 / 精确度";
            return;
        }

        Results.Clear();
        foreach (var value in result.Values) Results.Add(value);
        ResultsText = result.Text;
        ErrorText = "";

        // 网格信息重新算一遍（纯展示，不参与生成）
        var rangeText = "";
        if (DecimalStepRange.TryCreate(Cfg.Lower, Cfg.Upper, Cfg.Step, out var range, out _) && range is not null)
            rangeText = $"网格 {range.PointCount} 个刻度 · 实际区间 [{range.Lower.ToPlainString()}, {range.Upper.ToPlainString()}]";
        if (!string.IsNullOrEmpty(result.UpperAlignedHint)) rangeText += " · " + result.UpperAlignedHint;
        RangeHint = rangeText;

        OnPropertyChanged(nameof(HasResults));
        StatusText = $"已生成 {Results.Count} 个随机数";
    }

    private void ClearResults()
    {
        ClearResultOnly();
        ErrorText = "";
        RangeHint = "";
        StatusText = "已清空结果";
    }

    private void ClearResultOnly()
    {
        Results.Clear();
        ResultsText = "";
        OnPropertyChanged(nameof(HasResults));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void CopyResults()
    {
        if (string.IsNullOrEmpty(ResultsText))
        {
            StatusText = "还没有结果可复制";
            return;
        }

        try
        {
            Clipboard.SetText(ResultsText);
            StatusText = $"已复制 {Results.Count} 个结果到剪贴板";
        }
        catch
        {
            StatusText = "复制失败（剪贴板可能被其它程序占用）";
        }
    }

    private void ApplyStepPreset(string? step)
    {
        if (string.IsNullOrWhiteSpace(step)) return;
        Cfg.Step = step;
        StatusText = $"精确度已设为 {step}";
    }

    // ---------- 配置联动 ----------

    /// <summary>把配置里可能被手改坏的值收回合法范围（并立刻写回默认 JSON）。</summary>
    private void NormalizeConfig()
    {
        if (Cfg.Count != Cfg.SafeCount) Cfg.Count = Cfg.SafeCount;
        if (Cfg.Separator != Cfg.SafeSeparator) Cfg.Separator = Cfg.SafeSeparator;
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RandomNumberConfig.Separator):
                if (Results.Count > 0) ResultsText = DecimalStepRange.Join(Results, Cfg.SafeSeparator);
                break;
            case nameof(RandomNumberConfig.Count):
            case nameof(RandomNumberConfig.AllowDuplicates):
            case nameof(RandomNumberConfig.SortResults):
            case nameof(RandomNumberConfig.Lower):
            case nameof(RandomNumberConfig.Upper):
            case nameof(RandomNumberConfig.Step):
                // 参数变了只提示「需要重新生成」，不自动重算（大数网格没必要每敲一键就算一次）
                StatusText = "参数已改，点「生成随机数」出新结果";
                break;
        }
    }

    /// <summary>切走时摘掉配置事件（配置对象是缓存复用的，不摘会越挂越多）。</summary>
    protected override void OnClosed() => Cfg.PropertyChanged -= OnConfigChanged;
}
