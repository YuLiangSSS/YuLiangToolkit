using YuLiangToolkit.Core.Expressions;

namespace YuLiangToolkit.Tools.Calculator.Core;

/// <summary>
/// 一次求值的结果（纯数据，不碰界面 / 不碰存储，可直接被自检断言）。
///
/// <para>三种状态要分清楚：<see cref="Success"/>=false 表示<b>写法不对</b>（<see cref="Error"/> 是给用户看的中文提示）；
/// <see cref="Success"/>=true 且 <see cref="HasValue"/>=false 表示表达式是空的；
/// 其余情况看 <see cref="Text"/>（可能已经是「无定义」/「+∞」）。</para>
/// </summary>
public readonly record struct CalculatorResult(
    bool Success,
    bool HasValue,
    double Value,
    string Text,
    string? Error)
{
    /// <summary>空表达式（结果区什么都不显示）。</summary>
    public static CalculatorResult Empty => new(false, false, double.NaN, "", null);

    /// <summary>是不是一个能继续参与运算的有限值（Ans 只记这种）。</summary>
    public bool IsFinite => HasValue && double.IsFinite(Value);
}

/// <summary>
/// 计算器的求值封装：把共享表达式引擎 <see cref="MathExpression"/> + 结果格式化
/// <see cref="MathFormat"/> 包成「界面直接用」的结果，并统一处理两条计算器专属规则：
/// <list type="number">
///   <item>用了变量 <c>x</c> → 提示「计算器里不能使用变量 x（那是函数绘图用的）」；</item>
///   <item>数学上无定义（NaN / ∞）<b>不算错误</b>，结果区显示「无定义」/「+∞」（见 <see cref="MathFormat.Format"/>）。</item>
/// </list>
/// </summary>
public static class CalculatorEngine
{
    /// <summary>表达式里出现变量 x 时的提示（计算器没有变量概念）。</summary>
    public const string VariableHint = "计算器里不能使用变量 x（那是函数绘图用的）";

    /// <summary>表达式为空时的提示（正常情况下不显示，只在用户按 Enter 时提示）。</summary>
    public const string EmptyHint = "先输入一个表达式，例如 2+3*4";

    /// <summary>解析失败又拿不到具体原因时的兜底提示。</summary>
    public const string FallbackError = "表达式写法不对";

    /// <summary>
    /// 求值：空表达式返回 <see cref="CalculatorResult.Empty"/>；写法不对返回错误提示；
    /// 数学上无定义不抛异常，照常给出 <c>无定义</c> / <c>∞</c> 文本。
    /// </summary>
    /// <param name="expression">用户输入的表达式（允许 × ÷ − ² ³ 等计算器符号，引擎会规范化）。</param>
    /// <param name="angleUnit">三角函数按哪个单位解释。</param>
    /// <param name="significantDigits">结果有效位数（3 ~ 12）。</param>
    public static CalculatorResult Evaluate(string? expression, AngleUnit angleUnit, int significantDigits)
    {
        if (string.IsNullOrWhiteSpace(expression)) return CalculatorResult.Empty;

        if (!MathExpression.TryParse(expression, out var parsed, out var error) || parsed is null)
            return new CalculatorResult(false, false, double.NaN, "", error ?? FallbackError);

        // 计算器没有「变量」概念：x 是留给函数绘图用的，这里给出明确提示而不是算成 0
        if (parsed.UsesVariable)
            return new CalculatorResult(false, false, double.NaN, "", VariableHint);

        var value = parsed.Evaluate(0, angleUnit);
        return new CalculatorResult(true, true, value, MathFormat.Format(value, significantDigits), null);
    }
}
