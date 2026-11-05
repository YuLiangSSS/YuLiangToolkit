using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Core.Expressions;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.FunctionPlot.Core;
using AngleUnitKind = YuLiangToolkit.Core.Expressions.AngleUnit;

namespace YuLiangToolkit.Tools.FunctionPlot.ViewModels;

/// <summary>
/// 曲线调色板里的一个颜色选项（数据色）：界面只显示一个色块，配置里存的是 <see cref="Hex"/>。
/// <para>画刷做了缓存并冻结，切换颜色 / 重建曲线时不会反复 new Brush。</para>
/// </summary>
public sealed class ColorChoice : IEquatable<ColorChoice>
{
    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    public ColorChoice(string hex, string name)
    {
        Hex = NormalizeHex(hex);
        Name = name;
        Brush = CreateBrush(Hex);
    }

    /// <summary><c>#RRGGBB</c>（统一成带 # 的大写不改写的小写形式，仅去掉首尾空白）。</summary>
    public string Hex { get; }

    /// <summary>中文色名（仅界面显示）。</summary>
    public string Name { get; }

    /// <summary>已冻结的曲线画刷。</summary>
    public Brush Brush { get; }

    /// <summary>把任意字符串规整成 <c>#RRGGBB</c>（非法内容回退到调色板第一个颜色）。</summary>
    public static string NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return FunctionPlotPalette.Colors[0];
        var text = hex.Trim();
        return text.StartsWith('#') ? text : "#" + text;
    }

    /// <summary>按 <c>#RRGGBB</c> 取（并缓存）冻结画刷；解析失败回退到蓝色。</summary>
    public static SolidColorBrush CreateBrush(string? hex)
    {
        var key = NormalizeHex(hex);
        if (BrushCache.TryGetValue(key, out var cached)) return cached;

        SolidColorBrush brush;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(key);
            brush = new SolidColorBrush(color);
        }
        catch
        {
            brush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
        }

        brush.Freeze();
        BrushCache[key] = brush;
        return brush;
    }

    /// <summary>按颜色值在选项里找（找不到返回 null，界面自己兜底）。</summary>
    public static ColorChoice? Find(IEnumerable<ColorChoice> options, string? hex)
    {
        var key = NormalizeHex(hex);
        return options.FirstOrDefault(o => string.Equals(o.Hex, key, StringComparison.OrdinalIgnoreCase));
    }

    public bool Equals(ColorChoice? other)
        => other is not null && string.Equals(Hex, other.Hex, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as ColorChoice);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Hex);

    public override string ToString() => Name;
}

/// <summary>数字文本解析（范围输入 / 定义域输入共用）：空串 ≠ 0，必须由调用方区分。</summary>
internal static class NumberText
{
    /// <summary>解析一个有限数字；空串 / 非法 / ∞ 都返回 false。</summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        const NumberStyles styles = NumberStyles.Float | NumberStyles.AllowThousands;
        if (double.TryParse(text, styles, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) return true;
        if (double.TryParse(text, styles, CultureInfo.CurrentCulture, out value) && double.IsFinite(value)) return true;
        return false;
    }

    /// <summary>解析「可留空」的数字：空串 = null 且算成功。</summary>
    public static bool TryParseOptional(string? text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TryParse(text, out var parsed)) return false;
        value = parsed;
        return true;
    }
}

/// <summary>
/// 函数列表里一行（界面对象）：包着配置里的 <see cref="FunctionPlotItem"/>，负责
/// 表达式实时校验、颜色选择、定义域文本与「删除」命令，并把变化冒泡给
/// <see cref="FunctionPlotViewModel"/> 触发重绘。
/// </summary>
public sealed class FunctionItemViewModel : ObservableObject
{
    private static readonly string[] ColorNames = { "蓝", "橙", "绿", "红", "紫", "黄", "青", "粉" };

    private readonly IReadOnlyList<ColorChoice> _colorOptions;
    private readonly Action<FunctionItemViewModel> _removeRequested;

    private string _domainMinText;
    private string _domainMaxText;
    private string _errorText = "";
    private string _domainErrorText = "";
    private MathExpression? _compiled;
    private ColorChoice? _customColor;
    private string? _customColorHex;

    public FunctionItemViewModel(
        FunctionPlotItem model,
        IReadOnlyList<ColorChoice> colorOptions,
        Action<FunctionItemViewModel> removeRequested)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _colorOptions = colorOptions;
        _removeRequested = removeRequested;
        RemoveCommand = new RelayCommand(_ => _removeRequested(this));

