using System.Reflection;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Expressions;
using YuLiangToolkit.Tools.FunctionPlot;
using YuLiangToolkit.Tools.FunctionPlot.Core;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「函数绘图」自检：工具元数据 / 视口换算（世界↔屏幕 / 边距 / 缩放 / 平移 / 复位）/
/// 好看刻度（1/2/5×10ⁿ）/ 自动 Y 范围 / 配置默认值与夹取 / 每行函数的定义域与表达式校验 /
/// 与共享 <see cref="FunctionSampler"/> 的采样集成 / 共享格式化输出。
///
/// <para><b>全部是纯逻辑</b>：不创建窗口、不 new 控件、不碰 Data/Config、不渲染，
/// 所以可以在控制台自检进程里直接跑。</para>
/// </summary>
internal static class FunctionPlotChecks
{
    /// <summary>自检框架传进来的是 <c>Action&lt;string,bool,string&gt;</c>（detail 必填），这里包一层带默认值的委托。</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    /// <summary>浮点比较容差。</summary>
    private const double Tolerance = 1e-9;

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        _ = report;   // 本组不使用汇总回调
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckMetadata(fn);
        CheckViewport(fn);
        CheckAutoYRange(fn);
        CheckAxisTicks(fn);
        CheckConfig(fn);
        CheckFunctionItems(fn);
        CheckSampling(fn);
        CheckFormatting(fn);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(CheckFn check)
    {
        var tool = new FunctionPlotTool();

        check("[函数绘图] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "function-plot" && tool.Name == "函数绘图"
            && tool.Category == "数学" && tool.Order == 520,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[函数绘图] Emoji 图标为 📈",
            tool.EmojiIcon == "📈", tool.EmojiIcon);

        check("[函数绘图] 标签覆盖 数学 / 函数 / 绘图 / 图像 / 坐标系",
            tool.Tags.Contains("数学") && tool.Tags.Contains("函数") && tool.Tags.Contains("绘图")
            && tool.Tags.Contains("图像") && tool.Tags.Contains("坐标系"),
            string.Join(" / ", tool.Tags));

        check("[函数绘图] 描述提到多函数 / 定义域 / 自动求值 / 自动取点",
            tool.Description.Contains("多个函数") && tool.Description.Contains("定义域")
            && tool.Description.Contains("求值") && tool.Description.Contains("10 个点"),
            tool.Description);

        check("[函数绘图] 工具 Id 与 FunctionPlotConfig.ToolId 一致（Data / Config 目录名）",
            tool.Id == FunctionPlotConfig.ToolId, FunctionPlotConfig.ToolId);

        check("[函数绘图] 实现了 ITool 接口",
            tool is YuLiangToolkit.Core.Tools.ITool);
    }

    // ---------------------------------------------------------------- 视口换算

