using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Calculator.Core;

namespace YuLiangToolkit.Tools.Calculator.ViewModels;

/// <summary>
/// 「计算器」的 ViewModel：表达式输入 / 实时预览 / 键盘按钮 / 历史记录 / 精度与角度单位 / 复制结果。
///
/// <para>业务重点（都在这个文件里，界面只做绑定）：</para>
/// <list type="number">
///   <item><b>实时预览</b>：<see cref="Expression"/> 一改就重新求值（可在设置里关掉），
///         解析失败只在结果区显示中文错误，不弹窗；</item>
///   <item><b>求值记录</b>：按 Enter / <c>=</c> 时把 <c>"表达式 = 结果"</c> 记进历史（相邻去重 + 上限裁剪），
///         并把有限结果记成 Ans；</item>
///   <item><b>输入构造</b>：按钮追加什么、小数点去重、±、Ans 补乘号等规则全部委托给
///         <see cref="CalculatorInput"/> / <see cref="CalculatorHistory"/> 两个纯逻辑类（好断言）。</item>
/// </list>
/// </summary>
public sealed class CalculatorViewModel : ToolViewModelBase
{
    /// <summary>上一次结果的文本（精确大数也用文本，绝不塞回 double 丢精度）。「无定义」/ ∞ 不更新它。</summary>
    private string _lastValueText = "";

    /// <summary>有没有可用的 Ans。</summary>
    private bool _hasAns;

    public CalculatorViewModel() : base(CalculatorConfig.ToolId)
    {
        Cfg = LoadConfig<CalculatorConfig>();
        NormalizeConfig();

        Kin = new KinCalculatorViewModel(Cfg);

        KeyCommand = new RelayCommand(
            parameter => PressKey(parameter as CalculatorKey),
            parameter => parameter is CalculatorKey);
        EvaluateCommand = new RelayCommand(_ => EvaluateExpression(recordHistory: true));
        ClearCommand = new RelayCommand(_ => ClearInput());
        BackspaceCommand = new RelayCommand(_ => Expression = CalculatorInput.Backspace(Expression));
        ToggleSignCommand = new RelayCommand(_ => Expression = CalculatorInput.ToggleSign(Expression));
        InsertAnsCommand = new RelayCommand(_ => InsertAns(), _ => _hasAns);
        ClearHistoryCommand = new RelayCommand(_ => ClearHistory(), _ => HistoryItems.Count > 0);
        CopyResultCommand = new RelayCommand(_ => CopyResult(), _ => !string.IsNullOrEmpty(ResultText));
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());

        // 选项变了要让结果 / 预览 / 历史立刻跟上（配置本身写盘是自动的，这里只管界面联动）
        Cfg.PropertyChanged += OnConfigChanged;

