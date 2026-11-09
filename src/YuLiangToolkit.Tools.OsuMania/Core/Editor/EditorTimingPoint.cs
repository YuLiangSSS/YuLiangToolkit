namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 编辑器里的一个时间点（红线 / 绿线）。
///
/// <para><b>红线 / 绿线的判定照 osu! 原文语义</b>：<c>uninherited = 1</c> 是红线（BPM 变化），
/// <c>uninherited = 0</c> 是绿线（SV / 音量等继承点）。注意**不能**用 <c>BeatLength &gt;= 0</c> 判断：
/// 老谱面里红线 BeatLength 恒正、绿线是负数，但极少数谱面的绿线 BeatLength 也可能是正数
/// （0 或异常值），编辑器一律按 uninherited 位判断，读 / 写都保留这个位。</para>
///
/// <para><b>SV</b>：绿线的 <see cref="SvMultiplier"/> = <c>100 / -BeatLength</c>（osu! 的定义），
/// 红线恒为 1。用户能看到的「SV 0.5x / 2.0x」就是这个值。</para>
/// </summary>
public readonly record struct EditorTimingPoint
{
    /// <summary>时间（毫秒）。</summary>
    public double Time { get; init; }

    /// <summary>
    /// 原文的 BeatLength 字段。红线是「每拍毫秒数」（正），绿线是负的 SV 倒数
    /// （-10000 / SV%）。编辑器保存时按原文写回这个字段。
    /// </summary>
    public double BeatLength { get; init; }

    /// <summary>是否为红线（原文 <c>uninherited</c> 位）。</summary>
    public bool Uninherited { get; init; }

    /// <summary>拍号（红线有效，默认 4）。</summary>
    public int TimeSignature { get; init; }

    /// <summary>采样组（0 = auto / 1 = Normal / 2 = Soft / 3 = Drum）。</summary>
    public int SampleSet { get; init; }

    /// <summary>自定义采样序号。</summary>
    public int CustomSampleSet { get; init; }

    /// <summary>音量（0~100）。</summary>
    public int Volume { get; init; }

    /// <summary>效果位（原文第 8 列，如 kiai / omit-first-bar；原样保留）。</summary>
    public int Effects { get; init; }

    /// <summary>BPM（红线有意义；绿线返回 0）。</summary>
    public double Bpm => Uninherited && BeatLength > 0 ? 60000.0 / BeatLength : 0;

    /// <summary>SV 倍率：红线 1.0，绿线 <c>100 / -BeatLength</c>（BeatLength 异常时退回 1.0）。</summary>
    public double SvMultiplier
    {
        get
        {
            if (Uninherited) return 1.0;
            if (BeatLength >= 0) return 1.0;
            var sv = 100.0 / -BeatLength;
            return double.IsFinite(sv) && sv > 0 ? sv : 1.0;
        }
    }

    /// <summary>造一条红线（<paramref name="bpm"/> 转成 BeatLength）。</summary>
    public static EditorTimingPoint NewRedLine(double time, double bpm, int timeSignature = 4)
    {
        var beatLength = bpm > 0 ? 60000.0 / bpm : 500.0;
        return new EditorTimingPoint
        {
            Time = time,
            BeatLength = beatLength,
            Uninherited = true,
            TimeSignature = timeSignature <= 0 ? 4 : timeSignature,
            SampleSet = 2,
            Volume = 60,
        };
    }

    /// <summary>造一条绿线（按 SV 倍率折算成原文 BeatLength）。</summary>
    public static EditorTimingPoint NewGreenLine(double time, double sv)
    {
        var clamped = Math.Clamp(sv, 0.01, 10.0);
        return new EditorTimingPoint
        {
            Time = time,
            BeatLength = -100.0 / clamped,
            Uninherited = false,
            TimeSignature = 4,
            SampleSet = 2,
            Volume = 60,
        };
    }

    /// <summary>改时间（拖动 / 输入）。</summary>
    public EditorTimingPoint WithTime(double time) => this with { Time = time };

    /// <summary>改 BPM（只对红线有意义；同时把 uninherited 置为 true）。</summary>
    public EditorTimingPoint WithBpm(double bpm)
    {
        var clamped = Math.Clamp(bpm, 1.0, 600.0);
        return this with { BeatLength = 60000.0 / clamped, Uninherited = true };
    }

    /// <summary>改 SV（同时把 uninherited 置为 false）。</summary>
    public EditorTimingPoint WithSv(double sv)
    {
        var clamped = Math.Clamp(sv, 0.01, 10.0);
        return this with { BeatLength = -100.0 / clamped, Uninherited = false };
    }

    /// <summary>一行便于调试的摘要。</summary>
    public override string ToString()
        => Uninherited
            ? $"红线 {Time:0}ms {Bpm:0.##}BPM"
            : $"绿线 {Time:0}ms SV {SvMultiplier:0.##}x";
}
