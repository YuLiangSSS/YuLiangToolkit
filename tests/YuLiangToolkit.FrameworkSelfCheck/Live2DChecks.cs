using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Live2D;
using YuLiangToolkit.Tools.Live2D.Audio;
using YuLiangToolkit.Tools.Live2D.Audio.Analysis;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Mocap;
using YuLiangToolkit.Tools.Live2D.Model;
using YuLiangToolkit.Tools.Live2D.Rendering;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「Live2D 桌宠」自检：内置 moc3 真加载（Cubism Core）/ 参数与部件 / 动作表情解析 / 物理求值 /
/// 眼睛跟随数学 / 软件渲染器纯逻辑。全部无头跑，不弹窗、不碰真实 Data / Config。
///
/// <para><b>分层写法</b>：Interop / JSON 文件格式 / 配置 / 工具元数据在主线跑（快、无 WPF）；
/// 模型运行时 + 软件渲染器需要 WPF 位图，统一放到一个 STA 线程里跑（做法同
/// <see cref="KpsInputChecks"/> / <see cref="ThemeChecks"/>），任何一段抛异常只记 FAIL 不中断。</para>
///
/// <para><b>基准值</b>都是<b>实测</b>的：内置模型文件固定（moc3 16.5 MB 不换），所以数量可以精确断言；
/// 参数 / 动作 / 表情 / 物理的文件数量与实测的 509 / 147 / 715 / 8 / 23 / 131 对得上。</para>
/// </summary>
internal static class Live2DChecks
{
    // ---------- 内置模型基准（实测值，换模型时同步改） ----------
    private const string BuiltInModelDirectoryName = "DeepSeek";
    private const string MocFileName = "大肥鱼.moc3";
    private const int ExpectedParameterCount = 509;
    private const int ExpectedPartCount = 147;
    private const int ExpectedDrawableCount = 715;
    private const int ExpectedTextureCount = 5;
    private const int ExpectedVertexCount = 58967;
    private const int ExpectedTriangleCount = 88668;
    private const int ExpectedVisibleDrawableCount = 460;
    private const int ExpectedVisibleTriangleCount = 61218;
    private const int ExpectedMotionCount = 8;
    private const int ExpectedExpressionCount = 23;
    private const int ExpectedPhysicsSettingCount = 131;
    private const int ExpectedPhysicsInputCount = 299;
    private const int ExpectedPhysicsOutputCount = 292;
    private const long ExpectedMocLength = 16_498_560;

    public static void Run(Action<string, bool, string> check, Action<string, bool> checkSimple)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        var modelDir = Path.Combine(AppContext.BaseDirectory, "Live2D", "Models", BuiltInModelDirectoryName);
        var mocPath = Path.Combine(modelDir, MocFileName);
        var model3Path = Path.Combine(modelDir, "大肥鱼.model3.json");
        var physicsPath = Path.Combine(modelDir, "大肥鱼.physics3.json");
        var cdi3Path = Path.Combine(modelDir, "大肥鱼.cdi3.json");

        // ================================================================
        // 一、Cubism Core / 内置模型文件（不碰 WPF，主线跑）
        // ================================================================
        C("内置模型随构建拷贝到输出目录（<exe 旁边>\\Live2D\\Models\\DeepSeek）",
            Directory.Exists(modelDir), modelDir);
        C("Cubism Core 原生库可加载且版本 ≥ 5.0（csmGetVersion）",
            TryParseVersion(CubismMoc.GetCoreVersionText(), out var coreMajor, out var coreMinor, out _) && coreMajor >= 5,
            CubismMoc.GetCoreVersionText());
        C("Core 版本与发布物一致（5.0.x）",
            TryParseVersion(CubismMoc.GetCoreVersionText(), out var cm, out var cmin, out var cpatch)
            && cm == 5 && cmin == 0 && cpatch == 0,
            CubismMoc.GetCoreVersionText());
        C("moc3 文件存在且是完整内置模型（16.5 MB）",
            File.Exists(mocPath) && new FileInfo(mocPath).Length == ExpectedMocLength,
            File.Exists(mocPath) ? new FileInfo(mocPath).Length.ToString(CultureInfo.InvariantCulture) : "(不存在)");
        C("moc3 头魔数与格式版本 = 5（Cubism 5）",
            File.Exists(mocPath) && CubismMoc.ReadMocVersion(mocPath) == 5,
            File.Exists(mocPath) ? CubismMoc.ReadMocVersion(mocPath).ToString(CultureInfo.InvariantCulture) : "-");

