using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.NumberConverter.Core;

namespace YuLiangToolkit.Tools.NumberConverter.ViewModels;

/// <summary>
/// 「数字转换器」的 ViewModel：输入一改就重算（纯字符串运算，没有 IO，随手就能算），
/// 结果铺成一列「标题 | 内容」，每行可单独复制。
///
/// <para>业务全在 <see cref="NumberConverter"/> / <see cref="ChineseNumber"/> / <see cref="EnglishNumber"/> 里，
/// 界面只负责绑定，配置一改自动落盘（无保存按钮）。</para>
/// </summary>
public sealed class NumberConverterViewModel : ToolViewModelBase
{
    public NumberConverterViewModel() : base(NumberConverterConfig.ToolId)
    {
        Cfg = LoadConfig<NumberConverterConfig>();
        NormalizeConfig();

        CopyAllCommand = new RelayCommand(_ => CopyAll(), _ => Rows.Count > 0);
        CopyRowCommand = new RelayCommand(p => CopyRow(p as ConversionRow));
        ClearCommand = new RelayCommand(_ => ClearInput());
        UseSampleCommand = new RelayCommand(p => UseSample(p as string));

        Cfg.PropertyChanged += OnConfigChanged;
        Recalculate();
    }

    // ---------- 配置 ----------

    /// <summary>强类型配置（→ <c>Config\number-converter\config.json</c>）。</summary>
    public NumberConverterConfig Cfg { get; }

    /// <summary>货币单位下拉框选项（圆 / 元）。</summary>
    public IReadOnlyList<string> CurrencyOptions => NumberConverterConfig.CurrencyOptions;

    /// <summary>示例输入：点一下直接填进输入框。</summary>
    public IReadOnlyList<SampleInput> Samples { get; } = new[]
    {
        new SampleInput("1234.56", "金额：中文大写 + 英文念法"),
        new SampleInput("123", "one hundred and twenty-three"),
        new SampleInput("1000", "one thousand / a thousand"),
        new SampleInput("12345.67", "五位金额，看清零与整的规则"),
        new SampleInput("1000000", "百万：中文 / 英文各自的单位"),
        new SampleInput("1680.32", "元后角为零、分有值（补零）"),
        new SampleInput("-0.05", "负数 / 不足一元"),
        new SampleInput("壹拾贰圆叁角肆分", "反向解析中文金额 → 12.34"),
        new SampleInput("一百二十三", "反向解析中文小写 → 123"),
        new SampleInput("0x1F", "十六进制 → 十进制"),
    };

    // ---------- 结果 ----------

    /// <summary>所有可复制的结果行（空内容行已在 <see cref="Recalculate"/> 里过滤掉）。</summary>
    public IReadOnlyList<ConversionRow> Rows { get; private set; } = Array.Empty<ConversionRow>();

    private string _errorText = "";

    /// <summary>输入错误提示（红色小字，不弹窗）。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    private string _summaryText = "";

    /// <summary>这一轮转换的一句话摘要（显示在结果区顶部）。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    /// <summary>有没有结果（空状态提示用）。</summary>
    public bool HasRows => Rows.Count > 0;

    // ---------- 命令 ----------

    public RelayCommand CopyAllCommand { get; }
    public RelayCommand CopyRowCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand UseSampleCommand { get; }

    // ---------- 实现 ----------

    /// <summary>重新转换一遍（输入 / 口味 / 货币单位一变就调）。</summary>
    private void Recalculate()
    {
        var options = BuildOptions();
        var result = NumberConverterEngine.Convert(Cfg.Input, options);

        if (!result.Success)
        {
            Rows = Array.Empty<ConversionRow>();
            ErrorText = result.Error ?? "看不懂这个数字";
            SummaryText = "";
            OnRowsChanged();
            StatusText = "输入无法识别";
            return;
        }

        Rows = result.Rows.Where(r => r.HasText).ToArray();
        ErrorText = "";
        SummaryText = NumberConverterEngine.TryParseInput(Cfg.Input, out var value, out _)
            ? $"标准值 {value.ToPlainString()}"
            : "";
        OnRowsChanged();
        StatusText = $"已转换 {Rows.Count} 种写法";
    }

    private void OnRowsChanged()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasRows));
        CommandManager.InvalidateRequerySuggested();
    }

    private ConverterOptions BuildOptions() => new()
    {
        Currency = Cfg.SafeCurrency,
        ZeroOnJiaoGap = Cfg.ZeroOnJiaoGap,
        WholeAfterJiao = Cfg.WholeAfterJiao,
        Radix = Cfg.SafeRadix,
        English = new EnglishStyle
        {
            UseAnd = Cfg.EnglishUseAnd,
            UseA = Cfg.EnglishUseA,
            Hyphenate = Cfg.EnglishHyphenate,
        },
    };

    private void CopyAll()
    {
        if (Rows.Count == 0)
        {
            StatusText = "还没有结果可复制";
            return;
        }

        var text = string.Join(Environment.NewLine, Rows.Select(r => $"{r.Title}：{r.Text}"));
        CopyToClipboard(text, $"已复制 {Rows.Count} 行结果");
    }

    private void CopyRow(ConversionRow? row)
    {
        if (row is null || !row.HasText) return;
        CopyToClipboard(row.Text, $"已复制「{row.Title}」");
    }

    private void CopyToClipboard(string text, string okText)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText = okText;
        }
        catch
        {
            StatusText = "复制失败（剪贴板可能被其它程序占用）";
        }
    }

    private void ClearInput()
    {
        Cfg.Input = "";
        StatusText = "已清空输入";
    }

    private void UseSample(string? sample)
    {
        if (string.IsNullOrEmpty(sample)) return;
        Cfg.Input = sample;
        StatusText = $"已填入示例：{sample}";
    }

    // ---------- 配置联动 ----------

    /// <summary>把配置里可能被手改坏的值收回合法范围（并立刻写回默认 JSON）。</summary>
    private void NormalizeConfig()
    {
        if (Cfg.Currency != Cfg.SafeCurrency) Cfg.Currency = Cfg.SafeCurrency;
        if (Cfg.Radix != Cfg.SafeRadix) Cfg.Radix = Cfg.SafeRadix;
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NumberConverterConfig.Input):
            case nameof(NumberConverterConfig.Currency):
            case nameof(NumberConverterConfig.ZeroOnJiaoGap):
            case nameof(NumberConverterConfig.WholeAfterJiao):
            case nameof(NumberConverterConfig.EnglishUseAnd):
            case nameof(NumberConverterConfig.EnglishUseA):
            case nameof(NumberConverterConfig.EnglishHyphenate):
            case nameof(NumberConverterConfig.Radix):
                Recalculate();
                break;
        }
    }

    /// <summary>切走时摘掉配置事件（配置对象是缓存复用的，不摘会越挂越多）。</summary>
    protected override void OnClosed() => Cfg.PropertyChanged -= OnConfigChanged;
}

/// <summary>一个示例输入（按钮上显示内容，悬停显示这句说明）。</summary>
public sealed record SampleInput(string Value, string Hint);
