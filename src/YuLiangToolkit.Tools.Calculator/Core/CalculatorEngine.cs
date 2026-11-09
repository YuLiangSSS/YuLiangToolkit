using YuLiangToolkit.Core.Expressions;

namespace YuLiangToolkit.Tools.Calculator.Core;

/// <summary>
/// 一次求值的结果（纯数据，不碰界面 / 不碰存储，可直接被自检断言）。
///
/// <para>三种状态要分清楚：<see cref="Success"/>=false 表示<b>写法不对</b>（<see cref="Error"/> 是给用户看的中文提示）；
/// <see cref="Success"/>=true 且 <see cref="HasValue"/>=false 表示表达式是空的；
/// 其余情况看 <see cref="Text"/>（可能已经是「无定义」/「+∞」）。</para>
///
/// <para><see cref="IsExact"/>=true 表示这次走了任意精度十进制引擎（BigInteger 尾数），
/// <see cref="Text"/> 是每一位都对的完整数字；此时 <see cref="Value"/> 只是给旧代码兼容的近似值，
/// Ans / 历史一律用 <see cref="Text"/>，别再把它塞回 double。</para>
/// </summary>
public readonly record struct CalculatorResult(
    bool Success,
    bool HasValue,
    double Value,
    string Text,
    string? Error,
    bool IsExact = false)
{
    /// <summary>空表达式（结果区什么都不显示）。</summary>
    public static CalculatorResult Empty => new(false, false, double.NaN, "", null);

    /// <summary>是不是一个能继续参与运算的有限值（非精确结果才用它判断 Ans 的数值）。</summary>
    public bool IsFinite => HasValue && !IsExact && double.IsFinite(Value);

    /// <summary>这次结果能不能当 Ans 用（精确大数也可以，用文本插回表达式）。</summary>
    public bool CanBeAns => HasValue && (IsExact || double.IsFinite(Value));
}

/// <summary>
/// 计算器的求值封装：<b>精确优先</b> —— 先让 <see cref="ExactMathExpression"/> 用 BigInteger 十进制算
/// （<c>100!</c>、<c>2^1000</c>、大整数四则、有限小数都能给完整结果）；它管不了的式子
/// （函数 / 常量 / 变量 / 除不尽 / 太大）自动回退到共享 double 引擎 <see cref="MathExpression"/> +
/// <see cref="MathFormat"/>，所以科学计算与函数绘图的行为一字不变。
///
/// <para>另外两条计算器专属规则：用了变量 <c>x</c> → 中文提示；数学上无定义（NaN / ∞）
/// 不算错误，结果区显示「无定义」/「+∞」。</para>
/// </summary>
public static class CalculatorEngine
{
    /// <summary>表达式里出现变量 x 时的提示（计算器没有变量概念）。</summary>
    public const string VariableHint = "计算器里不能使用变量 x（那是函数绘图用的）";

    /// <summary>表达式为空时的提示（正常情况下不显示，只在用户按 Enter 时提示）。</summary>
    public const string EmptyHint = "先输入一个表达式，例如 2+3*4";

    /// <summary>解析失败又拿不到具体原因时的兜底提示。</summary>
    public const string FallbackError = "表达式写法不对";

    /// <summary>Ans 插入表达式时用的文本（精确结果优先取它，不用 double 格式化）。</summary>
    public static string GetAnsText(CalculatorResult result)
        => result.IsExact ? result.Text : MathFormat.Format(result.Value, MathFormat.DefaultSignificantDigits);

    /// <summary>
    /// 求值：空表达式返回 <see cref="CalculatorResult.Empty"/>；写法不对返回错误提示；
    /// 数学上无定义不抛异常，照常给出 <c>无定义</c> / <c>∞</c> 文本。
    /// </summary>
    /// <param name="expression">用户输入的表达式（允许 × ÷ − ² ³ 等计算器符号，引擎会规范化）。</param>
    /// <param name="angleUnit">三角函数按哪个单位解释（精确引擎不涉及三角回退时才用得上）。</param>
    /// <param name="significantDigits">结果有效位数（3 ~ 12，只对 double 回退结果生效）。</param>
    public static CalculatorResult Evaluate(string? expression, AngleUnit angleUnit, int significantDigits)
    {
        if (string.IsNullOrWhiteSpace(expression)) return CalculatorResult.Empty;

        // ① 精确路径：整数 / 有限小数 + 四则 / 乘方 / 阶乘 / 取模，用 BigInteger 十进制算，不丢一位
        if (ExactMathExpression.TryEvaluate(expression, out var exact, out var exactText))
        {
            // 兼容旧的 Value 字段（自检里 Near(...) 之类仍可用）；精确文本才是给用户看的
            var approximate = double.TryParse(exactText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ? parsed : double.NaN;
            return new CalculatorResult(true, true, approximate, exactText, null, IsExact: true);
        }

        // ② 回退路径：科学函数 / 常量 / 变量 / 除不尽 / 超大结果 —— 与改动前完全一致
        if (!MathExpression.TryParse(expression, out var parsedExpression, out var error) || parsedExpression is null)
            return new CalculatorResult(false, false, double.NaN, "", error ?? FallbackError);

        // 计算器没有「变量」概念：x 是留给函数绘图用的，这里给出明确提示而不是算成 0
        if (parsedExpression.UsesVariable)
            return new CalculatorResult(false, false, double.NaN, "", VariableHint);

        var value = parsedExpression.Evaluate(0, angleUnit);
        return new CalculatorResult(true, true, value, MathFormat.Format(value, significantDigits), null);
    }
}