        _domainMinText = FormatOptional(model.DomainMin);
        _domainMaxText = FormatOptional(model.DomainMax);

        ValidateExpression();
        ValidateDomain();
    }

    /// <summary>对应的配置项（保存 / JSON 用）。</summary>
    public FunctionPlotItem Model { get; }

    /// <summary>可选的 8 个曲线颜色（ComboBox 数据源）。</summary>
    public IReadOnlyList<ColorChoice> ColorOptions => _colorOptions;

    /// <summary>删除这一条函数。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>行内任何会影响绘图的变化（表达式 / 颜色 / 显示开关 / 定义域）都触发它。</summary>
    public event Action? Changed;

    /// <summary>编译好的表达式；表达式非法时为 null（这一条不画，但其它函数不受影响）。</summary>
    public MathExpression? Compiled => _compiled;

    /// <summary>能不能画（表达式合法）。</summary>
    public bool IsDrawable => _compiled is not null;

    /// <summary>界面上显示的名字。</summary>
    public string DisplayName => Model.DisplayName;

    /// <summary>原始表达式（双向绑定）。</summary>
    public string Expression
    {
        get => Model.Expression;
        set
        {
            var text = value ?? "";
            if (Model.Expression == text) return;
            Model.Expression = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            ValidateExpression();
            Changed?.Invoke();
        }
    }

    /// <summary>是否显示这条曲线。</summary>
    public bool IsVisible
    {
        get => Model.IsVisible;
        set
        {
            if (Model.IsVisible == value) return;
            Model.IsVisible = value;
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }

    /// <summary>当前颜色值（配置里存的值，可能不在调色板内）。</summary>
    public string ColorHex => Model.ColorHex;

    /// <summary>曲线画刷（已冻结缓存）。</summary>
    public Brush StrokeBrush => ColorChoice.CreateBrush(Model.ColorHex);

    /// <summary>下拉框选中的颜色。</summary>
    public ColorChoice SelectedColor
    {
        get => ResolveColorChoice();
        set
        {
            if (value is null) return;
            if (string.Equals(Model.ColorHex, value.Hex, StringComparison.OrdinalIgnoreCase)) return;
            Model.ColorHex = value.Hex;
            _customColor = null;
            _customColorHex = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ColorHex));
            OnPropertyChanged(nameof(StrokeBrush));
            Changed?.Invoke();
        }
    }

    /// <summary>定义域左端文本（空串 = 不限制）。</summary>
    public string DomainMinText
    {
        get => _domainMinText;
        set
        {
            var text = (value ?? "").Trim();
            if (_domainMinText == text) return;
            _domainMinText = text;
            OnPropertyChanged();
            ValidateDomain();
        }
    }

    /// <summary>定义域右端文本（空串 = 不限制）。</summary>
    public string DomainMaxText
    {
        get => _domainMaxText;
        set
        {
            var text = (value ?? "").Trim();
            if (_domainMaxText == text) return;
            _domainMaxText = text;
            OnPropertyChanged();
            ValidateDomain();
        }
    }

    /// <summary>表达式是否有错（这一行会标红）。</summary>
    public bool HasError => _errorText.Length > 0;

    /// <summary>表达式错误提示（中文）。</summary>
    public string ErrorText => _errorText;

    /// <summary>定义域文本是否有错。</summary>
    public bool HasDomainError => _domainErrorText.Length > 0;

    /// <summary>定义域错误提示（中文）。</summary>
    public string DomainErrorText => _domainErrorText;

    /// <summary>求值用：x 是否落在定义域内。</summary>
    public bool IsInDomain(double x) => Model.IsInDomain(x);

    /// <summary>求值用：这条函数在 x 处的值（表达式非法返回 NaN）。</summary>
    public double Evaluate(double x, AngleUnitKind unit) => _compiled?.Evaluate(x, unit) ?? double.NaN;

    /// <summary>画图用：定义域与当前可视范围的交集；null = 这条函数在当前范围内没有可画的点。</summary>
    public (double Min, double Max)? ResolveDomain(double viewXMin, double viewXMax)
        => Model.EffectiveDomain(viewXMin, viewXMax);

    // ---------------------------------------------------------------- 私有

    private void ValidateExpression()
    {
        if (Model.TryCompile(out var compiled, out var error))
        {
            _compiled = compiled;
            _errorText = "";
        }
        else
        {
            _compiled = null;
            _errorText = string.IsNullOrWhiteSpace(Model.Expression) ? "表达式不能为空" : error ?? "表达式写法有误";
        }

        OnPropertyChanged(nameof(Compiled));
        OnPropertyChanged(nameof(IsDrawable));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
    }

    private void ValidateDomain()
    {
        var minOk = NumberText.TryParseOptional(_domainMinText, out var min);
        var maxOk = NumberText.TryParseOptional(_domainMaxText, out var max);

        if (!minOk || !maxOk)
        {
            _domainErrorText = "定义域要填数字（留空表示用当前可视范围）";
        }
        else
        {
            _domainErrorText = "";
            Model.DomainMin = min;
            Model.DomainMax = max;
            Changed?.Invoke();
        }

        OnPropertyChanged(nameof(HasDomainError));
        OnPropertyChanged(nameof(DomainErrorText));
    }

    private ColorChoice ResolveColorChoice()
    {
        var found = ColorChoice.Find(_colorOptions, Model.ColorHex);
        if (found is not null) return found;

        // 手改配置文件写了个调色板外的颜色：造一个临时选项显示，别把选中项丢掉
        if (_customColor is null || !string.Equals(_customColorHex, Model.ColorHex, StringComparison.OrdinalIgnoreCase))
        {
            _customColor = new ColorChoice(Model.ColorHex, "自定义");
            _customColorHex = Model.ColorHex;
        }

        return _customColor;
    }

    private static string FormatOptional(double? value)
        => value.HasValue && double.IsFinite(value.Value) ? MathFormat.Format(value.Value) : "";

    /// <summary>供 ViewModel 造「调色板外的自定义色」选项时复用。</summary>
    internal static string ColorNameAt(int index) => ColorNames[Math.Min(index, ColorNames.Length - 1)];
}

