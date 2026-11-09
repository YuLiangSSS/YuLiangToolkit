using System.Numerics;
using System.Security.Cryptography;
using YuLiangToolkit.Core.Numbers;

namespace YuLiangToolkit.Tools.RandomNumber.Core;

/// <summary>
/// 按十进制步长把 <c>[下限, 上限]</c> 划成网格，并用密码学随机数在网格上无偏取点。
///
/// <para><b>不用 int / long / double 存边界、步长或刻度数</b>：全部是 <see cref="ExactDecimal"/>
/// （BigInteger 尾数）与 <see cref="BigInteger"/>，所以下限 1e100、步长 1e-30 也能算；
/// 结果用 <c>刻度 × 步长</c> 精确还原，不会出现 0.30000000000000004。</para>
///
/// <para>「精确度」即步长：生成值一定是 <c>下限 + k × 步长</c>（k = 0…刻度数），闭区间含上下限。
/// 上限不在网格上时自动向下对齐到最后一个不越界的刻度，保证结果绝不超上限。</para>
///
/// <para>随机源可注入（自检传确定性假随机源），生产用 <see cref="RandomNumberGenerator"/>
/// + 拒绝采样消除取模偏差。纯逻辑，不碰界面 / 存储。</para>
/// </summary>
public sealed class DecimalStepRange
{
    /// <summary>一次最多生成多少个结果（防界面卡死 / 内存爆掉）。</summary>
    public const int MaxCount = 10000;

    /// <summary>下限（含）。</summary>
    public ExactDecimal Lower { get; }

    /// <summary>对齐后的上限（含；可能比用户填的略小，因为要落在步长网格上）。</summary>
    public ExactDecimal Upper { get; }

    /// <summary>步长（精确度，必须 &gt; 0）。</summary>
    public ExactDecimal Step { get; }

    /// <summary>刻度数（网格点的总数，含 0 和最后一个刻度）。</summary>
    public BigInteger PointCount { get; }

    private DecimalStepRange(ExactDecimal lower, ExactDecimal upper, ExactDecimal step, BigInteger pointCount)
    {
        Lower = lower;
        Upper = upper;
        Step = step;
        PointCount = pointCount;
    }

    /// <summary>
    /// 解析用户输入的三段文本并返回可用网格；任何不合法（空、下限&gt;上限、步长≤0、小数位过多）
    /// 都返回中文原因，不抛异常。
    /// </summary>
    public static bool TryCreate(string? lowerText, string? upperText, string? stepText,
        out DecimalStepRange? range, out string? error)
    {
        range = null;
        error = null;

        if (!ExactDecimal.TryParse(lowerText, out var lower))
        {
            error = "下限不是合法的十进制数（可写 1e30、-5、0.001）";
            return false;
        }
        if (!ExactDecimal.TryParse(upperText, out var upper))
        {
            error = "上限不是合法的十进制数（可写 1e30、-5、0.001）";
            return false;
        }
        if (!ExactDecimal.TryParse(stepText, out var step))
        {
            error = "精确度不是合法的十进制数（可写 1、0.1、0.01、1e-18）";
            return false;
        }
        if (step <= ExactDecimal.Zero)
        {
            error = "精确度必须大于 0（例如 0.1 或 0.01）";
            return false;
        }
        if (upper < lower)
        {
            error = "上限不能小于下限";
            return false;
        }

        // (上限 - 下限) / 步长 → 向下取整，得到最后一个可用刻度编号 k。
        // 这里刻意不要求整除（0~1 配步长 0.3 应当对齐到 0.9），用对齐标度后的 BigInteger 截断除法算。
        var span = upper - lower;
        var scale = Math.Max(span.Scale, step.Scale);
        var numerator = span.Unscaled * ExactDecimal.Pow10(scale - span.Scale);
        var denominator = step.Unscaled * ExactDecimal.Pow10(scale - step.Scale);
        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }
        var integer = numerator.IsZero ? BigInteger.Zero : BigInteger.Divide(numerator, denominator);
        if (integer < 0) integer = BigInteger.Zero;

