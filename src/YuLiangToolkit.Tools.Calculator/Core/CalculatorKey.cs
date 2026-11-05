namespace YuLiangToolkit.Tools.Calculator.Core;

/// <summary>键盘按钮点了以后要干什么。</summary>
public enum CalculatorKeyKind
{
    /// <summary>往输入框追加 <see cref="CalculatorKey.Insert"/> 文本。</summary>
    Insert = 0,

    /// <summary>求值（= 键）。</summary>
    Evaluate = 1,

    /// <summary>清空输入（C 键）。</summary>
    Clear = 2,

    /// <summary>退格（⌫ 键）。</summary>
    Backspace = 3,

    /// <summary>正负号（± 键）。</summary>
    ToggleSign = 4,

    /// <summary>插入上次结果（Ans 键）。</summary>
    InsertAns = 5,

    /// <summary>平方（x² 键）。</summary>
    Square = 6,

    /// <summary>倒数（1/x 键）。</summary>
    Reciprocal = 7,
}

/// <summary>
/// 一个计算器按键（纯数据）：界面上显示的文字、点一下追加的文本、以及动作类型。
/// 键盘按钮用两份静态表描述（<see cref="Basic"/> / <see cref="Scientific"/>），
/// 界面用 <c>ItemsControl + UniformGrid</c> 生成，新增按钮只改这里、不用动 XAML。
/// </summary>
public sealed class CalculatorKey
{
    public CalculatorKey(
        string label,
        CalculatorKeyKind kind,
        string insert = "",
        bool isAccent = false,
        string toolTip = "")
    {
        Label = label;
        Kind = kind;
        Insert = insert;
        IsAccent = isAccent;
        ToolTipText = toolTip;
    }

    /// <summary>按钮上显示的文字（<c>sin</c> / <c>×</c> / <c>=</c> …）。</summary>
    public string Label { get; }

    /// <summary>点了以后的动作。</summary>
    public CalculatorKeyKind Kind { get; }

    /// <summary>动作是 <see cref="CalculatorKeyKind.Insert"/> 时追加的文本（用解析器认识的 ASCII 写法）。</summary>
    public string Insert { get; }

    /// <summary>是不是主按钮（<c>=</c> 用主色样式）。</summary>
    public bool IsAccent { get; }

    /// <summary>悬停提示。</summary>
    public string ToolTipText { get; }

    /// <summary>
    /// 基础键盘：4 列 × 6 行 = 24 个（0-9 / . / + - × ÷ / 括号 / = / C / ⌫ / ± / Ans / x² / 1/x）。
    /// 顺序即界面顺序（每行 4 个）。
    /// </summary>
    public static IReadOnlyList<CalculatorKey> Basic { get; } = new CalculatorKey[]
    {
        new("C", CalculatorKeyKind.Clear, toolTip: "清空输入（Esc）"),
        new("⌫", CalculatorKeyKind.Backspace, toolTip: "退格"),
        new("(", CalculatorKeyKind.Insert, "(", toolTip: "左括号"),
        new(")", CalculatorKeyKind.Insert, ")", toolTip: "右括号"),

        new("7", CalculatorKeyKind.Insert, "7"),
        new("8", CalculatorKeyKind.Insert, "8"),
        new("9", CalculatorKeyKind.Insert, "9"),
        new("÷", CalculatorKeyKind.Insert, "/", toolTip: "除"),

        new("4", CalculatorKeyKind.Insert, "4"),
        new("5", CalculatorKeyKind.Insert, "5"),
        new("6", CalculatorKeyKind.Insert, "6"),
        new("×", CalculatorKeyKind.Insert, "*", toolTip: "乘"),

        new("1", CalculatorKeyKind.Insert, "1"),
        new("2", CalculatorKeyKind.Insert, "2"),
        new("3", CalculatorKeyKind.Insert, "3"),
        new("−", CalculatorKeyKind.Insert, "-", toolTip: "减"),

        new("0", CalculatorKeyKind.Insert, "0"),
        new(".", CalculatorKeyKind.Insert, ".", toolTip: "小数点（同一个数字里只会有一个）"),
        new("±", CalculatorKeyKind.ToggleSign, toolTip: "正负号"),
        new("+", CalculatorKeyKind.Insert, "+", toolTip: "加"),

        new("x²", CalculatorKeyKind.Square, toolTip: "平方（在末尾数字后加 ²）"),
        new("1/x", CalculatorKeyKind.Reciprocal, toolTip: "倒数（把整条表达式放进 1/(…)）"),
        new("Ans", CalculatorKeyKind.InsertAns, toolTip: "插入上一次的求值结果"),
        new("=", CalculatorKeyKind.Evaluate, isAccent: true, toolTip: "求值并记入历史（Enter）"),
    };