    private static void CheckViewport(CheckFn check)
    {
        var viewport = new PlotViewport();
        check("[函数绘图] 视口默认范围是 −10..10 / −6..6",
            viewport.XMin == -10 && viewport.XMax == 10 && viewport.YMin == -6 && viewport.YMax == 6,
            $"[{viewport.XMin}, {viewport.XMax}] / [{viewport.YMin}, {viewport.YMax}]");

        viewport.Resize(800, 600);
        var rect = viewport.PlotRect;
        check("[函数绘图] Resize 后绘图区扣掉四周刻度边距",
            Near(rect.Left, viewport.MarginLeft) && Near(rect.Top, viewport.MarginTop)
            && Near(rect.Width, 800 - viewport.MarginLeft - viewport.MarginRight)
            && Near(rect.Height, 600 - viewport.MarginTop - viewport.MarginBottom)
            && viewport.HasSize,
            $"rect={rect}");

        var origin = viewport.WorldToScreen(0, 0);
        check("[函数绘图] 世界原点落在绘图区正中（含边距）",
            Near(origin.X, rect.Left + rect.Width / 2) && Near(origin.Y, rect.Top + rect.Height / 2),
            $"({origin.X:0.###}, {origin.Y:0.###})");

        var roundTripOk = true;
        foreach (var (x, y) in new[] { (0.0, 0.0), (3.5, -2.0), (-7.0, 5.0) })
        {
            var screen = viewport.WorldToScreen(x, y);
            var (backX, backY) = viewport.ScreenToWorld(screen.X, screen.Y);
            roundTripOk &= Near(backX, x) && Near(backY, y);
        }

        check("[函数绘图] 世界 → 屏幕 → 世界往返（含边距）", roundTripOk);

        var screenX = viewport.WorldToScreenX(2.5);
        var screenY = viewport.WorldToScreenY(-1.5);
        check("[函数绘图] ScreenToWorldX / ScreenToWorldY 与整体换算一致",
            Near(viewport.ScreenToWorldX(screenX), 2.5) && Near(viewport.ScreenToWorldY(screenY), -1.5),
            $"{screenX:0.##} / {screenY:0.##}");

        // 缩放：以光标处为不动点，范围内所有边界按 factor 收缩
        var zoomViewport = new PlotViewport();
        zoomViewport.Resize(800, 600);
        var anchorBefore = zoomViewport.ScreenToWorld(300, 200);
        zoomViewport.ZoomAt(300, 200, 0.5);
        var anchorAfter = zoomViewport.ScreenToWorld(300, 200);
        check("[函数绘图] ZoomAt 光标处的世界坐标保持不动",
            Near(anchorBefore.X, anchorAfter.X, 1e-7) && Near(anchorBefore.Y, anchorAfter.Y, 1e-7),
            $"{anchorBefore} → {anchorAfter}");

        check("[函数绘图] ZoomAt factor<1 放大（X / Y 范围按同一比例缩小）",
            Near(zoomViewport.XMax - zoomViewport.XMin, 10) && Near(zoomViewport.YMax - zoomViewport.YMin, 6),
            $"{zoomViewport.XMax - zoomViewport.XMin} / {zoomViewport.YMax - zoomViewport.YMin}");

        // 连续放大：范围最终被夹到最小跨度，不会变成 0 / 负数
        for (var i = 0; i < 80; i++) zoomViewport.ZoomAt(300, 200, 0.5);
        check("[函数绘图] 连续放大时范围夹到 MinimumSpan（不出现 0 跨度）",
            zoomViewport.XMax - zoomViewport.XMin >= PlotViewport.MinimumSpan * 0.999
            && zoomViewport.YMax - zoomViewport.YMin >= PlotViewport.MinimumSpan * 0.999,
            $"{zoomViewport.XMax - zoomViewport.XMin:E3} / {zoomViewport.YMax - zoomViewport.YMin:E3}");

        // 平移：拖动 100px 后同一世界点在屏幕上右移 100px
        var panViewport = new PlotViewport();
        panViewport.Resize(800, 600);
        var beforePan = panViewport.WorldToScreen(0, 0);
        panViewport.PanByPixels(100, 50);
        var afterPan = panViewport.WorldToScreen(0, 0);
        check("[函数绘图] PanByPixels：内容随鼠标拖动（屏幕位移 = 像素位移）",
            Near(afterPan.X - beforePan.X, 100, 1e-7) && Near(afterPan.Y - beforePan.Y, 50, 1e-7),
            $"dx={afterPan.X - beforePan.X:0.###} dy={afterPan.Y - beforePan.Y:0.###}");

        check("[函数绘图] PanByPixels：像素 → 世界换算正确（X 窗口反向移动、Y 窗口同向）",
            Near(panViewport.XMin, -10 - 100 / panViewport.PixelsPerUnitX)
            && Near(panViewport.YMax, 6 + 50 / panViewport.PixelsPerUnitY),
            $"XMin={panViewport.XMin:0.####} YMax={panViewport.YMax:0.####}");

        // 尺寸变化后换算按新尺寸走
        var resizeViewport = new PlotViewport();
        resizeViewport.Resize(400, 300);
        var resizedOrigin = resizeViewport.WorldToScreen(0, 0);
        var resizedRect = resizeViewport.PlotRect;
        check("[函数绘图] Resize 后世界 → 屏幕换算立即按新尺寸计算",
            Near(resizedOrigin.X, resizedRect.Left + resizedRect.Width / 2)
            && Near(resizedOrigin.Y, resizedRect.Top + resizedRect.Height / 2)
            && resizedRect.Width < rect.Width,
            $"({resizedOrigin.X:0.##}, {resizedOrigin.Y:0.##})");

        // 范围写反自动交换、跨度太小自动撑开
        var swapViewport = new PlotViewport();
        swapViewport.SetBounds(10, -10, 6, -6);
        check("[函数绘图] SetBounds 上下界写反时自动交换",
            swapViewport.XMin == -10 && swapViewport.XMax == 10 && swapViewport.YMin == -6 && swapViewport.YMax == 6,
            $"[{swapViewport.XMin}, {swapViewport.XMax}] / [{swapViewport.YMin}, {swapViewport.YMax}]");

        var degenerate = new PlotViewport();
        degenerate.SetBounds(5, 5, 2, 2);
        check("[函数绘图] SetBounds 跨度退化成一点时撑到最小跨度",
            degenerate.XMax - degenerate.XMin >= PlotViewport.MinimumSpan * 0.999
            && degenerate.YMax - degenerate.YMin >= PlotViewport.MinimumSpan * 0.999,
            $"{degenerate.XMax - degenerate.XMin:E3} / {degenerate.YMax - degenerate.YMin:E3}");

        var fallback = PlotViewport.NormalizeRange(double.NaN, 6, -10, 10);
        check("[函数绘图] NormalizeRange 非有限值时回退到给定默认范围",
            fallback.Min == -10 && fallback.Max == 10, $"{fallback}");

        var clone = new PlotViewport();
        var copy = clone.Clone();
        copy.SetXRange(0, 1);
        check("[函数绘图] Clone 是独立副本（改副本不影响原视口）",
            clone.XMin == -10 && copy.XMin == 0, $"copy.XMin={copy.XMin}");
    }