        RebuildHistoryItems();
        UpdateAnsText();
        RefreshPreview();
    }

    // ---------- 配置 ----------

    /// <summary>强类型配置（→ <c>Config\calculator\config.json</c>）。</summary>
    public CalculatorConfig Cfg { get; }

    /// <summary>辈分计算模式的关系链 / 结果 / 对照表（与标准计算共用同一个工具页与同一份配置）。</summary>
    public KinCalculatorViewModel Kin { get; }

    /// <summary>当前是不是辈分计算模式（界面切显示哪个模式，代码后置判断要不要拦 Enter / Esc）。</summary>
    public bool IsKinMode
    {
        get => Cfg.IsKinMode;
        set
        {
            if (value == Cfg.IsKinMode) return;
            Cfg.Mode = value ? CalculatorConfig.KinMode : CalculatorConfig.StandardMode;
        }
    }

    /// <summary>模式切换：标准计算（与 <see cref="IsKinMode"/> 互斥，两个单选按钮各绑一个）。</summary>
    public bool IsStandardMode
    {
        get => !Cfg.IsKinMode;
        set { if (value) IsKinMode = false; }
    }

    /// <summary>模式徽章（页头小字，说明当前在哪个模式）。</summary>
    public string ModeText => Cfg.IsKinMode ? "辈分计算模式" : "标准计算模式";

    /// <summary>角度单位下拉框选项（弧度 / 角度）。</summary>
    public IReadOnlyList<string> AngleUnitOptions => CalculatorConfig.AngleUnitOptions;

    /// <summary>基础键盘按钮（0-9 / 运算符 / 括号 / C / ⌫ / ± / Ans / x² / 1/x / =，4 列 × 6 行）。</summary>
    public IReadOnlyList<CalculatorKey> BasicKeys => CalculatorKey.Basic;

    /// <summary>科学键盘按钮（三角 / 对数 / 根 / 幂 / 阶乘 / 常量 / 取整 / 取模，7 列 × 3 行）。</summary>
    public IReadOnlyList<CalculatorKey> ScientificKeys => CalculatorKey.Scientific;

    /// <summary>结果区左上角的当前单位徽章（角度制 / 弧度制）。</summary>
    public string AngleUnitBadgeText
        => Cfg.AngleUnit == CalculatorConfig.DegreesUnit ? "角度制（°）" : "弧度制（rad）";

    // ---------- 输入与结果 ----------

    private string _expression = "";

    /// <summary>输入框里的表达式（一改就实时预览）。</summary>
    public string Expression
    {
        get => _expression;
        set
        {
            if (!Set(ref _expression, value ?? "")) return;
            if (Cfg.EnableLivePreview) RefreshPreview();
        }
    }

    private string _resultText = "";

    /// <summary>结果区的大字结果（可能是「无定义」/「+∞」/「0.5」）。</summary>
    public string ResultText
    {
        get => _resultText;
        private set => Set(ref _resultText, value);
    }

    private string _errorText = "";

    /// <summary>错误提示（中文，只有解析失败 / 用了变量 x 时才有；界面用红色小字显示）。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    private string _ansText = "Ans = —";

    /// <summary>上次结果的提示（用于显示「Ans = 1.5」，也说明 Ans 键现在可不可用）。</summary>
    public string AnsText
    {
        get => _ansText;
        private set => Set(ref _ansText, value);
    }

    // ---------- 历史记录 ----------

    /// <summary>历史记录（新的在前）；点一行回填表达式。</summary>
    public ObservableCollection<CalculatorHistoryItem> HistoryItems { get; } = new();

    /// <summary>有没有历史（空状态提示用）。</summary>
    public bool HasHistory => HistoryItems.Count > 0;

    /// <summary>历史区的小字统计。</summary>
    public string HistoryCountText
        => HasHistory ? $"共 {HistoryItems.Count} 条 · 最多保留 {Cfg.SafeHistoryLimit} 条" : $"最多保留 {Cfg.SafeHistoryLimit} 条";

    // ---------- 命令 ----------

    /// <summary>键盘按钮统一入口（按钮的 <c>CommandParameter</c> 是 <see cref="CalculatorKey"/>）。</summary>
    public RelayCommand KeyCommand { get; }

    /// <summary>求值并记入历史（Enter / <c>=</c>）。</summary>
    public RelayCommand EvaluateCommand { get; }

    /// <summary>清空输入（Esc / <c>C</c>）。</summary>
    public RelayCommand ClearCommand { get; }

    /// <summary>退格（⌫）。</summary>
    public RelayCommand BackspaceCommand { get; }

    /// <summary>正负号（±）。</summary>
    public RelayCommand ToggleSignCommand { get; }

    /// <summary>插入上次结果（Ans）。</summary>
    public RelayCommand InsertAnsCommand { get; }

    /// <summary>清空历史记录。</summary>
    public RelayCommand ClearHistoryCommand { get; }

    /// <summary>复制结果到剪贴板。</summary>
    public RelayCommand CopyResultCommand { get; }

    /// <summary>打开本工具的配置文件夹。</summary>
    public RelayCommand OpenConfigFolderCommand { get; }

    // ---------- 键盘按钮 ----------

    private void PressKey(CalculatorKey? key)
    {
        if (key is null) return;

        switch (key.Kind)
        {
            case CalculatorKeyKind.Insert:
                Expression = CalculatorInput.Append(Expression, key.Insert);
                break;
            case CalculatorKeyKind.Evaluate:
                EvaluateExpression(recordHistory: true);
                break;
            case CalculatorKeyKind.Clear:
                ClearInput();
                break;
            case CalculatorKeyKind.Backspace:
                Expression = CalculatorInput.Backspace(Expression);
                break;
            case CalculatorKeyKind.ToggleSign:
                Expression = CalculatorInput.ToggleSign(Expression);
                break;
            case CalculatorKeyKind.InsertAns:
                InsertAns();
                break;
            case CalculatorKeyKind.Square:
                Expression = CalculatorInput.AppendSquare(Expression);
                break;
            case CalculatorKeyKind.Reciprocal:
                Expression = CalculatorInput.WrapReciprocal(Expression);
                break;
        }
    }

    // ---------- 求值与预览 ----------

    /// <summary>按当前表达式求值；<paramref name="recordHistory"/>=true 时记历史并更新 Ans。</summary>
    private void EvaluateExpression(bool recordHistory)
    {
        if (string.IsNullOrWhiteSpace(Expression))
        {
            ResultText = "";
            ErrorText = "";
            StatusText = CalculatorEngine.EmptyHint;
            return;
        }

        var result = CalculatorEngine.Evaluate(Expression, Cfg.AngleUnitValue, Cfg.SafeSignificantDigits);
        if (!result.Success)
        {
            ResultText = "";
            ErrorText = result.Error ?? CalculatorEngine.FallbackError;
            StatusText = "表达式有误，请检查输入";
            return;
        }

        ResultText = result.Text;
        ErrorText = "";

        if (result.CanBeAns)
        {
            _lastValueText = CalculatorEngine.GetAnsText(result);
            _hasAns = true;
            UpdateAnsText();
        }

        if (recordHistory)
        {
            AddHistory(CalculatorHistory.Compose(Expression.Trim(), result.Text));
            StatusText = $"已计算：{Expression.Trim()} = {result.Text}";
        }
    }

    /// <summary>实时预览：只刷新结果区，不记历史、不动 Ans（按 Enter 才算「真正求值」）。</summary>
    private void RefreshPreview()
    {
        if (string.IsNullOrWhiteSpace(Expression))
        {
            ResultText = "";
            ErrorText = "";
            return;
        }

        var result = CalculatorEngine.Evaluate(Expression, Cfg.AngleUnitValue, Cfg.SafeSignificantDigits);
        if (!result.Success)
        {
            ResultText = "";
            ErrorText = result.Error ?? CalculatorEngine.FallbackError;
            return;
        }

        ResultText = result.Text;
        ErrorText = "";
    }

    private void ClearInput()
    {
        Expression = "";
        ResultText = "";
        ErrorText = "";
        StatusText = "已清空输入";
    }

    private void InsertAns()
    {
        if (!_hasAns)
        {
            StatusText = "还没有算过结果，Ans 暂时不可用";
            return;
        }

        var text = _lastValueText;
        if (string.IsNullOrEmpty(text)) text = "0";
        Expression = CalculatorInput.InsertNumber(Expression, text);
        StatusText = $"已插入上次结果：{text}";
    }

    private void UpdateAnsText()
        => AnsText = _hasAns ? $"Ans = {_lastValueText}" : "Ans = —";

    // ---------- 历史记录 ----------

    /// <summary>点历史记录里的一行：把表达式回填进输入框并预览一次。</summary>
    public void UseHistory(CalculatorHistoryItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Expression)) return;

        Expression = item.Expression;
        if (!Cfg.EnableLivePreview) RefreshPreview();
        StatusText = $"已回填：{item.Expression}";
    }

    private void AddHistory(string entry)
    {
        Cfg.History = CalculatorHistory.Add(Cfg.History, entry, Cfg.SafeHistoryLimit);
        RebuildHistoryItems();
    }

    private void ClearHistory()
    {
        Cfg.History = CalculatorHistory.Clear();
        RebuildHistoryItems();
        StatusText = "已清空历史记录";
    }

    private void RebuildHistoryItems()
    {
        HistoryItems.Clear();
        foreach (var entry in Cfg.History) HistoryItems.Add(new CalculatorHistoryItem(entry));

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryCountText));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- 复制 ----------

    private void CopyResult()
    {
        if (string.IsNullOrWhiteSpace(ResultText))
        {
            StatusText = "结果区是空的，没有可复制的内容";
            return;
        }

        try
        {
            Clipboard.SetText(ResultText);
            StatusText = $"已复制结果：{ResultText}";
        }
        catch
        {
            // 剪贴板偶尔会被别的程序占住：静默失败，别让计算器崩
            StatusText = "复制失败（剪贴板可能被其它程序占用）";
        }
    }

    // ---------- 配置联动 ----------

    /// <summary>加载后把配置里可能被手改坏的值收回到合法范围（并立刻写回默认 JSON）。</summary>
    private void NormalizeConfig()
    {
        Cfg.AngleUnit = CalculatorConfig.NormalizeAngleUnit(Cfg.AngleUnit);
        Cfg.Mode = CalculatorConfig.NormalizeMode(Cfg.Mode);
        Cfg.KinRegion = CalculatorConfig.NormalizeRegion(Cfg.KinRegion);
        Cfg.KinGender = CalculatorConfig.NormalizeGender(Cfg.KinGender);

        if (Cfg.SignificantDigits != Cfg.SafeSignificantDigits)
            Cfg.SignificantDigits = Cfg.SafeSignificantDigits;

        if (Cfg.HistoryLimit != Cfg.SafeHistoryLimit)
            Cfg.HistoryLimit = Cfg.SafeHistoryLimit;

        var trimmed = CalculatorHistory.Trim(Cfg.History, Cfg.SafeHistoryLimit);
        if (trimmed.Count != Cfg.History.Count) Cfg.History = trimmed;
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CalculatorConfig.AngleUnit):
                OnPropertyChanged(nameof(AngleUnitBadgeText));
                RefreshPreview();
                break;
            case nameof(CalculatorConfig.SignificantDigits):
                // 精度只影响 double 回退结果的显示；Ans 若是精确结果就保持原文本
                RefreshPreview();
                break;
            case nameof(CalculatorConfig.HistoryLimit):
                TrimHistoryToLimit();
                break;
            case nameof(CalculatorConfig.EnableLivePreview):
                if (Cfg.EnableLivePreview) RefreshPreview();
                break;
            case nameof(CalculatorConfig.Mode):
                OnPropertyChanged(nameof(IsKinMode));
                OnPropertyChanged(nameof(IsStandardMode));
                OnPropertyChanged(nameof(ModeText));
                break;
        }
    }

    /// <summary>历史条数上限改小时，立刻按新上限裁剪（配置与界面一起更新）。</summary>
    private void TrimHistoryToLimit()
    {
        var trimmed = CalculatorHistory.Trim(Cfg.History, Cfg.SafeHistoryLimit);
        if (trimmed.SequenceEqual(Cfg.History))
        {
            OnPropertyChanged(nameof(HistoryCountText));
            return;
        }

        Cfg.History = trimmed;
        RebuildHistoryItems();
    }

    // ---------- 生命周期 ----------

    /// <summary>切走 / 关闭时摘掉配置事件（配置对象是缓存复用的，不摘会越挂越多）。</summary>
    protected override void OnClosed()
    {
        Cfg.PropertyChanged -= OnConfigChanged;
        Kin.Detach();
    }
}