    /// <summary>
    /// 科学区：6 列 × 4 行 = 24 个（三角 / 反三角 / 对数 / 根 / 幂 / 阶乘 / 常量 / 取整 / 倒数 / 取模）。
    /// 顺序即界面顺序（每行 6 个）。
    /// </summary>
    public static IReadOnlyList<CalculatorKey> Scientific { get; } = new CalculatorKey[]
    {
        new("sin", CalculatorKeyKind.Insert, "sin(", toolTip: "sin(角度)"),
        new("cos", CalculatorKeyKind.Insert, "cos(", toolTip: "cos(角度)"),
        new("tan", CalculatorKeyKind.Insert, "tan(", toolTip: "tan(角度)"),
        new("asin", CalculatorKeyKind.Insert, "asin(", toolTip: "asin(x) → 按当前角度单位返回"),
        new("acos", CalculatorKeyKind.Insert, "acos(", toolTip: "acos(x) → 按当前角度单位返回"),
        new("atan", CalculatorKeyKind.Insert, "atan(", toolTip: "atan(x) → 按当前角度单位返回"),

        new("ln", CalculatorKeyKind.Insert, "ln(", toolTip: "自然对数 ln(x)"),
        new("log", CalculatorKeyKind.Insert, "log(", toolTip: "log(x) 常用对数；log(底, x) 指定底"),
        new("log2", CalculatorKeyKind.Insert, "log2(", toolTip: "以 2 为底的对数"),
        new("sqrt", CalculatorKeyKind.Insert, "sqrt(", toolTip: "平方根"),
        new("cbrt", CalculatorKeyKind.Insert, "cbrt(", toolTip: "立方根"),
        new("^", CalculatorKeyKind.Insert, "^", toolTip: "乘方（右结合：2^3^2 = 512）"),

        new("x²", CalculatorKeyKind.Square, toolTip: "平方（在末尾数字后加 ²）"),
        new("!", CalculatorKeyKind.Insert, "!", toolTip: "阶乘（5! = 120，只能用于非负整数）"),
        new("π", CalculatorKeyKind.Insert, "π", toolTip: "圆周率 π"),
        new("e", CalculatorKeyKind.Insert, "e", toolTip: "自然常数 e"),
        new("abs", CalculatorKeyKind.Insert, "abs(", toolTip: "绝对值"),
        new("exp", CalculatorKeyKind.Insert, "exp(", toolTip: "e 的幂 exp(x)"),

        new("floor", CalculatorKeyKind.Insert, "floor(", toolTip: "向下取整"),
        new("ceil", CalculatorKeyKind.Insert, "ceil(", toolTip: "向上取整"),
        new("round", CalculatorKeyKind.Insert, "round(", toolTip: "四舍五入"),
        new("1/x", CalculatorKeyKind.Reciprocal, toolTip: "倒数（把整条表达式放进 1/(…)）"),
        new("mod", CalculatorKeyKind.Insert, " mod ", toolTip: "取模（7 mod 3 = 1）"),
        new("trunc", CalculatorKeyKind.Insert, "trunc(", toolTip: "向零取整"),
    };
}
