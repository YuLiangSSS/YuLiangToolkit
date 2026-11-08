using System.Text.Json;
using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>exp3.json 的混合模式。</summary>
internal enum ExpressionBlendType
{
    /// <summary>加算（官方 "Add"/"Additive"；缺省也是加算）。</summary>
    Additive,

    /// <summary>乘算（"Multiply"）。</summary>
    Multiply,

    /// <summary>覆盖（"Overwrite"）。</summary>
    Overwrite,
}

/// <summary>exp3.json 里的一条参数。</summary>
internal readonly struct ExpressionParameter
{
    public required string Id { get; init; }
    public required float Value { get; init; }
    public required ExpressionBlendType Blend { get; init; }
}

/// <summary>解析好的 exp3.json（官方 <c>CubismExpressionMotion</c> 的 JSON 部分）。</summary>
internal sealed class Expression3Data
{
    public const float DefaultFadeTime = 1.0f;

    public float FadeInTime { get; private init; } = DefaultFadeTime;
    public float FadeOutTime { get; private init; } = DefaultFadeTime;
    public ExpressionParameter[] Parameters { get; private init; } = Array.Empty<ExpressionParameter>();

    public static Expression3Data Load(string path) => Parse(File.ReadAllText(path));

    public static Expression3Data Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var fadeIn = ReadFloat(root, "FadeInTime", DefaultFadeTime);
        var fadeOut = ReadFloat(root, "FadeOutTime", DefaultFadeTime);

        var parameters = new List<ExpressionParameter>();
        if (root.TryGetProperty("Parameters", out var parameterArray) && parameterArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in parameterArray.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(element, "Id");
                if (string.IsNullOrEmpty(id)) continue;

                var value = ReadFloat(element, "Value", 0);
                var blend = ReadString(element, "Blend");
                var blendType = blend switch
                {
                    "Multiply" => ExpressionBlendType.Multiply,
                    "Overwrite" => ExpressionBlendType.Overwrite,
                    _ => ExpressionBlendType.Additive, // "Add" / "Additive" / 未知名都按加算恢复
                };

                parameters.Add(new ExpressionParameter { Id = id, Value = value, Blend = blendType });
            }
        }

        return new Expression3Data
        {
            FadeInTime = fadeIn,
            FadeOutTime = fadeOut,
            Parameters = parameters.ToArray(),
        };
    }

    private static float ReadFloat(JsonElement element, string property, float fallback)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : fallback;

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

/// <summary>
/// 表情管理器：多个表情可以<b>同时叠加</b>（这个模型的 23 个表情是「开关型」参数，例如 key1=黑脸、
/// key3=圈圈眼，可以一起开），每个表情按自己的淡入淡出权重逐条施加。
///
/// <para><b>和官方的差异</b>：官方 <c>CubismExpressionMotionManager</c> 是按「队列里前一个表情被后一个覆盖」
/// 的交叉淡化语义写的（后一个表情不引用的参数会被它冲回默认），适合同一时间只显示一个表情的场景。
/// 这里要支持 UI 上的多选开关，所以改成「每个表情独立按权重叠加」：加算直接相加、乘算相乘、覆盖按权重插值。
/// 单个表情时结果与官方一致。</para>
/// </summary>
internal sealed class ExpressionManager
{
    private sealed class Instance
    {
        public required ExpressionEntry Entry { get; init; }
        public required Expression3Data Data { get; init; }
        public double FadeInStartTime { get; init; }
        public double FadeInSeconds { get; init; }
        public bool FadingOut { get; set; }
        public double FadeOutStartTime { get; set; }
        public double FadeOutSeconds { get; set; }
    }

    private readonly List<Instance> _instances = new(4);
    private double _userTime;

    public bool HasActive => _instances.Count > 0;

    public int ActiveCount => _instances.Count;