    // ---------------------------------------------------------------- 自动 Y 范围

    private static void CheckAutoYRange(CheckFn check)
    {
        var empty = PlotViewport.ComputeAutoYRange(Array.Empty<CurvePoint[]>());
        check("[函数绘图] 自动 Y 范围：没有数据时用回退范围（−6..6）",
            empty.Min == -6 && empty.Max == 6, $"{empty}");

        var reversed = PlotViewport.ComputeAutoYRange(Array.Empty<CurvePoint[]>(), 6, -6);
        check("[函数绘图] 自动 Y 范围：回退范围写反时自动交换",
            reversed.Min == -6 && reversed.Max == 6, $"{reversed}");

        var skipInvalid = PlotViewport.ComputeAutoYRange(new[]
        {
            new[] { new CurvePoint(0, double.NaN), new CurvePoint(1, 1), new CurvePoint(2, 3) },
            new[] { new CurvePoint(3, double.PositiveInfinity), new CurvePoint(4, double.NegativeInfinity) },
        });
        check("[函数绘图] 自动 Y 范围：跳过 NaN / ∞，只按有效值算",
            double.IsFinite(skipInvalid.Min) && double.IsFinite(skipInvalid.Max)
            && skipInvalid.Min < 1 && skipInvalid.Max > 3,
            $"{skipInvalid}");

        var allInvalid = PlotViewport.ComputeAutoYRange(new[]
        {
            new[] { new CurvePoint(0, double.NaN), new CurvePoint(1, double.PositiveInfinity) },
        });
        check("[函数绘图] 自动 Y 范围：全是无效值时仍回退到默认范围",
            allInvalid.Min == -6 && allInvalid.Max == 6, $"{allInvalid}");

        var constant = PlotViewport.ComputeAutoYRange(new[]
        {
            new[] { new CurvePoint(0, 5), new CurvePoint(1, 5), new CurvePoint(2, 5) },
        }, minSpan: 2);
        check("[函数绘图] 自动 Y 范围：常数函数也保证最小跨度（不会缩成一个点）",
            constant.Max - constant.Min >= 2 && constant.Min < 5 && constant.Max > 5,
            $"{constant}（跨度 {constant.Max - constant.Min:0.###}）");

        var padded = PlotViewport.ComputeAutoYRange(new[]
        {
            new[] { new CurvePoint(0, 0), new CurvePoint(1, 10) },
        });
        check("[函数绘图] 自动 Y 范围：上下各留 padding，不贴边",
            padded.Min < 0 && padded.Min > -2 && padded.Max > 10 && padded.Max < 12,
            $"{padded}");

        // tan 那种「个别点飞上天」：分位裁剪后不应该被一个尖峰撑爆
        var spike = new List<CurvePoint[]>();
        var spikeSegment = new CurvePoint[201];
        for (var i = 0; i < 200; i++)
        {
            var x = -10 + 20.0 * i / 199;
            spikeSegment[i] = new CurvePoint(x, Math.Sin(x));
        }

        spikeSegment[200] = new CurvePoint(0.5, 1e12);
        spike.Add(spikeSegment);
        var robust = PlotViewport.ComputeAutoYRange(spike);
        check("[函数绘图] 自动 Y 范围：裁掉尖峰（tan 极点不会把 Y 撑到 1e12）",
            robust.Max < 100 && robust.Min > -100 && robust.Max > 1 && robust.Min < -1,
            $"{robust}");

        var wide = PlotViewport.ComputeAutoYRange(new[]
        {
            new[] { new CurvePoint(0, -1000), new CurvePoint(1, 1000) },
        }, minSpan: 1e-6);
        check("[函数绘图] 自动 Y 范围：大跨度数据保持有限且有序",
            double.IsFinite(wide.Min) && double.IsFinite(wide.Max) && wide.Min < wide.Max,
            $"{wide}");
    }

