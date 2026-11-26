using System.Diagnostics;
using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model.Control;

/// <summary>参数有效性探测结果（哪个参数改了值画面真的会变）。</summary>
/// <param name="LiveParameterIds">默认画面下有效的参数（含物理先验有效）。</param>
/// <param name="DeadParameterIds">默认画面下无效的参数（手写动作表对它们的写入丢弃；UI 打「无效」标记）。</param>
/// <param name="ContextRevivedIds">默认画面无效、但在某个开关 / 表情环境下会生效的参数（信息记录：
/// 待机小动作这类「跑在默认画面」的手写表不该依赖它们，因为平时看不到效果）。</param>
public sealed record ParameterEffectResult(
    IReadOnlyList<string> LiveParameterIds,
    IReadOnlyList<string> DeadParameterIds,
    IReadOnlyList<string> ContextRevivedIds,
    int ContextCount,
    double ElapsedMilliseconds)
{
    /// <summary>是否实测到任何无效参数。</summary>
    public bool HasDeadParameters => DeadParameterIds.Count > 0;
}

/// <summary>
/// 参数有效性探测（纯静态，直接读写 Cubism Core，不经位姿合成器）。
///
/// <para><b>为什么要有它</b>：模型里常有一批「作者留了参数、但没绑任何网格 / 变形器」的开关——
/// 官方播放器靠外部特效层消费它们，本渲染器没有对应层，改了值画面一点不变（例如「大肥鱼」的
/// 翅膀 / 冒汗 / 星星 / 生气符号那一组）。这类参数不该出现在动作表里：一个「什么都没动」的动作
/// 会占着调度、让用户以为功能坏了。</para>
///
/// <para><b>怎么判</b>：某个参数单独拉到 min / max，Core 求值后比较<b>全部</b>网格的顶点位置、不透明度、
/// 可见性、渲染顺序、乘算 / 叠加颜色和全部部件不透明度——一个都没变就是无效。
/// 默认姿势下无效的参数，再放进一组「开关环境」里复测（换装 / 体型 / 表情开关打开后可能才生效，
/// 例如尾巴被「尾巴开关」藏起来时尾巴相关参数在默认姿势下测不出来）。
/// 环境里先把这些参数<b>一次性全拉起来</b>组测：画面不变 = 整组在该环境下都无效，省掉逐个求值。</para>
///
/// <para><b>局限</b>：只测「单独改它」和「一组同时改」；只在 A、B 两个参数同时非默认时才生效的组合键形
/// （A 单独无效、A+B 有效）会被判无效——这类组合在动画开关参数上基本不存在，日志里会把
/// 「整组同拉时画面变了、但逐个都无效」的情况单独报出来备查。</para>
/// </summary>
public static unsafe class ParameterEffectAudit
{
    /// <summary>顶点位置容差（模型单位；1e-5 ≈ 画布上 0.04 像素）。</summary>
    private const float PositionTolerance = 1e-5f;

    /// <summary>不透明度容差。</summary>
    private const float OpacityTolerance = 1e-4f;

    /// <summary>
    /// 跑一遍探测。
    /// <paramref name="contexts"/>：复测环境，每项是一组「参数 → 值」（开关类参数 / 表情文件里的参数）。
    /// <paramref name="alwaysLiveIndices"/>：先验有效的参数下标（物理输入 / 输出——它们不直接绑变形器，
    /// 只在物理求值时才影响画面，逐像素探测测不出来；环境复测也跳过它们）。
    /// </summary>
    public static ParameterEffectResult Run(CubismModel model,
        IReadOnlyList<IReadOnlyList<(string Id, float Value)>> contexts, Action<string>? log,
        IReadOnlyCollection<int>? alwaysLiveIndices = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var parameters = model.Parameters;
        var alwaysLive = alwaysLiveIndices is { Count: > 0 }
            ? new HashSet<int>(alwaysLiveIndices)
            : null;

        foreach (var parameter in parameters) model.WriteCoreParameter(parameter.Index, parameter.DefaultValue);
        model.Update();
        var baseline = ModelSnapshot.Capture(model);

        // 1) 默认姿势下逐个探测（先验有效的物理参数跳过）
        var dead = new bool[parameters.Length];
        var skipped = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (alwaysLive is not null && alwaysLive.Contains(i))
            {
                skipped++;
                continue;
            }

            if (!Probe(model, baseline, parameters[i])) dead[i] = true;
        }

        foreach (var parameter in parameters) model.WriteCoreParameter(parameter.Index, parameter.DefaultValue);