/// <summary>「光标处数值」一行：颜色 + 函数名 + 该 x 处的 y（MathFormat.Format）。</summary>
public sealed class ReadoutRow : ObservableObject
{
    private string _name = "";
    private Brush _brush = Brushes.Gray;
    private string _valueText = "—";

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public Brush Brush
    {
        get => _brush;
        set => Set(ref _brush, value);
    }

    public string ValueText
    {
        get => _valueText;
        set => Set(ref _valueText, value);
    }
}

/// <summary>点值表一行：一个 x + 所有可见函数在这点的值（拼成一行文字，避免动态列）。</summary>
public sealed class PointTableRow
{
    public PointTableRow(string xText, string valuesText)
    {
        XText = xText;
        ValuesText = valuesText;
    }

    /// <summary>已经格式化好的 x 文本。</summary>
    public string XText { get; }

    /// <summary>各函数值的拼接文本。</summary>
    public string ValuesText { get; }
}

/// <summary>一条要画的曲线：世界坐标折线（可能多段）+ 颜色 + 图例名字。</summary>
public sealed class PlotCurve
{
    public PlotCurve(string label, string colorHex, Brush stroke, IReadOnlyList<CurvePoint[]> segments)
    {
        Label = label;
        ColorHex = colorHex;
        Stroke = stroke;
        Segments = segments;
    }

    /// <summary>图例文字（表达式）。</summary>
    public string Label { get; }

    /// <summary>配置里的颜色值（画笔缓存键）。</summary>
    public string ColorHex { get; }

    /// <summary>曲线画刷（已冻结）。</summary>
    public Brush Stroke { get; }

    /// <summary>采样折线：每段 ≥2 点；段断开代表无定义 / 出界 / 渐近线。</summary>
    public IReadOnlyList<CurvePoint[]> Segments { get; }
}

/// <summary>
/// 绘图控件的「一份不可变渲染快照」：ViewModel 每次重算后造一份新的塞给
/// <c>PlotCanvas.State</c>。控件只读它、不做任何数学计算（除了世界→屏幕换算），
/// 这样鼠标拖动与后台重算不会读到半新半旧的数据。
/// </summary>
public sealed class PlotRenderState
{
    /// <summary>生成快照时的视口（控件渲染时按自己的实际尺寸再 Resize 一次）。</summary>
    public PlotViewport Viewport { get; init; } = new();

    /// <summary>所有可见且合法的曲线。</summary>
    public IReadOnlyList<PlotCurve> Curves { get; init; } = Array.Empty<PlotCurve>();