    // ---------------------------------------------------------------- 好看刻度

    private static void CheckAxisTicks(CheckFn check)
    {
        check("[函数绘图] NiceStep 把原始步长取成 1/2/5×10ⁿ",
            Near(AxisTicks.NiceStep(0.003), 0.005) && Near(AxisTicks.NiceStep(0.7), 1)
            && Near(AxisTicks.NiceStep(1.3), 2) && Near(AxisTicks.NiceStep(3), 5)
            && Near(AxisTicks.NiceStep(7), 10) && Near(AxisTicks.NiceStep(0.011), 0.02),
            $"{AxisTicks.NiceStep(0.003)} / {AxisTicks.NiceStep(0.7)} / {AxisTicks.NiceStep(1.3)}");

        var normal = AxisTicks.Generate(-10, 10);
        check("[函数绘图] Generate(−10, 10)：刻度数量在 4~12 之间",
            normal.Count >= AxisTicks.MinGeneratedTicks && normal.Count <= AxisTicks.MaxGeneratedTicks,
            $"{normal.Count} 个（step={normal.Step}）");

        check("[函数绘图] Generate 的步长属于 1/2/5×10ⁿ 序列",
            IsNiceStep(normal.Step), $"step={normal.Step}");

        var equidistant = true;
        for (var i = 1; i < normal.Count; i++)
        {
            equidistant &= Near(normal.Values[i] - normal.Values[i - 1], normal.Step, Math.Max(1e-9, normal.Step * 1e-9));
        }

        check("[函数绘图] 刻度等距且落在范围内（含 0 刻度）",
            equidistant
            && normal.Values.All(v => v >= -10 - 1e-9 && v <= 10 + 1e-9)
            && normal.Values.Any(v => Math.Abs(v) < 1e-12),
            string.Join(", ", normal.Values.Select(v => v.ToString("0.####"))));

        check("[函数绘图] 上下界写反时生成的刻度与正序一致",
            AxisTicks.Generate(10, -10).Values.SequenceEqual(normal.Values));

        check("[函数绘图] 非有限范围返回空刻度集（不崩、不画 NaN 网格）",
            AxisTicks.Generate(double.NaN, 10).IsEmpty && AxisTicks.Generate(0, double.PositiveInfinity).IsEmpty);

        var tiny = AxisTicks.Generate(0.001, 0.005);
        check("[函数绘图] 小范围（0.001~0.005）也生成 4~12 个落界内的刻度",
            tiny.Count is >= AxisTicks.MinGeneratedTicks and <= AxisTicks.MaxGeneratedTicks
            && tiny.Values.All(v => v >= 0.001 - 1e-12 && v <= 0.005 + 1e-12),
            string.Join(", ", tiny.Values));

        var point = AxisTicks.Generate(5, 5);
        check("[函数绘图] 范围退化成单点也能生成刻度（先撑成 ±1）",
            point.Count >= AxisTicks.MinGeneratedTicks,
            $"{point.Count} 个（step={point.Step}）");

        check("[函数绘图] 刻度标签统一走 MathFormat.FormatTick",
            AxisTicks.FormatLabel(0.5) == MathFormat.FormatTick(0.5)
            && AxisTicks.FormatLabel(-2.25) == MathFormat.FormatTick(-2.25)
            && AxisTicks.FormatLabel(0) == "0",
            AxisTicks.FormatLabel(-2.25));
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var cfg = new FunctionPlotConfig();

        check("[函数绘图] 配置默认值：坐标轴 / 网格显示、准星与图例打开、自动 Y 范围打开",
            !cfg.HideAxes && !cfg.HideGrid && cfg.ShowCrosshair && cfg.ShowLegend && cfg.AutoYRange,
            $"HideAxes={cfg.HideAxes} HideGrid={cfg.HideGrid} Crosshair={cfg.ShowCrosshair} Legend={cfg.ShowLegend} AutoY={cfg.AutoYRange}");

        check("[函数绘图] 配置默认值：角度制=弧度、采样点数=1200、默认范围 −10..10 / −6..6",
            cfg.AngleUnit == FunctionPlotConfig.RadianLabel
            && cfg.Samples == FunctionPlotConfig.DefaultSamples
            && cfg.XMin == -10 && cfg.XMax == 10 && cfg.YMin == -6 && cfg.YMax == 6,
            $"{cfg.AngleUnit} / {cfg.Samples} / [{cfg.XMin}, {cfg.XMax}] / [{cfg.YMin}, {cfg.YMax}]");

        check("[函数绘图] 默认函数列表非空，且颜色都在 8 色调色板里",
            cfg.Functions.Count >= 1
            && cfg.Functions.All(f => FunctionPlotPalette.Colors.Contains(f.ColorHex)),
            string.Join(" / ", cfg.Functions.Select(f => f.Expression + " " + f.ColorHex)));

        check("[函数绘图] 调色板正好 8 个颜色且互不重复",
            FunctionPlotPalette.Colors.Count == 8
            && FunctionPlotPalette.Colors.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 8,
            string.Join(", ", FunctionPlotPalette.Colors));

        cfg.Samples = 1;
        var tooSmall = cfg.Samples;
        cfg.Samples = 999999;
        var tooBig = cfg.Samples;
        check("[函数绘图] 采样点数夹取到 200 ~ 4000",
            tooSmall == FunctionPlotConfig.MinSamples && tooBig == FunctionPlotConfig.MaxSamples,
            $"{tooSmall} / {tooBig}");

        cfg.XMin = 10;
        cfg.XMax = -10;
        var safeX = cfg.SafeXRange;
        check("[函数绘图] 手动 X / Y 范围写反时 SafeXRange / SafeYRange 自动交换",
            safeX.Min == -10 && safeX.Max == 10 && cfg.SafeYRange.Min == -6 && cfg.SafeYRange.Max == 6,
            $"{safeX}");

        cfg.NormalizeRanges();
        check("[函数绘图] NormalizeRanges 把写反的范围就地修好",
            cfg.XMin == -10 && cfg.XMax == 10, $"[{cfg.XMin}, {cfg.XMax}]");

        cfg.AngleUnit = "角度";
        var degrees = cfg.ParsedAngleUnit;
        cfg.AngleUnit = "看不懂的值";
        var radians = cfg.ParsedAngleUnit;
        check("[函数绘图] 角度制值归一化：只认「角度」，其余按弧度",
            degrees == YuLiangToolkit.Core.Expressions.AngleUnit.Degrees
            && radians == YuLiangToolkit.Core.Expressions.AngleUnit.Radians
            && cfg.AngleUnit == FunctionPlotConfig.RadianLabel,
            $"{degrees} / {radians} / {cfg.AngleUnit}");

        var usesDegreesProperty = typeof(FunctionPlotConfig).GetProperty(nameof(FunctionPlotConfig.UsesDegrees));
        var hasJsonIgnore = usesDegreesProperty?.GetCustomAttribute<JsonIgnoreAttribute>() is not null;
        check("[函数绘图] 带 setter 的计算属性 UsesDegrees 标了 [JsonIgnore]（不会写进配置）",
            hasJsonIgnore);

        var functionsProperty = typeof(FunctionPlotConfig).GetProperty(nameof(FunctionPlotConfig.Functions));
        check("[函数绘图] 函数列表是公开可读写的 List<FunctionPlotItem>（可被 JSON 反序列化）",
            functionsProperty?.PropertyType == typeof(List<FunctionPlotItem>));
    }

