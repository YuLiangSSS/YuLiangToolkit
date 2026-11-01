using System.Numerics;
using System.Text.Json;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>物理输入的取值类型（物理 JSON 里的 "Type"）。</summary>
internal enum PhysicsValueType
{
    /// <summary>"X"：把参数归一化后加到水平位移上。</summary>
    X,

    /// <summary>"Y"：把参数归一化后加到垂直位移上。</summary>
    Y,

    /// <summary>"Angle"：把参数归一化后加到总角度上。</summary>
    Angle,
}

/// <summary>一个物理子组的归一化范围（Position / Angle 两套）。</summary>
internal sealed class PhysicsNormalization
{
    public float PositionMinimum;
    public float PositionMaximum;
    public float PositionDefault;

    public float AngleMinimum;
    public float AngleMaximum;
    public float AngleDefault;
}

/// <summary>
/// 一个物理子组（physics3.json 里 PhysicsSettings 的一项）：
/// 输入 → 归一化 → 旋转 → 钟摆粒子链 → 输出参数。
/// </summary>
internal sealed class PhysicsSubRig
{
    public PhysicsNormalization Normalization = new();

    public int InputCount;
    public int BaseInputIndex;

    public int OutputCount;
    public int BaseOutputIndex;

    public int ParticleCount;
    public int BaseParticleIndex;
}

/// <summary>物理输入（Source 参数 + 权重 + 类型 + 是否取反）。</summary>
internal sealed class PhysicsInput
{
    public PhysicsValueType Type;
    public float Weight;
    public bool Reflect;
    public string SourceId = "";

    /// <summary>解析时缓存的模型参数下标（-1 = 模型里没这个参数，跳过）。</summary>
    public int SourceParameterIndex = -1;
}

/// <summary>物理输出（粒子下标 + 缩放 + 权重 + 类型 + 目标参数）。</summary>
internal sealed class PhysicsOutput
{
    public PhysicsValueType Type;
    public int VertexIndex;
    public float AngleScale;
    public float Weight;
    public bool Reflect;
    public string DestinationId = "";

    /// <summary>解析时缓存的模型参数下标（-1 = 模型里没这个参数，跳过）。</summary>
    public int DestinationParameterIndex = -1;

    /// <summary>调试用：输出值低于 / 超过参数范围时的极值（对照官方同名字段）。</summary>
    public float ValueBelowMinimum;
    public float ValueExceededMaximum;
}

/// <summary>钟摆粒子（照官方 <c>CubismPhysicsParticle</c>）。</summary>
internal struct PhysicsParticle
{
    public Vector2 InitialPosition;
    public Vector2 Position;
    public Vector2 LastPosition;
    public Vector2 LastGravity;
    public Vector2 Velocity;
    public Vector2 Force;

    public float Mobility;
    public float Delay;
    public float Acceleration;
    public float Radius;
}

/// <summary>整套物理配置（physics3.json）。</summary>
internal sealed class PhysicsRig
{
    public Vector2 Gravity = new(0.0f, -1.0f);
    public Vector2 Wind = Vector2.Zero;
    public float Fps;
    public PhysicsSubRig[] Settings = Array.Empty<PhysicsSubRig>();
    public PhysicsInput[] Inputs = Array.Empty<PhysicsInput>();
    public PhysicsOutput[] Outputs = Array.Empty<PhysicsOutput>();
    public PhysicsParticle[] Particles = Array.Empty<PhysicsParticle>();

    public static PhysicsRig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>解析 physics3.json（字段含义见官方 <c>CubismPhysicsJson</c>）。</summary>
    public static PhysicsRig Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rig = new PhysicsRig();