    /// <summary>隐藏坐标轴与刻度。</summary>
    public bool HideAxes { get; init; }

    /// <summary>隐藏网格。</summary>
    public bool HideGrid { get; init; }

    /// <summary>显示图例。</summary>
    public bool ShowLegend { get; init; } = true;

    /// <summary>显示十字准星。</summary>
    public bool ShowCrosshair { get; init; } = true;

    /// <summary>一条曲线都没有时图上显示的中文提示（空字符串 / null = 不显示）。</summary>
    public string? EmptyHint { get; init; }
}

/// <summary>
/// 「函数绘图」的 ViewModel：管理函数列表、视口（平移 / 缩放 / 复位）、截图快照，
/// 以及三种自动求值（光标读数 / 输入 x 求值 / 自动取 10 个点）。
///
/// <para>表达式解析与采样全部交给 Core 的共享引擎；本类只负责把结果整理成界面数据。
/// 配置改动走 <see cref="FunctionPlotConfig"/> 的自动落盘，没有保存按钮。</para>
/// </summary>
public sealed class FunctionPlotViewModel : ToolViewModelBase
{
    /// <summary>图上没有函数时的提示文字（空状态）。</summary>
    public const string EmptyPlotHint = "在右侧添加函数，例如 sin(x)";

    /// <summary>「自动取点」取的 x 个数。</summary>
    public const int AutoPointCount = 10;

    private readonly PlotViewport _viewport = new();
    private readonly Dictionary<FunctionItemViewModel, ReadoutRow> _cursorRowMap = new();
    private readonly IReadOnlyList<ColorChoice> _colorOptions;

    private PlotRenderState? _plotState;
    private bool _syncingRanges;
    private bool _hasCursor;
    private double _cursorWorldX;
    private double _cursorWorldY;
    private string _cursorText = "光标：移到绘图区查看函数值";
    private string _evaluateX = "0";
    private string _evaluateError = "";
    private string _pointTableTitle = "点值表";
    private string _rangeError = "";
    private string _xMinText = "";
    private string _xMaxText = "";
    private string _yMinText = "";
    private string _yMaxText = "";

    public FunctionPlotViewModel() : base(FunctionPlotConfig.ToolId)
    {
        Cfg = LoadConfig<FunctionPlotConfig>();
        Cfg.NormalizeRanges();   // 手改过配置文件（写反 / 填了 inf）时兜底

        AngleOptions = OptionItem.From(
            (FunctionPlotConfig.RadianLabel, "弧度（数学默认）"),
            (FunctionPlotConfig.DegreeLabel, "角度（°）"));

        _colorOptions = FunctionPlotPalette.Colors
            .Select((hex, index) => new ColorChoice(hex, FunctionItemViewModel.ColorNameAt(index)))
            .ToArray();

        AddFunctionCommand = new RelayCommand(_ => AddFunction());
        ResetViewCommand = new RelayCommand(_ => ResetView());
        EvaluateCommand = new RelayCommand(_ => EvaluateAtInput());
        AutoSampleCommand = new RelayCommand(_ => AutoSamplePoints());

        Cfg.PropertyChanged += OnConfigChanged;

        LoadFunctionsFromConfig();
        SyncRangeTexts();
        ApplyConfigRanges();
        RebuildPlot();
        RefreshCursorRows();
        EvaluateAtInput();

        if (IsFirstRun) Log("首次运行：已生成默认配置（sin(x) / cos(x)），在右侧列表里直接改即可。");
    }

    // ---------------------------------------------------------------- 配置 / 选项

    /// <summary>强类型配置（自动落盘）。</summary>
    public FunctionPlotConfig Cfg { get; }

    /// <summary>角度制下拉框选项。</summary>
    public IReadOnlyList<OptionItem<string>> AngleOptions { get; }