    // ---------------------------------------------------------------- 每条函数（校验 / 定义域）

    private static void CheckFunctionItems(CheckFn check)
    {
        var empty = new FunctionPlotItem { Expression = "" };
        var emptyOk = empty.TryCompile(out var emptyExpr, out var emptyError);
        check("[函数绘图] 空表达式校验失败并给出中文提示",
            !emptyOk && emptyExpr is null && !string.IsNullOrWhiteSpace(emptyError) && emptyError.Contains("空"),
            emptyError ?? "(null)");

        var invalid = new FunctionPlotItem { Expression = "sin(" };
        var invalidOk = invalid.TryCompile(out _, out var invalidError);
        check("[函数绘图] 非法表达式（sin(）校验失败且错误提示非空",
            !invalidOk && !string.IsNullOrWhiteSpace(invalidError) && invalidError.Length > 0,
            invalidError ?? "(null)");

        var valid = new FunctionPlotItem { Expression = "sin(x)" };
        var validOk = valid.TryCompile(out var validExpr, out var validError);
        check("[函数绘图] 合法表达式 sin(x) 编译成功（用到变量 x）",
            validOk && validExpr is not null && validExpr.UsesVariable && validError is null,
            validError ?? "ok");

        var power = new FunctionPlotItem { Expression = "x^2" };
        check("[函数绘图] 合法表达式 x^2 在 x=3 处求值为 9",
            power.TryCompile(out var powerExpr, out _) && Near(powerExpr!.Evaluate(3), 9),
            power.GetExpressionError());

        check("[函数绘图] 定义域两端留空 = 使用当前可视范围",
            new FunctionPlotItem().EffectiveDomain(-10, 10) is { Min: -10, Max: 10 });

        var reversedDomain = new FunctionPlotItem { DomainMin = 5, DomainMax = 1 };
        check("[函数绘图] 定义域写反（5 ≤ x ≤ 1）自动交换成 1..5",
            reversedDomain.EffectiveDomain(0, 10) is { Min: 1, Max: 5 },
            reversedDomain.EffectiveDomain(0, 10)?.ToString() ?? "(null)");

        var clippedDomain = new FunctionPlotItem { DomainMin = 1, DomainMax = 4 };
        check("[函数绘图] 定义域与可视范围求交（[1,4] ∩ [−10,3] = [1,3]）",
            clippedDomain.EffectiveDomain(-10, 3) is { Min: 1, Max: 3 });

        var outsideDomain = new FunctionPlotItem { DomainMin = 20, DomainMax = 30 };
        check("[函数绘图] 定义域完全在可视范围外 → 不采样（返回 null）",
            outsideDomain.EffectiveDomain(-10, 10) is null);

        check("[函数绘图] IsInDomain 判断单点是否在定义域内（端点也算）",
            clippedDomain.IsInDomain(1) && clippedDomain.IsInDomain(3) && clippedDomain.IsInDomain(2.5)
            && !clippedDomain.IsInDomain(0.5) && !clippedDomain.IsInDomain(4.5),
            "1 / 3 / 2.5 in, 0.5 / 4.5 out");

        var invalidDomain = new FunctionPlotItem { DomainMin = double.NaN, DomainMax = double.PositiveInfinity };
        check("[函数绘图] 非有限定义域按「不限制」处理",
            invalidDomain.DomainMin is null && invalidDomain.DomainMax is null,
            $"{invalidDomain.DomainMin} / {invalidDomain.DomainMax}");

        check("[函数绘图] 定义域写反时 IsInDomain 同样按交换后的区间判断",
            reversedDomain.IsInDomain(3) && !reversedDomain.IsInDomain(6),
            "3 in, 6 out");
    }

