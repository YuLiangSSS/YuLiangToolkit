using System.Text.Json;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>motion3.json 的曲线段类型（数值与 JSON 里的段类型一致）。</summary>
internal enum MotionSegmentType
{
    /// <summary>0：线性插值。</summary>
    Linear = 0,

    /// <summary>1：贝塞尔（3 个控制点）。</summary>
    Bezier = 1,

    /// <summary>2：阶梯（值保持段起点，到段终点瞬间跳变）。</summary>
    Stepped = 2,

    /// <summary>3：反向阶梯（整段保持段终点值）。</summary>
    InverseStepped = 3,
}

/// <summary>曲线目标类型。</summary>
internal enum MotionCurveTarget
{
    /// <summary>模型级效果（EyeBlink / LipSync / Opacity）。</summary>
    Model,

    /// <summary>参数。</summary>
    Parameter,

    /// <summary>部件不透明度。</summary>
    PartOpacity,
}

/// <summary>
/// 一条曲线里的一个插值段：起点（上一段的终点）+ 可选的两个贝塞尔控制点 + 终点。
/// 段类型决定怎么用这些点（见 <see cref="Motion3Data.EvaluateSegment"/>）。
/// </summary>
internal struct MotionSegment
{
    public MotionSegmentType Type;
    public float StartTime;
    public float StartValue;
    public float Control1Time;
    public float Control1Value;
    public float Control2Time;
    public float Control2Value;
    public float EndTime;
    public float EndValue;
}

/// <summary>motion3.json 里的一条曲线。</summary>
internal sealed class MotionCurve
{
    public MotionCurveTarget Target;
    public string Id = "";
    public float FadeInTime = -1;
    public float FadeOutTime = -1;

    /// <summary>第一个关键点（段求值走到头时兜底用 / 循环结尾修正用）。</summary>
    public float FirstTime;
    public float FirstValue;

    public MotionSegment[] Segments = Array.Empty<MotionSegment>();
}

/// <summary>
/// 解析好的 motion3.json。语义对照官方 <c>CubismNativeFramework/src/Motion/CubismMotion.cpp</c>：
/// <list type="bullet">
/// <item>段类型 0=线性 / 1=贝塞尔 / 2=阶梯 / 3=反向阶梯；</item>
/// <item><c>Meta.AreBeziersRestricted</c> 为 true 时贝塞尔按参数 t 直接求值，否则用 Cardano 算法解 x(t)=time；</item>
/// <item>循环动作在 <c>Meta.Duration + 1/Fps</c> 处回绕，末段到首点做线性修正（V2 行为）。</item>
/// </list>
/// </summary>
internal sealed class Motion3Data
{
    private const float BezierEpsilon = 0.00001f;

    public float Duration { get; private init; }
    public float Fps { get; private init; } = 30;
    public bool Loop { get; private init; }
    public bool AreBeziersRestricted { get; private init; }
    public bool HasFadeInTime { get; private init; }
    public bool HasFadeOutTime { get; private init; }
    public float FadeInTime { get; private init; } = 1.0f;
    public float FadeOutTime { get; private init; } = 1.0f;
    public MotionCurve[] Curves { get; private init; } = Array.Empty<MotionCurve>();

    public static Motion3Data Load(string path) => Parse(File.ReadAllText(path));

    public static Motion3Data Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var duration = 0f;
        var fps = 30f;
        var loop = false;
        var areBeziersRestricted = false;
        var hasFadeIn = false;
        var hasFadeOut = false;
        var fadeIn = 1.0f;
        var fadeOut = 1.0f;

