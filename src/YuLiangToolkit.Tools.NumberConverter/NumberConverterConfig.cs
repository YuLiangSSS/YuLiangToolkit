using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.NumberConverter;

/// <summary>
/// 「数字转换器」的强类型配置 → <c>Config\number-converter\config.json</c>。
///
/// <para>改一下自动落盘，没有保存按钮。输入框里的内容也存进来（下次打开还是上次那个数），
/// 所有口味开关（圆 / 元、and、a thousand、连字符、角分补零）同样是持久化项。</para>
/// </summary>
public sealed class NumberConverterConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名）。</summary>
    [JsonIgnore]
    public static string ToolId => "number-converter";

    /// <summary>「圆」写法（财务 / 支票常用）。</summary>
    public const string YuanCircle = "圆";

    /// <summary>「元」写法（合同 / 发票也常见）。</summary>
    public const string YuanYuan = "元";

    /// <summary>货币单位的两个选项（下拉框）。</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> CurrencyOptions { get; } = new[] { YuanCircle, YuanYuan };

    private string _input = "1234.56";

    /// <summary>要转换的内容（阿拉伯数字，或中文数字 / 金额文本）。</summary>
    public string Input
    {
        get => _input;
        set => Set(ref _input, value ?? "");
    }

    private string _currency = YuanCircle;

    /// <summary>金额单位：圆 / 元。</summary>
    public string Currency
    {
        get => _currency;
        set => Set(ref _currency, value ?? "");
    }

    private bool _zeroOnJiaoGap = true;

    /// <summary>元后角位为零而分位有值时补「零」（1680.32 → 壹仟陆佰捌拾圆零叁角贰分）。</summary>
    public bool ZeroOnJiaoGap
    {
        get => _zeroOnJiaoGap;
        set => Set(ref _zeroOnJiaoGap, value);
    }

    private bool _wholeAfterJiao = true;

    /// <summary>角后有值而分位为零时写「整」（12.30 → 壹拾贰圆叁角整）。</summary>
    public bool WholeAfterJiao
    {
        get => _wholeAfterJiao;
        set => Set(ref _wholeAfterJiao, value);
    }

    private bool _englishUseAnd = true;

    /// <summary>英式：百位后加 and（one hundred and twenty-three）。关掉 = 美式。</summary>
    public bool EnglishUseAnd
    {
        get => _englishUseAnd;
        set => Set(ref _englishUseAnd, value);
    }

    private bool _englishUseA = true;

    /// <summary>开头的 1 念 a（只对带量级的档生效：1000 → a thousand、1000000 → a million）。</summary>
    public bool EnglishUseA
    {
        get => _englishUseA;
        set => Set(ref _englishUseA, value);
    }

    private bool _englishHyphenate = true;

    /// <summary>十位与个位之间用连字符（twenty-three）。</summary>
    public bool EnglishHyphenate
    {
        get => _englishHyphenate;
        set => Set(ref _englishHyphenate, value);
    }

    private int _radix = 16;

    /// <summary>进制（输入框里没写 0x / 0b / 0o 前缀时，按这个进制解析）。</summary>
    public int Radix
    {
        get => _radix;
        set => Set(ref _radix, value);
    }

    /// <summary>夹到合法值的货币单位。</summary>
    [JsonIgnore]
    public string SafeCurrency => CurrencyOptions.Contains(Currency) ? Currency : YuanCircle;

    /// <summary>夹到合法值的进制（2 ~ 36）。</summary>
    [JsonIgnore]
    public int SafeRadix => Math.Clamp(Radix, 2, 36);
}