    // ---------------------------------------------------------------- 采样集成

    private static void CheckSampling(CheckFn check)
    {
        var sin = MathExpression.Parse("sin(x)");

        var sinSegments = FunctionSampler.Sample(sin, 0, 2 * Math.PI, 400);
        check("[函数绘图] 采样集成：sin 在 [0, 2π] 上是一段连续折线（400 点）",
            sinSegments.Count == 1 && sinSegments[0].Length == 400
            && Near(sinSegments[0][0].X, 0) && Near(sinSegments[0][^1].X, 2 * Math.PI, 1e-9),
            $"{sinSegments.Count} 段 / {sinSegments.FirstOrDefault()?.Length ?? 0} 点");

        var tan = MathExpression.Parse("tan(x)");
        var tanSegments = FunctionSampler.Sample(tan, -10, 10, 1200, AngleUnit.Radians, -5, 5);
        check("[函数绘图] 采样集成：tan 多段（渐近线 / 出界处断开，不画假竖线）",
            tanSegments.Count > 1
            && tanSegments.All(segment => segment.Length >= 2
                && segment.All(point => Math.Abs(point.Y) <= 5 + 1e-9)),
            $"{tanSegments.Count} 段");

        var sqrt = MathExpression.Parse("sqrt(x)");
        var sqrtSegments = FunctionSampler.Sample(sqrt, -10, 10, 600);
        check("[函数绘图] 采样集成：sqrt(x) 负半轴无点",
            sqrtSegments.Count > 0 && sqrtSegments.All(segment => segment.All(point => point.X >= -1e-9)),
            $"{sqrtSegments.Count} 段");

        var narrowed = new FunctionPlotItem { DomainMin = 1, DomainMax = 2 };
        var narrowedDomain = narrowed.EffectiveDomain(-10, 10);
        var narrowedSegments = narrowedDomain is { } nd
            ? FunctionSampler.Sample(sin, nd.Min, nd.Max, 200)
            : Array.Empty<CurvePoint[]>();
        check("[函数绘图] 采样集成：定义域收窄后采样点只在 [1, 2] 内",
            narrowedDomain is { Min: 1, Max: 2 }
            && narrowedSegments.Count > 0
            && narrowedSegments.All(segment => segment.All(point => point.X >= 1 - 1e-9 && point.X <= 2 + 1e-9)),
            $"{narrowedSegments.Count} 段");

        var clipped = FunctionSampler.Sample(sin, -10, 10, 1200, AngleUnit.Radians, -0.5, 0.5);
        check("[函数绘图] 采样集成：Y 范围裁剪后仍连续（所有点都在范围内，多段）",
            clipped.Count > 1
            && clipped.All(segment => segment.Length >= 2
                && segment.All(point => Math.Abs(point.Y) <= 0.5 + 1e-9)),
            $"{clipped.Count} 段");

        var autoRange = PlotViewport.ComputeAutoYRange(
            FunctionSampler.Sample(sin, -10, 10, 1200).ToArray());
        check("[函数绘图] 采样 + 自动 Y 范围：sin 的自动范围包住 [−1, 1] 且不过分夸张",
            autoRange.Min <= -1 && autoRange.Max >= 1 && autoRange.Min > -5 && autoRange.Max < 5,
            $"{autoRange}");

        var degreesValue = sin.Evaluate(90, AngleUnit.Degrees);
        var radiansValue = sin.Evaluate(Math.PI / 2, AngleUnit.Radians);
        check("[函数绘图] 角度制 / 弧度制都传给共享引擎（sin(90°) = sin(π/2) = 1）",
            Near(degreesValue, 1, 1e-12) && Near(radiansValue, 1, 1e-12),
            $"{degreesValue} / {radiansValue}");

        var undefined = sqrt.Evaluate(-1);
        check("[函数绘图] 数学上无定义的点返回 NaN（不抛异常，点值表显示「无定义」）",
            double.IsNaN(undefined), undefined.ToString());

        check("[函数绘图] 采样器对非法 x 范围返回空结果（配置写坏也不崩）",
            FunctionSampler.Sample(sin, double.NaN, 10, 200).Count == 0
            && FunctionSampler.Sample(sin, 10, double.PositiveInfinity, 200).Count == 0);
    }