        if (root.TryGetProperty("Meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            duration = ReadFloat(meta, "Duration", 0);
            fps = ReadFloat(meta, "Fps", 30);
            if (fps <= 0) fps = 30;
            loop = meta.TryGetProperty("Loop", out var loopElement) && loopElement.ValueKind == JsonValueKind.True;
            areBeziersRestricted = meta.TryGetProperty("AreBeziersRestricted", out var restricted) &&
                                   restricted.ValueKind == JsonValueKind.True;

            if (meta.TryGetProperty("FadeInTime", out var fadeInElement) && fadeInElement.ValueKind == JsonValueKind.Number)
            {
                hasFadeIn = true;
                fadeIn = fadeInElement.GetSingle();
                if (fadeIn < 0) fadeIn = 1.0f;
            }

            if (meta.TryGetProperty("FadeOutTime", out var fadeOutElement) && fadeOutElement.ValueKind == JsonValueKind.Number)
            {
                hasFadeOut = true;
                fadeOut = fadeOutElement.GetSingle();
                if (fadeOut < 0) fadeOut = 1.0f;
            }
        }

        var curves = new List<MotionCurve>();
        if (root.TryGetProperty("Curves", out var curveArray) && curveArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var curveElement in curveArray.EnumerateArray())
            {
                if (curveElement.ValueKind != JsonValueKind.Object) continue;
                var curve = ParseCurve(curveElement);
                if (curve is not null) curves.Add(curve);
            }
        }