    /// <summary>角度制下拉框当前项。</summary>
    public OptionItem<string> SelectedAngleUnit
    {
        get => OptionItem<string>.Find(AngleOptions, Cfg.UsesDegrees ? FunctionPlotConfig.DegreeLabel : FunctionPlotConfig.RadianLabel)
               ?? AngleOptions[0];
        set
        {
            if (value is null) return;
            Cfg.AngleUnit = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>手动 Y 范围是否可编辑（= 关闭了自动 Y 范围）。</summary>
    public bool IsManualYRange => !Cfg.AutoYRange;

    // ---------------------------------------------------------------- 曲线列表

    /// <summary>函数列表（每行对应配置里的一项）。</summary>
    public ObservableCollection<FunctionItemViewModel> Functions { get; } = new();

    /// <summary>添加一条函数。</summary>
    public RelayCommand AddFunctionCommand { get; }

    /// <summary>复位视图（−10..10 / −6..6，等同双击绘图区）。</summary>
    public RelayCommand ResetViewCommand { get; }

    // ---------------------------------------------------------------- 绘图状态

    /// <summary>当前渲染快照（绑给 <c>PlotCanvas.State</c>）。</summary>
    public PlotRenderState? PlotState
    {
        get => _plotState;
        private set => Set(ref _plotState, value);
    }

    /// <summary>视口摘要（工具栏上显示当前范围）。</summary>
    public string ViewportSummary
        => $"x ∈ [{MathFormat.FormatTick(_viewport.XMin)}, {MathFormat.FormatTick(_viewport.XMax)}]"
           + $" · y ∈ [{MathFormat.FormatTick(_viewport.YMin)}, {MathFormat.FormatTick(_viewport.YMax)}]";

    // ---------------------------------------------------------------- 光标 / 求值

    /// <summary>光标提示文本。</summary>
    public string CursorText
    {
        get => _cursorText;
        private set => Set(ref _cursorText, value);
    }

    /// <summary>光标处每个可见函数的 y。</summary>
    public ObservableCollection<ReadoutRow> CursorRows { get; } = new();

    /// <summary>要查值的 x（文本，避免输入过程中绑定报错）。</summary>
    public string EvaluateX
    {
        get => _evaluateX;
        set => Set(ref _evaluateX, value ?? "");
    }

    /// <summary>求值按钮。</summary>
    public RelayCommand EvaluateCommand { get; }

    /// <summary>按当前可视范围均匀取 10 个点的按钮。</summary>
    public RelayCommand AutoSampleCommand { get; }

    /// <summary>求值输入的错误提示（空串 = 正常）。</summary>
    public string EvaluateError
    {
        get => _evaluateError;
        private set
        {
            if (Set(ref _evaluateError, value)) OnPropertyChanged(nameof(HasEvaluateError));
        }
    }

    /// <summary>求值输入是否有错。</summary>
    public bool HasEvaluateError => _evaluateError.Length > 0;

    /// <summary>点值表标题。</summary>
    public string PointTableTitle
    {
        get => _pointTableTitle;
        private set => Set(ref _pointTableTitle, value);
    }

    /// <summary>点值表（每个 x 一行）。</summary>
    public ObservableCollection<PointTableRow> PointRows { get; } = new();

    // ---------------------------------------------------------------- 手动范围（文本输入）

    /// <summary>手动 X 范围左端文本。</summary>
    public string XMinText
    {
        get => _xMinText;
        set => ApplyRangeText(ref _xMinText, value, nameof(XMinText), v => Cfg.XMin = v);
    }

    /// <summary>手动 X 范围右端文本。</summary>
    public string XMaxText
    {
        get => _xMaxText;
        set => ApplyRangeText(ref _xMaxText, value, nameof(XMaxText), v => Cfg.XMax = v);
    }

    /// <summary>手动 Y 范围下端文本（自动 Y 范围打开时不可编辑）。</summary>
    public string YMinText
    {
        get => _yMinText;
        set => ApplyRangeText(ref _yMinText, value, nameof(YMinText), v => Cfg.YMin = v);
    }

    /// <summary>手动 Y 范围上端文本（自动 Y 范围打开时不可编辑）。</summary>
    public string YMaxText
    {
        get => _yMaxText;
        set => ApplyRangeText(ref _yMaxText, value, nameof(YMaxText), v => Cfg.YMax = v);
    }

    /// <summary>手动范围输入的错误提示（空串 = 正常）。</summary>
    public string RangeError
    {
        get => _rangeError;
        private set
        {
            if (Set(ref _rangeError, value)) OnPropertyChanged(nameof(HasRangeError));
        }
    }

    /// <summary>手动范围是否有错。</summary>
    public bool HasRangeError => _rangeError.Length > 0;

    // ---------------------------------------------------------------- 交互入口（由 View 调）

    /// <summary>绘图控件尺寸变化时同步给视口（内部只更新像素尺寸，不重新采样）。</summary>
    public void UpdateViewportSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1) return;
        if (Math.Abs(_viewport.Width - width) < 0.5 && Math.Abs(_viewport.Height - height) < 0.5) return;

        _viewport.Resize(width, height);
        RefreshRenderViewport();
    }

