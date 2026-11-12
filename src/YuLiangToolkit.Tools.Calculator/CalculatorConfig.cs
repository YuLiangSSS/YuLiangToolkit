using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using Unit = YuLiangToolkit.Core.Expressions.AngleUnit;

namespace YuLiangToolkit.Tools.Calculator;

/// <summary>
/// 「计算器」的强类型配置 → <c>Config\calculator\config.json</c>。
/// 和其它工具一样：属性一改就自动落盘（几组配置都走 <c>Set(ref 字段, 值)</c>，界面 / 预览要立刻跟着刷新），
/// 不需要保存按钮。
/// </summary>
public sealed class CalculatorConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名，自检会核对它与 ITool.Id 一致）。</summary>
    [JsonIgnore]
    public static string ToolId => "calculator";

    /// <summary>角度制（一圈 360°）。</summary>
    public const string DegreesUnit = "角度";

    /// <summary>弧度制（数学默认，sin(π/2) = 1）。</summary>
    public const string RadiansUnit = "弧度";

    /// <summary>有效位数下限（再低就没法看了）。</summary>
    public const int MinSignificantDigits = 3;

    /// <summary>有效位数上限（与 <c>MathFormat.Format</c> 的精度上限对齐）。</summary>
    public const int MaxSignificantDigits = 12;

    /// <summary>历史记录条数下限。</summary>
    public const int MinHistoryLimit = 10;

    /// <summary>历史记录条数上限。</summary>
    public const int MaxHistoryLimit = 200;

    /// <summary>标准计算模式。</summary>
    public const string StandardMode = "标准计算";

    /// <summary>辈分计算模式。</summary>
    public const string KinMode = "辈分计算";

    /// <summary>北方叫法。</summary>
    public const string NorthRegion = "北方";

    /// <summary>南方叫法。</summary>
    public const string SouthRegion = "南方";

    /// <summary>「我」是男性。</summary>
    public const string MaleGender = "男";

    /// <summary>「我」是女性。</summary>
    public const string FemaleGender = "女";

    /// <summary>角度单位下拉框的选项（存进配置的就是这两个干净值）。</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> AngleUnitOptions { get; } = new[] { RadiansUnit, DegreesUnit };

    /// <summary>模式切换的选项：标准计算 / 辈分计算。</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> ModeOptions { get; } = new[] { StandardMode, KinMode };

    /// <summary>南北叫法的选项。</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> RegionOptions { get; } = new[] { NorthRegion, SouthRegion };

    /// <summary>「我」的性别选项（只影响对方怎么叫我，不影响正向称谓）。</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> GenderOptions { get; } = new[] { MaleGender, FemaleGender };

    private string _angleUnit = RadiansUnit;

    /// <summary>三角函数用的角度单位：<see cref="RadiansUnit"/>（默认）/ <see cref="DegreesUnit"/>。</summary>
    public string AngleUnit
    {
        get => _angleUnit;
        set => Set(ref _angleUnit, NormalizeAngleUnit(value));
    }

    private int _significantDigits = 12;

    /// <summary>结果保留的有效位数（3 ~ 12，默认 12）。</summary>
    public int SignificantDigits
    {
        get => _significantDigits;
        set => Set(ref _significantDigits, value);
    }

    private int _historyLimit = 50;

    /// <summary>历史记录最多保留多少条（10 ~ 200，默认 50；新的在前）。</summary>
    public int HistoryLimit
    {
        get => _historyLimit;
        set => Set(ref _historyLimit, value);
    }

    private bool _enableLivePreview = true;

    /// <summary>边打边实时预览结果（关掉后只有按 Enter / = 才更新结果区）。</summary>
    public bool EnableLivePreview
    {
        get => _enableLivePreview;
        set => Set(ref _enableLivePreview, value);
    }

    /// <summary>
    /// 历史记录（新的在前）。每条是 <c>"表达式 = 结果"</c> 的文本，
    /// 这样重新打开工具也能看到当时算出的结果（回填时取等号左边）。
    /// </summary>
    public List<string> History { get; set; } = new();

    private string _mode = StandardMode;

    /// <summary>当前模式：<see cref="StandardMode"/>（默认）/ <see cref="KinMode"/>。切走了下次打开还是它。</summary>
    public string Mode
    {
        get => _mode;
        set => Set(ref _mode, NormalizeMode(value));
    }

    private string _kinRegion = NorthRegion;

    /// <summary>辈分计算的叫法地区：<see cref="NorthRegion"/>（默认）/ <see cref="SouthRegion"/>。</summary>
    public string KinRegion
    {
        get => _kinRegion;
        set => Set(ref _kinRegion, NormalizeRegion(value));
    }

    private string _kinGender = MaleGender;

    /// <summary>辈分计算里「我」的性别（默认男）：只影响「对方叫我什么」。</summary>
    public string KinGender
    {
        get => _kinGender;
        set => Set(ref _kinGender, NormalizeGender(value));
    }

    private string _kinPath = "";

    /// <summary>
    /// 上次看着的关系链（紧凑写法，例如 <c>F+oB</c>），切走再回来还停在原处。
    /// 不认识的段在读取时会被丢掉（见 <c>KinPath.ParseToken</c>）。
    /// </summary>
    public string KinPath
    {
        get => _kinPath;
        set => Set(ref _kinPath, value ?? "");
    }

    /// <summary>夹到合法区间的有效位数（配置被手改成 0 / 999 也能用）。</summary>
    [JsonIgnore]
    public int SafeSignificantDigits => Math.Clamp(SignificantDigits, MinSignificantDigits, MaxSignificantDigits);

    /// <summary>夹到合法区间的历史条数上限。</summary>
    [JsonIgnore]
    public int SafeHistoryLimit => Math.Clamp(HistoryLimit, MinHistoryLimit, MaxHistoryLimit);

    /// <summary>配置里的中文单位 → 共享引擎的枚举。</summary>
    [JsonIgnore]
    public Unit AngleUnitValue => AngleUnit == DegreesUnit ? Unit.Degrees : Unit.Radians;

    /// <summary>当前是不是辈分计算模式。</summary>
    [JsonIgnore]
    public bool IsKinMode => Mode == KinMode;

    /// <summary>叫法用北方还是南方。</summary>
    [JsonIgnore]
    public bool PreferNorth => KinRegion != SouthRegion;

    /// <summary>「我」是不是男性。</summary>
    [JsonIgnore]
    public bool SelfIsMale => KinGender != FemaleGender;

    /// <summary>把配置里读到的任意文本规范成两个合法值之一（未知值按弧度处理）。</summary>
    public static string NormalizeAngleUnit(string? value)
        => string.Equals(value, DegreesUnit, StringComparison.Ordinal) ? DegreesUnit : RadiansUnit;

    /// <summary>模式：未知值回到标准计算。</summary>
    public static string NormalizeMode(string? value)
        => string.Equals(value, KinMode, StringComparison.Ordinal) ? KinMode : StandardMode;

    /// <summary>南北：未知值回到北方，并以「北方」为准（配置里写「北 / 北方地区」都算北方）。</summary>
    public static string NormalizeRegion(string? value)
        => value is not null && value.Contains("南", StringComparison.Ordinal) ? SouthRegion : NorthRegion;

    /// <summary>性别：未知值回到男性。</summary>
    public static string NormalizeGender(string? value)
        => string.Equals(value, FemaleGender, StringComparison.Ordinal) ? FemaleGender : MaleGender;
}
