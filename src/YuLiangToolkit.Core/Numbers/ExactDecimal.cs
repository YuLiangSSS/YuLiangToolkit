using System.Globalization;
using System.Numerics;
using System.Text;

namespace YuLiangToolkit.Core.Numbers;

/// <summary>
/// 任意精度、有限位十进制数：内部是 <see cref="BigInteger"/> 尾数 + 十进制标度（值 = 尾数 × 10^(-标度)）。
///
/// <para><b>不用 int / long / double 存数值</b>：整数部分可以任意大（上下限、刻度数、阶乘结果都走这里），
/// 小数位数也可以任意多（步长 0.000…01、结果 1e-30 都不会变成 0.30000000000000004 这种浮点尾巴）。</para>
///
/// <para>只表示「能写成有限位十进制的数」：除不尽的分数（1/3）、π、e 这类不属于本类型，
/// 由调用方回退到 double / 显示「无定义」（见 <c>ExactMathExpression</c>）。所有运算都精确，不四舍五入。</para>
///
/// <para>纯逻辑、不可变（readonly struct），不碰界面 / 存储，可直接被自检断言。</para>
/// </summary>
public readonly struct ExactDecimal : IComparable<ExactDecimal>, IEquatable<ExactDecimal>
{
    /// <summary>解析 / 运算时允许的最大小数位数：再大就没有实用价值，且会让乘方结果迅速膨胀。</summary>
    public const int MaxScale = 1000;

    /// <summary>格式化时允许输出的最大字符数（防手滑输一个 2^100000000 把界面卡死）。</summary>
    public const int MaxTextLength = 100_000;

    /// <summary>零（标度 0）。</summary>
    public static ExactDecimal Zero => new(BigInteger.Zero, 0);

    /// <summary>一。</summary>
    public static ExactDecimal One => new(BigInteger.One, 0);

    private ExactDecimal(BigInteger unscaled, int scale)
    {
        Unscaled = unscaled;
        Scale = scale;
    }

    /// <summary>尾数（值 = 尾数 × 10^-Scale）；BigInteger 没有字长上限。</summary>
    public BigInteger Unscaled { get; }

    /// <summary>小数位数（≥ 0）。</summary>
    public int Scale { get; }

    /// <summary>是不是 0。</summary>
    public bool IsZero => Unscaled.IsZero;

    /// <summary>是不是负的。</summary>
    public bool IsNegative => Unscaled.Sign < 0;

    /// <summary>是不是正整数（0 不算）。</summary>
    public bool IsPositiveInteger => IsInteger && Unscaled.Sign > 0;

    /// <summary>是不是整数（数学上，不要求 Scale 为 0：1.00 也算）。</summary>
    public bool IsInteger
    {
        get
        {
            if (Scale == 0 || IsZero) return true;
            var value = BigInteger.Abs(Unscaled);
            var divisor = Pow10(Scale);
            return value % divisor == 0;
        }
    }

    /// <summary>
    /// 把十进制文本解析成精确值。支持：可选正负号、整数、小数、科学计数法（<c>1e-30</c> / <c>1E+40</c>）、
    /// <c>.5</c> / <c>5.</c>；不认 NaN / Infinity / 十六进制 / 千分位。
    /// 失败返回 false，不抛异常（界面输入框直接喂进来也可以）。
    /// </summary>
    public static bool TryParse(string? text, out ExactDecimal value)
    {
        value = Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        var negative = false;
        var i = 0;
        if (s[i] is '+' or '-')
        {
            negative = s[i] == '-';
            i++;
        }
        if (i >= s.Length) return false;

        var digits = new StringBuilder(s.Length);
        var scale = 0;
        var seenDot = false;
        var seenDigit = false;
        for (; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch is >= '0' and <= '9')
            {
                digits.Append(ch);
                seenDigit = true;
                if (seenDot) scale++;
            }
            else if (ch == '.' && !seenDot)
            {
                seenDot = true;
            }
            else break;
        }
        if (!seenDigit) return false;

        // 科学计数法：指数为正向左补 0（整数变大），为负向右补 0（小数位变多）
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            var expNegative = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                expNegative = s[i] == '-';
                i++;
            }
            if (i >= s.Length) return false;
            var exponent = 0;
            for (; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch is < '0' or > '9') return false;
                // 指数本身只是「补多少个 0」，超过这个量级一定撞上 MaxScale / MaxTextLength，直接拒
                if (exponent > MaxTextLength) return false;
                exponent = exponent * 10 + (ch - '0');
                if (exponent > MaxTextLength) return false;
            }
            // 1.23e-5 → scale 2 + 5；1.23e+5 → 尾数后面补 5 个 0
            if (expNegative) scale += exponent;
            else
            {
                digits.Append('0', exponent);
            }
        }

        if (i != s.Length) return false;
        if (scale > MaxScale) return false;

        var unscaled = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative) unscaled = -unscaled;
        value = new ExactDecimal(unscaled, scale);
        return true;
    }

    /// <summary>从 <see cref="BigInteger"/> 造一个整数（标度 0）。</summary>
    public static ExactDecimal FromInteger(BigInteger value) => new(value, 0);

    /// <summary>把 double 的有限值按「最多 15 位有效数字」转成精确十进制（只用于回退路径的边界处理）。</summary>
    public static bool TryFromDouble(double value, int maxDecimals, out ExactDecimal result)
    {
        result = Zero;
        if (!double.IsFinite(value)) return false;
        return TryParse(value.ToString("F" + Math.Clamp(maxDecimals, 0, 15), CultureInfo.InvariantCulture), out result);
    }

    /// <summary>10 的 n 次方（n ≥ 0）。</summary>
    public static BigInteger Pow10(int n) => n <= 0 ? BigInteger.One : BigInteger.Pow(10, n);

    /// <summary>格式化成普通十进制文本（不用科学计数法，也不带末尾多余的 0；整数不带小数点）。</summary>
    public string ToPlainString()
    {
        if (IsZero) return "0";

        var negative = IsNegative;
        var digits = BigInteger.Abs(Unscaled).ToString(CultureInfo.InvariantCulture);
        string body;

        if (Scale == 0)
        {
            body = digits;
        }
        else
        {
            var padded = digits.PadLeft(Scale + 1, '0');
            var integerPart = padded[..^Scale];
            var fraction = padded[^Scale..].TrimEnd('0');
            body = fraction.Length == 0 ? integerPart : integerPart + "." + fraction;
        }

        return negative ? "-" + body : body;
    }

    public override string ToString() => ToPlainString();

    /// <summary>输出文本是否超过 <see cref="MaxTextLength"/>（乘方/阶乘的防卡死闸口）。</summary>
    public bool IsTooLongToDisplay()
    {
        if (IsZero) return false;
        // 尾数位数 + 可能的负号 / 小数点，先不真的拼出十万字符的字符串
        var digitCount = BigInteger.Abs(Unscaled).ToString(CultureInfo.InvariantCulture).Length;
        return (long)digitCount + (IsNegative ? 1 : 0) + 1 > MaxTextLength;
    }

    // ---------- 对齐 / 四则 ----------

    /// <summary>把两个数补齐到相同标度，返回各自的尾数。</summary>
    private static (BigInteger A, BigInteger B, int Scale) Align(ExactDecimal a, ExactDecimal b)
    {
        var scale = Math.Max(a.Scale, b.Scale);
        return (a.Unscaled * Pow10(scale - a.Scale), b.Unscaled * Pow10(scale - b.Scale), scale);
    }

    /// <summary>约掉尾数末尾的 0（让 1.500 变成 1.5、1200/100 变成 12，文本更干净）。</summary>
    private static ExactDecimal Normalize(BigInteger unscaled, int scale)
    {
        if (unscaled.IsZero) return Zero;
        while (scale > 0 && unscaled % 10 == 0)
        {
            unscaled /= 10;
            scale--;
        }
        return new ExactDecimal(unscaled, scale);
    }

    /// <summary>按指定小数位数截断（多余位直接丢，不四舍五入）。</summary>
    public ExactDecimal TruncateToScale(int maxDecimals)
    {
        if (maxDecimals < 0 || Scale <= maxDecimals) return this;
        return new ExactDecimal(Unscaled / Pow10(Scale - maxDecimals), maxDecimals);
    }

    public static ExactDecimal operator +(ExactDecimal a, ExactDecimal b)
    {
        var (x, y, scale) = Align(a, b);
        return Normalize(x + y, scale);
    }

    public static ExactDecimal operator -(ExactDecimal a, ExactDecimal b)
    {
        var (x, y, scale) = Align(a, b);
        return Normalize(x - y, scale);
    }

    public static ExactDecimal operator -(ExactDecimal value)
        => new(-value.Unscaled, value.Scale);

    public static ExactDecimal operator *(ExactDecimal a, ExactDecimal b)
    {
        if ((long)a.Scale + b.Scale > MaxScale * 4L) throw new ArithmeticException("小数位数过多");
        var product = Normalize(a.Unscaled * b.Unscaled, a.Scale + b.Scale);
        if (product.IsTooLongToDisplay()) throw new ArithmeticException("乘积过大");
        return product;
    }

    /// <summary>
    /// 精确除法：能除尽才返回结果（例如 6/4 = 1.5、1/8 = 0.125）；除不尽可能抛
    /// <see cref="DivideByZeroException"/> 或返回 false（<see cref="TryDivide"/>）。
    /// </summary>
    public static ExactDecimal operator /(ExactDecimal a, ExactDecimal b)
        => TryDivide(a, b, out var result)
            ? result
            : throw new ArithmeticException("这个除法除不尽（结果不是有限位十进制数）");

    /// <summary>除零 / 除不尽都返回 false，让调用方回退到 double（显示小数近似或 ∞）。</summary>
    public static bool TryDivide(ExactDecimal a, ExactDecimal b, out ExactDecimal result)
    {
        result = Zero;
        if (b.IsZero) return false;
        if (a.IsZero) return true;

        // 值 = (a.Unscaled / 10^a.Scale) / (b.Unscaled / 10^b.Scale)
        //    = (a.Unscaled * 10^b.Scale) / (b.Unscaled * 10^a.Scale)
        var numerator = a.Unscaled * Pow10(b.Scale);
        var denominator = b.Unscaled * Pow10(a.Scale);
        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        // 除得尽的判据：约掉公因子后，分母只含 2 / 5 这两种质因子（十进制有限小数）。
        // 先判再算，避免 1/3 这类式子空转上千次扩位（实时预览每敲一个字符都会走到这里）。
        var common = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), BigInteger.Abs(denominator));
        var reduced = BigInteger.Abs(denominator) / common;
        while (!reduced.IsZero && reduced % 2 == 0) reduced /= 2;
        while (!reduced.IsZero && reduced % 5 == 0) reduced /= 5;
        if (reduced > BigInteger.One) return false;

        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var scale = 0;
        while (!remainder.IsZero && scale < MaxScale)
        {
            var (q, r) = BigInteger.DivRem(remainder * 10, denominator);
            quotient = quotient * 10 + q;
            remainder = r;
            scale++;
        }
        if (!remainder.IsZero) return false;   // 理论上到不了；留着兜底

        result = Normalize(quotient, scale);
        return !result.IsTooLongToDisplay();
    }

    /// <summary>
    /// 整数次精确乘方（指数为非负整数）。指数为 0 返回 1；负指数交给 double（会变成有限小数甚至除不尽）。
    /// 结果超过 <see cref="MaxTextLength"/> 抛 <see cref="ArithmeticException"/>，由调用方回退。
    /// </summary>
    public ExactDecimal Pow(int exponent)
    {
        if (exponent < 0) throw new ArgumentOutOfRangeException(nameof(exponent), "负指数不是有限位十进制的保证范围");
        if (exponent == 0) return One;
        if (IsZero) return Zero;

        // 快速上界：(有效位数 × 指数) 超阈值就直接拒，别先把 BigInteger 撑到几十 MB
        var digits = BigInteger.Abs(Unscaled).ToString(CultureInfo.InvariantCulture).Length;
        if ((long)digits * exponent > MaxTextLength * 2L) throw new ArithmeticException("乘方结果过大");

        var result = One;
        var baseValue = this;
        var e = exponent;
        while (e > 0)
        {
            if ((e & 1) != 0)
            {
                result *= baseValue;
                if (result.IsTooLongToDisplay()) throw new ArithmeticException("乘方结果过大");
            }
            e >>= 1;
            if (e > 0)
            {
                baseValue *= baseValue;
                if (baseValue.IsTooLongToDisplay()) throw new ArithmeticException("乘方结果过大");
            }
        }
        return result;
    }

    /// <summary>取模（数学取模，符号跟除数走；步长为正时结果非负）。</summary>
    public static ExactDecimal operator %(ExactDecimal a, ExactDecimal b)
    {
        if (b.IsZero) throw new DivideByZeroException();
        var (x, y, scale) = Align(a, b);
        var remainder = x % y;   // BigInteger 的 % 是「带符号余」，和 C#/数学取模一致：a - b*floor(a/b) 需再修一次
        if (!remainder.IsZero && (remainder.Sign < 0) != (y.Sign < 0)) remainder += y;
        return Normalize(remainder, scale);
    }

    /// <summary>向下取整（floor）后是不是恰好等于自己（也就是「是不是整数」）。</summary>
    public bool TryToInt32(out int result)
    {
        result = 0;
        if (!IsInteger) return false;
        var value = Unscaled / Pow10(Scale);
        if (value < int.MinValue || value > int.MaxValue) return false;
        result = (int)value;
        return true;
    }

    /// <summary>转成 BigInteger（必须真的是整数，否则返回 false）。</summary>
    public bool TryToBigInteger(out BigInteger result)
    {
        result = BigInteger.Zero;
        if (!IsInteger) return false;
        result = Unscaled / Pow10(Scale);
        return true;
    }

    // ---------- 比较 ----------

    public int CompareTo(ExactDecimal other)
    {
        var (a, b, _) = Align(this, other);
        return a.CompareTo(b);
    }

    public bool Equals(ExactDecimal other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ExactDecimal other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Unscaled, Scale);

    public static bool operator ==(ExactDecimal a, ExactDecimal b) => a.Equals(b);
    public static bool operator !=(ExactDecimal a, ExactDecimal b) => !a.Equals(b);
    public static bool operator <(ExactDecimal a, ExactDecimal b) => a.CompareTo(b) < 0;
    public static bool operator >(ExactDecimal a, ExactDecimal b) => a.CompareTo(b) > 0;
    public static bool operator <=(ExactDecimal a, ExactDecimal b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ExactDecimal a, ExactDecimal b) => a.CompareTo(b) >= 0;
}