    /// <summary>鼠标拖动平移（像素位移，向右 / 向下为正）。</summary>
    public void PanByPixels(double dx, double dy)
    {
        _viewport.PanByPixels(dx, dy);
        RebuildPlot();
    }

    /// <summary>滚轮缩放（以鼠标位置为中心；<paramref name="zoomIn"/> = 滚轮向上）。</summary>
    public void ZoomAt(Point screenPoint, bool zoomIn)
    {
        _viewport.ZoomAt(screenPoint.X, screenPoint.Y, zoomIn ? 1 / 1.2 : 1.2);
        RebuildPlot();
    }

    /// <summary>双击绘图区 / 点「复位视图」：回到默认范围并同步手动范围配置。</summary>
    public void ResetView()
    {
        Cfg.XMin = PlotViewport.DefaultXMin;
        Cfg.XMax = PlotViewport.DefaultXMax;
        Cfg.YMin = PlotViewport.DefaultYMin;
        Cfg.YMax = PlotViewport.DefaultYMax;
        ApplyConfigRanges();
        RebuildPlot();
    }

    /// <summary>鼠标在图上移动（世界坐标）：刷新光标读数。</summary>
    public void OnCursorMoved(double worldX, double worldY)
    {
        _hasCursor = true;
        _cursorWorldX = worldX;
        _cursorWorldY = worldY;
        CursorText = $"光标：x = {MathFormat.Format(worldX)}，y = {MathFormat.Format(worldY)}";
        UpdateCursorValues();
    }

    /// <summary>鼠标离开绘图区。</summary>
    public void OnCursorLeft()
    {
        _hasCursor = false;
        CursorText = "光标：移到绘图区查看函数值";
        UpdateCursorValues();
    }

