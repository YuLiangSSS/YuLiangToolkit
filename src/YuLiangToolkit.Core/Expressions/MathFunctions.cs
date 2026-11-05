namespace YuLiangToolkit.Core.Expressions;

/// <summary>
/// 表达式的函数表与常量表（计算器 / 函数绘图共用，全部手写、不引第三方包）。
///
/// <para><b>角度制</b>只影响三角与反三角：<c>sin/cos/tan/sec/csc/cot</c> 的入参按
/// <see cref="AngleUnit"/> 换算成弧度；<c>asin/acos/atan/atan2</c> 的返回值换算回所选单位。</para>
///
/// <para>函数分两类：一元（1 个参数）与带参数个数的（如 <c>log(base, x)</c>、<c>clamp(x, 最小, 最大)</c>）。
/// 参数个数不匹配会抛 <see cref="ExpressionSyntaxException"/>，由解析器统一包成语法错误。</para>
/// </summary>
internal static class MathFunctions
{
    /// <summary>函数实现：参数数组 + 角度单位 → 值（无定义时返回 NaN / Infinity，不抛异常）。</summary>
    internal delegate double Impl(double[] args, AngleUnit unit);

    /// <summary>一个函数的定义：实现 + 允许的参数个数范围。</summary>
    internal readonly record struct FunctionDef(Impl Body, int MinArgs, int MaxArgs, string Signature)
    {
        internal bool AcceptsArity(int count) => count >= MinArgs && count <= MaxArgs;
    }

    /// <summary>函数名 → 定义（名字小写；解析器已把名字转小写再查）。</summary>
    private static readonly Dictionary<string, FunctionDef> Functions = BuildFunctions();