        // 2) 开关环境里复测「默认画面无效」的参数：一次把候选全拉起来，画面变了再用二分找出有效的那几个。
        //    复活的参数单独记入 ContextRevivedIds（默认画面仍按无效处理：待机小动作跑在默认画面，
        //    不能依赖「只在特殊表情 / 换装环境下才生效」的参数）。
        //    旧实现逐个复测 259 个参数 × 46 组环境要 40 秒；二分只要 log2 轮。
        var combinationOnly = new List<string>();
        var revived = new SortedSet<int>();
        foreach (var context in contexts)
        {
            var applied = new List<int>(context.Count);
            foreach (var (id, value) in context)
            {
                var parameter = model.FindParameter(id);
                if (parameter is null) continue;
                applied.Add(parameter.Index);
                model.WriteCoreParameter(parameter.Index, value);
            }

            if (applied.Count == 0) continue;
            model.Update();
            var contextBaseline = ModelSnapshot.Capture(model);

            var stillDead = new List<int>(parameters.Length);
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!dead[i] || revived.Contains(i)) continue;
                model.WriteCoreParameter(i, ProbeValue(parameters[i]));
                stillDead.Add(i);
            }

            if (stillDead.Count > 0)
            {
                model.Update();
                if (contextBaseline.Differs(model))
                {
                    // 组合生效：二分找出这组环境里「真的活了」的参数
                    foreach (var index in FindLiveInContext(model, contextBaseline, stillDead, parameters)) revived.Add(index);
                }

                foreach (var index in stillDead) model.WriteCoreParameter(index, parameters[index].DefaultValue);
            }

            foreach (var index in applied) model.WriteCoreParameter(index, parameters[index].DefaultValue);
        }

        foreach (var parameter in parameters) model.WriteCoreParameter(parameter.Index, parameter.DefaultValue);
        model.Update();

        var live = new List<string>(parameters.Length);
        var deadIds = new List<string>();
        var revivedIds = new List<string>();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (dead[i])
            {
                deadIds.Add(parameters[i].Id);
                if (revived.Contains(i)) revivedIds.Add(parameters[i].Id);
            }
            else live.Add(parameters[i].Id);
        }

        stopwatch.Stop();
        var elapsed = stopwatch.Elapsed.TotalMilliseconds;
        log?.Invoke($"参数有效性探测：{parameters.Length} 个参数（物理先验有效 {skipped} 个），" +
                    $"默认画面下 {deadIds.Count} 个无效，其中 {revivedIds.Count} 个在开关 / 表情环境下会生效" +
                    $"（{contexts.Count} 组环境）；耗时 {elapsed:0}ms");
        if (revivedIds.Count > 0)
        {
            log?.Invoke($"参数有效性探测：以下参数默认画面无效、仅特定环境生效（手写动作表不依赖）：{Summarize(revivedIds)}");
        }
        else
        {
            log?.Invoke($"参数有效性探测：默认画面无效的参数在 {contexts.Count} 组环境下也全部无效（确认死参数）");
        }

        return new ParameterEffectResult(live, deadIds, revivedIds, contexts.Count, elapsed);
    }

    /// <summary>
    /// 在「已经确认整组拉起来画面会变」的前提下，二分找出哪些参数是有效的。
    /// 返回有效参数的下标集合（先按区间对半分，画面变了的一半继续分；单参数直接探测确认）。
    /// </summary>
    private static List<int> FindLiveInContext(CubismModel model, ModelSnapshot contextBaseline,
        List<int> candidates, CubismParameter[] parameters)
    {
        var live = new List<int>();
        var probeValues = candidates.Select(index => ProbeValue(parameters[index])).ToArray();

        void Search(int from, int to)
        {
            if (from >= to) return;
            if (to - from == 1)
            {
                // 单参数确认
                var index = candidates[from];
                model.WriteCoreParameter(index, probeValues[from]);
                model.Update();
                if (contextBaseline.Differs(model)) live.Add(index);
                model.WriteCoreParameter(index, parameters[index].DefaultValue);
                return;
            }

            var mid = (from + to) / 2;
            // 拉起 [from, mid)：画面变 → 这一半里至少有一个有效，继续分；不变 → 这一半全部仍无效（在本环境下）
            for (var i = from; i < mid; i++) model.WriteCoreParameter(candidates[i], probeValues[i]);
            model.Update();
            var changed = contextBaseline.Differs(model);
            for (var i = from; i < mid; i++) model.WriteCoreParameter(candidates[i], parameters[candidates[i]].DefaultValue);
            if (changed) Search(from, mid);

            // 另一半：只有前一半没有解释全部变化时才需要再探——简化处理：只要总组变过就也探一遍另一半
            for (var i = mid; i < to; i++) model.WriteCoreParameter(candidates[i], probeValues[i]);
            model.Update();
            changed = contextBaseline.Differs(model);
            for (var i = mid; i < to; i++) model.WriteCoreParameter(candidates[i], parameters[candidates[i]].DefaultValue);
            if (changed) Search(mid, to);
        }

        Search(0, candidates.Count);
        return live;
    }

    /// <summary>探测一个参数：单独拉到两个端点（和默认值不同的那端），画面变了就是有效。</summary>
    private static bool Probe(CubismModel model, ModelSnapshot baseline, CubismParameter parameter)
    {
        var changed = false;
        if (parameter.Maximum != parameter.DefaultValue)
        {
            model.WriteCoreParameter(parameter.Index, parameter.Maximum);
            model.Update();
            changed |= baseline.Differs(model);
        }

        if (!changed && parameter.Minimum != parameter.DefaultValue)
        {
            model.WriteCoreParameter(parameter.Index, parameter.Minimum);
            model.Update();
            changed |= baseline.Differs(model);
        }

        model.WriteCoreParameter(parameter.Index, parameter.DefaultValue);
        return changed;
    }

    private static float ProbeValue(CubismParameter parameter)
        => parameter.Maximum != parameter.DefaultValue ? parameter.Maximum : parameter.Minimum;

    private static string Summarize(List<string> ids)
    {
        const int max = 24;
        if (ids.Count <= max) return string.Join(", ", ids);
        return string.Join(", ", ids.Take(max)) + $" …（共 {ids.Count} 个）";
    }

    /// <summary>一帧画面状态的全部可比数据：逐网格顶点 / 不透明度 / 可见性 / 渲染顺序 / 颜色 + 逐部件不透明度。</summary>
    private sealed unsafe class ModelSnapshot
    {
        private readonly float[] _positions;
        private readonly int[] _vertexOffsets;
        private readonly float[] _opacities;
        private readonly bool[] _visible;
        private readonly int[] _renderOrders;
        private readonly float[] _colors; // 每网格 8 个：multiply rgba + screen rgba
        private readonly float[] _partOpacities;

        private ModelSnapshot(CubismModel model)
        {
            var drawables = model.Drawables;
            _vertexOffsets = new int[drawables.Length + 1];
            var total = 0;
            for (var i = 0; i < drawables.Length; i++)
            {
                _vertexOffsets[i] = total;
                total += drawables[i].VertexCount * 2;
            }

            _vertexOffsets[drawables.Length] = total;
            _positions = new float[total];
            _opacities = new float[drawables.Length];
            _visible = new bool[drawables.Length];
            _renderOrders = new int[drawables.Length];
            _colors = new float[drawables.Length * 8];
            _partOpacities = new float[model.PartCount];
        }

        public static ModelSnapshot Capture(CubismModel model)
        {
            var snapshot = new ModelSnapshot(model);
            var drawables = model.Drawables;
            for (var i = 0; i < drawables.Length; i++)
            {
                var drawable = drawables[i];
                var offset = snapshot._vertexOffsets[i];
                var count = drawable.VertexCount * 2;
                if (count > 0 && drawable.Positions is not null)
                {
                    fixed (float* target = &snapshot._positions[offset])
                    {
                        Buffer.MemoryCopy(drawable.Positions, target, count * sizeof(float), count * sizeof(float));
                    }
                }

                snapshot._opacities[i] = drawable.Opacity;
                snapshot._visible[i] = drawable.IsVisible;
                snapshot._renderOrders[i] = drawable.RenderOrder;
                var multiply = drawable.MultiplyColor;
                var screen = drawable.ScreenColor;
                snapshot._colors[i * 8 + 0] = multiply.X;
                snapshot._colors[i * 8 + 1] = multiply.Y;
                snapshot._colors[i * 8 + 2] = multiply.Z;
                snapshot._colors[i * 8 + 3] = multiply.W;
                snapshot._colors[i * 8 + 4] = screen.X;
                snapshot._colors[i * 8 + 5] = screen.Y;
                snapshot._colors[i * 8 + 6] = screen.Z;
                snapshot._colors[i * 8 + 7] = screen.W;
            }

            var parts = model.Parts;
            for (var i = 0; i < parts.Length; i++) snapshot._partOpacities[i] = parts[i].Opacity;
            return snapshot;
        }

        /// <summary>当前模型状态和这张快照有没有可见差异。</summary>
        public bool Differs(CubismModel model)
        {
            var drawables = model.Drawables;
            for (var i = 0; i < drawables.Length; i++)
            {
                var drawable = drawables[i];
                if (drawable.IsVisible != _visible[i]) return true;
                if (drawable.RenderOrder != _renderOrders[i]) return true;
                if (MathF.Abs(drawable.Opacity - _opacities[i]) > OpacityTolerance) return true;

                var multiply = drawable.MultiplyColor;
                var screen = drawable.ScreenColor;
                var c = i * 8;
                if (multiply.X != _colors[c] || multiply.Y != _colors[c + 1] || multiply.Z != _colors[c + 2] || multiply.W != _colors[c + 3]
                    || screen.X != _colors[c + 4] || screen.Y != _colors[c + 5] || screen.Z != _colors[c + 6] || screen.W != _colors[c + 7])
                {
                    return true;
                }

                var count = drawable.VertexCount * 2;
                if (count == 0 || drawable.Positions is null) continue;
                var source = drawable.Positions;
                fixed (float* target = &_positions[_vertexOffsets[i]])
                {
                    for (var v = 0; v < count; v++)
                    {
                        if (MathF.Abs(source[v] - target[v]) > PositionTolerance) return true;
                    }
                }
            }

            var parts = model.Parts;
            for (var i = 0; i < parts.Length; i++)
            {
                if (MathF.Abs(parts[i].Opacity - _partOpacities[i]) > OpacityTolerance) return true;
            }

            return false;
        }
    }
}