    // ---------------------------------------------------------------- 配置变化

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FunctionPlotConfig.Functions):
                LoadFunctionsFromConfig();
                SyncRangeTexts();
                RebuildPlot();
                RefreshCursorRows();
                break;

            case nameof(FunctionPlotConfig.XMin):
            case nameof(FunctionPlotConfig.XMax):
                SyncRangeTexts();
                ApplyConfigXRange();
                RebuildPlot();
                break;

            case nameof(FunctionPlotConfig.YMin):
            case nameof(FunctionPlotConfig.YMax):
                SyncRangeTexts();
                if (!Cfg.AutoYRange)
                {
                    ApplyConfigYRange();
                    RebuildPlot();
                }
                break;

            case nameof(FunctionPlotConfig.AutoYRange):
                OnPropertyChanged(nameof(IsManualYRange));
                if (!Cfg.AutoYRange)
                {
                    ApplyConfigYRange();
                }
                RebuildPlot();
                break;

            case nameof(FunctionPlotConfig.AngleUnit):
                OnPropertyChanged(nameof(SelectedAngleUnit));
                RebuildPlot();
                RefreshCursorRows();
                break;

            case nameof(FunctionPlotConfig.HideAxes):
            case nameof(FunctionPlotConfig.HideGrid):
            case nameof(FunctionPlotConfig.ShowCrosshair):
            case nameof(FunctionPlotConfig.ShowLegend):
            case nameof(FunctionPlotConfig.Samples):
                RebuildPlot();
                break;
        }
    }

    // ---------------------------------------------------------------- 函数列表维护

    private void LoadFunctionsFromConfig()
    {
        foreach (var item in Functions) item.Changed -= OnFunctionChanged;
        Functions.Clear();
        _cursorRowMap.Clear();
        CursorRows.Clear();

        foreach (var model in Cfg.Functions)
        {
            if (model is null) continue;   // 手改配置写成 null 项时跳过
            Functions.Add(CreateItem(model));
        }
    }

    private FunctionItemViewModel CreateItem(FunctionPlotItem model)
    {
        var item = new FunctionItemViewModel(model, _colorOptions, RemoveFunction);
        item.Changed += OnFunctionChanged;
        return item;
    }

    private void AddFunction()
    {
        var index = Cfg.Functions.Count;
        var model = new FunctionPlotItem
        {
            Expression = SuggestExpression(index),
            ColorHex = FunctionPlotPalette.At(index),
        };

        Cfg.Functions.Add(model);
        Functions.Add(CreateItem(model));
        RebuildPlot();
        RefreshCursorRows();
        Cfg.SaveNow();
    }

    private void RemoveFunction(FunctionItemViewModel item)
    {
        item.Changed -= OnFunctionChanged;
        Functions.Remove(item);
        Cfg.Functions.Remove(item.Model);
        _cursorRowMap.Remove(item);
        RebuildPlot();
        RefreshCursorRows();
        Cfg.SaveNow();
    }

    private void OnFunctionChanged()
    {
        RebuildPlot();
        RefreshCursorRows();
    }

    private static string SuggestExpression(int index)
        => index switch
        {
            0 => "sin(x)",
            1 => "cos(x)",
            2 => "x^2",
            _ => "sin(x)",
        };

    // ---------------------------------------------------------------- 重建渲染快照

    private void ApplyConfigRanges()
    {
        var (xMin, xMax) = Cfg.SafeXRange;
        _viewport.SetXRange(xMin, xMax);

        if (!Cfg.AutoYRange)
        {
            var (yMin, yMax) = Cfg.SafeYRange;
            _viewport.SetYRange(yMin, yMax);
        }
    }

    private void ApplyConfigXRange()
    {
        var (xMin, xMax) = Cfg.SafeXRange;
        _viewport.SetXRange(xMin, xMax);
    }

    private void ApplyConfigYRange()
    {
        var (yMin, yMax) = Cfg.SafeYRange;
        _viewport.SetYRange(yMin, yMax);
    }

    /// <summary>按当前视口 + 配置重新采样所有可见函数，生成新的渲染快照。</summary>
    private void RebuildPlot()
    {
        var angleUnit = Cfg.ParsedAngleUnit;
        var samples = Cfg.Samples;
        var visible = Functions.Where(f => f.IsVisible && f.IsDrawable).ToList();

        // ① 自动 Y 范围：先不限 y 采一遍，按数据算上下界（会忽略 NaN / 裁掉极点尖峰）
        if (Cfg.AutoYRange && visible.Count > 0)
        {
            var autoSegments = new List<CurvePoint[]>();
            foreach (var item in visible)
            {
                if (item.ResolveDomain(_viewport.XMin, _viewport.XMax) is not { } domain) continue;
                autoSegments.AddRange(FunctionSampler.Sample(
                    item.Compiled!, domain.Min, domain.Max, samples, angleUnit));
            }

            var (yMin, yMax) = PlotViewport.ComputeAutoYRange(autoSegments);
            _viewport.SetYRange(yMin, yMax);
        }

        // ② 正式采样：带 y 裁剪，曲线在边界处补齐交点再断开，不会画出一根假竖线
        var curves = new List<PlotCurve>(visible.Count);
        foreach (var item in visible)
        {
            if (item.ResolveDomain(_viewport.XMin, _viewport.XMax) is not { } domain) continue;

            var segments = FunctionSampler.Sample(
                item.Compiled!, domain.Min, domain.Max, samples, angleUnit,
                _viewport.YMin, _viewport.YMax);

            if (segments.Count == 0) continue;
            curves.Add(new PlotCurve(item.DisplayName, item.Model.ColorHex, item.StrokeBrush, segments));
        }

        PlotState = BuildRenderState(curves);
        OnPropertyChanged(nameof(ViewportSummary));
    }

    private PlotRenderState BuildRenderState(IReadOnlyList<PlotCurve> curves)
        => new()
        {
            Viewport = _viewport.Clone(),
            Curves = curves,
            HideAxes = Cfg.HideAxes,
            HideGrid = Cfg.HideGrid,
            ShowLegend = Cfg.ShowLegend,
            ShowCrosshair = Cfg.ShowCrosshair,
            EmptyHint = curves.Count == 0 ? EmptyPlotHint : null,
        };

    /// <summary>只换像素尺寸（窗口 / 分隔条拖动）：复用已有曲线，不重新采样。</summary>
    private void RefreshRenderViewport()
    {
        if (PlotState is not { } current)
        {
            RebuildPlot();
            return;
        }

        PlotState = new PlotRenderState
        {
            Viewport = _viewport.Clone(),
            Curves = current.Curves,
            HideAxes = current.HideAxes,
            HideGrid = current.HideGrid,
            ShowLegend = current.ShowLegend,
            ShowCrosshair = current.ShowCrosshair,
            EmptyHint = current.EmptyHint,
        };
    }

    // ---------------------------------------------------------------- 光标读数

    private void RefreshCursorRows()
    {
        CursorRows.Clear();
        _cursorRowMap.Clear();

        foreach (var item in Functions.Where(f => f.IsVisible))
        {
            var row = new ReadoutRow { Name = item.DisplayName, Brush = item.StrokeBrush };
            _cursorRowMap[item] = row;
            CursorRows.Add(row);
        }

        UpdateCursorValues();
    }

    private void UpdateCursorValues()
    {
        var angleUnit = Cfg.ParsedAngleUnit;
        foreach (var item in Functions)
        {
            if (!_cursorRowMap.TryGetValue(item, out var row)) continue;

            row.Name = item.DisplayName;
            row.Brush = item.StrokeBrush;

            if (!item.IsDrawable)
            {
                row.ValueText = "表达式有误";
                continue;
            }

            if (!_hasCursor)
            {
                row.ValueText = "—";
                continue;
            }

            if (!item.IsInDomain(_cursorWorldX))
            {
                row.ValueText = "不在定义域";
                continue;
            }

            row.ValueText = MathFormat.Format(item.Evaluate(_cursorWorldX, angleUnit));
        }
    }

    // ---------------------------------------------------------------- 求值 / 自动取点

    private void EvaluateAtInput()
    {
        if (!NumberText.TryParse(EvaluateX, out var x))
        {
            EvaluateError = "请输入一个数字 x（例如 0、1.5、-3）";
            return;
        }

        EvaluateError = "";
        FillPointTable(new[] { x }, $"x = {MathFormat.FormatTick(x)} 处各函数的值");
    }

    private void AutoSamplePoints()
    {
        var xMin = _viewport.XMin;
        var xMax = _viewport.XMax;
        if (!double.IsFinite(xMin) || !double.IsFinite(xMax)) return;

        var xs = new double[AutoPointCount];
        for (var i = 0; i < AutoPointCount; i++)
        {
            // 最后一个点精确落在右边界，避免浮点误差差一丢丢
            xs[i] = i == AutoPointCount - 1 ? xMax : xMin + (xMax - xMin) * i / (AutoPointCount - 1);
        }

        FillPointTable(xs, $"当前可视范围均匀取 {AutoPointCount} 个点");
    }

    private void FillPointTable(IReadOnlyList<double> xs, string title)
    {
        PointTableTitle = title;
        PointRows.Clear();

        var visible = Functions.Where(f => f.IsVisible && f.IsDrawable).ToList();
        if (visible.Count == 0)
        {
            PointRows.Add(new PointTableRow("（没有可见函数）", "在右侧添加函数后，这里会列出各点的值"));
            return;
        }

        var angleUnit = Cfg.ParsedAngleUnit;
        foreach (var x in xs)
        {
            var parts = new List<string>(visible.Count);
            foreach (var item in visible)
            {
                var valueText = item.IsInDomain(x)
                    ? MathFormat.Format(item.Evaluate(x, angleUnit))
                    : "不在定义域";
                parts.Add($"{item.DisplayName} = {valueText}");
            }

            PointRows.Add(new PointTableRow(MathFormat.FormatTick(x), string.Join("；", parts)));
        }
    }

    // ---------------------------------------------------------------- 手动范围文本

    private void ApplyRangeText(ref string field, string? value, string propertyName, Action<double> apply)
    {
        var text = (value ?? "").Trim();

        if (_syncingRanges)
        {
            if (field == text) return;
            field = text;
            OnPropertyChanged(propertyName);
            return;
        }

        if (field == text) return;
        field = text;
        OnPropertyChanged(propertyName);

        if (NumberText.TryParse(text, out var number))
        {
            RangeError = "";
            apply(number);
        }
        else
        {
            RangeError = "范围要填一个数字（例如 -10 或 2.5）";
        }
    }

    private void SyncRangeTexts()
    {
        _syncingRanges = true;
        try
        {
            XMinText = MathFormat.Format(Cfg.XMin);
            XMaxText = MathFormat.Format(Cfg.XMax);
            YMinText = MathFormat.Format(Cfg.YMin);
            YMaxText = MathFormat.Format(Cfg.YMax);
        }
        finally
        {
            _syncingRanges = false;
        }
    }
}