        return new Motion3Data
        {
            Duration = duration,
            Fps = fps,
            Loop = loop,
            AreBeziersRestricted = areBeziersRestricted,
            HasFadeInTime = hasFadeIn,
            HasFadeOutTime = hasFadeOut,
            FadeInTime = fadeIn,
            FadeOutTime = fadeOut,
            Curves = curves.ToArray(),
        };
    }

    private static MotionCurve? ParseCurve(JsonElement curveElement)
    {
        var curve = new MotionCurve
        {
            Id = ReadString(curveElement, "Id"),
        };

        var target = ReadString(curveElement, "Target");
        curve.Target = target switch
        {
            "Model" => MotionCurveTarget.Model,
            "PartOpacity" => MotionCurveTarget.PartOpacity,
            _ => MotionCurveTarget.Parameter,
        };

        if (curveElement.TryGetProperty("FadeInTime", out var fadeIn) && fadeIn.ValueKind == JsonValueKind.Number)
            curve.FadeInTime = fadeIn.GetSingle();
        if (curveElement.TryGetProperty("FadeOutTime", out var fadeOut) && fadeOut.ValueKind == JsonValueKind.Number)
            curve.FadeOutTime = fadeOut.GetSingle();

        if (!curveElement.TryGetProperty("Segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            return null;

        // 先把整条曲线的数字读成 float 数组，再按官方规则切段
        var values = new float[segments.GetArrayLength()];
        var index = 0;
        foreach (var value in segments.EnumerateArray())
        {
            if (index >= values.Length) break;
            values[index++] = value.ValueKind == JsonValueKind.Number ? value.GetSingle() : 0f;
        }

        var result = new List<MotionSegment>();
        var firstPointTime = 0f;
        var firstPointValue = 0f;
        var cursor = 0;
        var currentTime = 0f;
        var currentValue = 0f;

        // 首段格式：[起始时间, 起始值, 段类型, ...]；之后每段都以段类型开头
        if (values.Length >= 2)
        {
            firstPointTime = values[0];
            firstPointValue = values[1];
            currentTime = firstPointTime;
            currentValue = firstPointValue;
            cursor = 2;
        }

        while (cursor < values.Length)
        {
            var segmentType = (MotionSegmentType)(int)values[cursor];
            cursor++;
            switch (segmentType)
            {
                case MotionSegmentType.Linear:
                case MotionSegmentType.Stepped:
                case MotionSegmentType.InverseStepped:
                {
                    if (cursor + 1 >= values.Length) { cursor = values.Length; break; }
                    var segment = new MotionSegment
                    {
                        Type = segmentType,
                        StartTime = currentTime,
                        StartValue = currentValue,
                        EndTime = values[cursor],
                        EndValue = values[cursor + 1],
                    };
                    cursor += 2;
                    result.Add(segment);
                    currentTime = segment.EndTime;
                    currentValue = segment.EndValue;
                    break;
                }

                case MotionSegmentType.Bezier:
                {
                    if (cursor + 5 >= values.Length) { cursor = values.Length; break; }
                    var segment = new MotionSegment
                    {
                        Type = segmentType,
                        StartTime = currentTime,
                        StartValue = currentValue,
                        Control1Time = values[cursor],
                        Control1Value = values[cursor + 1],
                        Control2Time = values[cursor + 2],
                        Control2Value = values[cursor + 3],
                        EndTime = values[cursor + 4],
                        EndValue = values[cursor + 5],
                    };
                    cursor += 6;
                    result.Add(segment);
                    currentTime = segment.EndTime;
                    currentValue = segment.EndValue;
                    break;
                }

                default:
                    // 未知段类型：直接放弃这条曲线的剩余部分，别死循环
                    cursor = values.Length;
                    break;
            }
        }

        curve.FirstTime = firstPointTime;
        curve.FirstValue = firstPointValue;
        curve.Segments = result.ToArray();
        return curve;
    }

    /// <summary>
    /// 求某条曲线在 <paramref name="time"/> 秒处的值（官方 <c>EvaluateCurve</c> 的移植）。
    /// </summary>
    /// <param name="isCorrection">循环修正：超出末段且还没到循环终点时，线性补回首点（V2 行为）。</param>
    /// <param name="endTime">循环修正用的终点时间（= Duration + 1/Fps）。</param>
    public float EvaluateCurve(int curveIndex, float time, bool isCorrection, float endTime)
    {
        var curve = Curves[curveIndex];
        var segments = curve.Segments;
        if (segments.Length == 0) return curve.FirstValue;

        var target = -1;
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].EndTime > time)
            {
                target = i;
                break;
            }
        }

        if (target < 0)
        {
            if (isCorrection && time < endTime)
            {
                // 循环结尾修正：从末段终点值线性回到首点值（首点时间替换成循环终点时间）
                var last = segments[^1];
                switch (last.Type)
                {
                    case MotionSegmentType.Stepped:
                        return last.EndValue;
                    case MotionSegmentType.InverseStepped:
                        return curve.FirstValue;
                    default:
                    {
                        var span = endTime - last.EndTime;
                        if (span <= 0) return curve.FirstValue;
                        var t = (time - last.EndTime) / span;
                        if (t < 0) t = 0;
                        return last.EndValue + ((curve.FirstValue - last.EndValue) * t);
                    }
                }
            }

            return segments[^1].EndValue;
        }

        return EvaluateSegment(segments[target], time);
    }

    /// <summary>按段类型求值（官方 Linear/Bezier/Stepped/InverseStepped 四个 Evaluate 函数的移植）。</summary>
    private float EvaluateSegment(in MotionSegment segment, float time)
    {
        switch (segment.Type)
        {
            case MotionSegmentType.Linear:
            {
                var t = LinearT(segment.StartTime, segment.EndTime, time);
                return segment.StartValue + ((segment.EndValue - segment.StartValue) * t);
            }

            case MotionSegmentType.Bezier:
            {
                var t = AreBeziersRestricted
                    ? LinearT(segment.StartTime, segment.EndTime, time)
                    : SolveBezierT(segment.StartTime, segment.Control1Time, segment.Control2Time, segment.EndTime, time);
                var p01Value = Lerp(segment.StartValue, segment.Control1Value, t);
                var p12Value = Lerp(segment.Control1Value, segment.Control2Value, t);
                var p23Value = Lerp(segment.Control2Value, segment.EndValue, t);

                var p012Value = Lerp(p01Value, p12Value, t);
                var p123Value = Lerp(p12Value, p23Value, t);
                return Lerp(p012Value, p123Value, t);
            }

            case MotionSegmentType.Stepped:
                return segment.StartValue;

            case MotionSegmentType.InverseStepped:
                return segment.EndValue;

            default:
                return segment.EndValue;
        }
    }

    private static float LinearT(float startTime, float endTime, float time)
    {
        var span = endTime - startTime;
        if (span == 0) return 1.0f;
        var t = (time - startTime) / span;
        return t < 0 ? 0 : t;
    }

    private static float Lerp(float from, float to, float t) => from + ((to - from) * t);

    /// <summary>
    /// 解贝塞尔曲线的 x(t) = time（官方 <c>BezierEvaluateCardanoInterpretation</c> + <c>CubismMath::CardanoAlgorithmForBezier</c>）。
    /// </summary>
    private static float SolveBezierT(float x1, float cx1, float cx2, float x2, float time)
    {
        var a = x2 - (3.0f * cx2) + (3.0f * cx1) - x1;
        var b = (3.0f * cx2) - (6.0f * cx1) + (3.0f * x1);
        var c = (3.0f * cx1) - (3.0f * x1);
        var d = x1 - time;
        return CardanoAlgorithmForBezier(a, b, c, d);
    }

    private static float CardanoAlgorithmForBezier(float a, float b, float c, float d)
    {
        // 官方 CubismMath::CardanoAlgorithmForBezier：a 太小时退化成二次方程
        // （注意官方是把 (b, c, d) 交给 QuadraticEquation(b, c, d)，即二次方程的系数是 b、c、d）
        if (MathF.Abs(a) < BezierEpsilon)
        {
            if (MathF.Abs(b) < BezierEpsilon)
            {
                // 三次、二次系数都≈0：退化成 c*t + d = 0
                if (MathF.Abs(c) < BezierEpsilon) return 0.5f;
                return Clamp01(-d / c);
            }

            var quadraticDiscriminant = (c * c) - (4.0f * b * d);
            if (quadraticDiscriminant < 0) quadraticDiscriminant = 0;
            return Clamp01(-(c + MathF.Sqrt(quadraticDiscriminant)) / (2.0f * b));
        }

        var ba = b / a;
        var ca = c / a;
        var da = d / a;

        var p = ((3.0f * ca) - (ba * ba)) / 3.0f;
        var p3 = p / 3.0f;
        var q = ((2.0f * ba * ba * ba) - (9.0f * ba * ca) + (27.0f * da)) / 27.0f;
        var q2 = q / 2.0f;
        var discriminant = (q2 * q2) + (p3 * p3 * p3);

        const float center = 0.5f;
        const float threshold = center + 0.01f;

        if (discriminant < 0.0f)
        {
            var mp3 = -p / 3.0f;
            var mp33 = mp3 * mp3 * mp3;
            var r = MathF.Sqrt(mp33);
            if (r <= 0) return 0.5f;
            var t = -q / (2.0f * r);
            var cosphi = Math.Clamp(t, -1.0f, 1.0f);
            var phi = MathF.Acos(cosphi);
            var crtr = MathF.Cbrt(r);
            var t1 = 2.0f * crtr;

            var root1 = (t1 * MathF.Cos(phi / 3.0f)) - (ba / 3.0f);
            if (MathF.Abs(root1 - center) < threshold) return Clamp01(root1);

            var root2 = (t1 * MathF.Cos((phi + (2.0f * MathF.PI)) / 3.0f)) - (ba / 3.0f);
            if (MathF.Abs(root2 - center) < threshold) return Clamp01(root2);

            var root3 = (t1 * MathF.Cos((phi + (4.0f * MathF.PI)) / 3.0f)) - (ba / 3.0f);
            return Clamp01(root3);
        }

        if (discriminant == 0.0f)
        {
            var u1 = q2 < 0.0f ? MathF.Cbrt(-q2) : -MathF.Cbrt(q2);
            var root1 = (2.0f * u1) - (ba / 3.0f);
            if (MathF.Abs(root1 - center) < threshold) return Clamp01(root1);
            var root2 = -u1 - (ba / 3.0f);
            return Clamp01(root2);
        }

        var sd = MathF.Sqrt(discriminant);
        var u = MathF.Cbrt(sd - q2);
        var v = MathF.Cbrt(sd + q2);
        var root = u - v - (ba / 3.0f);
        return Clamp01(root);
    }

    private static float Clamp01(float value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static float ReadFloat(JsonElement element, string property, float fallback)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : fallback;

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