        var alignedUpper = lower + step * ExactDecimal.FromInteger(integer);
        var pointCount = integer + BigInteger.One;
        range = new DecimalStepRange(lower, alignedUpper, step, pointCount);
        return true;
    }

    /// <summary>第 k 个刻度实际代表的数（k 必须在 [0, PointCount) 内）。</summary>
    public ExactDecimal ValueAt(BigInteger index)
    {
        if (index < 0 || index >= PointCount) throw new ArgumentOutOfRangeException(nameof(index));
        return Lower + Step * ExactDecimal.FromInteger(index);
    }

    /// <summary>用户填的上限是否需要向下对齐（用于界面提示「已对齐到 …」）。</summary>
    public bool IsUpperAligned(string? upperText)
        => ExactDecimal.TryParse(upperText, out var requested) && requested == Upper;

    /// <summary>
    /// 生成 <paramref name="count"/> 个结果。<paramref name="allowDuplicates"/>=false 时改为不重复抽样
    /// （最多只能抽 <see cref="PointCount"/> 个，界面会先夹取）。
    /// <paramref name="randomBelow"/> 是可注入的「[0, n) 均匀随机 BigInteger」；生产不传，
    /// 用 <see cref="UnbiasedBelow"/>（RandomNumberGenerator + 拒绝采样）。
    /// </summary>
    public IReadOnlyList<string> Generate(int count, bool allowDuplicates, bool sortResults,
        Func<BigInteger, BigInteger>? randomBelow = null)
    {
        count = Math.Clamp(count, 1, MaxCount);
        var next = randomBelow ?? UnbiasedBelow;
        var results = new List<string>(count);

        if (allowDuplicates)
        {
            for (var i = 0; i < count; i++)
                results.Add(ValueAt(next(PointCount)).ToPlainString());
        }
        else
        {
            var capacity = PointCount > int.MaxValue ? int.MaxValue : (int)PointCount;
            var wanted = Math.Min(count, capacity);
            var chosen = new HashSet<BigInteger>();
            // 拒绝采样：真实随机源几乎一次命中；给个尝试上限，防止注入的病态随机源把循环卡死
            var attempts = 0L;
            var attemptLimit = (long)wanted * 64 + 256;
            while (chosen.Count < wanted && attempts++ < attemptLimit)
                chosen.Add(next(PointCount));

            // 尝试上限用完还没凑齐（随机源退化）→ 顺次补齐，保证一定终止且结果仍然不重复
            if (chosen.Count < wanted)
            {
                for (var i = BigInteger.Zero; i < PointCount && chosen.Count < wanted; i++)
                    chosen.Add(i);
            }

            foreach (var index in chosen)
                results.Add(ValueAt(index).ToPlainString());
        }

        if (sortResults && results.Count > 1)
            results.Sort(CompareDecimalText);
        return results;
    }

    /// <summary>按十进制文本排序（生成阶段已排好序时用；不重复模式下 HashSet 顺序是乱的）。</summary>
    public static IReadOnlyList<string> Sort(IEnumerable<string> values)
    {
        var list = values.ToList();
        list.Sort(CompareDecimalText);
        return list;
    }

    private static int CompareDecimalText(string a, string b)
    {
        if (ExactDecimal.TryParse(a, out var x) && ExactDecimal.TryParse(b, out var y)) return x.CompareTo(y);
        return string.CompareOrdinal(a, b);
    }

    /// <summary>
    /// [0, n) 的均匀随机 BigInteger（n &gt; 0）。用密码学随机字节 + 拒绝采样消掉取模偏差：
    /// 先取 limit = n * floor(2^k / n)，再丢掉 ≥ limit 的采样值。
    /// </summary>
    public static BigInteger UnbiasedBelow(BigInteger n)
    {
        if (n <= BigInteger.Zero) throw new ArgumentOutOfRangeException(nameof(n));
        if (n == BigInteger.One) return BigInteger.Zero;

        var bits = (int)BigInteger.Log2(n - 1) + 1;   // 覆盖 [0, n) 所需位数
        var byteCount = (bits + 7) / 8;
        var twoToK = BigInteger.One << (byteCount * 8);
        var limit = twoToK - twoToK % n;
        var bytes = new byte[byteCount];

        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            var candidate = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
            if (candidate < limit) return candidate % n;
        }
    }

    /// <summary>把结果列表拼成可复制的一段文本（分隔符由配置决定）。</summary>
    public static string Join(IReadOnlyList<string> values, string separator)
    {
        var sep = separator switch
        {
            "换行" => Environment.NewLine,
            "逗号" => ", ",
            "空格" => " ",
            "制表" => "\t",
            _ => Environment.NewLine,
        };
        return string.Join(sep, values);
    }
}
