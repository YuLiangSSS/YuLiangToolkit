using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Expressions;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.FunctionPlot.Core;
using AngleUnitKind = YuLiangToolkit.Core.Expressions.AngleUnit;

namespace YuLiangToolkit.Tools.FunctionPlot;

/// <summary>
/// 函数绘图里「一条函数」的配置项（会随 <see cref="FunctionPlotConfig.Functions"/> 写进
/// <c>Config\function-plot\config.json</c>）。
///
/// <para>它同时承担一点纯逻辑：表达式编译 / 中文错误、定义域规整（写反自动交换、可留空）。
/// 这些方法不碰界面，自检里可以直接 <c>new FunctionPlotItem()</c> 断言。</para>
/// </summary>
public sealed class FunctionPlotItem : ObservableObject
{
    private string _expression = "sin(x)";
    private string _colorHex = FunctionPlotPalette.Colors[0];
    private bool _isVisible = true;
    private double? _domainMin;
    private double? _domainMax;

    /// <summary>函数表达式（例如 <c>sin(x)</c>、<c>x^2</c>；支持常量与省略乘号）。</summary>
    public string Expression
    {
        get => _expression;
        set => Set(ref _expression, value ?? "");
    }

    /// <summary>曲线颜色（<c>#RRGGBB</c>）。这是**数据色**，从固定调色板里选，不算主题色。</summary>
    public string ColorHex
    {
        get => _colorHex;
        set => Set(ref _colorHex, string.IsNullOrWhiteSpace(value) ? FunctionPlotPalette.Colors[0] : value.Trim());
    }

    /// <summary>是否在图上显示（关掉只是不画，表达式与定义域仍保留）。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    /// <summary>定义域左端；<c>null</c> = 不限制（用当前可视范围）。</summary>
    public double? DomainMin
    {
        get => _domainMin;
        set => Set(ref _domainMin, double.IsFinite(value ?? 0) ? value : null);
    }

    /// <summary>定义域右端；<c>null</c> = 不限制（用当前可视范围）。</summary>
    public double? DomainMax
    {
        get => _domainMax;
        set => Set(ref _domainMax, double.IsFinite(value ?? 0) ? value : null);
    }

    /// <summary>界面上显示的名字（表达式本身；空表达式给个占位）。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Expression) ? "（空表达式）" : Expression.Trim();

    /// <summary>是否填了定义域（任意一端）。</summary>
    [JsonIgnore]
    public bool HasDomain => DomainMin.HasValue || DomainMax.HasValue;

    /// <summary>编译表达式；失败时 <paramref name="error"/> 是给用户看的中文提示（空表达式也给中文）。</summary>
    public bool TryCompile(out MathExpression? expression, out string? error)
    {
        if (string.IsNullOrWhiteSpace(Expression))
        {
            expression = null;
            error = "表达式不能为空";
            return false;
        }

        return MathExpression.TryParse(Expression, out expression, out error);
    }

    /// <summary>只取错误提示：合法返回空字符串。</summary>
    public string GetExpressionError() => TryCompile(out _, out var error) ? "" : error ?? "表达式写法有误";

    /// <summary>
    /// 求这条函数要采样的 x 区间：定义域与可视范围求交；定义域写反自动交换；
    /// 交集为空（比如定义域 [5, 6] 而可视范围是 [−10, 10]）返回 <c>null</c>（不画）。
    /// </summary>
    public (double Min, double Max)? EffectiveDomain(double viewXMin, double viewXMax)
    {
        if (!double.IsFinite(viewXMin) || !double.IsFinite(viewXMax)) return null;
        if (viewXMin > viewXMax) (viewXMin, viewXMax) = (viewXMax, viewXMin);

        var min = DomainMin;
        var max = DomainMax;

        // 定义域写反（先填上限后填下限）自动交换，别让用户以为自己填错了
        if (min.HasValue && max.HasValue && min.Value > max.Value) (min, max) = (max, min);

        var left = min.HasValue && double.IsFinite(min.Value) ? Math.Max(viewXMin, min.Value) : viewXMin;
        var right = max.HasValue && double.IsFinite(max.Value) ? Math.Min(viewXMax, max.Value) : viewXMax;

        return right > left ? (left, right) : null;
    }