    /// <summary>某个表情是否正在生效（UI 勾选状态用）。</summary>
    public bool IsActive(ExpressionEntry entry)
    {
        foreach (var instance in _instances)
        {
            if (ReferenceEquals(instance.Entry, entry) ||
                string.Equals(instance.Entry.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>施加表情：同一个表情重复施加时重新淡入；不同表情叠加。</summary>
    public void Apply(ExpressionEntry entry, Expression3Data data, double fadeSeconds)
    {
        for (var i = _instances.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_instances[i].Entry.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
                _instances.RemoveAt(i);
        }

        _instances.Add(new Instance
        {
            Entry = entry,
            Data = data,
            FadeInStartTime = _userTime,
            FadeInSeconds = fadeSeconds >= 0 ? fadeSeconds : data.FadeInTime,
        });
    }

    /// <summary>清除所有表情（淡出；fadeSeconds ≤ 0 立即清掉）。</summary>
    public void Clear(double fadeSeconds)
    {
        if (fadeSeconds <= 0)
        {
            _instances.Clear();
            return;
        }

        foreach (var instance in _instances)
        {
            instance.FadingOut = true;
            instance.FadeOutStartTime = _userTime;
            instance.FadeOutSeconds = fadeSeconds;
        }
    }

    /// <summary>移除单个表情（只淡出它，其它叠加的表情继续；fadeSeconds ≤ 0 立即移除）。</summary>
    public void Remove(ExpressionEntry entry, double fadeSeconds)
    {
        for (var i = _instances.Count - 1; i >= 0; i--)
        {
            var instance = _instances[i];
            if (!ReferenceEquals(instance.Entry, entry) &&
                !string.Equals(instance.Entry.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (fadeSeconds <= 0)
            {
                _instances.RemoveAt(i);
                continue;
            }

            instance.FadingOut = true;
            instance.FadeOutStartTime = _userTime;
            instance.FadeOutSeconds = fadeSeconds;
        }
    }

    /// <summary>立刻清空（不淡出）。</summary>
    public void ClearImmediate() => _instances.Clear();

    /// <summary>推进一帧：按每个表情自己的淡入淡出权重逐条写参数。</summary>
    public void Update(CubismModel model, double deltaSeconds)
    {
        if (deltaSeconds <= 0) return;
        _userTime += deltaSeconds;

        for (var i = _instances.Count - 1; i >= 0; i--)
        {
            var instance = _instances[i];
            if (instance.FadingOut && _userTime >= instance.FadeOutStartTime + instance.FadeOutSeconds)
                _instances.RemoveAt(i);
        }

        foreach (var instance in _instances)
        {
            var weight = ComputeFadeWeight(instance);
            if (weight <= 0) continue;

            foreach (var expressionParameter in instance.Data.Parameters)
            {
                var parameter = model.FindParameter(expressionParameter.Id);
                if (parameter is null) continue;

                var currentValue = parameter.Value;
                switch (expressionParameter.Blend)
                {
                    case ExpressionBlendType.Multiply:
                    {
                        // 乘算：从 1 平滑过渡到目标倍数
                        var multiplier = 1.0f + ((expressionParameter.Value - 1.0f) * weight);
                        parameter.Value = currentValue * multiplier;
                        break;
                    }

                    case ExpressionBlendType.Overwrite:
                    {
                        // 覆盖：从当前值平滑过渡到目标值
                        parameter.Value = currentValue + ((expressionParameter.Value - currentValue) * weight);
                        break;
                    }

                    default:
                    {
                        // 加算：在默认值（每帧已恢复）基础上叠加按权重缩放的值
                        parameter.Value = currentValue + (expressionParameter.Value * weight);
                        break;
                    }
                }
            }
        }
    }

    private float ComputeFadeWeight(Instance instance)
    {
        var fadeIn = instance.FadeInSeconds <= 0
            ? 1.0f
            : MotionPlayer.EasingSine((_userTime - instance.FadeInStartTime) / instance.FadeInSeconds);
        var fadeOut = !instance.FadingOut || instance.FadeOutSeconds <= 0
            ? 1.0f
            : MotionPlayer.EasingSine(
                (instance.FadeOutStartTime + instance.FadeOutSeconds - _userTime) / instance.FadeOutSeconds);
        return fadeIn * fadeOut;
    }
}