        // 模型对象（加载一次，后面参数 / 部件 / 单位换算都用它）
        var expectedBounds = new double[4];
        var hasExpectedBounds = false;
        HashSet<string>? modelParameterIds = null;
        try
        {
            using var moc = CubismMoc.Load(mocPath);
            C("moc3 加载成功且一致性校验通过（csmHasMocConsistency）", moc.IsConsistent);
            C("moc3 加载版本 = 5（Cubism 5.0+）", moc.MocVersion == 5, CubismMoc.DescribeMocVersion(moc.MocVersion));
            C("moc3 长度与文件一致", moc.Length == ExpectedMocLength, moc.Length.ToString(CultureInfo.InvariantCulture));

            using var model = CubismModel.Create(moc);
            modelParameterIds = new HashSet<string>(model.Parameters.Select(p => p.Id), StringComparer.Ordinal);
            C("画布尺寸 = 3749 × 7697（回归基准）",
                Math.Abs(model.CanvasWidthPixels - 3749) < 0.001f && Math.Abs(model.CanvasHeightPixels - 7697) < 0.001f,
                $"{model.CanvasWidthPixels}×{model.CanvasHeightPixels}");
            C("画布原点 = (1874.5, 3848.5)（模型单位原点在画布上的位置）",
                Math.Abs(model.CanvasOriginX - 1874.5f) < 0.001f && Math.Abs(model.CanvasOriginY - 3848.5f) < 0.001f,
                $"({model.CanvasOriginX}, {model.CanvasOriginY})");
            C("PixelsPerUnit = 3749（画布宽 = 1.0 模型单位）",
                Math.Abs(model.PixelsPerUnit - 3749f) < 0.001f, model.PixelsPerUnit.ToString(CultureInfo.InvariantCulture));

            C($"参数 {ExpectedParameterCount} / 部件 {ExpectedPartCount}（回归基准）",
                model.ParameterCount == ExpectedParameterCount && model.PartCount == ExpectedPartCount,
                $"参数={model.ParameterCount} 部件={model.PartCount}");
            C($"网格 {ExpectedDrawableCount} / 三角形 {ExpectedTriangleCount} / 纹理 {ExpectedTextureCount}（回归基准）",
                model.DrawableCount == ExpectedDrawableCount
                && model.TotalTriangleCount == ExpectedTriangleCount
                && model.TexturesUsed == ExpectedTextureCount
                && model.TotalVertexCount == ExpectedVertexCount,
                $"网格={model.DrawableCount} 三角形={model.TotalTriangleCount} 纹理={model.TexturesUsed} 顶点={model.TotalVertexCount}");

            // 可见性 / 蒙版 / 混合模式（这些标志变化通常意味着换模型或 Core 升级）
            var visible = model.Drawables.Where(d => d.IsVisible).ToArray();
            C($"静止姿态可见网格 {ExpectedVisibleDrawableCount} / 可见三角形 {ExpectedVisibleTriangleCount}",
                visible.Length == ExpectedVisibleDrawableCount
                && visible.Sum(d => d.IndexCount / 3) == ExpectedVisibleTriangleCount,
                $"可见网格={visible.Length} 可见三角形={visible.Sum(d => d.IndexCount / 3)}");
            C("蒙版 / 混合模式统计与基准一致（带蒙版 181、最多 16 个蒙版、加算 9、乘算 75、蒙版反转 51）",
                model.Drawables.Count(d => d.MaskIndices.Length > 0) == 181
                && model.Drawables.Max(d => d.MaskIndices.Length) == 16
                && model.Drawables.Count(d => d.IsAdditive) == 9
                && model.Drawables.Count(d => d.IsMultiplicative) == 75
                && model.Drawables.Count(d => d.IsInvertedMask) == 51
                && model.Drawables.All(d => d.IsDoubleSided));
            C("渲染顺序 = 715 项、按 renderOrder 单调递增、覆盖全部网格下标",
                model.RenderOrderedDrawableIndices.Length == model.DrawableCount
                && model.RenderOrderedDrawableIndices.ToArray().Distinct().Count() == model.DrawableCount
                && IsRenderOrderSorted(model));

            // ---------- 参数读写 / 夹取 / 复位 ----------
            var angleX = model.FindParameter("ParamAngleX");
            CheckParams(C, model, angleX);
            CheckFaceParameters(C, model);

            // ---------- 参数 → 顶点变形 ----------
            var movedDrawables = CountDrawablesWithVertexChanges(model, () =>
            {
                if (angleX is not null) angleX.Value = 30;
            });
            C("参数改变后网格顶点跟着变形（ParamAngleX=30 时 ≥200 个网格顶点变化）",
                movedDrawables >= 200, $"{movedDrawables} 个网格有变化");

            // ---------- 部件不透明度 → 网格不透明度 ----------
            model.ResetParametersToDefault();
            model.ResetPartOpacities();
            model.Update();
            var baselineOpacities = model.Drawables.Select(d => d.Opacity).ToArray();
            var part99 = model.FindPart("Part99");
            var partChanged = 0;
            if (part99 is not null)
            {
                part99.Opacity = 0;
                model.Update();
                for (var i = 0; i < model.Drawables.Length; i++)
                    if (Math.Abs(model.Drawables[i].Opacity - baselineOpacities[i]) > 1e-4f) partChanged++;
                part99.Opacity = 1;
                model.ResetPartOpacities();
                model.Update();
            }

            C("部件不透明度写入后 Update 生效（Part99 置 0 → 子网格不透明度变化）",
                part99 is not null && partChanged >= 50, $"变化的网格 = {partChanged} 个");
            C("ResetPartOpacities 把全部部件不透明度恢复成 1",
                model.Parts.All(p => Math.Abs(p.Opacity - 1f) < 1e-4f));

            // ---------- 坐标换算 ----------
            var (originCanvasX, originCanvasY) = model.UnitsToCanvas(0f, 0f);
            var (halfCanvasX, halfCanvasY) = model.UnitsToCanvas(0.5f, 0.5f);
            C("模型单位 → 画布像素：原点 / (0.5, 0.5) / y 轴翻转正确",
                Math.Abs(originCanvasX - 1874.5f) < 0.01f && Math.Abs(originCanvasY - 3848.5f) < 0.01f
                && Math.Abs(halfCanvasX - 3749f) < 0.01f && Math.Abs(halfCanvasY - 1974f) < 0.01f,
                $"(0,0)→({originCanvasX},{originCanvasY})，(0.5,0.5)→({halfCanvasX},{halfCanvasY})");
            C("网格包围盒换算到画布坐标系后落在画布范围内",
                TryGetUnionCanvasBounds(model, out var canvasMinX, out var canvasMinY, out var canvasMaxX, out var canvasMaxY)
                && canvasMinX >= -1 && canvasMinY >= -1
                && canvasMaxX <= model.CanvasWidthPixels + 1 && canvasMaxY <= model.CanvasHeightPixels + 1,
                TryGetUnionCanvasBounds(model, out var bx0, out var by0, out var bx1, out var by1)
                    ? $"({bx0:0.#},{by0:0.#})-({bx1:0.#},{by1:0.#})"
                    : "(没有顶点)");

            // 渲染器断言用：按 FitCanvas 算出来的理论角色包围盒（320×640 输出）
            if (TryGetUnionCanvasBounds(model, out var uMinX, out var uMinY, out var uMaxX, out var uMaxY))
            {
                var fitScale = Math.Min(320.0 / model.CanvasWidthPixels, 640.0 / model.CanvasHeightPixels);
                var fitOffsetX = (320.0 - model.CanvasWidthPixels * fitScale) / 2.0;
                var fitOffsetY = (640.0 - model.CanvasHeightPixels * fitScale) / 2.0;
                expectedBounds[0] = uMinX * fitScale + fitOffsetX;
                expectedBounds[1] = uMinY * fitScale + fitOffsetY;
                expectedBounds[2] = uMaxX * fitScale + fitOffsetX;
                expectedBounds[3] = uMaxY * fitScale + fitOffsetY;
                hasExpectedBounds = true;
            }
        }
        catch (Exception ex)
        {
            C("Interop 层（Cubism Core / 模型）运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 二、模型资源发现（model3.json / 纹理 / 物理 / cdi3 / 动作 / 表情）
        // ================================================================
        Live2DModelAsset? asset = null;
        try
        {
            asset = Live2DModelAsset.Discover(modelDir);
            C("资源发现：从模型文件夹找到 model3.json 与 moc3",
                asset.Model3Path is not null && File.Exists(asset.Model3Path) && File.Exists(asset.MocPath),
                asset.Model3Path ?? "(没找到 model3.json)");
            C("资源发现：纹理 5 张且全部存在",
                asset.TexturePaths.Count == ExpectedTextureCount && asset.TexturePaths.All(File.Exists),
                string.Join(",", asset.TexturePaths.Select(Path.GetFileName)));
            C("资源发现：物理 / cdi3 文件都被认出来",
                asset.PhysicsPath is not null && File.Exists(asset.PhysicsPath)
                && asset.Cdi3Path is not null && File.Exists(asset.Cdi3Path),
                $"physics={Path.GetFileName(asset.PhysicsPath)} cdi3={Path.GetFileName(asset.Cdi3Path)}");
            C($"资源发现：动作 {ExpectedMotionCount} 个 / 表情 {ExpectedExpressionCount} 个",
                asset.MotionPaths.Count == ExpectedMotionCount && asset.ExpressionPaths.Count == ExpectedExpressionCount,
                $"动作={asset.MotionPaths.Count} 表情={asset.ExpressionPaths.Count}");
            C("资源发现：显示名 / 文件夹路径正确（内置模型 = DeepSeek）",
                !string.IsNullOrWhiteSpace(asset.Name)
                && string.Equals(Path.GetFileName(asset.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    BuiltInModelDirectoryName, StringComparison.OrdinalIgnoreCase),
                $"{asset.Name} @ {asset.Directory}");

            var explicitModel3 = Live2DModelAsset.Discover(model3Path);
            C("资源发现：直接给 model3.json 路径也能发现（显式路径 = 文件夹路径）",
                explicitModel3.MocPath == asset.MocPath
                && explicitModel3.TexturePaths.Count == asset.TexturePaths.Count
                && explicitModel3.MotionPaths.Count == asset.MotionPaths.Count);

            var badPathThrew = false;
            try { Live2DModelAsset.Discover(Path.Combine(modelDir, "这个文件夹不存在")); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or DirectoryNotFoundException or FileNotFoundException)
            { badPathThrew = true; }
            C("资源发现：路径不存在时抛异常而不是静默返回空模型", badPathThrew);
        }
        catch (Exception ex)
        {
            C("资源发现（Live2DModelAsset.Discover）运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 三、动作文件 motion3.json（文件级解析：格式 / 时长 / 段求值）
        // ================================================================
        var motions = new List<MotionFileData>();
        try
        {
            var motionPaths = Directory.GetFiles(Path.Combine(modelDir, "Animation"), "*.motion3.json")
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (var path in motionPaths)
            {
                var parsed = TryParseMotion(path);
                if (parsed is not null) motions.Add(parsed);
            }

            C($"动作文件 {ExpectedMotionCount} 个全部可解析为 motion3.json", motions.Count == ExpectedMotionCount,
                $"成功 {motions.Count} / 共 {motionPaths.Length}");

            var badMeta = motions.Where(m => m.Duration <= 0 || m.BadSegmentTypes > 0 || !m.TimesNonDecreasing).ToList();
            C("每个动作 Duration > 0、段类型合法（0 线性 / 1 贝塞尔）、段时间单调递增",
                motions.Count == ExpectedMotionCount && badMeta.Count == 0,
                string.Join("；", badMeta.Select(m => $"{m.Name}:dur={m.Duration},badSeg={m.BadSegmentTypes}")));

            var countMismatch = motions.Where(m => m.MetaCurveCount != m.Curves.Count
                                                   || (m.MetaSegmentCount >= 0 && m.MetaSegmentCount != m.ActualSegmentCount)
                                                   || (m.MetaPointCount >= 0 && m.MetaPointCount != m.ActualPointCount)).ToList();
            C("动作元数据自洽（Meta.CurveCount / TotalSegmentCount / TotalPointCount 与内容一致）",
                countMismatch.Count == 0,
                string.Join("；", countMismatch.Select(m =>
                    $"{m.Name}: curves {m.MetaCurveCount}/{m.Curves.Count} segs {m.MetaSegmentCount}/{m.ActualSegmentCount} points {m.MetaPointCount}/{m.ActualPointCount}")));

            var badTarget = motions.SelectMany(m => m.Curves).Where(c => c.Target != "Parameter").ToList();
            C("动作曲线目标都是 Parameter（本模型没有 PartOpacity / Model 曲线）", badTarget.Count == 0,
                string.Join(",", badTarget.Select(c => c.Target).Distinct()));

            // 段求值：t=0 起点值、t=Duration 末点值、中段值在控制点包络内
            var evalBad = new List<string>();
            foreach (var motion in motions)
            {
                foreach (var curve in motion.Curves)
                {
                    if (curve.Segments.Length < 2) { evalBad.Add($"{motion.Name}/{curve.Id}:空曲线"); continue; }
                    var start = EvaluateCurve(curve, 0);
                    var end = EvaluateCurve(curve, motion.Duration);
                    var mid = EvaluateCurve(curve, motion.Duration * 0.37);
                    var last = GetCurveEndValue(curve);
                    if (!Near(start, curve.Segments[1], 1e-6) || !Near(end, last, 1e-4)
                        || !double.IsFinite(mid) || mid < curve.MinValue - 1e-3 || mid > curve.MaxValue + 1e-3)
                        evalBad.Add($"{motion.Name}/{curve.Id}:{start:0.###}/{mid:0.###}/{end:0.###}");
                }
            }

            C("段求值：t=0 取起点值、t=Duration 取末点值、中段值在曲线包络内",
                evalBad.Count == 0, string.Join("；", evalBad.Take(5)));

            // 具体语义：默认手切举手在 0.5s 时手应该已经抬起来（Movement2 曲线中段 = 1）
            var raise = motions.FirstOrDefault(m => m.Name == "默认手切举手");
            var movement2 = raise?.Curves.FirstOrDefault(c => c.Id == "Movement2");
            C("动作语义：默认手切举手在 0.5s 时 Movement2 ≈ 1（手已抬起，末帧在 1.133s）",
                raise is not null && movement2 is not null
                && Near(raise.Duration, 1.133, 0.001) && EvaluateCurve(movement2, 0.5) > 0.9
                && Near(EvaluateCurve(movement2, raise.Duration), 1, 1e-4),
                movement2 is null ? "(没找到 Movement2 曲线)" : $"movement2(0.5)={EvaluateCurve(movement2, 0.5):0.###}");

            var idle = motions.FirstOrDefault(m => m.Name.Equals("IDLE", StringComparison.OrdinalIgnoreCase));
            C("IDLE 动作：时长 5.983s、Loop=true（待机动画）",
                idle is not null && Near(idle.Duration, 5.983, 0.001) && idle.Loop);
            var uncap = motions.FirstOrDefault(m => m.Name == "脱捕");
            C("最长动作「脱捕」时长 13.15s 且全部动作都是 Loop=true",
                uncap is not null && Near(uncap.Duration, 13.15, 0.01) && motions.All(m => m.Loop));
            C("动作 Meta 没写 FadeInTime / FadeOutTime（运行时按实现默认 0.3 秒淡入淡出）",
                motions.Count == ExpectedMotionCount && motions.All(m => !m.HasFadeIn && !m.HasFadeOut));
        }
        catch (Exception ex)
        {
            C("动作文件解析运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 四、表情文件 exp3.json
        // ================================================================
        try
        {
            var expressionPaths = Directory.GetFiles(Path.Combine(modelDir, "Exp3"), "*.exp3.json");
            var expressions = expressionPaths.Select(TryParseExpression).Where(e => e is not null).Select(e => e!).ToList();
            C($"表情文件 {ExpectedExpressionCount} 个全部可解析为 exp3.json", expressions.Count == ExpectedExpressionCount,
                $"成功 {expressions.Count} / 共 {expressionPaths.Length}");
            C("表情文件都是 Live2D Expression 且至少声明 1 个参数",
                expressions.All(e => e.Type == "Live2D Expression" && e.Parameters.Count > 0));
            C("表情名非空且不重复（文件名就是 UI 上显示的名字）",
                expressions.All(e => !string.IsNullOrWhiteSpace(e.Name))
                && expressions.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() == expressions.Count);
            C("表情混合模式都是 Add（本模型 23 个表情都只用加算）",
                expressions.SelectMany(e => e.Parameters).All(p => p.Blend == "Add"),
                string.Join(",", expressions.SelectMany(e => e.Parameters).Select(p => p.Blend).Distinct()));
            C("表情声明的参数都存在于 moc（21 个不同参数全部命中）",
                modelParameterIds is not null
                && expressions.SelectMany(e => e.Parameters).Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == 21
                && expressions.SelectMany(e => e.Parameters).All(p => p.Id.Length > 0 && modelParameterIds.Contains(p.Id)));

            var heartEyes = expressions.FirstOrDefault(e => e.Name == "爱心眼");
            C("「爱心眼」表情把 key4 置为 1（Add）",
                heartEyes?.Parameters.Any(p => p.Id == "key4" && Near(p.Value, 1, 1e-6) && p.Blend == "Add") == true,
                heartEyes is null ? "(没找到爱心眼)" : string.Join(",", heartEyes.Parameters.Select(p => $"{p.Id}={p.Value}")));
            var circleEyes = expressions.FirstOrDefault(e => e.Name == "圈圈眼");
            C("「圈圈眼」表情改 4 个参数（key3=1 且 key4/key5/key6=0）",
                circleEyes?.Parameters.Count == 4
                && circleEyes.Parameters.Any(p => p.Id == "key3" && Near(p.Value, 1, 1e-6))
                && circleEyes.Parameters.Any(p => p.Id == "key4" && Near(p.Value, 0, 1e-6)),
                circleEyes is null ? "(没找到圈圈眼)" : string.Join(",", circleEyes.Parameters.Select(p => $"{p.Id}={p.Value}")));
        }
        catch (Exception ex)
        {
            C("表情文件解析运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 五、物理文件 physics3.json（文件级）
        // ================================================================
        try
        {
            var physics = ParsePhysics(physicsPath);
            C($"物理配置：{ExpectedPhysicsSettingCount} 组设置 / {ExpectedPhysicsInputCount} 路输入 / {ExpectedPhysicsOutputCount} 路输出 / 431 个摆球顶点",
                physics.SettingCount == ExpectedPhysicsSettingCount
                && physics.InputCount == ExpectedPhysicsInputCount
                && physics.OutputCount == ExpectedPhysicsOutputCount
                && physics.VertexCount == 431,
                $"设置={physics.SettingCount} 输入={physics.InputCount} 输出={physics.OutputCount} 顶点={physics.VertexCount}");
            C("物理模拟频率 Fps = 60，且元数据计数与内容一致",
                physics.Fps == 60
                && physics.MetaSettingCount == physics.SettingCount
                && physics.MetaInputCount == physics.InputCount
                && physics.MetaOutputCount == physics.OutputCount,
                $"fps={physics.Fps} meta={physics.MetaSettingCount}/{physics.MetaInputCount}/{physics.MetaOutputCount}");
            C("物理设置的 Normalization 有效（Position / Angle 的 Minimum < Default < Maximum）",
                physics.InvalidNormalizationCount == 0,
                $"非法 {physics.InvalidNormalizationCount} 组");
            C("物理输入 / 输出参数都存在于 moc（299 路输入去重 50 个、292 路输出全部命中）",
                modelParameterIds is not null
                && physics.InputIds.All(id => modelParameterIds.Contains(id))
                && physics.OutputIds.All(id => modelParameterIds.Contains(id)),
                $"输入参数={physics.InputIds.Count} 输出参数={physics.OutputIds.Count}（模型参数表 {(modelParameterIds is null ? "未加载" : modelParameterIds.Count.ToString(CultureInfo.InvariantCulture))} 条）");
            C("物理字典带中文名（131 条 PhysicsDictionary，例如「身体X」「头发」）",
                physics.DictionaryNames.Count == ExpectedPhysicsSettingCount
                && physics.DictionaryNames.Count(n => ContainsCjk(n)) >= 100,
                string.Join(",", physics.DictionaryNames.Take(6)));
            C("物理输出参数去重后 292 个（每组输出不重复，回写不会互相覆盖）",
                physics.DistinctOutputCount == ExpectedPhysicsOutputCount,
                physics.DistinctOutputCount.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            C("物理文件解析运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 六、cdi3.json（显示信息）
        // ================================================================
        try
        {
            var display = ParseCdi3(cdi3Path);
            C("cdi3：509 条参数显示名 / 21 个参数分组（第 1 组「开关」）",
                display.ParameterCount == ExpectedParameterCount
                && display.ParameterGroupCount == 21
                && display.ParameterGroupNames.FirstOrDefault() == "开关",
                $"参数={display.ParameterCount} 分组={display.ParameterGroupCount}");
            C("cdi3：参数中文名 ≥ 400 条（UI 列表显示中文而不是 ParamAngleX）",
                display.ChineseParameterNameCount >= 400,
                display.ChineseParameterNameCount.ToString(CultureInfo.InvariantCulture));
            C("cdi3：部件名 148 条（比 moc 的 147 多 1 条历史条目，按 Id 匹配不碍事）",
                display.PartCount == 148 && display.ChinesePartNameCount == 148,
                $"部件名={display.PartCount}");
            C("cdi3：文件里没有 PartGroups 段（部件统一归「其他」）",
                display.HasPartGroups == false);
        }
        catch (Exception ex)
        {
            C("cdi3 解析运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 七、配置（Config\live2d\config.json 默认值 + 自动落盘往返）
        // ================================================================
        try
        {
            var defaults = new Live2DConfig();
            C("配置默认值：桌宠 420 宽 / 不透明 1.0 / 置顶 / 点击穿透 / 不自动启动",
                Near(defaults.PetWidth, 420, 1e-9) && Near(defaults.PetOpacity, 1.0, 1e-9)
                && defaults.PetTopMost && defaults.PetClickThrough && !defaults.AutoStartPet && !defaults.PetEnabled
                && defaults.PetLeft is null && defaults.PetTop is null);
            C("配置默认值：纹理 2048 / 30 帧 / 倍率 1.0 / 内置模型",
                defaults.TextureMaxSize == 2048 && defaults.FpsLimit == 30
                && Near(defaults.RenderScale, 1.0, 1e-9) && defaults.ModelFolder.Length == 0);
            C("配置默认值：物理 / 待机 / 眨眼 / 眼睛跟随全开，跟随参数是 1.0 / 1.0 / 0.5 / 0.35",
                defaults.PhysicsEnabled && defaults.IdleEnabled && defaults.AutoBlink && defaults.EyeTracking
                && Near(defaults.PhysicsStrength, 1.0, 1e-9)
                && Near(defaults.EyeTrackingSmoothing, 0.35, 1e-9)
                && Near(defaults.EyeTrackingHeadStrength, 1.0, 1e-9)
                && Near(defaults.EyeTrackingEyeStrength, 1.0, 1e-9)
                && Near(defaults.EyeTrackingBodyStrength, 0.5, 1e-9));
            C("配置默认值：摄像头动捕默认关闭（隐私默认关）/ 镜像开 / 15fps / 平滑 0.45 / 中性位正中",
                !defaults.MocapEnabled && defaults.MocapMirror && defaults.MocapFps == 15
                && defaults.MocapHeadEnabled && defaults.MocapBlinkEnabled && defaults.MocapMouthEnabled
                && Near(defaults.MocapHeadStrength, 1.0, 1e-9)
                && Near(defaults.MocapEyeStrength, 1.0, 1e-9)
                && Near(defaults.MocapMouthStrength, 1.0, 1e-9)
                && Near(defaults.MocapSmoothing, 0.45, 1e-9) && defaults.MocapShowPreview
                && Near(defaults.MocapNeutralFaceX, 0.5, 1e-9) && Near(defaults.MocapNeutralFaceY, 0.5, 1e-9)
                && defaults.MocapCameraId.Length == 0 && defaults.MocapCameraName.Length == 0);
            C("配置默认值：身体晃动默认关闭 / 模式「左右摇」/ 幅度 12° / 速度 0.5Hz / 头部跟随 0.35 / 含腰腿",
                !defaults.BodySwayEnabled && defaults.BodySwayMode == "左右摇"
                && Near(defaults.BodySwayAmplitude, 12, 1e-9) && Near(defaults.BodySwaySpeed, 0.5, 1e-9)
                && Near(defaults.BodySwayHeadFollow, 0.35, 1e-9) && defaults.BodySwayIncludeHips);
            C("配置默认值：鼠标跟随增益 1 / 1 / 1、纵向偏移 0（新增手感参数默认 = 原来手感）",
                Near(defaults.EyeTrackingHorizontalGain, 1.0, 1e-9)
                && Near(defaults.EyeTrackingUpGain, 1.0, 1e-9)
                && Near(defaults.EyeTrackingDownGain, 1.0, 1e-9)
                && Near(defaults.EyeTrackingVerticalOffset, 0.0, 1e-9));
            C("配置默认值：音乐跟唱默认关 / 模式「自动识别」/ 灵敏度 1 / 嘴型与摇晃幅度 1 / 摇晃与点头开",
                !defaults.MusicReactiveEnabled && defaults.MusicReactiveMode == "自动识别"
                && Near(defaults.MusicReactiveSensitivity, 1.0, 1e-9)
                && Near(defaults.MusicLipSyncStrength, 1.0, 1e-9)
                && Near(defaults.MusicSwayStrength, 1.0, 1e-9)
                && defaults.MusicSwayEnabled && defaults.MusicHeadBobEnabled);

            var store = ToolConfigStore.For(Live2DTool.ToolId);
            var cfg = store.Load<Live2DConfig>();
            C("配置落在 Config\\live2d\\config.json（工具 Id = 目录名）",
                string.Equals(Path.GetFileName(Path.GetDirectoryName(cfg.FilePath)), Live2DTool.ToolId, StringComparison.Ordinal)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(cfg.FilePath))), "Config", StringComparison.Ordinal)
                && string.Equals(Path.GetFileName(cfg.FilePath), "config.json", StringComparison.Ordinal),
                cfg.FilePath);

            cfg.FpsLimit = 60;
            cfg.TextureMaxSize = 4096;
            cfg.PetWidth = 520;
            cfg.EyeTracking = false;
            cfg.PhysicsStrength = 1.5;
            cfg.MocapEnabled = true;
            cfg.MocapCameraId = "cam-自检";
            cfg.MocapCameraName = "自检摄像头";
            cfg.MocapFps = 24;
            cfg.BodySwayEnabled = true;
            cfg.BodySwayMode = "椭圆";
            cfg.BodySwayAmplitude = 20;
            cfg.EyeTrackingHorizontalGain = 2.2;
            cfg.EyeTrackingUpGain = 0.8;
            cfg.EyeTrackingDownGain = 1.6;
            cfg.EyeTrackingVerticalOffset = 0.12;
            cfg.MusicReactiveEnabled = true;
            cfg.MusicReactiveMode = "强制跟唱";
            cfg.MusicReactiveSensitivity = 2.5;
            cfg.MusicLipSyncStrength = 1.4;
            cfg.MusicSwayStrength = 0.6;
            cfg.MusicSwayEnabled = false;
            cfg.MusicHeadBobEnabled = false;
            ToolConfigStore.FlushAll();
            var reloaded = JsonFileStore.Read<Live2DConfig>(cfg.FilePath);
            var configDirectory = Path.GetDirectoryName(cfg.FilePath);
            var badBackups = configDirectory is not null && Directory.Exists(configDirectory)
                ? Directory.GetFiles(configDirectory, "config.bad-*.json").Length
                : 0;
            C("配置改动自动落盘并读回（帧率 / 纹理 / 桌宠宽 / 眼睛跟随 / 物理 / 动捕 / 身体晃动 / 跟随增益 / 音乐）",
                reloaded is
                {
                    FpsLimit: 60, TextureMaxSize: 4096, PetWidth: 520, EyeTracking: false,
                    MocapEnabled: true, MocapCameraId: "cam-自检", MocapCameraName: "自检摄像头", MocapFps: 24,
                    BodySwayEnabled: true, BodySwayMode: "椭圆", BodySwayAmplitude: 20,
                    EyeTrackingHorizontalGain: 2.2, EyeTrackingUpGain: 0.8,
                    EyeTrackingDownGain: 1.6, EyeTrackingVerticalOffset: 0.12,
                    MusicReactiveEnabled: true, MusicReactiveMode: "强制跟唱", MusicReactiveSensitivity: 2.5,
                    MusicLipSyncStrength: 1.4, MusicSwayStrength: 0.6,
                    MusicSwayEnabled: false, MusicHeadBobEnabled: false,
                }
                && Near(reloaded.PhysicsStrength, 1.5, 1e-9),
                reloaded is null
                    ? $"读回失败：config 存在={File.Exists(cfg.FilePath)}，被备份的坏文件={badBackups} 个，LastError={store.LastError ?? "(无)"}"
                    : JsonFileStore.Serialize(reloaded).Replace(Environment.NewLine, " "));
            C("配置按 camelCase 落盘（手改配置时键名好认：fpsLimit / mocapEnabled / bodySwayEnabled / eyeTrackingHorizontalGain / musicReactiveEnabled）",
                File.ReadAllText(cfg.FilePath).Contains("\"fpsLimit\"")
                && File.ReadAllText(cfg.FilePath).Contains("\"textureMaxSize\"")
                && File.ReadAllText(cfg.FilePath).Contains("\"petWidth\"")
                && File.ReadAllText(cfg.FilePath).Contains("\"mocapEnabled\"")
                && File.ReadAllText(cfg.FilePath).Contains("\"bodySwayEnabled\"")
                && File.ReadAllText(cfg.FilePath).Contains("\"eyeTrackingHorizontalGain\"")
                && File.ReadAllText(cfg.FilePath).Contains("\"eyeTrackingVerticalOffset\"")
                && File.ReadAllText(cfg.FilePath).Contains("\"musicReactiveEnabled\"")
                && File.ReadAllText(cfg.FilePath).Contains("\"musicReactiveSensitivity\"")
                && File.ReadAllText(cfg.FilePath).Contains("\"musicLipSyncStrength\""),
                File.ReadAllText(cfg.FilePath).Replace(Environment.NewLine, " "));
        }
        catch (Exception ex)
        {
            C("配置断言运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 八、工具元数据（ITool 契约；不 CreateView，避免 WPF 资源依赖）
        // ================================================================
        try
        {
            var tool = new Live2DTool();
            C("工具元数据：Id = live2d / 名称 = Live2D 桌宠 / 分类 = 桌面",
                tool.Id == "live2d" && tool.Id == Live2DTool.ToolId
                && tool.Name == "Live2D 桌宠" && tool.Category == "桌面",
                $"{tool.Id} / {tool.Name} / {tool.Category}");
            C("工具元数据：分类路径单层「桌面」（主页根层级，不进子分类页）",
                tool.CategoryPath.Count == 1 && tool.CategoryPath[0] == "桌面");
            C("工具元数据：标签含 Live2D / 桌宠 / Cubism 等（搜索能搜到）",
                tool.Tags.Count >= 5
                && tool.Tags.Contains("Live2D") && tool.Tags.Contains("桌宠") && tool.Tags.Contains("Cubism"),
                string.Join(",", tool.Tags));
            C("工具元数据：Order = 160、Emoji = 🐟、描述非空（回归基准）",
                tool.Order == 160 && tool.EmojiIcon == "🐟" && tool.Description.Length > 20,
                $"order={tool.Order}");
        }
        catch (Exception ex)
        {
            C("工具元数据断言运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 九、渲染参数对象（纯逻辑，不需要 WPF 线程）
        // ================================================================
        try
        {
            var options = new RenderOptions();
            C("RenderOptions 默认值：512×1024 / FitCanvas / 透明背景 / 高亮 -1",
                options.Width == 512 && options.Height == 1024 && options.FitCanvas
                && options.TransparentBackground && options.HighlightDrawableIndex == -1
                && options.ShowMaskBufferForDrawableIndex == -1
                && Near(options.Scale, 1.0, 1e-9) && !options.ShowDrawableBounds && !options.ShowMaskRanges);
            var clone = options.Clone();
            clone.Width = 777;
            clone.ShowDrawableBounds = true;
            C("RenderOptions.Clone 是独立副本（改副本不影响原对象）",
                !ReferenceEquals(clone, options) && options.Width == 512 && clone.Width == 777
                && !options.ShowDrawableBounds && clone.ShowDrawableBounds);
            C("RenderStats.Empty 各项为 0",
                RenderStats.Empty.DrawablesDrawn == 0 && RenderStats.Empty.TrianglesDrawn == 0
                && RenderStats.Empty.PixelsFilled == 0 && RenderStats.Empty.MaskPasses == 0
                && RenderStats.Empty.RenderMilliseconds == 0);
        }
        catch (Exception ex)
        {
            C("RenderOptions 断言运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 十、摄像头面部动捕（纯逻辑断言；自检绝不打开摄像头、不弹隐私授权）
        // ================================================================
        try
        {
            // ---------- MocapMath：脸在画面里的位置 → 头部角度 / 眼球方向 ----------
            C("动捕数学常量：左右窗口 0.20 / 上下窗口 0.16 / 最大角度 30° / 22°",
                Near(MocapMath.YawWindow, 0.20, 1e-12) && Near(MocapMath.PitchWindow, 0.16, 1e-12)
                && Near(MocapMath.MaxYawDegrees, 30, 1e-12) && Near(MocapMath.MaxPitchDegrees, 22, 1e-12));

            var (centerYaw, centerPitch) = MocapMath.MapHead(0.5, 0.5, 0.5, 0.5);
            C("动捕数学：脸在画面正中 = 中性位置 → Yaw / Pitch ≈ 0",
                Near(centerYaw, 0, 1e-9) && Near(centerPitch, 0, 1e-9),
                $"yaw={centerYaw:0.###} pitch={centerPitch:0.###}");

            var (rightYaw, rightPitch) = MocapMath.MapHead(0.6, 0.5, 0.5, 0.5);
            var (leftYaw, _) = MocapMath.MapHead(0.4, 0.5, 0.5, 0.5);
            C("动捕数学：脸偏右 → Yaw 为正（0.10 窗口 = 15°），脸偏左对称取负",
                Near(rightYaw, 15, 1e-6) && Near(rightPitch, 0, 1e-9) && Near(leftYaw, -15, 1e-6),
                $"右={rightYaw:0.###} 左={leftYaw:0.###}");

            var (downYaw, downPitch) = MocapMath.MapHead(0.5, 0.58, 0.5, 0.5);
            var (upYaw, upPitch) = MocapMath.MapHead(0.5, 0.42, 0.5, 0.5);
            C("动捕数学：脸偏下 → Pitch 为负（低头 ≈ -11°），脸偏上 → 为正（抬头）",
                Near(downYaw, 0, 1e-9) && downPitch < -10 && upPitch > 10 && Near(upYaw, 0, 1e-9),
                $"下={downPitch:0.###} 上={upPitch:0.###}");

            var (clampRightYaw, clampDownPitch) = MocapMath.MapHead(0.95, 0.95, 0.5, 0.5);
            var (clampLeftYaw, clampUpPitch) = MocapMath.MapHead(0.05, 0.05, 0.5, 0.5);
            C("动捕数学：偏移超出窗口 → 角度夹在 ±30° / ±22°（不会转过头）",
                Near(clampRightYaw, 30, 1e-9) && Near(clampLeftYaw, -30, 1e-9)
                && Near(clampDownPitch, -22, 1e-9) && Near(clampUpPitch, 22, 1e-9),
                $"({clampRightYaw:0.#},{clampDownPitch:0.#}) / ({clampLeftYaw:0.#},{clampUpPitch:0.#})");

            var (calibratedYaw, calibratedPitch) = MocapMath.MapHead(0.42, 0.63, 0.42, 0.63);
            C("动捕数学：中性位可以校准（脸在中性位时角度 = 0，不要求画面正中心）",
                Near(calibratedYaw, 0, 1e-9) && Near(calibratedPitch, 0, 1e-9));

            var (pupilX, pupilY) = MocapMath.MapPupil(30, 22);
            var (pupilLeft, pupilDown) = MocapMath.MapPupil(-30, -22);
            var (pupilClampX, pupilClampY) = MocapMath.MapPupil(90, -90);
            C("动捕数学：头部角度 → 眼球方向（满角 → 0.9 / 0.6，反向对称，超界夹到 ±1）",
                Near(pupilX, 0.9, 1e-9) && Near(pupilY, 0.6, 1e-9)
                && Near(pupilLeft, -0.9, 1e-9) && Near(pupilDown, -0.6, 1e-9)
                && Near(pupilClampX, 1, 1e-9) && Near(pupilClampY, -1, 1e-9),
                $"({pupilX:0.##},{pupilY:0.##}) / ({pupilClampX:0.##},{pupilClampY:0.##})");

            var smoothDirect = MocapMath.Smooth(0, 10, 0);
            var smoothHalf = MocapMath.Smooth(0, 10, 0.5);
            var smoothTenPercent = MocapMath.Smooth(10, 0, 0.9);
            var smoothClamped = MocapMath.Smooth(0, 10, 1.5);
            C("动捕平滑：smoothing=0 等同直接赋值、0.5 = 走一半、0.9 = 走 10%，越界系数被夹",
                Near(smoothDirect, 10, 1e-9) && Near(smoothHalf, 5, 1e-9)
                && Near(smoothTenPercent, 9, 1e-9) && Near(smoothClamped, 0.5, 1e-9),
                $"{smoothDirect} / {smoothHalf} / {smoothTenPercent} / {smoothClamped}");
            var smoothCurrent = 0.0;
            for (var i = 0; i < 60; i++) smoothCurrent = MocapMath.Smooth(smoothCurrent, 20, 0.5);
            C("动捕平滑：连续迭代会收敛到目标（60 帧内到目标的 99% 以上）",
                Math.Abs(smoothCurrent - 20) < 0.2, smoothCurrent.ToString("0.###", CultureInfo.InvariantCulture));

            // ---------- FaceExpressionAnalyzer：用合成 BGRA 图直接测启发式（不碰摄像头） ----------
            var faceBox = new FaceExpressionAnalyzer.FaceBox(80, 60, 160, 160);
            var frameEyesOpen = MakeSyntheticFaceFrame(320, 240, faceBox, leftEyeOpen: true, rightEyeOpen: true, mouthOpen: false);
            var frameEyesClosed = MakeSyntheticFaceFrame(320, 240, faceBox, leftEyeOpen: false, rightEyeOpen: false, mouthOpen: false);
            var frameMouthOpen = MakeSyntheticFaceFrame(320, 240, faceBox, leftEyeOpen: false, rightEyeOpen: false, mouthOpen: true);

            var eyeAnalyzer = new FaceExpressionAnalyzer();
            for (var i = 0; i < 40; i++)
                eyeAnalyzer.Analyze(i % 2 == 0 ? frameEyesOpen : frameEyesClosed, 320, 240, in faceBox);
            var openEyeMetrics = eyeAnalyzer.Analyze(frameEyesOpen, 320, 240, in faceBox);
            var closedEyeMetrics = eyeAnalyzer.Analyze(frameEyesClosed, 320, 240, in faceBox);
            C("动捕表情分析：睁眼眼开度 > 闭眼（合成图 + 滚动分位数自动校准）",
                openEyeMetrics.EyeOpenLeft > 0.7 && openEyeMetrics.EyeOpenRight > 0.7
                && closedEyeMetrics.EyeOpenLeft < 0.3 && closedEyeMetrics.EyeOpenRight < 0.3,
                $"睁={openEyeMetrics.EyeOpenLeft:0.###} 闭={closedEyeMetrics.EyeOpenLeft:0.###}");

            var frameLeftClosed = MakeSyntheticFaceFrame(320, 240, faceBox, leftEyeOpen: false, rightEyeOpen: true, mouthOpen: false);
            var asymmetricMetrics = eyeAnalyzer.Analyze(frameLeftClosed, 320, 240, in faceBox);
            C("动捕表情分析：单眼闭时左右眼分开输出（左闭右睁，互不串味）",
                asymmetricMetrics.EyeOpenLeft < asymmetricMetrics.EyeOpenRight - 0.3,
                $"左={asymmetricMetrics.EyeOpenLeft:0.###} 右={asymmetricMetrics.EyeOpenRight:0.###}");

            var mouthAnalyzer = new FaceExpressionAnalyzer();
            for (var i = 0; i < 40; i++)
                mouthAnalyzer.Analyze(i % 2 == 0 ? frameMouthOpen : frameEyesClosed, 320, 240, in faceBox);
            var mouthOpenMetrics = mouthAnalyzer.Analyze(frameMouthOpen, 320, 240, in faceBox);
            var mouthClosedMetrics = mouthAnalyzer.Analyze(frameEyesClosed, 320, 240, in faceBox);
            C("动捕表情分析：张嘴张嘴度 > 闭嘴（嘴部暗块比例 + 对比度启发式）",
                mouthOpenMetrics.MouthOpen > 0.7 && mouthClosedMetrics.MouthOpen < 0.3,
                $"张={mouthOpenMetrics.MouthOpen:0.###} 闭={mouthClosedMetrics.MouthOpen:0.###}");

            var resetAnalyzer = new FaceExpressionAnalyzer();
            for (var i = 0; i < 40; i++)
                resetAnalyzer.Analyze(i % 2 == 0 ? frameEyesOpen : frameEyesClosed, 320, 240, in faceBox);
            resetAnalyzer.ResetCalibration();
            var afterResetMetrics = resetAnalyzer.Analyze(frameEyesOpen, 320, 240, in faceBox);
            C("动捕表情分析：ResetCalibration 后回到安全默认（眼=1 / 嘴=0，不会拿旧基准乱跳）",
                Near(afterResetMetrics.EyeOpenLeft, 1, 1e-9) && Near(afterResetMetrics.EyeOpenRight, 1, 1e-9)
                && Near(afterResetMetrics.MouthOpen, 0, 1e-9));

            var tinyBox = new FaceExpressionAnalyzer.FaceBox(80, 60, 12, 12);
            var tinyMetrics = resetAnalyzer.Analyze(frameEyesOpen, 320, 240, in tinyBox);
            C("动捕表情分析：脸框太小 → 安全值（眼=1 / 嘴=0），不按噪声乱算",
                Near(tinyMetrics.EyeOpenLeft, 1, 1e-9) && Near(tinyMetrics.EyeOpenRight, 1, 1e-9)
                && Near(tinyMetrics.MouthOpen, 0, 1e-9));

            // ---------- MocapSettings.FromConfig：配置 → 运行参数（含 clamp） ----------
            var mocapCfg = new Live2DConfig
            {
                MocapCameraId = "cam-1",
                MocapCameraName = "测试摄像头",
                MocapMirror = false,
                MocapFps = 99,
                MocapSmoothing = 3,
                MocapShowPreview = false,
                MocapNeutralFaceX = 0.42,
                MocapNeutralFaceY = 0.6,
            };
            var mocapSettings = MocapSettings.FromConfig(mocapCfg);
            C("动捕设置映射：摄像头 Id / 名字 / 镜像 / 预览 / 中性位原样带过去",
                mocapSettings.CameraId == "cam-1" && mocapSettings.CameraName == "测试摄像头"
                && !mocapSettings.Mirror && !mocapSettings.EnablePreview
                && Near(mocapSettings.NeutralFaceX, 0.42, 1e-9) && Near(mocapSettings.NeutralFaceY, 0.6, 1e-9));
            C("动捕设置 clamp 上界：帧率夹到 24、平滑夹到 0.95",
                mocapSettings.Fps == 24 && Near(mocapSettings.Smoothing, 0.95, 1e-9),
                $"fps={mocapSettings.Fps} smoothing={mocapSettings.Smoothing}");
            var mocapLow = MocapSettings.FromConfig(new Live2DConfig { MocapFps = 1, MocapSmoothing = -5 });
            C("动捕设置 clamp 下界：帧率夹到 10、平滑夹到 0",
                mocapLow.Fps == 10 && Near(mocapLow.Smoothing, 0, 1e-9));
            var mocapDefaults = MocapSettings.FromConfig(new Live2DConfig());
            C("动捕设置默认值：自动选摄像头 / 镜像开 / 15fps / 平滑 0.45 / 预览开 / 中性位正中",
                mocapDefaults.CameraId.Length == 0 && mocapDefaults.CameraName.Length == 0
                && mocapDefaults.Mirror && mocapDefaults.Fps == 15 && mocapDefaults.EnablePreview
                && Near(mocapDefaults.Smoothing, 0.45, 1e-9)
                && Near(mocapDefaults.NeutralFaceX, 0.5, 1e-9) && Near(mocapDefaults.NeutralFaceY, 0.5, 1e-9));

            // ---------- 动捕数据契约 ----------
            var noFaceResult = MocapTrackingResult.NoFace(123456);
            C("动捕数据契约：NoFace() = HasFace=false 的空结果，时间戳保留",
                !noFaceResult.HasFace && noFaceResult.TimestampMs == 123456
                && noFaceResult.EyeOpenLeft == 0 && noFaceResult.MouthOpen == 0);

            // ---------- 摄像头枚举（只枚举设备，不启动采集、不弹隐私授权） ----------
            try
            {
                var enumerateTask = Task.Run(async () => await MocapCameraSource.EnumerateCamerasAsync());
                if (enumerateTask.Wait(TimeSpan.FromSeconds(20)))
                {
                    var cameras = enumerateTask.Result;
                    C("动捕：摄像头枚举不抛异常、返回列表（0 个也算 PASS；自检不打开摄像头）",
                        cameras is not null, $"枚举到 {cameras?.Count ?? 0} 个摄像头（未启动采集）");
                    C("动捕：枚举到的摄像头条目 Id / Name 都非空（0 个时直接 PASS）",
                        cameras is null || cameras.Count == 0
                        || cameras.All(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Name)),
                        cameras is null || cameras.Count == 0
                            ? "(本机枚举到 0 个摄像头)"
                            : string.Join(" / ", cameras.Select(c => c.Name)));
                }
                else
                {
                    C("动捕：摄像头枚举在 20 秒内没有返回（不启动摄像头也可能因系统服务忙超时）", false, "EnumerateCamerasAsync 超时");
                }
            }
            catch (Exception ex)
            {
                C("动捕：摄像头枚举抛出了异常（应内部吞掉并返回空列表）", false, $"{ex.GetType().Name}: {ex.Message}");
            }

            // 构造数据源不应该碰摄像头（隐私：只有显式 StartAsync 才初始化摄像头）
            try
            {
                using var cameraSource = new MocapCameraSource(MocapSettings.FromConfig(new Live2DConfig()));
                C("动捕：构造 MocapCameraSource 不会启动采集（IsRunning=false、Latest=null，隐私默认关）",
                    !cameraSource.IsRunning && cameraSource.Latest is null && cameraSource.LatestPreview is null);
                C("动捕：未启动时状态字段是安全默认（无错误 / 帧率 0 / 画面 0×0）",
                    cameraSource.LastError is null && cameraSource.MeasuredFps == 0
                    && cameraSource.FrameSize == (0, 0));
            }
            catch (Exception ex)
            {
                C("动捕：构造 MocapCameraSource 运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            C("动捕纯逻辑断言运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 十一、音乐跟唱：音频分析纯逻辑（只测合成信号；自检绝不开 WASAPI 回环 / 麦克风）
        // ================================================================
        try
        {
            // ---------- 数据契约 ----------
            var silentSnapshot = MusicAnalysisSnapshot.Silent(123456);
            C("音乐分析：Silent() 快照 = 未播放 / Kind=Silent / 置信度 0 / 时间戳保留",
                !silentSnapshot.IsPlaying && silentSnapshot.Kind == MusicContentKind.Silent
                && Near(silentSnapshot.Confidence, 0, 1e-12) && silentSnapshot.TimestampMs == 123456);
            C("音乐分析：内容类型枚举顺序固定（Unknown=0 / Silent=1 / Instrumental=2 / VocalSong=3）",
                (int)MusicContentKind.Unknown == 0 && (int)MusicContentKind.Silent == 1
                && (int)MusicContentKind.Instrumental == 2 && (int)MusicContentKind.VocalSong == 3);

            // ---------- FastFft：长度校验 ----------
            C("音乐分析：FFT 长度必须是 2 的幂（1/2/4/1024 是，0/3/6/1000 不是）",
                FastFft.IsPowerOfTwo(1) && FastFft.IsPowerOfTwo(2) && FastFft.IsPowerOfTwo(4)
                && FastFft.IsPowerOfTwo(1024) && !FastFft.IsPowerOfTwo(0) && !FastFft.IsPowerOfTwo(3)
                && !FastFft.IsPowerOfTwo(6) && !FastFft.IsPowerOfTwo(1000));

            // ---------- FastFft vs 朴素 DFT 对拍 ----------
            const int fftLength = 256;
            var mixedSignal = new double[fftLength];
            for (var i = 0; i < fftLength; i++)
            {
                mixedSignal[i] = (0.6 * Math.Sin(2 * Math.PI * 13.0 * i / fftLength))
                                 + (0.3 * Math.Sin((2 * Math.PI * 37.0 * i / fftLength) + 0.7))
                                 + (0.05 * (((i * 2654435761u) % 1000) / 1000.0 - 0.5));
            }

            var fftReal = (double[])mixedSignal.Clone();
            var fftImag = new double[fftLength];
            FastFft.Transform(fftReal, fftImag);

            var dftReal = new double[fftLength];
            var dftImag = new double[fftLength];
            FastFft.NaiveDft(mixedSignal, dftReal, dftImag);

            var maxFftError = 0.0;
            for (var i = 0; i < fftLength; i++)
            {
                maxFftError = Math.Max(maxFftError, Math.Abs(fftReal[i] - dftReal[i]));
                maxFftError = Math.Max(maxFftError, Math.Abs(fftImag[i] - dftImag[i]));
            }

            C("音乐分析：FastFft 与朴素 DFT 对拍一致（混合正弦 + 噪声，最大误差 < 1e-6）",
                maxFftError < 1e-6, $"maxErr={maxFftError:0.###e+0}");

            // ---------- 峰值频点 / 直流 ----------
            var toneReal = new double[1024];
            for (var i = 0; i < toneReal.Length; i++)
                toneReal[i] = 0.8 * Math.Sin(2 * Math.PI * 8.0 * i / toneReal.Length);
            var toneImag = new double[toneReal.Length];
            FastFft.Transform(toneReal, toneImag);
            var toneMagnitude = new double[(toneReal.Length / 2) + 1];
            FastFft.MagnitudeSpectrum(toneReal, toneImag, toneMagnitude);
            var peakBin = 0;
            for (var i = 1; i < toneMagnitude.Length; i++)
                if (toneMagnitude[i] > toneMagnitude[peakBin]) peakBin = i;
            C("音乐分析：FFT 峰值频点正确（1024 点、第 8 个 bin 的正弦 → 峰值落在 bin 8）",
                peakBin == 8, $"peakBin={peakBin}");

            var dcReal = Enumerable.Repeat(1.0, 1024).ToArray();
            var dcImag = new double[1024];
            FastFft.Transform(dcReal, dcImag);
            var dcMagnitude = new double[513];
            FastFft.MagnitudeSpectrum(dcReal, dcImag, dcMagnitude);
            var dcLeak = dcMagnitude.Skip(1).Max();
            C("音乐分析：纯直流信号的幅度谱只在 bin 0 有能量（= N），其它频点 ≈ 0",
                Near(dcMagnitude[0], 1024, 1e-6) && dcLeak < 1e-6, $"bin0={dcMagnitude[0]:0.###} 最大漏={dcLeak:0.###e+0}");

            // ---------- 边界 / 异常 ----------
            var emptyReal = Array.Empty<double>();
            var emptyImag = Array.Empty<double>();
            FastFft.Transform(emptyReal, emptyImag);
            var singleReal = new[] { 3.0 };
            var singleImag = new[] { 0.0 };
            FastFft.Transform(singleReal, singleImag);
            C("音乐分析：FFT 空输入 / 单点输入不崩（直接返回，单点值不变）",
                singleReal[0] == 3.0 && singleImag[0] == 0.0);

            var nonPowerOfTwoThrew = false;
            try
            {
                var badReal = new double[100];
                var badImag = new double[100];
                FastFft.Transform(badReal, badImag);
            }
            catch (ArgumentException)
            {
                nonPowerOfTwoThrew = true;
            }

            C("音乐分析：FFT 长度不是 2 的幂时抛 ArgumentException（不静默算错）", nonPowerOfTwoThrew);

            // ---------- Hann 窗 / 加窗 ----------
            var hann = new double[1024];
            FastFft.FillHannWindow(hann);
            C("音乐分析：Hann 窗端点 0 / 中心 1 / 左右对称（w[1] == w[N-1]）",
                Near(hann[0], 0, 1e-12) && Near(hann[512], 1, 1e-12)
                && Near(hann[1], hann[1023], 1e-12) && hann[1] > 0 && hann[1] < 0.001);

            var rawSamples = new double[64];
            for (var i = 0; i < rawSamples.Length; i++) rawSamples[i] = i + 1;
            var windowed = new double[128];
            FastFft.ApplyWindow(rawSamples, hann, windowed);
            C("音乐分析：加窗拷贝把样本乘窗、超出样本长度的部分填 0",
                Near(windowed[0], 0, 1e-12) && Near(windowed[32], 33.0 * hann[32], 1e-12)
                && windowed.Skip(rawSamples.Length).All(value => value == 0));

            // ---------- YIN 基频（只喂合成信号） ----------
            const int yinSampleRate = 16000;
            var sine220 = MakeSine(yinSampleRate, 2.0, 220.0);
            var yin220 = MusicAnalyzer.EstimatePitchYin(sine220, yinSampleRate, 16, 178, out var clarity220);
            C("音乐分析：YIN 估 220Hz 正弦 → 基频误差 < 3%、清晰度高",
                Math.Abs(yin220 - 220.0) / 220.0 < 0.03 && clarity220 > 0.7,
                $"{yin220:0.##}Hz clarity={clarity220:0.##}");

            var sine440 = MakeSine(yinSampleRate, 2.0, 440.0);
            var yin440 = MusicAnalyzer.EstimatePitchYin(sine440, yinSampleRate, 16, 178, out var clarity440);
            C("音乐分析：YIN 估 440Hz 正弦 → 基频误差 < 3%、清晰度高",
                Math.Abs(yin440 - 440.0) / 440.0 < 0.03 && clarity440 > 0.7,
                $"{yin440:0.##}Hz clarity={clarity440:0.##}");

            var noise = MakeWhiteNoise(yinSampleRate, 2.0, seed: 20261101);
            var yinNoise = MusicAnalyzer.EstimatePitchYin(noise, yinSampleRate, 16, 178, out var clarityNoise);
            C("音乐分析：YIN 对白噪声不会给出可信基频（清晰度低）",
                yinNoise == 0 || clarityNoise < 0.5, $"{yinNoise:0.##}Hz clarity={clarityNoise:0.##}");

            // ---------- 谱特征 ----------
            var flatMagnitude = Enumerable.Repeat(0.1, 512).ToArray();
            var flatness = MusicAnalyzer.ComputeSpectralFlatness(flatMagnitude, 19, 218);
            var peakMagnitude = new double[512];
            for (var i = 19; i <= 218; i++) peakMagnitude[i] = 0.01;
            for (var i = 60; i <= 66; i++) peakMagnitude[i] = 1.0;
            var peakFlatness = MusicAnalyzer.ComputeSpectralFlatness(peakMagnitude, 19, 218);
            C("音乐分析：谱平坦度 平坦噪声谱 ≈ 1、有尖峰的音调谱明显更小",
                flatness > 0.95 && peakFlatness < 0.35,
                $"flat={flatness:0.###} peak={peakFlatness:0.###}");

            var centroidMagnitude = new double[512];
            centroidMagnitude[40] = 1.0;
            var centroidHz = MusicAnalyzer.ComputeSpectralCentroid(centroidMagnitude, 19, 218, 15.625);
            C("音乐分析：谱质心 单峰在第 40 bin → 约 40×15.625Hz ≈ 625Hz",
                Math.Abs(centroidHz - 625.0) < 20.0, $"{centroidHz:0.##}Hz");

            var fluxCurrent = new double[512];
            var fluxPrevious = new double[512];
            fluxCurrent[50] = 0.4;
            fluxCurrent[51] = 2.0;
            fluxPrevious[50] = 0.1;
            fluxPrevious[51] = 3.0;
            var flux = MusicAnalyzer.ComputePositiveSpectralFlux(fluxCurrent, fluxPrevious, 13, 256);
            var fluxSame = MusicAnalyzer.ComputePositiveSpectralFlux(fluxPrevious, fluxPrevious, 13, 256);
            C("音乐分析：正向谱通量 相同频谱 = 0、变亮频点累计正确",
                fluxSame == 0 && Math.Abs(flux - 0.3) < 1e-9, $"flux={flux:0.###}");

            var formantMagnitude = new double[512];
            for (var i = 0; i < formantMagnitude.Length; i++) formantMagnitude[i] = 0.02;
            for (var i = 45; i <= 55; i++) formantMagnitude[i] = 1.0;
            for (var i = 92; i <= 102; i++) formantMagnitude[i] = 0.8;
            for (var i = 165; i <= 175; i++) formantMagnitude[i] = 0.7;
            var formantSmooth = new double[512];
            var formantScore = MusicAnalyzer.ComputeFormantScore(formantMagnitude, formantSmooth, 19, 218);
            var flatFormant = MusicAnalyzer.ComputeFormantScore(flatMagnitude, formantSmooth, 19, 218);
            C("音乐分析：共振峰打分 多宽峰谱 > 0、平坦谱 = 0（数的是平滑后的峰）",
                formantScore > 0.2 && flatFormant == 0, $"peaks={formantScore:0.###} flat={flatFormant:0.###}");

            // ---------- 节拍 BPM（onset 包络自相关） ----------
            const double hopRate = 16000.0 / 256.0; // 62.5 帧/秒
            var onset120 = MakeClickOnsetEnvelope(6.0, hopRate, bpm: 120.0);
            var bpm120 = MusicAnalyzer.EstimateBpmFromOnsetEnvelope(onset120, hopRate, out var corr120);
            C("音乐分析：120BPM 点击的 onset 包络 → BPM 估在 110~130、相关度 > 0.2",
                bpm120 >= 110 && bpm120 <= 130 && corr120 > 0.2, $"bpm={bpm120:0.##} corr={corr120:0.###}");

            var flatOnset = Enumerable.Repeat(0.05, (int)(6.0 * hopRate)).ToArray();
            var bpmFlat = MusicAnalyzer.EstimateBpmFromOnsetEnvelope(flatOnset, hopRate, out _);
            C("音乐分析：平稳 onset 包络（无节拍）→ BPM = 0（不硬猜）", bpmFlat == 0, $"bpm={bpmFlat:0.##}");

            // ---------- 完整 Analyzer：静音 ----------
            var silentAnalyzer = new MusicAnalyzer(48000);
            var silentAnalyzerSnapshot = FeedAnalyzer(silentAnalyzer, new float[48000 * 2], 48000, 480);
            C("音乐分析：喂 0.5 秒静音 → Kind=Silent、IsPlaying=false、MouthOpen=0",
                silentAnalyzerSnapshot.Kind == MusicContentKind.Silent && !silentAnalyzerSnapshot.IsPlaying
                && Near(silentAnalyzerSnapshot.MouthOpen, 0, 1e-9) && silentAnalyzerSnapshot.Bpm == 0,
                $"kind={silentAnalyzerSnapshot.Kind} playing={silentAnalyzerSnapshot.IsPlaying}");

            // ---------- 完整 Analyzer：120BPM 点击（纯音乐） ----------
            var clickAnalyzer = new MusicAnalyzer(48000);
            var clickTrack = MakeClickTrackStereo(6.0, 48000, bpm: 120.0);
            var clickSnapshots = FeedAnalyzerCollect(clickAnalyzer, clickTrack, 48000, 480);
            var clickLast = clickSnapshots[^1];
            var bpmOk = clickLast.Bpm >= 110 && clickLast.Bpm <= 130;
            var phaseValues = clickSnapshots.TakeLast(40).Select(s => s.BeatPhase).Distinct().Count();
            C("音乐分析：120BPM 点击连喂 6 秒 → IsPlaying=true、BPM 110~130、节拍相位随时间推进",
                clickLast.IsPlaying && bpmOk && clickLast.BeatPhase >= 0 && clickLast.BeatPhase < 1
                && phaseValues >= 5,
                $"playing={clickLast.IsPlaying} bpm={clickLast.Bpm:0.##} phase={clickLast.BeatPhase:0.###} 变化点={phaseValues}");
            C("音乐分析：点击（无旋律 / 无人声特征）判为纯音乐、MouthOpen / BeatStrength 在 0~1",
                clickLast.Kind == MusicContentKind.Instrumental
                && clickLast.MouthOpen >= 0 && clickLast.MouthOpen <= 1
                && clickLast.BeatStrength >= 0 && clickLast.BeatStrength <= 1,
                $"kind={clickLast.Kind} mouth={clickLast.MouthOpen:0.##} beat={clickLast.BeatStrength:0.##}");

            // 无节拍回退：稳态长音（没有鼓点 / onset）→ BPM=0，但相位仍缓慢推进、BeatStrength 有保底
            var steadyAnalyzer = new MusicAnalyzer(48000);
            var steadyTone = MakeSteadyToneStereo(6.0, 48000, 220.0, 0.2);
            var steadySnapshots = FeedAnalyzerCollect(steadyAnalyzer, steadyTone, 48000, 480);
            var steadyLast = steadySnapshots[^1];
            var steadyPhaseChanges = steadySnapshots.TakeLast(60).Select(s => s.BeatPhase).Distinct().Count();
            C("音乐分析：无节拍（稳态长音）→ BPM=0 但相位仍缓慢推进、BeatStrength 有保底（纯音乐也会轻晃）",
                steadyLast.IsPlaying && steadyLast.Bpm == 0 && steadyPhaseChanges >= 3
                && steadyLast.BeatStrength >= 0.2,
                $"bpm={steadyLast.Bpm:0.##} phase变化点={steadyPhaseChanges} beat={steadyLast.BeatStrength:0.###}");

            // ---------- 完整 Analyzer：类人声（中心声道 + 颤音 + 4Hz 音节包络 + 共振峰谐波） ----------
            var vocalAnalyzer = new MusicAnalyzer(48000);
            var vocalSignal = MakeVocalLikeStereo(8.0, 48000, f0: 220.0);
            var vocalSnapshots = FeedAnalyzerCollect(vocalAnalyzer, vocalSignal, 48000, 480);
            var vocalLast = vocalSnapshots[^1];
            var instrumentalAnalyzer = new MusicAnalyzer(48000);
            var instrumentalSignal = MakeInstrumentalStereo(8.0, 48000);
            var instrumentalSnapshots = FeedAnalyzerCollect(instrumentalAnalyzer, instrumentalSignal, 48000, 480);
            var instrumentalLast = instrumentalSnapshots[^1];

            C("音乐分析：类人声信号喂 8 秒 → Kind=VocalSong、VocalScore 明显高于纯乐器信号",
                vocalLast.Kind == MusicContentKind.VocalSong && vocalLast.IsPlaying
                && vocalLast.VocalScore > instrumentalLast.VocalScore + 0.15,
                $"vocalKind={vocalLast.Kind} score={vocalLast.VocalScore:0.###} 纯乐器score={instrumentalLast.VocalScore:0.###}");
            C("音乐分析：类人声的基频接近设定 220Hz（±10%）、中心声道占比高、音节调制 > 0",
                vocalLast.PitchValid && Math.Abs(vocalLast.PitchHz - 220.0) / 220.0 < 0.10
                && vocalLast.CenterVoiceRatio > 0.6 && vocalLast.SyllabicModulation > 0.05,
                $"pitch={vocalLast.PitchHz:0.##}Hz center={vocalLast.CenterVoiceRatio:0.###} syllabic={vocalLast.SyllabicModulation:0.###}");
            C("音乐分析：类人声的 MouthOpen 在 0~1 且随音节包络起伏（最近 2 秒内有明显变化）",
                vocalSnapshots.TakeLast(125).All(s => s.MouthOpen >= 0 && s.MouthOpen <= 1)
                && vocalSnapshots.TakeLast(125).Max(s => s.MouthOpen) - vocalSnapshots.TakeLast(125).Min(s => s.MouthOpen) > 0.1,
                $"范围 {vocalSnapshots.TakeLast(125).Min(s => s.MouthOpen):0.##}~{vocalSnapshots.TakeLast(125).Max(s => s.MouthOpen):0.##}");
            C("音乐分析：MouthForm / Bpm / CenterVoiceRatio / SyllabicModulation 都在合理范围",
                vocalLast.MouthForm is >= -1 and <= 1 && vocalLast.Bpm is >= 0 and <= 200
                && vocalLast.CenterVoiceRatio is >= 0 and <= 1
                && vocalLast.SyllabicModulation is >= 0 and <= 1,
                $"form={vocalLast.MouthForm:0.###} bpm={vocalLast.Bpm:0.##}");

            // ---------- 完整 Analyzer：纯乐器 ----------
            C("音乐分析：纯乐器信号喂 8 秒 → Kind=Instrumental（不跟唱，只摇晃）",
                instrumentalLast.Kind == MusicContentKind.Instrumental && instrumentalLast.IsPlaying,
                $"kind={instrumentalLast.Kind} score={instrumentalLast.VocalScore:0.###}");

            // ---------- 灵敏度：高灵敏度更容易判成人声 ----------
            var sensitiveAnalyzer = new MusicAnalyzer(48000) { Sensitivity = 2.5 };
            var sensitiveSnapshots = FeedAnalyzerCollect(sensitiveAnalyzer, vocalSignal, 48000, 480);
            var sensitiveLast = sensitiveSnapshots[^1];
            var conservativeAnalyzer = new MusicAnalyzer(48000) { Sensitivity = 0.5 };
            var conservativeSnapshots = FeedAnalyzerCollect(conservativeAnalyzer, vocalSignal, 48000, 480);
            var conservativeLast = conservativeSnapshots[^1];
            C("音乐分析：灵敏度 2.5 的 VocalScore 高于灵敏度 0.5（同一段类人声信号）",
                sensitiveLast.VocalScore > conservativeLast.VocalScore + 0.05,
                $"2.5={sensitiveLast.VocalScore:0.###} 0.5={conservativeLast.VocalScore:0.###}");

            // ---------- Reset：清空状态后重新开始 ----------
            var resetAnalyzer = new MusicAnalyzer(48000);
            FeedAnalyzer(resetAnalyzer, MakeClickTrackStereo(3.0, 48000, 120.0), 48000, 480);
            resetAnalyzer.Reset();
            var resetSnapshot = FeedAnalyzer(resetAnalyzer, new float[48000 * 2], 48000, 480);
            C("音乐分析：Reset 后状态清空（再喂静音 → Silent、时间戳更新、BPM 归零）",
                resetSnapshot.Kind == MusicContentKind.Silent && !resetSnapshot.IsPlaying
                && resetSnapshot.Bpm == 0 && resetSnapshot.TimestampMs > 0,
                $"kind={resetSnapshot.Kind} bpm={resetSnapshot.Bpm:0.##}");
        }
        catch (Exception ex)
        {
            C("音乐分析纯逻辑断言运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        // 十二、模型运行时 + 软件渲染器（要 WPF 位图 → 单独 STA 线程）
        // ================================================================
        if (!Directory.Exists(modelDir))
        {
            C("模型运行时 / 软件渲染器（跳过：内置模型目录不存在）", false, modelDir);
            return;
        }

        RunOnSta("Live2D 运行时", check, () =>
        {
            CheckRuntimeAndRenderer(check, modelDir, hasExpectedBounds, expectedBounds);

            // ---- 纹理图集（单独验降采样，用很小的上限省时间） ----
            try
            {
                var texturePaths = Directory.GetFiles(Path.Combine(modelDir, "大肥鱼.4096"), "*.png").OrderBy(p => p, StringComparer.Ordinal).ToArray();
                using var textures = TextureAtlasSet.Load(texturePaths, 64);
                C("纹理图集：5 张全部可解码、降采样到最长边 64（验证 8192 大图的缩放逻辑）",
                    textures.Count == 5
                    && Enumerable.Range(0, textures.Count).All(i =>
                        textures[i].Width > 0 && textures[i].Height > 0
                        && Math.Max(textures[i].Width, textures[i].Height) == 64
                        && textures[i].Pixels.Length == textures[i].Width * textures[i].Height));

                var threw = false;
                try { _ = TextureAtlasSet.Load(new[] { Path.Combine(modelDir, "不存在的纹理.png") }, 64); }
                catch (FileNotFoundException) { threw = true; }
                C("纹理图集：纹理文件缺失时抛 FileNotFoundException", threw);
            }
            catch (Exception ex)
            {
                C("纹理图集加载运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    // ====================================================================
    // 运行时 + 渲染器断言（在 STA 线程里跑）
    // ====================================================================
    private static void CheckRuntimeAndRenderer(Action<string, bool, string> check, string modelDir,
        bool hasExpectedBounds, double[] expectedBounds)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        ModelRuntime? runtime = null;
        try
        {
            var asset = Live2DModelAsset.Discover(modelDir);
            runtime = ModelRuntime.Load(asset, new RuntimeLoadOptions
            {
                TextureMaxSize = 1024,   // 自检用 1024 省内存 / 时间；显示效果与 2048 无关
                IdleMotionName = "IDLE",
            });
            C("ModelRuntime 加载成功（IsLoaded = true、模型名非空）",
                runtime.IsLoaded && !string.IsNullOrWhiteSpace(runtime.ModelName),
                runtime.ModelName);
        }
        catch (Exception ex)
        {
            C("ModelRuntime.Load 抛出异常（模型运行时不可用）", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        if (runtime is null) return;

        try
        {
            // 编译器对 local function / 内插字符串里的可空流分析会丢状态：这里显式拿一个非空局部引用。
            var rt = runtime;

            // 让模型回到安静状态：关掉会自己写参数的系统，后面逐项打开
            void Quiet()
            {
                runtime.IdleEnabled = false;
                runtime.AutoBlink = false;
                runtime.PhysicsEnabled = false;
                runtime.EyeTrackingEnabled = false;
                runtime.StopMotion();
                runtime.ClearEyeTrackingTarget();
                runtime.ClearExpressions(0);
                runtime.ResetParameters();
            }

            void TickFrames(int frames, double secondsPerFrame = 1.0 / 60.0)
            {
                for (var i = 0; i < frames; i++) runtime.Tick(TimeSpan.FromSeconds(secondsPerFrame));
            }

            float Param(string id) => rt.Model.FindParameter(id)?.Value ?? float.NaN;
            bool AllParamsFinite()
                => rt.Model.Parameters.All(p => float.IsFinite(p.Value));

            // ---------------- A. 运行时数据 / 绑定 ----------------
            C("运行时模型对象与渲染器可用（Model / Renderer 非空），物理配置已加载",
                runtime.Model is not null && runtime.Renderer is not null && runtime.HasPhysics);
            C($"运行时列表：动作 {ExpectedMotionCount} 个 / 表情 {ExpectedExpressionCount} 个",
                runtime.Motions.Count == ExpectedMotionCount && runtime.Expressions.Count == ExpectedExpressionCount,
                $"动作={runtime.Motions.Count} 表情={runtime.Expressions.Count}");
            C($"运行时绑定：参数 {ExpectedParameterCount} 行 / 部件 {ExpectedPartCount} 行",
                runtime.Parameters.Count == ExpectedParameterCount && runtime.Parts.Count == ExpectedPartCount,
                $"参数={runtime.Parameters.Count} 部件={runtime.Parts.Count}");
            C("参数绑定带 cdi3 中文名（≥400 行显示名含中文）",
                runtime.Parameters.Count(p => ContainsCjk(p.DisplayName)) >= 400,
                runtime.Parameters.Count(p => ContainsCjk(p.DisplayName)).ToString(CultureInfo.InvariantCulture));
            C("部件绑定带 cdi3 中文名（≥140 行）",
                runtime.Parts.Count(p => ContainsCjk(p.DisplayName)) >= 140,
                runtime.Parts.Count(p => ContainsCjk(p.DisplayName)).ToString(CultureInfo.InvariantCulture));
            C("Display 显示信息：≥21 个参数分组（第 1 组「开关」）、部件分组为空（cdi3 没有 PartGroups）",
                runtime.Display is not null
                && runtime.Display.ParameterGroups.Count >= 21
                && runtime.Display.ParameterGroups[0].Name == "开关"
                && runtime.Display.PartGroups.Count == 0,
                runtime.Display is null ? "(Display 为 null)" : $"参数组={runtime.Display.ParameterGroups.Count} 部件组={runtime.Display.PartGroups.Count}");

            var idleEntry = runtime.Motions.FirstOrDefault(m => m.Name.Equals("IDLE", StringComparison.OrdinalIgnoreCase));
            var escapeEntry = runtime.Motions.FirstOrDefault(m => m.Name == "脱捕");
            C("运行时动作时长与文件一致（IDLE 5.983s / 脱捕 13.15s）且文件都存在",
                idleEntry is not null && escapeEntry is not null
                && Near(idleEntry.Duration, 5.983, 0.01) && Near(escapeEntry.Duration, 13.15, 0.01)
                && runtime.Motions.All(m => File.Exists(m.FilePath) && m.Duration > 0),
                idleEntry is null ? "(没有 IDLE)" : $"IDLE={idleEntry.Duration:0.###}s");
            C("运行时把 IDLE 识别成待机动作（IdleMotion 默认 = IDLE）",
                runtime.IdleMotion is not null
                && runtime.IdleMotion.Name.Equals("IDLE", StringComparison.OrdinalIgnoreCase)
                && idleEntry is { IsIdleCandidate: true },
                runtime.IdleMotion?.Name ?? "(null)");
            var heartEyes = runtime.Expressions.FirstOrDefault(e => e.Name == "爱心眼");
            C("运行时表情列表含「爱心眼」「圈圈眼」且文件都存在",
                heartEyes is not null
                && runtime.Expressions.Any(e => e.Name == "圈圈眼")
                && runtime.Expressions.All(e => File.Exists(e.FilePath)),
                heartEyes?.Name ?? "(没有爱心眼)");

            // ---------------- B. Tick 与动作 ----------------
            Quiet();
            TickFrames(1);
            C("Tick 一帧不抛异常，参数保持有限（509 个参数全 finite）",
                AllParamsFinite(),
                $"tickMs={runtime.LastTickMilliseconds:0.###} updateMs={rt.Model.LastUpdateMilliseconds:0.###}");

            Quiet();
            var shortMotion = runtime.Motions.FirstOrDefault(m => m.Name == "默认手切举手");
            if (shortMotion is not null)
            {
                runtime.PlayMotion(shortMotion, loop: false);
                TickFrames(15);  // 0.25s
                var time1 = runtime.CurrentMotionTime;
                TickFrames(15);  // 再 0.25s
                var time2 = runtime.CurrentMotionTime;
                C("播放动作：CurrentMotion 指向该动作、播放时间随 Tick 前进",
                    ReferenceEquals(runtime.CurrentMotion, shortMotion) && time2 > time1 && time1 > 0,
                    $"{time1:0.###}s → {time2:0.###}s");
                C("动作驱动参数：默认手切举手在 0.5s 时 Movement2 已抬起（>0.2），不再是默认值 0",
                    Param("Movement2") > 0.2f,
                    $"Movement2={Param("Movement2"):0.###}");
                runtime.StopMotion();
                runtime.ResetParameters();
                TickFrames(2);
                C("停止动作 + ResetParameters 后参数回默认（Movement2 ≈ 0）",
                    Math.Abs(Param("Movement2")) < 0.05f,
                    $"Movement2={Param("Movement2"):0.###}");
            }

            // 循环：IDLE 播一个周期以上后时间应该回绕、动作仍挂在 IDLE 上
            Quiet();
            runtime.PlayMotion(idleEntry!, loop: true);
            TickFrames(60, 0.1); // 6 秒 > 5.983s
            C("循环动作：IDLE 播完一个周期后回绕（CurrentMotionTime < Duration、动作仍在播）",
                ReferenceEquals(runtime.CurrentMotion, idleEntry)
                && runtime.CurrentMotionTime >= 0 && runtime.CurrentMotionTime < idleEntry!.Duration + 0.5,
                $"{runtime.CurrentMotionTime:0.###}s / {idleEntry!.Duration:0.###}s");

            // 淡入：IDLE 第一帧 ShockR 曲线起点是 -0.159，刚播放时权重接近 0 → 参数应接近默认 0
            // （运行时对没有 FadeInTime 的动作用 0.3 秒默认淡入，见 MotionPlayer.ResolveFadeIn）
            Quiet();
            runtime.PlayMotion(idleEntry!, loop: true);
            TickFrames(1, 0.016); // 16ms，0.3s 淡入 → 权重 ≈ 0.007
            var earlyShock = Math.Abs(Param("ShockR"));
            TickFrames(80, 0.016); // 再 1.28s，超过淡入时间 → 权重 ≈ 1
            var laterShock = Math.Abs(Param("ShockR"));
            C("动作淡入：刚播放 16ms 时动作参数只占小权重（|ShockR| < 0.05），淡入结束后完全由动作驱动（|ShockR| > 0.1）",
                earlyShock < 0.05f && laterShock > 0.1f,
                $"16ms={earlyShock:0.###} 1.3s={laterShock:0.###}");
            runtime.StopMotion();
            runtime.ResetParameters();
            TickFrames(2);

            // ---------------- C. 表情 ----------------
            if (heartEyes is not null)
            {
                Quiet();
                runtime.ApplyExpression(heartEyes, fadeSeconds: 0.1);
                TickFrames(20, 0.016); // 320ms > 淡入
                C("施加「爱心眼」后 key4 淡入到 1（Add 混合生效）",
                    Math.Abs(Param("key4") - 1f) < 0.1f,
                    $"key4={Param("key4"):0.###}");
                C("「爱心眼」不影响其它表情参数（key3 仍是 0）",
                    Math.Abs(Param("key3")) < 0.05f, $"key3={Param("key3"):0.###}");
                runtime.ClearExpressions(fadeSeconds: 0.1);
                TickFrames(20, 0.016);
                C("清除表情后 key4 淡出回 0", Math.Abs(Param("key4")) < 0.05f, $"key4={Param("key4"):0.###}");

                // UI 是单选：切换表情 = 先清掉旧的、再施加新的（两边各自淡出 / 淡入）
                var blush = runtime.Expressions.FirstOrDefault(e => e.Name == "脸红");
                if (blush is not null)
                {
                    runtime.ClearExpressions(0.05);
                    runtime.ApplyExpression(blush, 0.05);
                    TickFrames(30, 0.016);
                    C("表情切换（单选 + 交叉淡入淡出）：切到「脸红」后 key7 = 1，旧表情的 key4 回默认 0",
                        Math.Abs(Param("key7") - 1f) < 0.1f && Math.Abs(Param("key4")) < 0.1f,
                        $"key7={Param("key7"):0.###} key4={Param("key4"):0.###}");
                    runtime.ClearExpressions(0);
                    TickFrames(2);
                    C("清除切换后的表情：key7 / key4 都回默认",
                        Math.Abs(Param("key7")) < 0.1f && Math.Abs(Param("key4")) < 0.1f);

                    // 扩展能力（实现是「叠加」超集）：调用方不先清、直接连续施加两个表情时两个都生效。
                    // UI 是单选（切换先 Clear），这里只保护 API 层能力，将来若改回官方单激活语义可删。
                    runtime.ApplyExpression(heartEyes, 0.03);
                    runtime.ApplyExpression(blush, 0.03);
                    TickFrames(30, 0.016);
                    C("表情实现是叠加超集：调用方连续施加两个表情（不先清）时两个都生效（key4 / key7 = 1）",
                        Math.Abs(Param("key4") - 1f) < 0.15f && Math.Abs(Param("key7") - 1f) < 0.15f,
                        $"key4={Param("key4"):0.###} key7={Param("key7"):0.###}");
                    runtime.ClearExpressions(0);
                    TickFrames(2);
                }

                var circleEyes = runtime.Expressions.FirstOrDefault(e => e.Name == "圈圈眼");
                if (circleEyes is not null)
                {
                    runtime.ClearExpressions(0);
                    TickFrames(2);
                    runtime.ApplyExpression(circleEyes, 0.05);
                    TickFrames(20, 0.016);
                    C("多参数表情：圈圈眼把 key3 置 1、key4/5/6 置 0（表情内多条参数一起生效）",
                        Math.Abs(Param("key3") - 1f) < 0.1f
                        && Math.Abs(Param("key4")) < 0.1f
                        && Math.Abs(Param("key5")) < 0.1f
                        && Math.Abs(Param("key6")) < 0.1f,
                        $"key3={Param("key3"):0.###} key4={Param("key4"):0.###} key5={Param("key5"):0.###} key6={Param("key6"):0.###}");
                    runtime.ClearExpressions(0);
                    TickFrames(2);
                    C("清除多参数表情后全部参数回默认（圈圈眼 → key3/4/5/6 ≈ 0）",
                        Math.Abs(Param("key3")) < 0.1f && Math.Abs(Param("key4")) < 0.1f
                        && Math.Abs(Param("key5")) < 0.1f && Math.Abs(Param("key6")) < 0.1f);
                }
            }

            // ---------------- D. 眼睛跟随 ----------------
            Quiet();
            runtime.EyeTrackingEnabled = true;
            var canvasCenterX = rt.Model.CanvasOriginX;
            var canvasCenterY = rt.Model.CanvasOriginY;
            runtime.SetEyeTrackingTarget(canvasCenterX, canvasCenterY);
            TickFrames(90); // 1.5s，足够收敛
            C("眼睛跟随：目标在画布中心 → ParamAngleX / ParamEyeBallX ≈ 0",
                Math.Abs(Param("ParamAngleX")) < 1f && Math.Abs(Param("ParamEyeBallX")) < 0.05f,
                $"AngleX={Param("ParamAngleX"):0.###} EyeBallX={Param("ParamEyeBallX"):0.###}");

            runtime.ClearEyeTrackingTarget();
            runtime.SetEyeTrackingTarget(rt.Model.CanvasWidthPixels * 0.85f, canvasCenterY);
            TickFrames(1);
            var firstAngle = Param("ParamAngleX");
            TickFrames(9);
            var tenthAngle = Param("ParamAngleX");
            TickFrames(110);
            var finalAngle = Param("ParamAngleX");
            C("眼睛跟随：目标在画布右侧 → ParamAngleX / ParamEyeBallX 为正且在参数范围内",
                finalAngle > 0.5f && Param("ParamEyeBallX") > 0.05f
                && finalAngle <= 30.01f && Param("ParamEyeBallX") <= 1.01f,
                $"AngleX={finalAngle:0.###} EyeBallX={Param("ParamEyeBallX"):0.###}");
            C("眼睛跟随平滑：第 10 帧比第 1 帧更接近稳定值（平滑插值在收敛）",
                Math.Abs(finalAngle - tenthAngle) < Math.Abs(finalAngle - firstAngle)
                && Math.Abs(tenthAngle - firstAngle) > 1e-4f,
                $"1帧={firstAngle:0.###} 10帧={tenthAngle:0.###} 稳定={finalAngle:0.###}");
            C("眼睛跟随参数不越界（AngleX ≤ 30、EyeBallX ≤ 1、所有参数有限）",
                AllParamsFinite()
                && Math.Abs(Param("ParamAngleY")) <= 30.01f
                && Math.Abs(Param("ParamEyeBallY")) <= 1.01f);

            runtime.ClearEyeTrackingTarget();
            TickFrames(120); // 2 秒平滑回中
            C("ClearEyeTrackingTarget 后回默认朝向（AngleX / EyeBallX ≈ 0）",
                Math.Abs(Param("ParamAngleX")) < 1f && Math.Abs(Param("ParamEyeBallX")) < 0.05f,
                $"AngleX={Param("ParamAngleX"):0.###} EyeBallX={Param("ParamEyeBallX"):0.###}");

            // ---------------- D2. 鼠标跟随手感（横向 / 抬头 / 低头增益 + 纵向偏移 + 强度 1.5） ----------------
            Quiet();
            runtime.PhysicsEnabled = false;
            runtime.IdleEnabled = false;
            runtime.AutoBlink = false;
            runtime.EyeTrackingEnabled = true;
            runtime.EyeTrackingSmoothing = 0; // 0 = 立即到位，单帧就能断言映射（避免多帧收敛干扰）
            runtime.ClearEyeTrackingTarget();

            // 读一帧：把目标摆到指定画布坐标 → Tick 一帧 → 读参数
            (float AngleX, float AngleY, float EyeX, float EyeY) Aim(float canvasX, float canvasY)
            {
                runtime.SetEyeTrackingTarget(canvasX, canvasY);
                runtime.Tick(TimeSpan.FromSeconds(1.0 / 60.0));
                return (Param("ParamAngleX"), Param("ParamAngleY"), Param("ParamEyeBallX"), Param("ParamEyeBallY"));
            }

            void ResetEyeGains()
            {
                runtime.EyeTrackingHorizontalGain = 1.0;
                runtime.EyeTrackingUpGain = 1.0;
                runtime.EyeTrackingDownGain = 1.0;
                runtime.EyeTrackingVerticalOffset = 0;
                runtime.EyeTrackingHeadStrength = 1.0;
                runtime.EyeTrackingEyeStrength = 1.0;
                runtime.EyeTrackingBodyStrength = 0.5;
            }

            ResetEyeGains();
            C("眼控手感：运行时默认增益 1 / 1 / 1、纵向偏移 0、头部/眼球强度 1",
                Near(runtime.EyeTrackingHorizontalGain, 1.0, 1e-9)
                && Near(runtime.EyeTrackingUpGain, 1.0, 1e-9)
                && Near(runtime.EyeTrackingDownGain, 1.0, 1e-9)
                && Near(runtime.EyeTrackingVerticalOffset, 0.0, 1e-9)
                && Near(runtime.EyeTrackingHeadStrength, 1.0, 1e-9)
                && Near(runtime.EyeTrackingEyeStrength, 1.0, 1e-9));

            // 横向：目标在中心右侧 0.5 个「中心距离」→ 默认 15°，横向灵敏度 2 → 满 30°
            var horizontalDefault = Aim(canvasCenterX * 1.5f, canvasCenterY);
            runtime.EyeTrackingHorizontalGain = 2.0;
            var horizontalGain2 = Aim(canvasCenterX * 1.5f, canvasCenterY);
            C("眼控手感：横向灵敏度 ×2 → 半程偏移从 15° 变满 30°（眼球同步到 1）",
                Near(horizontalDefault.AngleX, 15, 0.6) && Near(horizontalGain2.AngleX, 30, 0.6)
                && Near(horizontalDefault.EyeX, 0.5, 0.01) && Near(horizontalGain2.EyeX, 1, 0.01),
                $"默认={horizontalDefault.AngleX:0.##}°/{horizontalDefault.EyeX:0.##}，×2={horizontalGain2.AngleX:0.##}°/{horizontalGain2.EyeX:0.##}");
            C("眼控手感：横向灵敏度夹在 0.3~3（超界被夹：99 → 满 30°，-5 → 0.3 倍只转 4.5°）",
                Near(AimWithGain(99).AngleX, 30, 0.6) && Near(AimWithGain(-5).AngleX, 4.5, 0.6),
                $"99→{AimWithGain(99).AngleX:0.##}°  -5→{AimWithGain(-5).AngleX:0.##}°");

            (float AngleX, float AngleY, float EyeX, float EyeY) AimWithGain(double gain)
            {
                runtime.EyeTrackingHorizontalGain = gain;
                return Aim(canvasCenterX * 1.5f, canvasCenterY);
            }

            // 低头：目标在中心下方 0.25 个「中心距离」→ 默认 -7.5°；低头灵敏度 2 → -15°
            ResetEyeGains();
            var downDefault = Aim(canvasCenterX, canvasCenterY * 1.25f);
            runtime.EyeTrackingDownGain = 2.0;
            var downGain2 = Aim(canvasCenterX, canvasCenterY * 1.25f);
            C("眼控手感：低头灵敏度 ×2 → 向下角度约为默认两倍（-7.5° → -15°，眼球 -0.25 → -0.5）",
                Near(downDefault.AngleY, -7.5, 0.6) && Near(downGain2.AngleY, -15, 0.6)
                && Near(downDefault.EyeY, -0.25, 0.01) && Near(downGain2.EyeY, -0.5, 0.01),
                $"默认={downDefault.AngleY:0.##}°  ×2={downGain2.AngleY:0.##}°");

            // 抬头：目标在中心上方 0.25 → 默认 +7.5°；抬头灵敏度 2 → +15°（且不吃低头的增益）
            ResetEyeGains();
            var upDefault = Aim(canvasCenterX, canvasCenterY * 0.75f);
            runtime.EyeTrackingUpGain = 2.0;
            var upGain2 = Aim(canvasCenterX, canvasCenterY * 0.75f);
            C("眼控手感：抬头灵敏度只作用于上方（向下时仍走低头增益）",
                Near(upDefault.AngleY, 7.5, 0.6) && Near(upGain2.AngleY, 15, 0.6)
                && Near(Aim(canvasCenterX, canvasCenterY * 1.25f).AngleY, -7.5, 0.6),
                $"抬头默认={upDefault.AngleY:0.##}° ×2={upGain2.AngleY:0.##}°；下方不受 UpGain 影响");

            // 纵向偏移：目标在中心上方 0.2 → 默认 +6°；偏移 +0.2 后正好抵消成 0°；目标正中心 + 偏移 +0.2 → -6°
            ResetEyeGains();
            var offsetNone = Aim(canvasCenterX, canvasCenterY * 0.8f);
            runtime.EyeTrackingVerticalOffset = 0.2;
            var offsetCancel = Aim(canvasCenterX, canvasCenterY * 0.8f);
            var offsetCenter = Aim(canvasCenterX, canvasCenterY);
            C("眼控手感：纵向偏移改变中性点（+0.2 时上方 0.2 恰好抵消→0°，中心目标→向下 6°）",
                Near(offsetNone.AngleY, 6, 0.6) && Near(offsetCancel.AngleY, 0, 0.6)
                && Near(offsetCenter.AngleY, -6, 0.6),
                $"无偏移={offsetNone.AngleY:0.##}° 抵消={offsetCancel.AngleY:0.##}° 中心+偏移={offsetCenter.AngleY:0.##}°");

            // 强度 1.5：半程目标下头部 7.5°→11.25°、眼球 0.25→0.375（先放大后由参数范围兜底）
            ResetEyeGains();
            var strength1 = Aim(canvasCenterX * 1.25f, canvasCenterY);
            runtime.EyeTrackingHeadStrength = 1.5;
            runtime.EyeTrackingEyeStrength = 1.5;
            var strength15 = Aim(canvasCenterX * 1.25f, canvasCenterY);
            C("眼控手感：强度 1.5 > 强度 1 的输出（头 11.25° > 7.5°，眼球 0.375 > 0.25，仍在参数范围内）",
                Near(strength1.AngleX, 7.5, 0.6) && Near(strength15.AngleX, 11.25, 0.6)
                && Near(strength1.EyeX, 0.25, 0.01) && Near(strength15.EyeX, 0.375, 0.01)
                && Math.Abs(strength15.AngleX) <= 30.01f && Math.Abs(strength15.EyeX) <= 1.01f,
                $"强度1={strength1.AngleX:0.##}°/{strength1.EyeX:0.##} 强度1.5={strength15.AngleX:0.##}°/{strength15.EyeX:0.##}");

            ResetEyeGains();
            runtime.EyeTrackingSmoothing = 0.35;
            runtime.ClearEyeTrackingTarget();
            TickFrames(2);

            // ---------------- E. 物理 ----------------
            Quiet();
            runtime.PhysicsEnabled = true;
            runtime.EyeTrackingEnabled = true;
            runtime.SetEyeTrackingTarget(rt.Model.CanvasWidthPixels * 0.85f, canvasCenterY);
            var hairIds = new[] { "FrontHairX1", "FrontHairX2", "SideHairX1", "LateHairX1" };
            var hairDefaults = hairIds.ToDictionary(id => id, id => (double)(rt.Model.FindParameter(id)?.DefaultValue ?? 0));
            TickFrames(90); // 1.5s：头转过去后头发物理应该被带动
            var hairChanged = hairIds.Count(id => Math.Abs(Param(id) - hairDefaults[id]) > 1e-3);
            C("物理：头部转动后头发物理输出被驱动（4 个毛发参数至少 1 个偏离默认值）",
                hairChanged >= 1,
                string.Join(", ", hairIds.Select(id => $"{id}={Param(id):0.###}")));
            C("物理：全部参数保持有限（无 NaN / Infinity，不会把模型画炸）", AllParamsFinite());

            var physicsOffDefault = hairDefaults["FrontHairX1"];
            runtime.PhysicsEnabled = false;
            runtime.ResetParameters();
            runtime.SetEyeTrackingTarget(rt.Model.CanvasWidthPixels * 0.85f, canvasCenterY);
            TickFrames(60);
            C("关掉物理后毛发输出不再被驱动（FrontHairX1 回到默认值）",
                Math.Abs(Param("FrontHairX1") - physicsOffDefault) < 1e-2f,
                $"FrontHairX1={Param("FrontHairX1"):0.###} default={physicsOffDefault:0.###}");

            // 待机 / 眨眼 / 物理 / 眼睛跟随全开跑一段：不崩、参数有限
            Quiet();
            runtime.IdleEnabled = true;
            runtime.AutoBlink = true;
            runtime.PhysicsEnabled = true;
            runtime.EyeTrackingEnabled = true;
            runtime.SetEyeTrackingTarget(rt.Model.CanvasWidthPixels * 0.7f, canvasCenterY);
            TickFrames(120);
            C("多系统同时开启（待机 + 眨眼 + 物理 + 眼睛跟随）连跑 2 秒参数全部有限",
                AllParamsFinite() && runtime.LastTickMilliseconds >= 0,
                $"tickMs={runtime.LastTickMilliseconds:0.###}");

            // ---------------- F. 软件渲染器 ----------------
            // 用「默认参数姿态」渲染：和上面理论包围盒（也是默认姿态）对得上
            Quiet();
            TickFrames(2);

            var renderOptions = new RenderOptions
            {
                Width = 320,
                Height = 640,
                FitCanvas = true,
                TransparentBackground = true,
            };
            runtime.RenderFrame(renderOptions);
            var bitmap = rt.Renderer.Bitmap;
            C("渲染器：输出位图尺寸跟随 RenderOptions（320×640）",
                bitmap is not null && bitmap.PixelWidth == 320 && bitmap.PixelHeight == 640,
                bitmap is null ? "(Bitmap 为 null)" : $"{bitmap.PixelWidth}×{bitmap.PixelHeight}");
            if (bitmap is not null)
            {
                var pixels = ReadPixels(bitmap);
                var alpha = GetAlphaStats(pixels, 320, 640);
                C("渲染器：320×640 一帧画出角色（不透明像素 > 3000，不是黑块 / 空白）",
                    alpha.OpaquePixels > 3000,
                    $"不透明像素={alpha.OpaquePixels} 半透明={alpha.SemiPixels}");
                C("渲染器：透明背景四角 alpha = 0（桌面宠物不会有白底 / 黑底）",
                    alpha.CornerAlphas.All(a => a == 0),
                    string.Join(",", alpha.CornerAlphas));
                var stats = rt.Renderer.LastStats;
                C("渲染统计：绘制了网格 / 三角形 / 像素填充，且蒙版 pass > 0",
                    stats.DrawablesDrawn > 300 && stats.TrianglesDrawn > 10000
                    && stats.PixelsFilled > 10000 && stats.MaskPasses > 0,
                    $"网格={stats.DrawablesDrawn} 三角形={stats.TrianglesDrawn} 像素={stats.PixelsFilled} 蒙版={stats.MaskPasses}");

                if (hasExpectedBounds)
                {
                    var bbox = OpaqueBounds(pixels, 320, 640, alphaThreshold: 8);
                    var theoryWidth = expectedBounds[2] - expectedBounds[0];
                    var theoryHeight = expectedBounds[3] - expectedBounds[1];
                    C("渲染器：FitCanvas 变换正确（实际画面落在理论画布范围内、竖向覆盖 ≥85%、水平居中）",
                        bbox is not null
                        && bbox.Value.MinX >= expectedBounds[0] - 3 && bbox.Value.MaxX <= expectedBounds[2] + 3
                        && bbox.Value.MinY >= expectedBounds[1] - 3 && bbox.Value.MaxY <= expectedBounds[3] + 3
                        && bbox.Value.MaxY - bbox.Value.MinY >= theoryHeight * 0.85
                        && Math.Abs((bbox.Value.MinX + bbox.Value.MaxX) / 2 - (expectedBounds[0] + expectedBounds[2]) / 2) < 25,
                        bbox is null
                            ? "(没有不透明像素)"
                            : $"实际=({bbox.Value.MinX},{bbox.Value.MinY})-({bbox.Value.MaxX},{bbox.Value.MaxY}) 理论画布=({expectedBounds[0]:0.#},{expectedBounds[1]:0.#})-({expectedBounds[2]:0.#},{expectedBounds[3]:0.#}) 宽{theoryWidth:0.#}×高{theoryHeight:0.#}");

                    // 放大一倍（640×1280）时角色包围盒也应约放大一倍（等比适配，不是被拉伸）
                    var bigOptions = new RenderOptions { Width = 640, Height = 1280, FitCanvas = true, TransparentBackground = true };
                    runtime.RenderFrame(bigOptions);
                    var bigBitmap = rt.Renderer.Bitmap;
                    if (bigBitmap is not null)
                    {
                        var bigBounds = OpaqueBounds(ReadPixels(bigBitmap), 640, 1280, alphaThreshold: 8);
                        C("渲染器：FitCanvas 等比（640×1280 的角色包围盒约为 320×640 的 2 倍，不变形）",
                            bbox is not null && bigBounds is not null
                            && Near(bigBounds.Value.MaxX - bigBounds.Value.MinX, (bbox.Value.MaxX - bbox.Value.MinX) * 2, 8)
                            && Near(bigBounds.Value.MaxY - bigBounds.Value.MinY, (bbox.Value.MaxY - bbox.Value.MinY) * 2, 8),
                            bigBounds is null ? "(大图没有不透明像素)" : $"小={bbox?.MaxX - bbox?.MinX:0.#}×{bbox?.MaxY - bbox?.MinY:0.#} 大={bigBounds.Value.MaxX - bigBounds.Value.MinX:0.#}×{bigBounds.Value.MaxY - bigBounds.Value.MinY:0.#}");
                    }
                }

                // 不透明背景：角落应该是 BackgroundColor
                var solidOptions = new RenderOptions
                {
                    Width = 320,
                    Height = 640,
                    FitCanvas = true,
                    TransparentBackground = false,
                    BackgroundColor = 0xFF1B1E28,
                };
                runtime.RenderFrame(solidOptions);
                var solidBitmap = rt.Renderer.Bitmap;
                if (solidBitmap is not null)
                {
                    var solidPixels = ReadPixels(solidBitmap);
                    // Pbgra32 字节序是 B,G,R,A；拼成 0xAARRGGBB 方便和 RenderOptions.BackgroundColor 比
                    var corner = (uint)(solidPixels[0] | solidPixels[1] << 8 | solidPixels[2] << 16 | solidPixels[3] << 24);
                    C("渲染器：不透明背景色生效（左上角 = 0xFF1B1E28）",
                        (corner >> 24) == 0xFF && ((corner >> 16) & 0xFF) == 0x1B
                        && ((corner >> 8) & 0xFF) == 0x1E && (corner & 0xFF) == 0x28,
                        $"0x{corner:X8}");
                }
            }

            // 渲染耗时（宽松哨兵：先渲染 2 帧预热，再取 3 帧最小值）
            var warmOptions = new RenderOptions { Width = 320, Height = 640, FitCanvas = true, TransparentBackground = true };
            runtime.RenderFrame(warmOptions);
            runtime.RenderFrame(warmOptions);
            var best = double.MaxValue;
            for (var i = 0; i < 3; i++)
            {
                runtime.RenderFrame(warmOptions);
                best = Math.Min(best, rt.Renderer.LastStats.RenderMilliseconds);
            }
            C("渲染器：320×640 一帧渲染耗时 < 200ms（宽松性能哨兵，只防渲染退化到不可用）",
                best < 200,
                $"{best:0.###}ms / 帧（Core update 另算）");

            // ---------------- G. 摄像头动捕集成（自写假数据源，绝不打开摄像头） ----------------
            Quiet();
            runtime.PhysicsEnabled = false;   // 只看动捕写了什么，不让物理再改同一批参数
            var fakeMocap = new FakeMocapProvider();
            runtime.Mocap = fakeMocap;
            runtime.MocapEnabled = true;
            runtime.MocapHeadEnabled = true;
            runtime.MocapBlinkEnabled = true;
            runtime.MocapMouthEnabled = true;
            runtime.EyeTrackingEnabled = false;
            fakeMocap.Latest = MakeFakeTracking(yaw: 15, pitch: -10, eyeOpen: 0.2, mouth: 0.8);
            TickFrames(1);
            C("动捕集成：假数据（Yaw=15 / Pitch=-10 / 眼睛 0.2 / 嘴 0.8）→ 模型参数被驱动",
                Math.Abs(Param("ParamAngleX") - 15f) < 1e-3f
                && Math.Abs(Param("ParamAngleY") + 10f) < 1e-3f
                && Math.Abs(Param("ParamEyeLOpen") - 0.2f) < 1e-3f
                && Math.Abs(Param("ParamMouthOpenY") - 0.8f) < 1e-3f,
                $"AngleX={Param("ParamAngleX"):0.###} AngleY={Param("ParamAngleY"):0.###} EyeL={Param("ParamEyeLOpen"):0.###} Mouth={Param("ParamMouthOpenY"):0.###}");
            C("动捕集成：头部动捕时身体温和跟随（ParamBodyAngleX = Yaw × 0.35）",
                Math.Abs(Param("ParamBodyAngleX") - 15f * 0.35f) < 1e-3f
                && Math.Abs(Param("PositionX") - 15f * 0.35f) < 1e-3f,
                $"bodyX={Param("ParamBodyAngleX"):0.###}");
            C("动捕集成：HasFace=true → MocapHasFace=true、状态文案「正在跟踪」",
                runtime.MocapHasFace && runtime.MocapStatusText == "正在跟踪", runtime.MocapStatusText);

            // 有脸时鼠标眼控让位（不会两边抢同一批参数）
            runtime.EyeTrackingEnabled = true;
            runtime.SetEyeTrackingTarget(rt.Model.CanvasWidthPixels * 0.85f, rt.Model.CanvasOriginY);
            TickFrames(3);
            C("动捕优先级：有脸时动捕接管头部（鼠标眼控不会覆盖动捕角度）",
                Math.Abs(Param("ParamAngleX") - 15f) < 0.5f, $"AngleX={Param("ParamAngleX"):0.###}");

            // 没脸 → 回落到鼠标眼控 / 自动眨眼，不崩
            fakeMocap.Latest = MocapTrackingResult.NoFace(Environment.TickCount64);
            TickFrames(5);
            C("动捕集成：没脸 → MocapHasFace=false、状态「未检测到人脸」、参数有限（回落到鼠标眼控）",
                !runtime.MocapHasFace && runtime.MocapStatusText == "未检测到人脸" && AllParamsFinite(),
                runtime.MocapStatusText);

            // 过期帧（>600ms）不再驱动（相机卡住时角度不会僵住）
            fakeMocap.Latest = new MocapTrackingResult
            {
                HasFace = true, Yaw = 25, Pitch = 0,
                EyeOpenLeft = 0, EyeOpenRight = 0, MouthOpen = 1,
                TimestampMs = Environment.TickCount64 - 2000,
            };
            TickFrames(1);
            C("动捕集成：过期帧（>600ms）不再驱动模型（相机卡住时不会僵在旧角度）",
                !runtime.MocapHasFace && runtime.MocapStatusText == "未检测到人脸",
                runtime.MocapStatusText);

            // MocapEnabled=false → 完全不写参数（隐私默认关）
            runtime.MocapEnabled = false;
            Quiet();
            runtime.PhysicsEnabled = false;
            fakeMocap.Latest = MakeFakeTracking(yaw: 15, pitch: -10, eyeOpen: 0.2, mouth: 0.8);
            TickFrames(3);
            C("动捕集成：MocapEnabled=false 时动捕完全不写参数（默认隐私关闭）",
                !runtime.MocapHasFace && runtime.MocapStatusText == "无数据"
                && Math.Abs(Param("ParamAngleX")) < 1e-3f && Math.Abs(Param("ParamMouthOpenY")) < 1e-3f,
                $"status={runtime.MocapStatusText} AngleX={Param("ParamAngleX"):0.###}");

            // 单项开关：关头部后只保留眨眼 / 张嘴
            runtime.MocapEnabled = true;
            runtime.MocapHeadEnabled = false;
            fakeMocap.Latest = MakeFakeTracking(yaw: 15, pitch: -10, eyeOpen: 0.2, mouth: 0.8);
            TickFrames(2);
            C("动捕集成：关掉「头部转动」后只驱动眨眼 / 张嘴（头部角度回默认）",
                Math.Abs(Param("ParamAngleX")) < 1e-3f
                && Math.Abs(Param("ParamEyeLOpen") - 0.2f) < 1e-3f
                && Math.Abs(Param("ParamMouthOpenY") - 0.8f) < 1e-3f,
                $"AngleX={Param("ParamAngleX"):0.###} EyeL={Param("ParamEyeLOpen"):0.###}");
            runtime.MocapHeadEnabled = true;
            runtime.MocapEnabled = false;
            runtime.Mocap = null;

            // ---------------- H. 身体晃动测试集成 ----------------
            Quiet();
            runtime.PhysicsEnabled = false;
            runtime.SwayEnabled = true;
            runtime.SwayMode = BodySwayController.ModeLeftRight;
            runtime.SwayAmplitudeDegrees = 10;
            runtime.SwaySpeedHz = 0.5;
            runtime.SwayHeadFollow = 0.35;
            runtime.SwayIncludeHips = true;
            runtime.Sway?.Reset();
            var bodySamples = new List<float>();
            var bodyTriples = new List<(float BodyX, float PositionX, float HeadX)>();
            for (var i = 0; i < 8; i++)
            {
                runtime.Tick(TimeSpan.FromSeconds(0.125)); // 8 帧 = 1 秒 = 半个周期（0.5Hz）
                bodySamples.Add(Param("ParamBodyAngleX"));
                bodyTriples.Add((Param("ParamBodyAngleX"), Param("PositionX"), Param("ParamAngleX")));
            }
            C("身体晃动（左右摇）：ParamBodyAngleX 随时间变化且在 ±幅度内",
                bodySamples.Any(v => Math.Abs(v) > 1f) && bodySamples.All(v => Math.Abs(v) <= 10.01f)
                && bodySamples.Distinct().Count() >= 3,
                string.Join(",", bodySamples.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture))));
            var midSway = bodyTriples.OrderByDescending(sample => Math.Abs(sample.BodyX)).First();
            C("身体晃动：物理输入 PositionX 与身体主轴同向、头部 ParamAngleX 反向补偿（HeadFollow×0.9）",
                Math.Abs(midSway.BodyX) > 2f
                && Math.Abs(midSway.PositionX - midSway.BodyX) < 1e-3f
                && Math.Abs(midSway.HeadX + midSway.BodyX * 0.35f * 0.9f) < 0.5f,
                $"bodyX={midSway.BodyX:0.##} positionX={midSway.PositionX:0.##} paramAngleX={midSway.HeadX:0.##}");
            C("身体晃动：执行过的参数都有限（无 NaN / Infinity），且包含腰腿参数",
                runtime.Sway is not null && runtime.Sway.LastApplied.Count > 0
                && runtime.Sway.LastApplied.All(a => float.IsFinite(a.Value))
                && runtime.Sway.LastApplied.Any(a => a.Id == "HipAngleX" || a.Id == "ParamHipAngleZ"),
                $"写入 {runtime.Sway?.LastApplied.Count ?? 0} 个参数");

            // HeadFollow=0 → 不写头部参数（不干扰鼠标 / 动捕眼控）
            runtime.SwayHeadFollow = 0;
            runtime.Sway?.Reset();
            TickFrames(3);
            C("身体晃动：头部跟随=0 时不写头部参数（ParamAngleX 保持默认，不干扰鼠标 / 动捕）",
                Math.Abs(Param("ParamAngleX")) < 1e-3f
                && runtime.Sway is not null && runtime.Sway.LastApplied.All(a => a.Id != "ParamAngleX"),
                $"AngleX={Param("ParamAngleX"):0.###}");

            // 5 种模式都不抛异常、值有限
            var modeIssues = new List<string>();
            foreach (var mode in BodySwayController.Modes)
            {
                try
                {
                    runtime.SwayMode = mode;
                    runtime.ResetParameters();
                    TickFrames(4);
                    var applied = runtime.Sway?.LastApplied;
                    if (applied is null || applied.Count == 0 || applied.Any(a => !float.IsFinite(a.Value)))
                        modeIssues.Add(mode + "(无写入或非有限)");
                }
                catch (Exception ex)
                {
                    modeIssues.Add($"{mode}:{ex.GetType().Name}");
                }
            }

            C("身体晃动：5 种模式（左右摇 / 前后摇 / 扭腰 / 椭圆 / 自然晃）都不抛异常且写入有限值",
                modeIssues.Count == 0, string.Join("；", modeIssues));
            C("身体晃动：模式名列表 5 种、默认「左右摇」在列表里（UI 下拉口径一致）",
                BodySwayController.Modes.Count == 5 && BodySwayController.Modes.Contains("左右摇")
                && BodySwayController.Modes.Contains("自然晃"));

            // 椭圆模式 X / Z 相位差 ≈ 90°（直接用控制器按精确步长推进）
            Quiet();
            runtime.PhysicsEnabled = false;
            var swayDirect = runtime.Sway!;
            swayDirect.Mode = BodySwayController.ModeEllipse;
            swayDirect.AmplitudeDegrees = 10;
            swayDirect.SpeedHz = 0.5;
            swayDirect.HeadFollow = 0;
            swayDirect.IncludeHips = false;
            swayDirect.Reset();
            swayDirect.Apply(rt.Model, 0.5);  // progress = 0.25 周期 → X 最大、Z ≈ 0
            var ellipseX1 = Param("ParamBodyAngleX");
            var ellipseZ1 = Param("ParamBodyAngleZ");
            swayDirect.Apply(rt.Model, 0.5);  // progress = 0.5 周期 → X ≈ 0、Z 反向最大
            var ellipseX2 = Param("ParamBodyAngleX");
            var ellipseZ2 = Param("ParamBodyAngleZ");
            C("身体晃动（椭圆）：X / Z 相位差约 90°（X 最大时 Z≈0，半周期后 X≈0、Z 反向最大）",
                Math.Abs(ellipseX1 - 10f) < 0.2f && Math.Abs(ellipseZ1) < 0.2f
                && Math.Abs(ellipseX2) < 0.2f && Math.Abs(ellipseZ2 + 6f) < 0.2f,
                $"X1={ellipseX1:0.##} Z1={ellipseZ1:0.##} X2={ellipseX2:0.##} Z2={ellipseZ2:0.##}");

            // 「摇一摇」限时演示：倒计时结束自动停
            Quiet();
            runtime.PhysicsEnabled = false;
            runtime.SwayEnabled = false;
            runtime.StartSwayDemo(0.4);
            TickFrames(20, 0.05); // 1 秒 > 0.4s
            C("身体晃动：「摇一摇」限时演示倒计时能结束（SwayDemoRemainingSeconds 归 0）",
                runtime.SwayDemoRemainingSeconds == 0,
                $"{runtime.SwayDemoRemainingSeconds:0.###}s");

            // 关闭晃动 → 不再写参数（前后值不变）
            Quiet();
            runtime.PhysicsEnabled = false;
            runtime.SwayEnabled = false;
            runtime.StopSwayDemo();
            runtime.Sway?.Reset();
            runtime.ResetParameters();
            var bodyDefaults = new[] { "ParamBodyAngleX", "ParamBodyAngleZ", "ParamAngleX" }.Select(Param).ToArray();
            TickFrames(3);
            C("身体晃动：关闭后不再写参数（身体 / 头部保持默认值，Sway.LastApplied 清空）",
                runtime.Sway is not null && runtime.Sway.LastApplied.Count == 0
                && new[] { "ParamBodyAngleX", "ParamBodyAngleZ", "ParamAngleX" }.Select(Param)
                    .Select((value, index) => Math.Abs(value - bodyDefaults[index]) < 1e-6f)
                    .All(ok => ok));

            // ---------------- I. 受管参数动态化（关掉的系统不再每帧复位 → 面部测试手调值能保留） ----------------
            Quiet();
            runtime.PhysicsEnabled = false;
            runtime.IdleEnabled = false;
            runtime.AutoBlink = false;
            runtime.EyeTrackingEnabled = false;
            runtime.MocapEnabled = false;
            runtime.Mocap = null;
            runtime.SwayEnabled = false;
            runtime.StopSwayDemo();
            runtime.StopMotion();
            runtime.ClearExpressions(0);
            TickFrames(2); // 让「开→关」过渡复位走完

            var mouthParam = rt.Model.FindParameter("ParamMouthOpenY");
            var tongueOutParam = rt.Model.FindParameter("TongueOut");
            var eyeballYParam = rt.Model.FindParameter("ParamEyeBallY");
            if (mouthParam is not null) mouthParam.Value = 0.8f;
            if (tongueOutParam is not null) tongueOutParam.Value = 1f;
            if (eyeballYParam is not null) eyeballYParam.Value = -1f;
            TickFrames(30);
            C("受管参数动态化：动捕/眼控/物理/待机/晃动全关后，手调面部参数 Tick 30 帧仍保留（嘴 0.8 / 舌 1 / 眼珠 -1）",
                Near(Param("ParamMouthOpenY"), 0.8, 1e-3) && Near(Param("TongueOut"), 1, 1e-3)
                && Near(Param("ParamEyeBallY"), -1, 1e-3),
                $"嘴={Param("ParamMouthOpenY"):0.###} 舌={Param("TongueOut"):0.###} 眼珠={Param("ParamEyeBallY"):0.###}");

            // 打开动捕（假 provider 有脸）→ 嘴参数被每帧驱动覆盖
            runtime.Mocap = fakeMocap;
            runtime.MocapEnabled = true;
            fakeMocap.Latest = MakeFakeTracking(yaw: 0, pitch: 0, eyeOpen: 0.9, mouth: 0.25);
            TickFrames(2);
            C("受管参数动态化：打开动捕后嘴参数被每帧驱动覆盖（手调 0.8 → 动捕 0.25）",
                Near(Param("ParamMouthOpenY"), 0.25, 1e-2),
                $"嘴={Param("ParamMouthOpenY"):0.###}");

            // 关闭动捕 → 过渡帧复位一次（回到默认），之后手动值可保留
            runtime.MocapEnabled = false;
            TickFrames(1);
            C("受管参数动态化：关闭动捕的那一帧把参数组复位一次（嘴回默认 0，不会停在动捕值）",
                Near(Param("ParamMouthOpenY"), 0, 1e-3),
                $"嘴={Param("ParamMouthOpenY"):0.###}");
            if (mouthParam is not null) mouthParam.Value = 0.55f;
            TickFrames(5);
            C("受管参数动态化：关掉动捕后手动再调嘴值仍保留（0.55）",
                Near(Param("ParamMouthOpenY"), 0.55, 1e-3),
                $"嘴={Param("ParamMouthOpenY"):0.###}");

            // 打开鼠标眼控 → 眼球参数被接管（手调 -1 → 0），关掉后不再复位、手动值保留
            runtime.Mocap = null;
            if (eyeballYParam is not null) eyeballYParam.Value = -1f;
            runtime.EyeTrackingEnabled = true;
            runtime.EyeTrackingSmoothing = 0;
            runtime.SetEyeTrackingTarget(canvasCenterX, canvasCenterY);
            TickFrames(2);
            C("受管参数动态化：打开鼠标眼控后眼球参数被接管（手调 -1 → 中心目标 0）",
                Near(Param("ParamEyeBallY"), 0, 1e-3),
                $"眼珠Y={Param("ParamEyeBallY"):0.###}");
            runtime.EyeTrackingEnabled = false;
            runtime.ClearEyeTrackingTarget();
            TickFrames(1);
            if (eyeballYParam is not null) eyeballYParam.Value = -0.6f;
            TickFrames(5);
            C("受管参数动态化：关掉鼠标眼控后眼球参数不再被复位（手调 -0.6 保留）",
                Near(Param("ParamEyeBallY"), -0.6, 1e-3),
                $"眼珠Y={Param("ParamEyeBallY"):0.###}");

            // ---------------- J. 音乐跟唱 / 律动集成（假 IMusicReactiveProvider，绝不打开 WASAPI 回环） ----------------
            var fakeMusic = new FakeMusicProvider();
            Quiet();
            runtime.MocapEnabled = false;
            runtime.Mocap = null;
            runtime.EyeTrackingEnabled = false;
            runtime.SwayEnabled = false;
            runtime.StopSwayDemo();
            runtime.Sway?.Reset();
            runtime.Music = fakeMusic;
            runtime.MusicEnabled = false;
            runtime.MusicMode = MusicVocalMode.Auto;
            runtime.MusicLipSyncStrength = 1.0;
            runtime.MusicSwayStrength = 1.0;
            runtime.MusicSwayEnabled = true;
            runtime.MusicHeadBobEnabled = true;
            fakeMusic.Latest = null;
            TickFrames(2);

            // 带歌词 → 嘴动
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.8, mouthForm: 0.5,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(1);
            C("音乐集成：MusicEnabled=false 时即使有带歌词数据也不写嘴（默认关），整身轻倾保持常态",
                Near(Param("ParamMouthOpenY"), 0, 1e-3) && Math.Abs(runtime.BodyLeanDegrees) <= 0.7f
                && runtime.MusicKind == MusicContentKind.Silent && runtime.MusicStatusText == "静音",
                $"嘴={Param("ParamMouthOpenY"):0.###} 倾斜={runtime.BodyLeanDegrees:0.###}° 状态={runtime.MusicStatusText}");

            runtime.MusicEnabled = true;
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.8, mouthForm: 0.5,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(1);
            C("音乐集成：带歌词 → Tick 后嘴张开（ParamMouthOpenY=0.8、嘴形 0.3、JawOpen 0.4）",
                Near(Param("ParamMouthOpenY"), 0.8, 1e-2) && Near(Param("ParamMouthForm"), 0.3, 1e-2)
                && Near(Param("JawOpen"), 0.4, 1e-2),
                $"嘴={Param("ParamMouthOpenY"):0.###} 嘴形={Param("ParamMouthForm"):0.###} 下巴={Param("JawOpen"):0.###}");
            C("音乐集成：带歌词 → 状态「带歌词」、MusicKind=VocalSong",
                runtime.MusicStatusText == "带歌词" && runtime.MusicKind == MusicContentKind.VocalSong,
                $"{runtime.MusicStatusText}/{runtime.MusicKind}");

            // 纯音乐 → 不跟唱（身体律动改由整身轻倾表现，见 K 段）
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.Instrumental, mouthOpen: 0.8, mouthForm: 0.5,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            runtime.ResetParameters();
            TickFrames(2);
            C("音乐集成：纯音乐 → 嘴保持 0（不跟唱）",
                Near(Param("ParamMouthOpenY"), 0, 1e-3),
                $"嘴={Param("ParamMouthOpenY"):0.###}");
            C("音乐集成：纯音乐 → 状态「纯音乐」、MusicKind=Instrumental",
                runtime.MusicStatusText == "纯音乐" && runtime.MusicKind == MusicContentKind.Instrumental,
                $"{runtime.MusicStatusText}/{runtime.MusicKind}");

            // 旧「音乐写 ParamBodyAngle* 扭身体」逻辑已删除：确认音乐播放时这些参数保持默认（=不写，不扭身体）
            var musicBodyParamsUntouched = new[] { "ParamBodyAngleX", "ParamBodyAngleZ", "PositionX", "HipAngleX" }
                .All(id => Near(Param(id), rt.Model.FindParameter(id)?.DefaultValue ?? 0, 1e-3));
            C("音乐集成：音乐不再写 ParamBodyAngleX/Z / PositionX / HipAngleX（删掉扭身体逻辑，改整身轻倾）",
                musicBodyParamsUntouched,
                string.Join(", ", new[] { "ParamBodyAngleX", "ParamBodyAngleZ", "PositionX", "HipAngleX" }
                    .Select(id => $"{id}={Param(id):0.###}")));

            // 关闭「摇晃」开关：只影响整身轻倾加快，嘴型照常
            runtime.MusicSwayEnabled = false;
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.8, mouthForm: 0.5,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(2);
            C("音乐集成：MusicSwayEnabled=false 时嘴型照常（该开关只控制整身轻倾加快）",
                Near(Param("ParamMouthOpenY"), 0.8, 1e-2),
                $"嘴={Param("ParamMouthOpenY"):0.###}");
            runtime.MusicSwayEnabled = true;

            // 点头：眼睛跟随开启时不抢头部；关闭眼控后按节拍点头
            runtime.MusicHeadBobEnabled = true;
            runtime.EyeTrackingEnabled = true;
            runtime.EyeTrackingSmoothing = 0;
            runtime.SetEyeTrackingTarget(rt.Model.CanvasOriginX, rt.Model.CanvasOriginY);
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.Instrumental, mouthOpen: 0, mouthForm: 0,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(2);
            var headYWithEyeTracking = Param("ParamAngleY");
            runtime.EyeTrackingEnabled = false;
            runtime.ClearEyeTrackingTarget();
            runtime.ResetParameters();
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.Instrumental, mouthOpen: 0, mouthForm: 0,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(2);
            var headYWithBob = Param("ParamAngleY");
            C("音乐集成：点头不抢鼠标眼控的头部（眼控开启时 AngleY≈0；关闭后节拍点头 -2°）",
                Math.Abs(headYWithEyeTracking) < 0.5 && Near(headYWithBob, -2.0, 0.2),
                $"眼控开启={headYWithEyeTracking:0.###}° 关闭后点头={headYWithBob:0.###}°");

            // 动捕有脸 → 嘴让位给动捕（音乐不抢）
            runtime.Mocap = fakeMocap;
            runtime.MocapEnabled = true;
            fakeMocap.Latest = MakeFakeTracking(yaw: 0, pitch: 0, eyeOpen: 0.9, mouth: 0.6);
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.9, mouthForm: 0.5,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(2);
            C("音乐集成：动捕有脸时音乐嘴型让位（嘴 = 动捕 0.6，不是音乐 0.9）",
                Near(Param("ParamMouthOpenY"), 0.6, 1e-2),
                $"嘴={Param("ParamMouthOpenY"):0.###}");
            runtime.MocapEnabled = false;
            runtime.Mocap = null;

            // 强制模式
            runtime.MusicMode = MusicVocalMode.ForceVocal;
            runtime.ResetParameters();
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.Instrumental, mouthOpen: 0.7, mouthForm: 0.2,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(2);
            C("音乐集成：强制跟唱 → 纯音乐数据下嘴也动（0.7）",
                Near(Param("ParamMouthOpenY"), 0.7, 1e-2),
                $"嘴={Param("ParamMouthOpenY"):0.###}");

            runtime.MusicMode = MusicVocalMode.ForceInstrumental;
            runtime.ResetParameters();
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.9, mouthForm: 0.5,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(2);
            C("音乐集成：强制纯音乐 → 带歌词数据下嘴不动（整身轻倾仍按音乐加快，见 K 段）",
                Near(Param("ParamMouthOpenY"), 0, 1e-3),
                $"嘴={Param("ParamMouthOpenY"):0.###}");
            runtime.MusicMode = MusicVocalMode.Auto;

            // 强度倍率
            runtime.MusicLipSyncStrength = 0.5;
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.8, mouthForm: 0.5,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(2);
            C("音乐集成：跟唱嘴型幅度 0.5 → 嘴张开度减半（0.8 → 0.4）",
                Near(Param("ParamMouthOpenY"), 0.4, 1e-2),
                $"嘴={Param("ParamMouthOpenY"):0.###}");
            runtime.MusicLipSyncStrength = 1.0;

            // 数据过期 → 按静音处理（嘴 / 状态立即恢复；整身轻倾幅度有 0.35s 平滑，给 1 秒回常态）
            var stale = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.8, mouthForm: 0.5,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            stale = MusicSnapshotWithTimestamp(stale, Environment.TickCount64 - 5000);
            fakeMusic.Latest = stale;
            runtime.ResetParameters();
            TickFrames(2);
            C("音乐集成：数据超过 800ms 过期 → 嘴 / 状态立即按静音处理",
                Near(Param("ParamMouthOpenY"), 0, 1e-3) && runtime.MusicStatusText == "静音",
                $"嘴={Param("ParamMouthOpenY"):0.###} 状态={runtime.MusicStatusText}");
            TickFrames(60); // 1 秒：整身轻倾幅度从 1.0° 平滑回常态 0.6°（τ≈0.35s）
            var staleLeanMaxDelta = 0.0;
            var staleLeanPrevious = runtime.BodyLeanDegrees;
            for (var i = 0; i < 60; i++)
            {
                TickFrames(1);
                var currentLean = runtime.BodyLeanDegrees;
                staleLeanMaxDelta = Math.Max(staleLeanMaxDelta, Math.Abs(currentLean - staleLeanPrevious));
                staleLeanPrevious = currentLean;
            }
            C("音乐集成：数据过期后整身轻倾 1 秒内回常态幅度（|角度| ≤ 0.7°，相邻帧不跳变）",
                Math.Abs(runtime.BodyLeanDegrees) <= 0.7f && staleLeanMaxDelta < 0.2,
                $"倾斜={runtime.BodyLeanDegrees:0.###}° 最大帧间变化={staleLeanMaxDelta:0.####}°");

            // 关闭音乐后手动值保留
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.8, mouthForm: 0.5,
                beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            TickFrames(1);
            runtime.MusicEnabled = false;
            TickFrames(1); // 复位上一帧音乐写过的参数
            if (mouthParam is not null) mouthParam.Value = 0.6f;
            TickFrames(20);
            C("音乐集成：关闭音乐后手动嘴值保留（0.6 保持 20 帧，不被复位）",
                Near(Param("ParamMouthOpenY"), 0.6, 1e-3),
                $"嘴={Param("ParamMouthOpenY"):0.###}");

            // 帧开销（放宽阈值，避免 Debug + 机器繁忙时 flaky）
            runtime.MusicEnabled = true;
            fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.6, mouthForm: 0.3,
                beatPhase: 0.4, beatStrength: 0.8, bpm: 118);
            var musicOnTimes = new List<double>();
            for (var i = 0; i < 15; i++)
            {
                fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong, mouthOpen: 0.6, mouthForm: 0.3,
                    beatPhase: 0.4, beatStrength: 0.8, bpm: 118);
                TickFrames(1);
                musicOnTimes.Add(runtime.LastTickMilliseconds);
            }

            runtime.MusicEnabled = false;
            var musicOffTimes = new List<double>();
            for (var i = 0; i < 15; i++)
            {
                TickFrames(1);
                musicOffTimes.Add(runtime.LastTickMilliseconds);
            }

            var musicOnAverage = musicOnTimes.Average();
            var musicOffAverage = musicOffTimes.Average();
            C("音乐集成：音乐响应每帧开销可忽略（开启时平均 Tick ≤ 关闭时 + 3ms，Debug 放宽）",
                musicOnAverage <= musicOffAverage + 3.0,
                $"开启={musicOnAverage:0.###}ms 关闭={musicOffAverage:0.###}ms");

            // ---------------- K. 整身轻倾 BodyLean（渲染层旋转角度：常态 4s/±0.6°，音乐 2.6s/1.0°×幅度） ----------------
            runtime.Music = fakeMusic;
            runtime.MusicEnabled = false;
            runtime.MusicSwayEnabled = true;
            runtime.MusicSwayStrength = 1.0;
            runtime.BodyLeanEnabled = true;
            fakeMusic.Latest = null;
            var leanFrame = 0;
            for (var i = 0; i < 300; i++) // 5 秒，等幅度 / 周期一阶平滑到常态
            {
                TickFrames(1);
                leanFrame++;
            }

            List<(double T, float Deg)> SampleLean(int frames, Action? beforeTick = null)
            {
                var samples = new List<(double, float)>(frames);
                for (var i = 0; i < frames; i++)
                {
                    beforeTick?.Invoke();
                    TickFrames(1);
                    leanFrame++;
                    samples.Add((leanFrame / 60.0, runtime.BodyLeanDegrees));
                }
                return samples;
            }

            void SettleLean(int frames, Action? beforeTick = null)
            {
                for (var i = 0; i < frames; i++)
                {
                    beforeTick?.Invoke();
                    TickFrames(1);
                    leanFrame++;
                }
            }

            double EstimateLeanPeriod(IReadOnlyList<(double T, float Deg)> samples)
            {
                var upCrossings = new List<double>();
                for (var i = 1; i < samples.Count; i++)
                {
                    if (samples[i - 1].Deg < 0 && samples[i].Deg >= 0)
                    {
                        var delta = samples[i].Deg - samples[i - 1].Deg;
                        var ratio = Math.Abs(delta) < 1e-9 ? 0 : -samples[i - 1].Deg / delta;
                        upCrossings.Add(samples[i - 1].T + (ratio * (samples[i].T - samples[i - 1].T)));
                    }
                }
                if (upCrossings.Count < 2) return double.NaN;
                var intervals = upCrossings.Skip(1).Select((t, i) => t - upCrossings[i]).ToList();
                return intervals.Average();
            }

            double MaxAdjacentLeanDelta(IReadOnlyList<(double T, float Deg)> samples)
            {
                var max = 0.0;
                for (var i = 1; i < samples.Count; i++)
                    max = Math.Max(max, Math.Abs(samples[i].Deg - samples[i - 1].Deg));
                return max;
            }

            var idleLean = SampleLean(600); // 10 秒
            var idleLeanPeriod = EstimateLeanPeriod(idleLean);
            var idleLeanAmplitude = idleLean.Max(s => Math.Abs(s.Deg));
            C("整身轻倾：常态周期 ≈4 秒、幅度 ≈±0.6°（固定 dt 采样过零点 / 峰值，±15% 容差）",
                idleLeanPeriod is >= 3.4 and <= 4.6 && idleLeanAmplitude is >= 0.51f and <= 0.69f,
                $"周期={idleLeanPeriod:0.###}s 幅度={idleLeanAmplitude:0.###}°");
            C("整身轻倾：常态相邻帧角度连续（60fps 下每帧变化 < 0.1°，不跳变）",
                MaxAdjacentLeanDelta(idleLean) < 0.1, $"最大帧间变化={MaxAdjacentLeanDelta(idleLean):0.####}°");

            // 音乐播放（纯音乐）→ 周期 ~2.6s、幅度 1.0°；嘴仍不跟唱
            runtime.MusicEnabled = true;
            Action refreshInstrumental = () => fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.Instrumental,
                mouthOpen: 0.8, mouthForm: 0.4, beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            SettleLean(180, refreshInstrumental); // 3 秒
            var musicInstrumentalLean = SampleLean(360, refreshInstrumental); // 6 秒
            var instrumentalLeanPeriod = EstimateLeanPeriod(musicInstrumentalLean);
            var instrumentalLeanAmplitude = musicInstrumentalLean.Max(s => Math.Abs(s.Deg));
            C("整身轻倾：音乐播放（纯音乐）→ 周期加快到 ≈2.6 秒、幅度 ≈1.0°，嘴保持 0",
                instrumentalLeanPeriod is >= 2.2 and <= 3.0
                && instrumentalLeanAmplitude is >= 0.85f and <= 1.15f
                && Near(Param("ParamMouthOpenY"), 0, 1e-3),
                $"周期={instrumentalLeanPeriod:0.###}s 幅度={instrumentalLeanAmplitude:0.###}° 嘴={Param("ParamMouthOpenY"):0.###}");

            // 音乐播放（带歌词）→ 嘴动 + lean 同样加快
            Action refreshVocal = () => fakeMusic.Latest = MakeMusicSnapshot(MusicContentKind.VocalSong,
                mouthOpen: 0.8, mouthForm: 0.5, beatPhase: 0.25, beatStrength: 1.0, bpm: 120);
            SettleLean(120, refreshVocal);
            var vocalLean = SampleLean(480, refreshVocal);
            var vocalLeanPeriod = EstimateLeanPeriod(vocalLean);
            C("整身轻倾：音乐播放（带歌词）→ 嘴动（0.8）+ 周期同样加快（≈2.6 秒）",
                Near(Param("ParamMouthOpenY"), 0.8, 1e-2) && vocalLeanPeriod is >= 2.2 and <= 3.0,
                $"嘴={Param("ParamMouthOpenY"):0.###} 周期={vocalLeanPeriod:0.###}s");

            // MusicSwayEnabled=false：lean 保持常态（4s/±0.6°）
            runtime.MusicSwayEnabled = false;
            SettleLean(180, refreshVocal);
            var swayOffLean = SampleLean(600, refreshVocal);
            var swayOffPeriod = EstimateLeanPeriod(swayOffLean);
            var swayOffAmplitude = swayOffLean.Max(s => Math.Abs(s.Deg));
            C("整身轻倾：MusicSwayEnabled=false 时保持常态（≈4 秒 / ±0.6°，不跟音乐加快）",
                swayOffPeriod is >= 3.4 and <= 4.6 && swayOffAmplitude is >= 0.51f and <= 0.69f,
                $"周期={swayOffPeriod:0.###}s 幅度={swayOffAmplitude:0.###}°");
            runtime.MusicSwayEnabled = true;

            // BodyLeanEnabled=false：平滑归零（相邻帧小变化，不跳变）
            runtime.BodyLeanEnabled = false;
            var leanDisabling = SampleLean(120); // 2 秒
            var leanDisableMaxDelta = MaxAdjacentLeanDelta(leanDisabling);
            C("整身轻倾：BodyLeanEnabled=false → 平滑归零（2 秒后 |角度| < 0.05°，相邻帧变化 < 0.2° 不跳变）",
                Math.Abs(leanDisabling[^1].Deg) < 0.05 && leanDisableMaxDelta < 0.2,
                $"末帧={leanDisabling[^1].Deg:0.####}° 最大帧间变化={leanDisableMaxDelta:0.####}°");

            // 重新打开：从 0 平滑恢复（不是瞬间跳到 ±0.6°）
            runtime.BodyLeanEnabled = true;
            SettleLean(240); // 4 秒
            var leanReenabled = SampleLean(300);
            C("整身轻倾：重新打开后从 0 平滑恢复常态幅度（> 0.3°，不瞬间跳满）",
                leanReenabled.Max(s => Math.Abs(s.Deg)) > 0.3,
                $"恢复后幅度={leanReenabled.Max(s => Math.Abs(s.Deg)):0.###}°");

            // 关闭音乐 → 回到常态周期 / 幅度
            runtime.MusicEnabled = false;
            SettleLean(180);
            var musicOffLean = SampleLean(600);
            var musicOffLeanPeriod = EstimateLeanPeriod(musicOffLean);
            var musicOffLeanAmplitude = musicOffLean.Max(s => Math.Abs(s.Deg));
            C("整身轻倾：关闭音乐后回到常态周期 / 幅度（≈4 秒 / ±0.6°）",
                musicOffLeanPeriod is >= 3.4 and <= 4.6 && musicOffLeanAmplitude is >= 0.51f and <= 0.69f,
                $"周期={musicOffLeanPeriod:0.###}s 幅度={musicOffLeanAmplitude:0.###}°");

            runtime.Music = null;
            runtime.MusicEnabled = false;
            runtime.ResetParameters();
            TickFrames(2);

            // 收尾：把所有子系统恢复默认状态，避免影响后续（其实后面不再有断言，保持干净）
            runtime.ResetParameters();
            runtime.EyeTrackingSmoothing = 0.35;
            TickFrames(2);
        }
        catch (Exception ex)
        {
            C("模型运行时 / 渲染器断言运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        }
        finally
        {
            runtime.Dispose();
        }
    }

    // ====================================================================
    // Interop 参数断言（单独拆出来，避免主流程里塞太多）
    // ====================================================================
    private static void CheckParams(Action<string, bool, string> check, CubismModel model, CubismParameter? angleX)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        C("参数查找：ParamAngleX / ParamEyeBallX / ParamMouthOpenY 都能按 Id 找到",
            angleX is not null
            && model.FindParameter("ParamEyeBallX") is not null
            && model.FindParameter("ParamMouthOpenY") is not null
            && model.FindParameter("ParamAngleX") is not null);
        C("参数范围：ParamAngleX = -30 .. 30、ParamEyeBallX = -1 .. 1、ParamEyeLOpen = 0 .. 1.5",
            angleX is not null && Near(angleX.Minimum, -30, 1e-6) && Near(angleX.Maximum, 30, 1e-6)
            && model.FindParameter("ParamEyeBallX") is { } eyeBall && Near(eyeBall.Minimum, -1, 1e-6) && Near(eyeBall.Maximum, 1, 1e-6)
            && model.FindParameter("ParamEyeLOpen") is { } eyeOpen && Near(eyeOpen.Minimum, 0, 1e-6) && Near(eyeOpen.Maximum, 1.5, 1e-6));
        C("参数查找：不存在的 Id 返回 null（UI 列表不会拿到野参数）",
            model.FindParameter("ParamNotExist_自检") is null
            && model.FindPart("PartNotExist_自检") is null
            && model.FindDrawable("ArtMeshNotExist_自检") is null);

        if (angleX is not null)
        {
            angleX.Value = 100;
            var clampedHigh = angleX.Value;
            angleX.Value = -100;
            var clampedLow = angleX.Value;
            C("参数写入自动夹取到 min~max（写 100 → 30，写 -100 → -30）",
                Near(clampedHigh, 30, 1e-6) && Near(clampedLow, -30, 1e-6),
                $"{clampedHigh} / {clampedLow}");

            angleX.Value = 15;
            model.ResetParametersToDefault();
            C("ResetParametersToDefault 把参数恢复成 moc 默认值（ParamAngleX = 0）",
                Near(angleX.Value, angleX.DefaultValue, 1e-6) && Near(angleX.Value, 0, 1e-6),
                angleX.Value.ToString(CultureInfo.InvariantCulture));
        }

        // 默认值遍历：每个参数都应该是 min <= default <= max
        var badDefaults = model.Parameters.Count(p => p.DefaultValue < p.Minimum - 1e-4f || p.DefaultValue > p.Maximum + 1e-4f
                                                       || !float.IsFinite(p.DefaultValue));
        C("全部 509 个参数的 min ≤ default ≤ max 且默认值有限", badDefaults == 0, $"非法 {badDefaults} 个");
    }

    /// <summary>
    /// 「😛 面部测试」卡直接写的面部参数（眼睛位置/开闭、嘴、舌头）自检：
    /// 参数存在性 / 范围 / 默认值可读、写入自动夹取、写读一致。全部走 Interop，不需要 UI、不碰摄像头。
    /// </summary>
    private static void CheckFaceParameters(Action<string, bool, string> check, CubismModel model)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        var eyeBallX = model.FindParameter("ParamEyeBallX");
        var eyeBallY = model.FindParameter("ParamEyeBallY");
        var eyeLOpen = model.FindParameter("ParamEyeLOpen");
        var eyeROpen = model.FindParameter("ParamEyeROpen");
        var mouthOpen = model.FindParameter("ParamMouthOpenY");
        var mouthForm = model.FindParameter("ParamMouthForm");
        var tongueOut = model.FindParameter("TongueOut");
        var tongueUp = model.FindParameter("ParamTongueUpDown");
        var tongueWidth = model.FindParameter("ParamTongueWidth");

        C("面部测试：眼睛位置/开闭 4 个参数都能按 Id 找到（ParamEyeBallX/Y、ParamEyeLOpen/ROpen）",
            eyeBallX is not null && eyeBallY is not null && eyeLOpen is not null && eyeROpen is not null);
        C("面部测试：嘴 2 个参数都能按 Id 找到（ParamMouthOpenY / ParamMouthForm）",
            mouthOpen is not null && mouthForm is not null);
        C("面部测试：舌头三参数都能按 Id 找到（TongueOut / ParamTongueUpDown / ParamTongueWidth）",
            tongueOut is not null && tongueUp is not null && tongueWidth is not null,
            tongueOut is null ? "(TongueOut 不存在)" : $"TongueOut={tongueOut.Minimum}..{tongueOut.Maximum} 默认 {tongueOut.DefaultValue}");

        var faceRangesOk = eyeBallX is not null && eyeBallY is not null && eyeLOpen is not null && eyeROpen is not null
                           && mouthOpen is not null && mouthForm is not null
                           && tongueOut is not null && tongueUp is not null && tongueWidth is not null
                           && eyeBallX.Minimum <= 0 && eyeBallX.Maximum >= 1
                           && eyeLOpen.Minimum <= 0 && eyeLOpen.Maximum >= 1
                           && mouthOpen.Minimum <= 0 && mouthOpen.Maximum >= 1
                           && tongueOut.Minimum <= 0 && tongueOut.Maximum >= 1;
        C("面部测试：眼睛/嘴/舌头参数范围包含 0~1（TongueOut 与 ParamMouthOpenY 都能从 0 张到 1）",
            faceRangesOk,
            $"眼={eyeLOpen?.Minimum}..{eyeLOpen?.Maximum} 嘴={mouthOpen?.Minimum}..{mouthOpen?.Maximum} " +
            $"舌={tongueOut?.Minimum}..{tongueOut?.Maximum} 舌上下={tongueUp?.Minimum}..{tongueUp?.Maximum} 舌宽={tongueWidth?.Minimum}..{tongueWidth?.Maximum}");

        // 写读一致 + 自动夹取（面部测试卡就是直接写这些参数；slider 拖到端点要能和模型对上）
        var writeOk = mouthOpen is not null && tongueOut is not null && eyeBallY is not null;
        if (writeOk)
        {
            mouthOpen!.Value = 1f;
            tongueOut!.Value = 1f;
            eyeBallY!.Value = -1f;
            writeOk = Near(mouthOpen.Value, 1, 1e-6) && Near(tongueOut.Value, 1, 1e-6) && Near(eyeBallY.Value, -1, 1e-6);
        }

        C("面部测试：写端点值（嘴 1 / 舌 1 / 眼球 -1）读回一致（滑块到端点不会漂）", writeOk);

        if (mouthOpen is not null && tongueOut is not null && tongueUp is not null && tongueWidth is not null)
        {
            mouthOpen.Value = 100f;
            tongueOut.Value = 100f;
            tongueUp.Value = 100f;
            tongueWidth.Value = 100f;
            var clamped = Near(mouthOpen.Value, mouthOpen.Maximum, 1e-6)
                          && Near(tongueOut.Value, tongueOut.Maximum, 1e-6)
                          && Near(tongueUp.Value, tongueUp.Maximum, 1e-6)
                          && Near(tongueWidth.Value, tongueWidth.Maximum, 1e-6);
            mouthOpen.Value = -100f;
            tongueOut.Value = -100f;
            tongueUp.Value = -100f;
            tongueWidth.Value = -100f;
            clamped = clamped
                      && Near(mouthOpen.Value, mouthOpen.Minimum, 1e-6)
                      && Near(tongueOut.Value, tongueOut.Minimum, 1e-6)
                      && Near(tongueUp.Value, tongueUp.Minimum, 1e-6)
                      && Near(tongueWidth.Value, tongueWidth.Minimum, 1e-6);
            C("面部测试：写超范围值被夹到参数 min~max（嘴 / 舌头参数越界不会崩）", clamped);
        }

        model.ResetParametersToDefault();
    }

    // ====================================================================
    // 音乐分析用的合成音频工具（自检只测算法：绝不打开 WASAPI 回环 / 麦克风）
    // ====================================================================

    /// <summary>生成单声道正弦（幅度默认 0.8，时长按秒）。</summary>
    private static double[] MakeSine(int sampleRate, double seconds, double frequency, double amplitude = 0.8)
    {
        var count = Math.Max(1, (int)Math.Round(seconds * sampleRate));
        var samples = new double[count];
        for (var i = 0; i < count; i++)
            samples[i] = amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate);
        return samples;
    }

    /// <summary>生成确定性白噪声（LCG，同一 seed 每台机器结果一致，便于断言）。</summary>
    private static double[] MakeWhiteNoise(int sampleRate, double seconds, uint seed)
    {
        var count = Math.Max(1, (int)Math.Round(seconds * sampleRate));
        var samples = new double[count];
        var state = seed;
        for (var i = 0; i < count; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            samples[i] = (((state >> 8) & 0xFFFF) / 32768.0) - 1.0;
        }
        return samples;
    }

    /// <summary>合成「稳态长音」立体声 interleaved float（无 onset / 无鼓点，用于验证无节拍回退）。</summary>
    private static float[] MakeSteadyToneStereo(double seconds, int sampleRate, double frequency, double amplitude)
    {
        var frames = Math.Max(1, (int)Math.Round(seconds * sampleRate));
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            data[i * 2] = sample;
            data[i * 2 + 1] = sample;
        }
        return data;
    }

    /// <summary>生成「每拍一个脉冲」的 onset 包络（用于 BPM 估计算法的对拍）。</summary>
    private static double[] MakeClickOnsetEnvelope(double seconds, double hopRate, double bpm)
    {
        var count = Math.Max(64, (int)Math.Round(seconds * hopRate));
        var envelope = new double[count];
        var period = hopRate * 60.0 / bpm;
        for (var beat = 0; ; beat++)
        {
            var index = (int)Math.Round(beat * period);
            if (index >= count) break;
            envelope[index] = 1.0;
        }
        return envelope;
    }

    /// <summary>合成 120BPM 点击（底鼓 + 噪声镲片 + 很轻的底噪，保证分析器始终「有声音」）。</summary>
    private static float[] MakeClickTrackStereo(double seconds, int sampleRate, double bpm)
    {
        var frames = Math.Max(1, (int)Math.Round(seconds * sampleRate));
        var data = new float[frames * 2];
        var period = 60.0 / bpm;
        var stateLeft = 987654321u;
        var stateRight = 123456789u;
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)sampleRate;
            var beatIndex = (long)(t / period);
            var phaseInBeat = t - (beatIndex * period);
            var kick = 0.0;
            var hatLeft = 0.0;
            var hatRight = 0.0;
            if (phaseInBeat < 0.06)
            {
                var kickDecay = Math.Exp(-phaseInBeat / 0.012);
                kick = 0.75 * kickDecay * Math.Sin(2 * Math.PI * 100 * phaseInBeat);
                var hatDecay = Math.Exp(-phaseInBeat / 0.006);
                stateLeft = (stateLeft * 1664525u) + 1013904223u;
                stateRight = (stateRight * 1664525u) + 1013904223u;
                hatLeft = 0.30 * hatDecay * (((stateLeft >> 8) & 0xFFFF) / 32768.0 - 1.0);
                hatRight = 0.30 * hatDecay * (((stateRight >> 8) & 0xFFFF) / 32768.0 - 1.0);
            }

            // 左右独立的极轻底噪：让静音段也高于噪声门限，同时压低「中心声道占比」和 YIN 清晰度
            stateLeft = (stateLeft * 1664525u) + 1013904223u;
            stateRight = (stateRight * 1664525u) + 1013904223u;
            var noiseLeft = 0.030 * (((stateLeft >> 8) & 0xFFFF) / 32768.0 - 1.0);
            var noiseRight = 0.030 * (((stateRight >> 8) & 0xFFFF) / 32768.0 - 1.0);

            data[i * 2] = (float)Math.Clamp(kick + hatLeft + noiseLeft, -1.0, 1.0);
            data[i * 2 + 1] = (float)Math.Clamp(kick + hatRight + noiseRight, -1.0, 1.0);
        }
        return data;
    }

    /// <summary>
    /// 合成「类人声」立体声信号：220Hz 基频 + 5Hz 颤音 + 共振峰加权谐波 + 4Hz 音节包络，
    /// 主唱居中（左右几乎相同）。
    /// </summary>
    private static float[] MakeVocalLikeStereo(double seconds, int sampleRate, double f0 = 220.0,
        double vibratoDepth = 0.02, double syllabicDepth = 0.55)
    {
        var frames = Math.Max(1, (int)Math.Round(seconds * sampleRate));
        var data = new float[frames * 2];
        var phase = 0.0;
        var state = 24681357u;
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)sampleRate;
            var vibrato = 1.0 + (vibratoDepth * Math.Sin(2 * Math.PI * 5.0 * t));
            phase += 2 * Math.PI * f0 * vibrato / sampleRate;
            var syllable = 0.45 + (syllabicDepth * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 4.0 * t)));
            var sample = 0.0;
            for (var harmonic = 1; harmonic <= 12; harmonic++)
            {
                var frequency = f0 * harmonic;
                if (frequency > 6000) break;
                sample += (FormantWeight(frequency) / harmonic) * Math.Sin(phase * harmonic);
            }

            state = (state * 1664525u) + 1013904223u;
            var noise = (((state >> 8) & 0xFFFF) / 32768.0) - 1.0;
            sample = (sample * syllable * 0.20) + (noise * 0.002);
            data[i * 2] = (float)Math.Clamp(sample, -1.0, 1.0);
            data[i * 2 + 1] = (float)Math.Clamp(sample, -1.0, 1.0);
        }
        return data;
    }

    /// <summary>共振峰加权：700 / 1220 / 2600Hz 附近放大，模拟人声频谱包络。</summary>
    private static double FormantWeight(double frequency)
    {
        var weight = 1.0;
        weight += 2.2 * Math.Exp(-Math.Pow((frequency - 700) / 250.0, 2));
        weight += 2.0 * Math.Exp(-Math.Pow((frequency - 1220) / 350.0, 2));
        weight += 1.5 * Math.Exp(-Math.Pow((frequency - 2600) / 600.0, 2));
        return weight;
    }

    /// <summary>
    /// 合成「纯乐器」立体声信号：鼓点为主（底鼓 + 左右独立的宽频镲片 / 底噪）+ 很轻的稳定和弦，
    /// 无人声的颤音 / 音节调制，侧声道能量明显——用来验证「不跟唱、只摇晃」。
    /// </summary>
    private static float[] MakeInstrumentalStereo(double seconds, int sampleRate, double bpm = 120.0)
    {
        var frames = Math.Max(1, (int)Math.Round(seconds * sampleRate));
        var data = new float[frames * 2];
        var chord = new[] { 196.00, 246.94, 293.66 };
        var period = 60.0 / bpm;
        var stateLeft = 1357924680u;
        var stateRight = 2468013579u;
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)sampleRate;
            var chordLeft = 0.0;
            var chordRight = 0.0;
            foreach (var frequency in chord)
            {
                chordLeft += (0.5 * Math.Sin(2 * Math.PI * frequency * t))
                             + (0.25 * Math.Sin(2 * Math.PI * frequency * 2 * t));
                // 右声道明显失谐：制造侧声道能量，压低「主唱居中」的中心声道占比
                chordRight += (0.5 * Math.Sin(2 * Math.PI * frequency * 1.02 * t))
                              + (0.25 * Math.Sin(2 * Math.PI * frequency * 2 * 1.02 * t));
            }

            var beatIndex = (long)(t / period);
            var phaseInBeat = t - (beatIndex * period);
            var kick = 0.0;
            if (phaseInBeat < 0.07)
            {
                var decay = Math.Exp(-phaseInBeat / 0.012);
                kick = 0.65 * decay * Math.Sin(2 * Math.PI * 90 * phaseInBeat);
            }

            // 镲片：每拍一个（2Hz，刻意避开 3~7Hz 音节调制频段），左右各自独立的宽频噪声 —— 把频谱拉平
            var hatLeft = 0.0;
            var hatRight = 0.0;
            if (phaseInBeat < 0.05)
            {
                var decay = Math.Exp(-phaseInBeat / 0.006);
                stateLeft = (stateLeft * 1664525u) + 1013904223u;
                stateRight = (stateRight * 1664525u) + 1013904223u;
                hatLeft = 0.55 * decay * (((stateLeft >> 8) & 0xFFFF) / 32768.0 - 1.0);
                hatRight = 0.55 * decay * (((stateRight >> 8) & 0xFFFF) / 32768.0 - 1.0);
            }

            // 连续底噪（左右独立）：保证分析器始终活跃、谱更平坦
            stateLeft = (stateLeft * 1664525u) + 1013904223u;
            stateRight = (stateRight * 1664525u) + 1013904223u;
            var noiseLeft = 0.050 * (((stateLeft >> 8) & 0xFFFF) / 32768.0 - 1.0);
            var noiseRight = 0.050 * (((stateRight >> 8) & 0xFFFF) / 32768.0 - 1.0);

            // 和弦非常轻（只留一点乐器感）：新的人声判定看「mid 周期显著度 / 谐波梳」，
            // 稳定和弦太响会被判成人声，所以这里压到几乎听不见。
            var left = (chordLeft * 0.006) + kick + hatLeft + noiseLeft;
            var right = (chordRight * 0.006) + (kick * 0.9) + hatRight + noiseRight;
            data[i * 2] = (float)Math.Clamp(left, -1.0, 1.0);
            data[i * 2 + 1] = (float)Math.Clamp(right, -1.0, 1.0);
        }
        return data;
    }

    /// <summary>按 10ms 一块喂给分析器（模拟 WASAPI 采集回调），返回最终快照。</summary>
    private static MusicAnalysisSnapshot FeedAnalyzer(MusicAnalyzer analyzer, float[] interleavedStereo,
        int sampleRate, int framesPerChunk)
    {
        var snapshots = FeedAnalyzerCollect(analyzer, interleavedStereo, sampleRate, framesPerChunk);
        return snapshots.Count > 0 ? snapshots[^1] : MusicAnalysisSnapshot.Silent(0);
    }

    /// <summary>按块喂给分析器并收集每块快照（用于断言唇形 / 节拍随时间变化）。</summary>
    private static List<MusicAnalysisSnapshot> FeedAnalyzerCollect(MusicAnalyzer analyzer, float[] interleavedStereo,
        int sampleRate, int framesPerChunk)
    {
        var frames = interleavedStereo.Length / 2;
        var snapshots = new List<MusicAnalysisSnapshot>();
        var timestamp = 0L;
        for (var offset = 0; offset < frames; offset += framesPerChunk)
        {
            var count = Math.Min(framesPerChunk, frames - offset);
            var snapshot = analyzer.Analyze(interleavedStereo.AsSpan(offset * 2, count * 2), count, timestamp);
            snapshots.Add(snapshot);
            timestamp += (long)Math.Round(count * 1000.0 / sampleRate);
        }
        return snapshots;
    }

    // ====================================================================
    // 动作文件解析（自带最小求值器：只对断言负责，不参与运行时）
    // ====================================================================

    private sealed class MotionCurveData
    {
        public string Target = "";
        public string Id = "";
        public double[] Segments = Array.Empty<double>();
        public double MinValue = double.MaxValue;
        public double MaxValue = double.MinValue;

        public void TrackValue(double value)
        {
            if (value < MinValue) MinValue = value;
            if (value > MaxValue) MaxValue = value;
        }
    }

    private sealed class MotionFileData
    {
        public string Name = "";
        public double Duration;
        public bool Loop;
        public bool HasFadeIn;
        public bool HasFadeOut;
        public int MetaCurveCount = -1;
        public int MetaSegmentCount = -1;
        public int MetaPointCount = -1;
        public int ActualSegmentCount;
        public int ActualPointCount;
        public int BadSegmentTypes;
        public bool TimesNonDecreasing = true;
        public readonly List<MotionCurveData> Curves = new();
    }

    private static MotionFileData? TryParseMotion(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("Meta", out var meta) || meta.ValueKind != JsonValueKind.Object) return null;

            var data = new MotionFileData
            {
                Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)),
                Duration = meta.TryGetProperty("Duration", out var duration) ? duration.GetDouble() : 0,
                Loop = meta.TryGetProperty("Loop", out var loop) && loop.ValueKind == JsonValueKind.True,
                HasFadeIn = meta.TryGetProperty("FadeInTime", out _),
                HasFadeOut = meta.TryGetProperty("FadeOutTime", out _),
                MetaCurveCount = TryGetInt(meta, "CurveCount"),
                MetaSegmentCount = TryGetInt(meta, "TotalSegmentCount"),
                MetaPointCount = TryGetInt(meta, "TotalPointCount"),
            };

            if (!root.TryGetProperty("Curves", out var curves) || curves.ValueKind != JsonValueKind.Array) return data;

            foreach (var curveElement in curves.EnumerateArray())
            {
                var curve = new MotionCurveData
                {
                    Target = curveElement.TryGetProperty("Target", out var target) ? target.GetString() ?? "" : "",
                    Id = curveElement.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : "",
                    Segments = curveElement.TryGetProperty("Segments", out var segments) && segments.ValueKind == JsonValueKind.Array
                        ? segments.EnumerateArray().Select(s => s.GetDouble()).ToArray()
                        : Array.Empty<double>(),
                };

                // Segments：[t0, v0] 然后每段 [type, t, v]（type 0/2/3）或 [type, t, v, c1x,c1y,c2x,c2y]（type 1）
                // TotalPointCount 的官方口径：每条曲线起点 1 个点 + 每段 1 个点（贝塞尔段再加 2 个控制点）。
                var s = curve.Segments;
                data.ActualPointCount += 1;
                if (s.Length >= 2)
                {
                    curve.TrackValue(s[1]);
                    var index = 2;
                    var lastTime = s[0];
                    while (index + 2 < s.Length)
                    {
                        var type = (int)s[index];
                        if (type is < 0 or > 3)
                        {
                            data.BadSegmentTypes++;
                            break;
                        }

                        var step = type == 1 ? 7 : 3;
                        if (index + step > s.Length)
                        {
                            data.BadSegmentTypes++;
                            break;
                        }

                        var endTime = s[index + 1];
                        var endValue = s[index + 2];
                        if (!double.IsFinite(endTime) || !double.IsFinite(endValue) || endTime < lastTime - 1e-9)
                            data.TimesNonDecreasing = false;

                        curve.TrackValue(endValue);
                        if (type == 1 && index + 6 < s.Length)
                        {
                            curve.TrackValue(s[index + 4]);
                            curve.TrackValue(s[index + 6]);
                            data.ActualPointCount += 3;
                        }
                        else
                        {
                            data.ActualPointCount += 1;
                        }

                        data.ActualSegmentCount++;
                        lastTime = endTime;
                        index += step;
                    }
                }

                data.Curves.Add(curve);
            }

            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>my 最小求值器：t=0 / t=Duration 精确，中段线性 / 贝塞尔 / 阶跃按语义求值（不追求与官方 Cardano 完全一致）。</summary>
    private static double EvaluateCurve(MotionCurveData curve, double time)
    {
        var s = curve.Segments;
        if (s.Length < 2) return double.NaN;
        if (time <= s[0]) return s[1];

        var previousTime = s[0];
        var previousValue = s[1];
        var index = 2;
        while (index + 2 < s.Length)
        {
            var type = (int)s[index];
            var endTime = s[index + 1];
            var endValue = s[index + 2];

            if (type == 1 && index + 6 < s.Length)
            {
                var control1Value = s[index + 4];
                var control2Value = s[index + 6];
                if (time <= endTime)
                {
                    var u = endTime - previousTime > 1e-12 ? Math.Clamp((time - previousTime) / (endTime - previousTime), 0, 1) : 1;
                    return Bezier1D(previousValue, control1Value, control2Value, endValue, u);
                }

                previousTime = endTime;
                previousValue = endValue;
                index += 7;
                continue;
            }

            if (time <= endTime)
            {
                if (type == 0) // 线性
                {
                    var u = endTime - previousTime > 1e-12 ? (time - previousTime) / (endTime - previousTime) : 1;
                    return previousValue + (endValue - previousValue) * u;
                }

                if (type == 2) return previousValue; // 阶跃：保持段起点值
                return endValue;                     // 反向阶跃：段一开始就跳到终点值
            }

            previousTime = endTime;
            previousValue = endValue;
            index += 3;
        }

        return previousValue;
    }

    private static double GetCurveEndValue(MotionCurveData curve)
    {
        // 最后一段的终点值：从后往前按段类型找（忽略贝塞尔控制点）
        var s = curve.Segments;
        var index = 2;
        var lastValue = s.Length >= 2 ? s[1] : 0;
        while (index + 2 < s.Length)
        {
            var type = (int)s[index];
            var step = type == 1 ? 7 : 3;
            if (index + step > s.Length) break;
            lastValue = s[index + 2];
            index += step;
        }

        return lastValue;
    }

    private static double Bezier1D(double p0, double p1, double p2, double p3, double t)
    {
        var mt = 1 - t;
        return mt * mt * mt * p0 + 3 * mt * mt * t * p1 + 3 * mt * t * t * p2 + t * t * t * p3;
    }

    // ====================================================================
    // 表情 / 物理 / cdi3 解析
    // ====================================================================

    private sealed class ExpressionData
    {
        public string Name = "";
        public string Type = "";
        public readonly List<(string Id, double Value, string Blend)> Parameters = new();
    }

    private static ExpressionData? TryParseExpression(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var data = new ExpressionData
            {
                Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)),
                Type = root.TryGetProperty("Type", out var type) ? type.GetString() ?? "" : "",
            };
            if (!root.TryGetProperty("Parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array) return data;
            foreach (var parameter in parameters.EnumerateArray())
            {
                var id = parameter.TryGetProperty("Id", out var idElement) ? idElement.GetString() ?? "" : "";
                var value = parameter.TryGetProperty("Value", out var valueElement) ? valueElement.GetDouble() : 0;
                var blend = parameter.TryGetProperty("Blend", out var blendElement) ? blendElement.GetString() ?? "" : "";
                data.Parameters.Add((id, value, blend));
            }

            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class PhysicsData
    {
        public int SettingCount;
        public int InputCount;
        public int OutputCount;
        public int VertexCount;
        public int Fps;
        public int MetaSettingCount = -1;
        public int MetaInputCount = -1;
        public int MetaOutputCount = -1;
        public int InvalidNormalizationCount;
        public int DistinctOutputCount;
        public readonly HashSet<string> InputIds = new(StringComparer.Ordinal);
        public readonly HashSet<string> OutputIds = new(StringComparer.Ordinal);
        public readonly List<string> DictionaryNames = new();
    }

    private static PhysicsData ParsePhysics(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var meta = root.GetProperty("Meta");
        var data = new PhysicsData
        {
            MetaSettingCount = TryGetInt(meta, "PhysicsSettingCount"),
            MetaInputCount = TryGetInt(meta, "TotalInputCount"),
            MetaOutputCount = TryGetInt(meta, "TotalOutputCount"),
            VertexCount = TryGetInt(meta, "VertexCount"),
            Fps = TryGetInt(meta, "Fps"),
        };

        if (meta.TryGetProperty("PhysicsDictionary", out var dictionary) && dictionary.ValueKind == JsonValueKind.Array)
            foreach (var item in dictionary.EnumerateArray())
                if (item.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String)
                    data.DictionaryNames.Add(name.GetString() ?? "");

        var settings = root.GetProperty("PhysicsSettings");
        data.SettingCount = settings.GetArrayLength();
        foreach (var setting in settings.EnumerateArray())
        {
            var normalization = setting.GetProperty("Normalization");
            var positionValid = TryGetRange(normalization, "Position", requireDefault: true, out _);
            var angleValid = TryGetRange(normalization, "Angle", requireDefault: true, out _)
                             || TryGetRange(normalization, "Angle", requireDefault: false, out _);
            if (!positionValid || !angleValid) data.InvalidNormalizationCount++;

            foreach (var input in setting.GetProperty("Input").EnumerateArray())
            {
                data.InputCount++;
                if (input.TryGetProperty("Source", out var source)
                    && source.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String)
                    data.InputIds.Add(id.GetString()!);
            }

            foreach (var output in setting.GetProperty("Output").EnumerateArray())
            {
                data.OutputCount++;
                if (output.TryGetProperty("Destination", out var destination)
                    && destination.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String)
                    data.OutputIds.Add(id.GetString()!);
            }
        }

        data.DistinctOutputCount = data.OutputIds.Count;
        return data;
    }

    private static bool TryGetRange(JsonElement normalization, string key, bool requireDefault, out (double Min, double Max) range)
    {
        range = default;
        if (!normalization.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty("Minimum", out var minimum) || minimum.ValueKind != JsonValueKind.Number) return false;
        if (!element.TryGetProperty("Maximum", out var maximum) || maximum.ValueKind != JsonValueKind.Number) return false;
        range = (minimum.GetDouble(), maximum.GetDouble());
        if (!(range.Min < range.Max)) return false;
        if (requireDefault)
        {
            if (!element.TryGetProperty("Default", out var defaultValue) || defaultValue.ValueKind != JsonValueKind.Number) return false;
            var d = defaultValue.GetDouble();
            if (!(range.Min <= d && d <= range.Max)) return false;
        }

        return true;
    }

    private sealed class Cdi3Data
    {
        public int ParameterCount;
        public int ParameterGroupCount;
        public int ChineseParameterNameCount;
        public int PartCount;
        public int ChinesePartNameCount;
        public bool HasPartGroups;
        public readonly List<string> ParameterGroupNames = new();
    }

    private static Cdi3Data ParseCdi3(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var data = new Cdi3Data();
        if (root.TryGetProperty("Parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            data.ParameterCount = parameters.GetArrayLength();
            foreach (var parameter in parameters.EnumerateArray())
            {
                if (parameter.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
                    && ContainsCjk(name.GetString() ?? ""))
                    data.ChineseParameterNameCount++;
            }
        }

        if (root.TryGetProperty("ParameterGroups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            data.ParameterGroupCount = groups.GetArrayLength();
            foreach (var group in groups.EnumerateArray())
            {
                if (group.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String)
                    data.ParameterGroupNames.Add(name.GetString() ?? "");
            }
        }

        if (root.TryGetProperty("Parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            data.PartCount = parts.GetArrayLength();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
                    && ContainsCjk(name.GetString() ?? ""))
                    data.ChinesePartNameCount++;
            }
        }

        data.HasPartGroups = root.TryGetProperty("PartGroups", out var partGroups)
                             && partGroups.ValueKind == JsonValueKind.Array && partGroups.GetArrayLength() > 0;
        return data;
    }

    // ====================================================================
    // 动捕自检用的合成人脸图（BGRA32；不依赖摄像头 / 不读磁盘图片）
    // ====================================================================

    /// <summary>
    /// 造一张「人脸」合成图：肤色背景 + 脸框（稍亮肤色）；
    /// 睁眼时在眼睛横带里画亮眼白 + 暗瞳孔（灰度标准差大），闭眼保持肤色；
    /// 张嘴时在嘴部横带里画暗色口腔（暗像素比例上升）。
    /// <para>区域比例严格按 <see cref="FaceExpressionAnalyzer"/> 取的横带（眼 30%~56%、
    /// 嘴 60%~88%、脸颊 55%~80%）来画，保证启发式拿到的就是测试想给它的信号。</para>
    /// </summary>
    private static byte[] MakeSyntheticFaceFrame(int width, int height,
        FaceExpressionAnalyzer.FaceBox box, bool leftEyeOpen, bool rightEyeOpen, bool mouthOpen)
    {
        var bgra = new byte[width * height * 4];
        FillSyntheticRect(bgra, width, height, 0, 0, width, height, 165);                  // 背景
        FillSyntheticRect(bgra, width, height, box.X, box.Y, box.Width, box.Height, 185);  // 脸

        var eyeTop = box.Y + (int)(box.Height * 0.30);
        var eyeBottom = box.Y + (int)(box.Height * 0.56);
        var leftEyeLeft = box.X + (int)(box.Width * 0.10);
        var leftEyeRight = box.X + (int)(box.Width * 0.44);
        var rightEyeLeft = box.X + (int)(box.Width * 0.56);
        var rightEyeRight = box.X + (int)(box.Width * 0.90);
        if (leftEyeOpen) DrawSyntheticOpenEye(bgra, width, height, leftEyeLeft, eyeTop, leftEyeRight, eyeBottom);
        if (rightEyeOpen) DrawSyntheticOpenEye(bgra, width, height, rightEyeLeft, eyeTop, rightEyeRight, eyeBottom);

        if (mouthOpen)
        {
            var mouthLeft = box.X + (int)(box.Width * 0.30);
            var mouthTop = box.Y + (int)(box.Height * 0.60);
            var mouthWidth = (int)(box.Width * 0.40);
            var mouthHeight = (int)(box.Height * 0.28);
            FillSyntheticRect(bgra, width, height,
                mouthLeft + (int)(mouthWidth * 0.10), mouthTop + (int)(mouthHeight * 0.15),
                (int)(mouthWidth * 0.80), (int)(mouthHeight * 0.70), 25);                  // 暗口腔
        }

        return bgra;
    }

    /// <summary>画一只睁开的眼睛：亮眼白 + 居中暗瞳孔（制造明显灰度方差）。</summary>
    private static void DrawSyntheticOpenEye(byte[] bgra, int width, int height, int x0, int y0, int x1, int y1)
    {
        var eyeWidth = x1 - x0;
        var eyeHeight = y1 - y0;
        if (eyeWidth <= 0 || eyeHeight <= 0) return;

        FillSyntheticRect(bgra, width, height, x0, y0, eyeWidth, eyeHeight, 235); // 眼白
        var pupilWidth = Math.Max(2, eyeWidth / 3);
        var pupilHeight = Math.Max(2, eyeHeight / 2);
        FillSyntheticRect(bgra, width, height,
            x0 + (eyeWidth - pupilWidth) / 2, y0 + (eyeHeight - pupilHeight) / 2,
            pupilWidth, pupilHeight, 20);                                        // 瞳孔
    }

    /// <summary>在 BGRA32 缓冲里填一个矩形（灰度值；自动裁到图像范围内）。</summary>
    private static void FillSyntheticRect(byte[] bgra, int width, int height,
        int x, int y, int w, int h, byte gray)
    {
        var x0 = Math.Clamp(x, 0, Math.Max(0, width - 1));
        var y0 = Math.Clamp(y, 0, Math.Max(0, height - 1));
        var x1 = Math.Clamp(x + w, 0, width);
        var y1 = Math.Clamp(y + h, 0, height);
        if (x1 <= x0 || y1 <= y0) return;

        for (var py = y0; py < y1; py++)
        {
            var row = py * width;
            for (var px = x0; px < x1; px++)
            {
                var index = (row + px) * 4;
                bgra[index] = gray;       // B
                bgra[index + 1] = gray;   // G
                bgra[index + 2] = gray;   // R
                bgra[index + 3] = 255;    // A
            }
        }
    }

    // ====================================================================
    // 动捕运行时集成自检用的假数据源（绝不打开摄像头）
    // ====================================================================

    /// <summary>造一帧「有脸」的动捕结果（时间戳取当前，避免被 600ms 过期检查挡掉）。</summary>
    private static MocapTrackingResult MakeFakeTracking(double yaw, double pitch, double eyeOpen, double mouth)
    {
        var (pupilX, pupilY) = MocapMath.MapPupil(yaw, pitch);
        return new MocapTrackingResult
        {
            HasFace = true,
            FaceX = 0.5,
            FaceY = 0.5,
            FaceScale = 0.3,
            Yaw = yaw,
            Pitch = pitch,
            Roll = 0,
            EyeOpenLeft = eyeOpen,
            EyeOpenRight = eyeOpen,
            MouthOpen = mouth,
            PupilX = pupilX,
            PupilY = pupilY,
            TimestampMs = Environment.TickCount64,
        };
    }

    /// <summary>自检用假动捕数据源：只提供内存里的固定结果，不碰任何摄像头 / WinRT API。</summary>
    private sealed class FakeMocapProvider : IMocapProvider
    {
        public MocapTrackingResult? Latest { get; set; }

        public MocapPreviewFrame? LatestPreview => null;

        public bool IsRunning { get; set; } = true;

        public string? LastError => null;

        public double MeasuredFps => 15;

        public (int Width, int Height) FrameSize => (640, 360);

        /// <summary>自检从不订阅状态变化；用空访问器实现接口（避免「事件未使用」的编译警告）。</summary>
        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }
    }

    /// <summary>假的音乐数据源：只放快照，不碰 WASAPI / 麦克风（自检用）。</summary>
    private sealed class FakeMusicProvider : IMusicReactiveProvider
    {
        public MusicAnalysisSnapshot? Latest { get; set; }

        public bool IsRunning { get; set; } = true;

        public string? LastError => null;

        public string MediaTitle => "自检歌曲";

        public string MediaArtist => "自检歌手";

        public string MediaApp => "selfcheck.exe";

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }
    }

    /// <summary>造一帧音乐分析快照（时间戳默认「现在」，可测数据过期场景）。</summary>
    private static MusicAnalysisSnapshot MakeMusicSnapshot(MusicContentKind kind, double mouthOpen, double mouthForm,
        double beatPhase, double beatStrength, double bpm, double pitchHz = 220.0, bool pitchValid = true,
        double centerVoiceRatio = 0.9, double syllabicModulation = 0.5)
    {
        return new MusicAnalysisSnapshot
        {
            TimestampMs = Environment.TickCount64,
            IsPlaying = kind is MusicContentKind.VocalSong or MusicContentKind.Instrumental,
            Kind = kind,
            Confidence = 0.8,
            VocalScore = kind == MusicContentKind.VocalSong ? 0.7 : 0.2,
            Rms = 0.5,
            BassEnergy = 0.3,
            VoiceEnergy = 0.4,
            MouthOpen = mouthOpen,
            MouthForm = mouthForm,
            BeatPhase = beatPhase,
            Bpm = bpm,
            BeatStrength = beatStrength,
            PitchValid = pitchValid,
            PitchHz = pitchHz,
            CenterVoiceRatio = centerVoiceRatio,
            SyllabicModulation = syllabicModulation,
        };
    }

    /// <summary>复制快照并替换时间戳（测「数据过期」用）。</summary>
    private static MusicAnalysisSnapshot MusicSnapshotWithTimestamp(MusicAnalysisSnapshot snapshot, long timestampMs)
    {
        return new MusicAnalysisSnapshot
        {
            TimestampMs = timestampMs,
            IsPlaying = snapshot.IsPlaying,
            Kind = snapshot.Kind,
            Confidence = snapshot.Confidence,
            VocalScore = snapshot.VocalScore,
            Rms = snapshot.Rms,
            BassEnergy = snapshot.BassEnergy,
            VoiceEnergy = snapshot.VoiceEnergy,
            MouthOpen = snapshot.MouthOpen,
            MouthForm = snapshot.MouthForm,
            BeatPhase = snapshot.BeatPhase,
            Bpm = snapshot.Bpm,
            BeatStrength = snapshot.BeatStrength,
            PitchValid = snapshot.PitchValid,
            PitchHz = snapshot.PitchHz,
            CenterVoiceRatio = snapshot.CenterVoiceRatio,
            SyllabicModulation = snapshot.SyllabicModulation,
        };
    }

    // ====================================================================
    // 杂项工具
    // ====================================================================

    /// <summary>在 STA 线程里跑一段需要 WPF 位图的断言；超时 / 异常只记 FAIL，不中断整个分组。</summary>
    private static void RunOnSta(string label, Action<string, bool, string> check, Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "live2d-selfcheck-sta",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(180)))
            check($"{label}：STA 测试线程超时（180 秒）", false, "");
        else if (failure is not null)
            check($"{label}：STA 测试线程异常（{failure.GetType().Name}）", false, failure.Message);
    }

    private static bool TryParseVersion(string text, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        var parts = text.Split('.');
        return parts.Length == 3
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor)
               && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out patch);
    }

    private static bool Near(double actual, double expected, double tolerance) => Math.Abs(actual - expected) <= tolerance;

    private static bool ContainsCjk(string text)
        => text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    private static int TryGetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : -1;

    private static bool IsRenderOrderSorted(CubismModel model)
    {
        var order = model.RenderOrderedDrawableIndices;
        for (var i = 1; i < order.Length; i++)
            if (model.Drawables[order[i]].RenderOrder < model.Drawables[order[i - 1]].RenderOrder)
                return false;
        return true;
    }

    /// <summary>把参数从默认值改成目标值，数有多少个网格的顶点动了（比完恢复默认）。</summary>
    private static int CountDrawablesWithVertexChanges(CubismModel model, Action applyChange)
    {
        model.ResetParametersToDefault();
        model.Update();
        var snapshot = model.Drawables.Select(d => d.VertexPositions.ToArray()).ToArray();
        applyChange();
        model.Update();

        var changed = 0;
        for (var i = 0; i < model.Drawables.Length; i++)
        {
            var current = model.Drawables[i].VertexPositions;
            var before = snapshot[i];
            if (current.Length != before.Length) { changed++; continue; }
            for (var v = 0; v < current.Length; v++)
            {
                if (Math.Abs(current[v] - before[v]) <= 1e-6f) continue;
                changed++;
                break;
            }
        }

        model.ResetParametersToDefault();
        model.Update();
        return changed;
    }

    private static bool TryGetUnionCanvasBounds(CubismModel model, out float minX, out float minY, out float maxX, out float maxY)
    {
        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;
        foreach (var drawable in model.Drawables)
        {
            if (!drawable.IsVisible || drawable.Opacity <= 0.01f) continue;
            if (!drawable.TryGetCanvasBounds(out var x0, out var y0, out var x1, out var y1)) continue;
            minX = Math.Min(minX, x0);
            minY = Math.Min(minY, y0);
            maxX = Math.Max(maxX, x1);
            maxY = Math.Max(maxY, y1);
        }

        return minX <= maxX && minY <= maxY;
    }

    private static byte[] ReadPixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private readonly record struct AlphaStats(int OpaquePixels, int SemiPixels, int[] CornerAlphas);

    private static AlphaStats GetAlphaStats(byte[] bgra, int width, int height)
    {
        var opaque = 0;
        var semi = 0;
        for (var i = 0; i < width * height; i++)
        {
            var a = bgra[i * 4 + 3];
            if (a >= 200) opaque++;
            else if (a > 0) semi++;
        }

        // 四角 alpha：左上 / 右上 / 右下 / 左下
        var corners = new[]
        {
            (int)bgra[3],
            (int)bgra[(width - 1) * 4 + 3],
            (int)bgra[(height * width - 1) * 4 + 3],
            (int)bgra[((height - 1) * width) * 4 + 3],
        };
        return new AlphaStats(opaque, semi, corners);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY)? OpaqueBounds(byte[] bgra, int width, int height, int alphaThreshold)
    {
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * width;
            for (var x = 0; x < width; x++)
            {
                if (bgra[(rowStart + x) * 4 + 3] < alphaThreshold) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }
}