    /// <summary>求值用：x 是否落在定义域内（定义域写反同样自动交换；两端都空 = 全实数）。</summary>
    public bool IsInDomain(double x)
    {
        if (!double.IsFinite(x)) return false;

        var min = DomainMin;
        var max = DomainMax;
        if (min.HasValue && max.HasValue && min.Value > max.Value) (min, max) = (max, min);

        if (min.HasValue && double.IsFinite(min.Value) && x < min.Value) return false;
        if (max.HasValue && double.IsFinite(max.Value) && x > max.Value) return false;
        return true;
    }
}

/// <summary>
/// 「函数绘图」的强类型配置 → <c>Config\function-plot\config.json</c>（改一下最多 400ms 自动落盘，没有保存按钮）。
///
/// <para>带 setter 的计算属性（<see cref="UsesDegrees"/> / <see cref="ParsedAngleUnit"/> /
/// <see cref="SafeXRange"/> …）都标了 <c>[JsonIgnore]</c>，不会被写进配置文件。</para>
/// </summary>
public sealed class FunctionPlotConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名，自检会核对它与 ITool.Id 一致）。</summary>
    [JsonIgnore]
    public static string ToolId => "function-plot";

    /// <summary>采样点数下限。</summary>
    public const int MinSamples = 200;

    /// <summary>采样点数上限。</summary>
    public const int MaxSamples = 4000;

    /// <summary>采样点数默认值。</summary>
    public const int DefaultSamples = 1200;

    /// <summary>角度制选项值：弧度。</summary>
    public const string RadianLabel = "弧度";

    /// <summary>角度制选项值：角度（°）。</summary>
    public const string DegreeLabel = "角度";

    private List<FunctionPlotItem> _functions = CreateDefaultFunctions();

    /// <summary>要画的函数列表（可增删；每项自己带表达式 / 颜色 / 显示开关 / 定义域）。</summary>
    public List<FunctionPlotItem> Functions
    {
        get => _functions;
        set => Set(ref _functions, value ?? new List<FunctionPlotItem>());
    }

    private bool _hideAxes;

    /// <summary>隐藏坐标系（坐标轴与刻度；网格是否显示由 <see cref="HideGrid"/> 单独控制）。</summary>
    public bool HideAxes
    {
        get => _hideAxes;
        set => Set(ref _hideAxes, value);
    }

    private bool _hideGrid;

    /// <summary>隐藏网格（只留坐标轴与曲线）。</summary>
    public bool HideGrid
    {
        get => _hideGrid;
        set => Set(ref _hideGrid, value);
    }

    private string _angleUnit = RadianLabel;

    /// <summary>三角函数角度单位：<see cref="RadianLabel"/> 或 <see cref="DegreeLabel"/>。</summary>
    public string AngleUnit
    {
        get => _angleUnit;
        set => Set(ref _angleUnit, NormalizeAngleUnit(value));
    }

    private int _samples = DefaultSamples;

    /// <summary>采样点数（200 ~ 4000；越大越平滑，越大也越费 CPU）。</summary>
    public int Samples
    {
        get => _samples;
        set => Set(ref _samples, Math.Clamp(value, MinSamples, MaxSamples));
    }

    private bool _showCrosshair = true;

    /// <summary>鼠标在图上时显示十字准星。</summary>
    public bool ShowCrosshair
    {
        get => _showCrosshair;
        set => Set(ref _showCrosshair, value);
    }

    private bool _showLegend = true;

    /// <summary>在绘图区右上角显示图例（颜色 + 表达式）。</summary>
    public bool ShowLegend
    {
        get => _showLegend;
        set => Set(ref _showLegend, value);
    }

    private bool _autoYRange = true;

    /// <summary>自动计算 Y 范围（关掉后用 <see cref="YMin"/> / <see cref="YMax"/> 手动范围）。</summary>
    public bool AutoYRange
    {
        get => _autoYRange;
        set => Set(ref _autoYRange, value);
    }

    private double _xMin = PlotViewport.DefaultXMin;

    /// <summary>手动 X 范围左端（双击复位会恢复成 −10）。</summary>
    public double XMin
    {
        get => _xMin;
        set => Set(ref _xMin, value);
    }

    private double _xMax = PlotViewport.DefaultXMax;

    /// <summary>手动 X 范围右端（双击复位会恢复成 10）。</summary>
    public double XMax
    {
        get => _xMax;
        set => Set(ref _xMax, value);
    }

    private double _yMin = PlotViewport.DefaultYMin;

    /// <summary>手动 Y 范围下端（<see cref="AutoYRange"/> 打开时不生效）。</summary>
    public double YMin
    {
        get => _yMin;
        set => Set(ref _yMin, value);
    }

    private double _yMax = PlotViewport.DefaultYMax;

    /// <summary>手动 Y 范围上端（<see cref="AutoYRange"/> 打开时不生效）。</summary>
    public double YMax
    {
        get => _yMax;
        set => Set(ref _yMax, value);
    }

    // ---------------------------------------------------------------- 计算属性（不落盘）

    /// <summary>界面上的「角度制」开关（勾上 = 角度）。带 setter 的计算属性，必须 <c>[JsonIgnore]</c>。</summary>
    [JsonIgnore]
    public bool UsesDegrees
    {
        get => NormalizeAngleUnit(AngleUnit) == DegreeLabel;
        set => AngleUnit = value ? DegreeLabel : RadianLabel;
    }

    /// <summary>给 <c>FunctionSampler</c> 用的角度单位枚举。</summary>
    [JsonIgnore]
    public AngleUnitKind ParsedAngleUnit => UsesDegrees ? AngleUnitKind.Degrees : AngleUnitKind.Radians;

    /// <summary>规整后的 X 范围（写反自动交换、非有限值回退默认）。</summary>
    [JsonIgnore]
    public (double Min, double Max) SafeXRange
        => PlotViewport.NormalizeRange(XMin, XMax, PlotViewport.DefaultXMin, PlotViewport.DefaultXMax);

    /// <summary>规整后的 Y 范围（写反自动交换、非有限值回退默认）。</summary>
    [JsonIgnore]
    public (double Min, double Max) SafeYRange
        => PlotViewport.NormalizeRange(YMin, YMax, PlotViewport.DefaultYMin, PlotViewport.DefaultYMax);

    /// <summary>把写反 / 非有限的手动范围就地修好（读取老配置 / 手改配置文件后调用）。</summary>
    public void NormalizeRanges()
    {
        var (xMin, xMax) = SafeXRange;
        if (XMin != xMin) XMin = xMin;
        if (XMax != xMax) XMax = xMax;

        var (yMin, yMax) = SafeYRange;
        if (YMin != yMin) YMin = yMin;
        if (YMax != yMax) YMax = yMax;
    }

    /// <summary>角度制值归一化：只认 <see cref="DegreeLabel"/>，其余一律按弧度。</summary>
    public static string NormalizeAngleUnit(string? value)
        => value is not null && value.Trim() == DegreeLabel ? DegreeLabel : RadianLabel;

    /// <summary>默认函数列表：一上来就能看到两条曲线，空状态留给用户自己删光时出现。</summary>
    private static List<FunctionPlotItem> CreateDefaultFunctions()
        => new()
        {
            new FunctionPlotItem { Expression = "sin(x)", ColorHex = FunctionPlotPalette.Colors[0] },
            new FunctionPlotItem { Expression = "cos(x)", ColorHex = FunctionPlotPalette.Colors[1] },
        };
}

/// <summary>
/// 曲线调色板（数据色，不是主题色）：8 个在深浅两种主题下都看得清的饱和色。
/// 选中色以 <c>#RRGGBB</c> 存进配置，网格 / 坐标轴 / 文字仍然走 <c>DynamicResource</c> 主题 token。
/// </summary>
public static class FunctionPlotPalette
{
    /// <summary>8 个固定曲线颜色。</summary>
    public static IReadOnlyList<string> Colors { get; } = new[]
    {
        "#4C8DFF", // 蓝
        "#FF9F43", // 橙
        "#3FB950", // 绿
        "#E5534B", // 红
        "#B47CFF", // 紫
        "#F2C94C", // 黄
        "#2EC4B6", // 青
        "#FF6FB5", // 粉
    };

    /// <summary>按序号取色（越界自动绕回，负数也安全）。</summary>
    public static string At(int index)
    {
        if (Colors.Count == 0) return "#4C8DFF";
        var normalized = ((index % Colors.Count) + Colors.Count) % Colors.Count;
        return Colors[normalized];
    }
}