        if (root.TryGetProperty("Meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            rig.Fps = ReadFloat(meta, "Fps", 0);
            if (meta.TryGetProperty("EffectiveForces", out var forces) && forces.ValueKind == JsonValueKind.Object)
            {
                if (forces.TryGetProperty("Gravity", out var gravity) && gravity.ValueKind == JsonValueKind.Object)
                    rig.Gravity = new Vector2(ReadFloat(gravity, "X", 0), ReadFloat(gravity, "Y", -1));
                if (forces.TryGetProperty("Wind", out var wind) && wind.ValueKind == JsonValueKind.Object)
                    rig.Wind = new Vector2(ReadFloat(wind, "X", 0), ReadFloat(wind, "Y", 0));
            }
        }

        var settingElements = new List<JsonElement>();
        if (root.TryGetProperty("PhysicsSettings", out var settings) && settings.ValueKind == JsonValueKind.Array)
        {
            foreach (var setting in settings.EnumerateArray())
            {
                if (setting.ValueKind == JsonValueKind.Object) settingElements.Add(setting);
            }
        }

        var subRigs = new List<PhysicsSubRig>(settingElements.Count);
        var inputs = new List<PhysicsInput>();
        var outputs = new List<PhysicsOutput>();
        var particles = new List<PhysicsParticle>();

        foreach (var setting in settingElements)
        {
            var subRig = new PhysicsSubRig
            {
                BaseInputIndex = inputs.Count,
                BaseOutputIndex = outputs.Count,
                BaseParticleIndex = particles.Count,
            };

            if (setting.TryGetProperty("Normalization", out var normalization) &&
                normalization.ValueKind == JsonValueKind.Object)
            {
                if (normalization.TryGetProperty("Position", out var position) && position.ValueKind == JsonValueKind.Object)
                {
                    subRig.Normalization.PositionMinimum = ReadFloat(position, "Minimum", 0);
                    subRig.Normalization.PositionMaximum = ReadFloat(position, "Maximum", 0);
                    subRig.Normalization.PositionDefault = ReadFloat(position, "Default", 0);
                }

                if (normalization.TryGetProperty("Angle", out var angle) && angle.ValueKind == JsonValueKind.Object)
                {
                    subRig.Normalization.AngleMinimum = ReadFloat(angle, "Minimum", 0);
                    subRig.Normalization.AngleMaximum = ReadFloat(angle, "Maximum", 0);
                    subRig.Normalization.AngleDefault = ReadFloat(angle, "Default", 0);
                }
            }

            // Input
            if (setting.TryGetProperty("Input", out var inputArray) && inputArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var inputElement in inputArray.EnumerateArray())
                {
                    if (inputElement.ValueKind != JsonValueKind.Object) continue;
                    var input = new PhysicsInput
                    {
                        Weight = ReadFloat(inputElement, "Weight", 0),
                        Reflect = ReadBool(inputElement, "Reflect"),
                        Type = ParseValueType(ReadString(inputElement, "Type")),
                    };

                    if (inputElement.TryGetProperty("Source", out var source) && source.ValueKind == JsonValueKind.Object)
                        input.SourceId = ReadString(source, "Id");

                    inputs.Add(input);
                    subRig.InputCount++;
                }
            }

            // Output
            if (setting.TryGetProperty("Output", out var outputArray) && outputArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var outputElement in outputArray.EnumerateArray())
                {
                    if (outputElement.ValueKind != JsonValueKind.Object) continue;
                    var output = new PhysicsOutput
                    {
                        VertexIndex = ReadInt32(outputElement, "VertexIndex", 0),
                        AngleScale = ReadFloat(outputElement, "Scale", 0),
                        Weight = ReadFloat(outputElement, "Weight", 0),
                        Reflect = ReadBool(outputElement, "Reflect"),
                        Type = ParseValueType(ReadString(outputElement, "Type")),
                    };

                    if (outputElement.TryGetProperty("Destination", out var destination) &&
                        destination.ValueKind == JsonValueKind.Object)
                        output.DestinationId = ReadString(destination, "Id");

                    outputs.Add(output);
                    subRig.OutputCount++;
                }
            }

            // Vertices（钟摆粒子链）
            if (setting.TryGetProperty("Vertices", out var vertexArray) && vertexArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var vertexElement in vertexArray.EnumerateArray())
                {
                    if (vertexElement.ValueKind != JsonValueKind.Object) continue;
                    var particle = new PhysicsParticle
                    {
                        Mobility = ReadFloat(vertexElement, "Mobility", 0),
                        Delay = ReadFloat(vertexElement, "Delay", 0),
                        Acceleration = ReadFloat(vertexElement, "Acceleration", 0),
                        Radius = ReadFloat(vertexElement, "Radius", 0),
                    };

                    if (vertexElement.TryGetProperty("Position", out var positionElement) &&
                        positionElement.ValueKind == JsonValueKind.Object)
                    {
                        particle.Position = new Vector2(
                            ReadFloat(positionElement, "X", 0),
                            ReadFloat(positionElement, "Y", 0));
                    }

                    particles.Add(particle);
                    subRig.ParticleCount++;
                }
            }

            subRigs.Add(subRig);
        }

        rig.Settings = subRigs.ToArray();
        rig.Inputs = inputs.ToArray();
        rig.Outputs = outputs.ToArray();
        rig.Particles = particles.ToArray();
        return rig;
    }

    private static PhysicsValueType ParseValueType(string value) => value switch
    {
        "X" => PhysicsValueType.X,
        "Y" => PhysicsValueType.Y,
        _ => PhysicsValueType.Angle,
    };

    private static float ReadFloat(JsonElement element, string property, float fallback)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : fallback;

    private static int ReadInt32(JsonElement element, string property, int fallback)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private static bool ReadBool(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