    /// <summary>常量名 → 值（π / e / τ / ∞ 等）。</summary>
    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["π"] = Math.PI,
        ["tau"] = Math.Tau,
        ["τ"] = Math.Tau,
        ["e"] = Math.E,
        ["inf"] = double.PositiveInfinity,
        ["∞"] = double.PositiveInfinity,
    };

    /// <summary>找函数；找不到返回 false。</summary>
    internal static bool TryGetFunction(string name, out FunctionDef def)
        => Functions.TryGetValue(name, out def);

    /// <summary>找常量；找不到返回 false。</summary>
    internal static bool TryGetConstant(string name, out double value)
        => Constants.TryGetValue(name, out value);

    /// <summary>这个标识符是不是已知函数名（用于「函数名后面必须跟括号」的提示）。</summary>
    internal static bool IsFunction(string name) => Functions.ContainsKey(name);

    /// <summary>可用函数名（排序后），界面「函数帮助」用。</summary>
    internal static IReadOnlyList<string> FunctionNames
        => Functions.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    // ---------- 换算 ----------

    private static double ToRadians(double value, AngleUnit unit)
        => unit == AngleUnit.Degrees ? value * Math.PI / 180.0 : value;

    private static double FromRadians(double value, AngleUnit unit)
        => unit == AngleUnit.Degrees ? value * 180.0 / Math.PI : value;

    // ---------- 函数表 ----------

    private static Dictionary<string, FunctionDef> BuildFunctions()
    {
        var map = new Dictionary<string, FunctionDef>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, int minArgs, int maxArgs, Impl body, string signature)
            => map[name] = new FunctionDef(body, minArgs, maxArgs, signature);

        void Add1(string name, Func<double, double> body, string signature)
            => Add(name, 1, 1, (a, _) => body(a[0]), signature);

        // —— 三角（入参按角度单位换算）——
        Add("sin", 1, 1, (a, u) => Math.Sin(ToRadians(a[0], u)), "sin(角度)");
        Add("cos", 1, 1, (a, u) => Math.Cos(ToRadians(a[0], u)), "cos(角度)");
        Add("tan", 1, 1, (a, u) => Math.Tan(ToRadians(a[0], u)), "tan(角度)");
        Add("sec", 1, 1, (a, u) => 1.0 / Math.Cos(ToRadians(a[0], u)), "sec(角度)");
        Add("csc", 1, 1, (a, u) => 1.0 / Math.Sin(ToRadians(a[0], u)), "csc(角度)");
        Add("cot", 1, 1, (a, u) => 1.0 / Math.Tan(ToRadians(a[0], u)), "cot(角度)");

        // —— 反三角（返回值按角度单位换算）——
        Add("asin", 1, 1, (a, u) => FromRadians(Math.Asin(a[0]), u), "asin(x)");
        Add("acos", 1, 1, (a, u) => FromRadians(Math.Acos(a[0]), u), "acos(x)");
        Add("atan", 1, 1, (a, u) => FromRadians(Math.Atan(a[0]), u), "atan(x)");
        Add("atan2", 2, 2, (a, u) => FromRadians(Math.Atan2(a[0], a[1]), u), "atan2(y, x)");

        // —— 双曲（与角度制无关）——
        Add1("sinh", Math.Sinh, "sinh(x)");
        Add1("cosh", Math.Cosh, "cosh(x)");
        Add1("tanh", Math.Tanh, "tanh(x)");
        Add1("asinh", Math.Asinh, "asinh(x)");
        Add1("acosh", Math.Acosh, "acosh(x)");
        Add1("atanh", Math.Atanh, "atanh(x)");

        // —— 幂 / 根 / 绝对值 / 取整 ——
        Add1("sqrt", v => v < 0 ? double.NaN : Math.Sqrt(v), "sqrt(x)");
        Add1("cbrt", Math.Cbrt, "cbrt(x)");
        Add1("abs", Math.Abs, "abs(x)");
        Add1("sign", v => Math.Sign(v), "sign(x)");
        Add1("floor", Math.Floor, "floor(x)");
        Add1("ceil", Math.Ceiling, "ceil(x)");
        Add1("round", v => Math.Round(v, MidpointRounding.AwayFromZero), "round(x)");
        Add1("trunc", Math.Truncate, "trunc(x)");
        Add("pow", 2, 2, (a, _) => Math.Pow(a[0], a[1]), "pow(底, 指数)");

        // —— 指数 / 对数 ——
        Add1("exp", Math.Exp, "exp(x)");
        Add("ln", 1, 1, (a, _) => a[0] > 0 ? Math.Log(a[0]) : double.NaN, "ln(x)");
        Add("log10", 1, 1, (a, _) => a[0] > 0 ? Math.Log10(a[0]) : double.NaN, "log10(x)");
        Add("lg", 1, 1, (a, _) => a[0] > 0 ? Math.Log10(a[0]) : double.NaN, "lg(x)");
        Add("log2", 1, 1, (a, _) => a[0] > 0 ? Math.Log2(a[0]) : double.NaN, "log2(x)");
        // log(x) = 常用对数；log(base, x) = 以 base 为底
        Add("log", 1, 2, (a, _) =>
        {
            if (a.Length == 1) return a[0] > 0 ? Math.Log10(a[0]) : double.NaN;
            var (baseValue, x) = (a[0], a[1]);
            return baseValue > 0 && baseValue != 1 && x > 0 ? Math.Log(x, baseValue) : double.NaN;
        }, "log(x) / log(底, x)");

        // —— 角度换算 ——
        Add1("rad", v => v * Math.PI / 180.0, "rad(角度) → 弧度");
        Add1("deg", v => v * 180.0 / Math.PI, "deg(弧度) → 角度");

        // —— 组合 / 统计 ——
        Add("mod", 2, 2, (a, _) => a[1] == 0 ? double.NaN : a[0] - a[1] * Math.Floor(a[0] / a[1]), "mod(a, b)");
        Add("hypot", 2, 2, (a, _) => Math.Sqrt(a[0] * a[0] + a[1] * a[1]), "hypot(a, b)");
        Add("clamp", 3, 3, (a, _) => Math.Min(Math.Max(a[0], a[1]), a[2]), "clamp(x, 最小, 最大)");
        Add("min", 1, 32, (a, _) => a.Min(), "min(a, b, …)");
        Add("max", 1, 32, (a, _) => a.Max(), "max(a, b, …)");

        // —— 组合数 / 阶乘（整数参数安全版）——
        Add("nCr", 2, 2, (a, _) => Combination(a[0], a[1]), "nCr(n, r)");
        Add("nPr", 2, 2, (a, _) => Permutation(a[0], a[1]), "nPr(n, r)");

        return map;
    }

    /// <summary>组合数 C(n, r)；非整数 / 越界返回 NaN。</summary>
    private static double Combination(double n, double r)
    {
        if (!TryToNonNegativeInteger(n, out var nn) || !TryToNonNegativeInteger(r, out var rr) || rr > nn) return double.NaN;
        rr = Math.Min(rr, nn - rr);
        var result = 1.0;
        for (var i = 1; i <= rr; i++) result = result * (nn - rr + i) / i;
        return Math.Round(result);
    }

    /// <summary>排列数 P(n, r)；非整数 / 越界返回 NaN。</summary>
    private static double Permutation(double n, double r)
    {
        if (!TryToNonNegativeInteger(n, out var nn) || !TryToNonNegativeInteger(r, out var rr) || rr > nn) return double.NaN;
        var result = 1.0;
        for (var i = 0; i < rr; i++) result *= nn - i;
        return result;
    }

    private static bool TryToNonNegativeInteger(double value, out int result)
    {
        result = 0;
        if (!double.IsFinite(value) || value < 0 || value > 170) return false;
        var rounded = Math.Round(value);
        if (Math.Abs(rounded - value) > 1e-9 * Math.Max(1, Math.Abs(value))) return false;
        result = (int)rounded;
        return true;
    }
}