    // ---------------------------------------------------------------- 共享格式化

    private static void CheckFormatting(CheckFn check)
    {
        check("[函数绘图] MathFormat.Format：无定义 / ∞ / 0 的处理",
            MathFormat.Format(double.NaN) == "无定义"
            && MathFormat.Format(double.PositiveInfinity) == "+∞"
            && MathFormat.Format(double.NegativeInfinity) == "-∞"
            && MathFormat.Format(0) == "0",
            $"{MathFormat.Format(double.NaN)} / {MathFormat.Format(double.PositiveInfinity)} / {MathFormat.Format(0)}");

        check("[函数绘图] MathFormat.Format：普通数字去掉多余尾巴",
            MathFormat.Format(1.5) == "1.5" && MathFormat.Format(-2.25) == "-2.25",
            $"{MathFormat.Format(1.5)} / {MathFormat.Format(-2.25)}");

        check("[函数绘图] MathFormat.Format：1/3 按 12 位有效数字截断",
            MathFormat.Format(1.0 / 3).StartsWith("0.3333333333", StringComparison.Ordinal),
            MathFormat.Format(1.0 / 3));

        check("[函数绘图] MathFormat.Format：特别大的数用科学计数法",
            MathFormat.Format(1.23456789012e15).Contains('e'),
            MathFormat.Format(1.23456789012e15));

        check("[函数绘图] MathFormat.FormatTick：坐标刻度用的短格式",
            MathFormat.FormatTick(0.5) == "0.5" && MathFormat.FormatTick(1000) == "1000"
            && MathFormat.FormatTick(-2.25) == "-2.25",
            $"{MathFormat.FormatTick(0.5)} / {MathFormat.FormatTick(1000)}");

        var sinusAtZero = MathExpression.Parse("sin(x)").Evaluate(0);
        check("[函数绘图] 点值表用 MathFormat 输出函数值（sin(0) → 0）",
            MathFormat.Format(sinusAtZero) == "0", MathFormat.Format(sinusAtZero));
    }

    // ---------------------------------------------------------------- 辅助

    private static bool Near(double a, double b, double tolerance = Tolerance)
        => Math.Abs(a - b) <= tolerance;

    /// <summary>判断步长是不是 1/2/5 × 10ⁿ。</summary>
    private static bool IsNiceStep(double step)
    {
        if (!double.IsFinite(step) || step <= 0) return false;

        var exponent = Math.Floor(Math.Log10(step));
        var mantissa = step / Math.Pow(10, exponent);
        return new[] { 1.0, 2.0, 5.0, 10.0 }.Any(candidate => Math.Abs(mantissa - candidate) < 1e-6);
    }
}
