using System.Globalization;
using System.Text;

namespace YuLiangToolkit.Core.Expressions;

/// <summary>
/// 一条数学表达式：<see cref="Parse"/> 一次得到语法树，之后可以反复求值（计算器按回车、绘图采样 2 万次都走这里）。
///
/// <para><b>支持的写法</b></para>
/// <list type="bullet">
///   <item>四则运算 <c>+ - * /</c>、乘方 <c>^</c>（右结合：<c>2^3^2 = 512</c>）、取模 <c>mod</c>、阶乘后缀 <c>!</c>；</item>
///   <item>括号、一元正负号；</item>
///   <item><b>省略乘号</b>：<c>2x</c>、<c>2(3+4)</c>、<c>(x+1)(x-1)</c>、<c>2sin(x)</c> 都当乘法；</item>
///   <item>函数：<c>sin cos tan asin acos atan atan2 sqrt cbrt abs exp ln lg log log2 log10
///         floor ceil round trunc sign sinh cosh tanh asinh acosh atanh pow mod hypot clamp min max
///         nCr nPr rad deg</c>（完整签名见 <see cref="MathFunctions"/>）；</item>
///   <item>常量：<c>pi</c> / <c>π</c>、<c>e</c>、<c>tau</c> / <c>τ</c>、<c>inf</c> / <c>∞</c>；</item>
///   <item>变量：<c>x</c>（绘图用；计算器不用变量时会提示错误）；</item>
///   <item>顺手兼容输入法打出来的 <c>× ÷ − π ² ³ （ ） ，</c> 和全角数字。</item>
/// </list>
///
/// <para><b>求值语义</b>：能解析但数学上无定义时<b>不抛异常</b>，返回 <see cref="double.NaN"/> /
/// <see cref="double.PositiveInfinity"/>（例如 <c>sqrt(-1)</c>、<c>1/0</c>、<c>ln(0)</c>），
/// 界面自己决定显示成「无定义」还是「∞」。只有写法不对才抛 <see cref="ExpressionSyntaxException"/>。</para>
///
/// <para>本类是<b>无状态纯逻辑</b>（除了解析结果不可变），不做任何界面 / 存储依赖，可直接被自检断言。</para>
/// </summary>
public sealed class MathExpression
{
    private readonly Node _root;
    private readonly string _source;

    private MathExpression(string source, Node root)
    {
        _source = source;
        _root = root;
    }

    /// <summary>原表达式（去掉首尾空白；帮界面回显 / 存历史记录）。</summary>
    public string Source => _source;

    /// <summary>表达式里是否用到变量 <c>x</c>（计算器据此提示「这里不能有变量」）。</summary>
    public bool UsesVariable => _root.UsesVariable;

    /// <summary>
    /// 解析表达式；写法不对抛 <see cref="ExpressionSyntaxException"/>（带出错位置）。
    /// 空字符串 / 全空白也抛。
    /// </summary>
    public static MathExpression Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ExpressionSyntaxException("表达式是空的", 0);

        var normalized = Normalize(text);
        var parser = new Parser(normalized);
        var root = parser.ParseFull();
        return new MathExpression(text.Trim(), root);
    }

    /// <summary>解析失败不抛异常的版本；<paramref name="error"/> 是给用户看的中文提示。</summary>
    public static bool TryParse(string? text, out MathExpression? expression, out string? error)
    {
        try
        {
            expression = Parse(text);
            error = null;
            return true;
        }
        catch (ExpressionSyntaxException ex)
        {
            expression = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 求值。<paramref name="x"/> 是变量的值（计算器用不到就保持 0）；
    /// <paramref name="angleUnit"/> 只影响三角函数。
    /// </summary>
    public double Evaluate(double x = 0, AngleUnit angleUnit = AngleUnit.Radians)
        => _root.Evaluate(x, angleUnit);

    /// <summary>
    /// 求值并判断「是不是有效数字」：<see cref="double.NaN"/> 视为无定义返回 false，
    /// 正负无穷算有效值（返回 true，界面自己显示 ∞）。
    /// </summary>
    public bool TryEvaluate(double x, AngleUnit angleUnit, out double value)
    {
        value = _root.Evaluate(x, angleUnit);
        return !double.IsNaN(value);
    }

    public override string ToString() => _source;

    // ======================================================================
    // 规范化：把计算器键盘 / 输入法常见符号换成 ASCII
    // ======================================================================

    /// <summary>把 × ÷ − ² ³ 全角括号 / 数字等换成解析器认识的字符。</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '×':
                case '·':
                case '∗':
                case '＊':
                    sb.Append('*');
                    break;
                case '÷':
                case '／':
                    sb.Append('/');
                    break;
                case '−':
                case '–':
                case '—':
                case '－':
                    sb.Append('-');
                    break;
                case '＋':
                    sb.Append('+');
                    break;
                case '＾':
                    sb.Append('^');
                    break;
                case '！':
                    sb.Append('!');
                    break;
                case '（':
                    sb.Append('(');
                    break;
                case '）':
                    sb.Append(')');
                    break;
                case '，':
                    sb.Append(',');
                    break;
                case '²':
                    sb.Append("^2");
                    break;
                case '³':
                    sb.Append("^3");
                    break;
                default:
                    // 全角数字 ０-９ → 半角
                    if (ch >= '０' && ch <= '９') sb.Append((char)('0' + (ch - '０')));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    // ======================================================================
    // 语法树节点（全部私有：外部只通过 MathExpression 用）
    // ======================================================================

    private abstract class Node
    {
        internal abstract double Evaluate(double x, AngleUnit unit);
        internal abstract bool UsesVariable { get; }
    }

    private sealed class NumberNode(double value) : Node
    {
        internal override double Evaluate(double x, AngleUnit unit) => value;
        internal override bool UsesVariable => false;
    }

    private sealed class VariableNode : Node
    {
        internal static readonly VariableNode Instance = new();
        internal override double Evaluate(double x, AngleUnit unit) => x;
        internal override bool UsesVariable => true;
    }

    private sealed class NegateNode(Node operand) : Node
    {
        internal override double Evaluate(double x, AngleUnit unit) => -operand.Evaluate(x, unit);
        internal override bool UsesVariable => operand.UsesVariable;
    }

    private sealed class FactorialNode(Node operand) : Node
    {
        internal override double Evaluate(double x, AngleUnit unit)
        {
            var v = operand.Evaluate(x, unit);
            if (!double.IsFinite(v) || v < 0 || v > 170) return double.NaN;
            var rounded = Math.Round(v);
            if (Math.Abs(rounded - v) > 1e-9) return double.NaN;

            var result = 1.0;
            for (var i = 2; i <= (int)rounded; i++) result *= i;
            return result;
        }

        internal override bool UsesVariable => operand.UsesVariable;
    }

    private sealed class BinaryNode(char op, Node left, Node right) : Node
    {
        internal override double Evaluate(double x, AngleUnit unit)
        {
            var a = left.Evaluate(x, unit);
            var b = right.Evaluate(x, unit);
            return op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => a / b,                 // 除以 0 → ±∞ / NaN，不抛异常
                '^' => Math.Pow(a, b),
                '%' => b == 0 ? double.NaN : a - b * Math.Floor(a / b),   // 数学取模（mod / %）
                _ => double.NaN,
            };
        }

        internal override bool UsesVariable => left.UsesVariable || right.UsesVariable;
    }

    private sealed class FunctionNode(string name, MathFunctions.FunctionDef def, List<Node> args) : Node
    {
        internal override double Evaluate(double x, AngleUnit unit)
        {
            var values = new double[args.Count];
            for (var i = 0; i < args.Count; i++) values[i] = args[i].Evaluate(x, unit);
            try
            {
                return def.Body(values, unit);
            }
            catch
            {
                // 函数实现本身不该抛；万一抛了也按「无定义」处理，别让界面崩
                return double.NaN;
            }
        }

        internal override bool UsesVariable => args.Exists(a => a.UsesVariable);

        public string Name => name;
    }

    // ======================================================================
    // 递归下降解析器
    // ======================================================================

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private readonly int _length = text.Length;
        private int _pos;

        /// <summary>解析整条表达式，并要求整串都被消费掉。</summary>
        internal Node ParseFull()
        {
            var root = ParseAdditive();
            SkipWhitespace();
            if (_pos < _length)
            {
                var rest = _text[_pos..].Trim();
                throw new ExpressionSyntaxException($"表达式末尾多了点东西：「{rest}」", _pos);
            }
            return root;
        }

        // 加减
        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _length) return left;

                if (_text[_pos] == '+')
                {
                    _pos++;
                    left = new BinaryNode('+', left, ParseMultiplicative());
                }
                else if (_text[_pos] == '-')
                {
                    _pos++;
                    left = new BinaryNode('-', left, ParseMultiplicative());
                }
                else
                {
                    return left;
                }
            }
        }

        // 乘除 / 取模 / 省略乘号
        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _length) return left;

                var ch = _text[_pos];
                if (ch == '*')
                {
                    _pos++;
                    left = new BinaryNode('*', left, ParseUnary());
                    continue;
                }
                if (ch == '/')
                {
                    _pos++;
                    left = new BinaryNode('/', left, ParseUnary());
                    continue;
                }
                // 7 mod 3（整词匹配，避免把 mod 当变量名吃掉）
                if (MatchKeyword("mod"))
                {
                    left = new BinaryNode('%', left, ParseUnary());
                    continue;
                }
                // 省略乘号：2x、2(3)、)(、2sin(x) 等
                if (StartsPrimary())
                {
                    left = new BinaryNode('*', left, ParseUnary());
                    continue;
                }
                return left;
            }
        }

        // 一元正负号（比 ^ 松：-2^2 = -(2^2) = -4）
        private Node ParseUnary()
        {
            SkipWhitespace();
            if (_pos < _length)
            {
                if (_text[_pos] == '+')
                {
                    _pos++;
                    return ParseUnary();
                }
                if (_text[_pos] == '-')
                {
                    _pos++;
                    return new NegateNode(ParseUnary());
                }
            }
            return ParsePower();
        }

        // 乘方（右结合：2^3^2 = 2^(3^2)）
        private Node ParsePower()
        {
            var left = ParsePostfix();
            SkipWhitespace();
            if (_pos < _length && _text[_pos] == '^')
            {
                _pos++;
                var right = ParseUnary();      // 允许 2^-1
                return new BinaryNode('^', left, right);
            }
            return left;
        }

        // 后缀阶乘：5!、5!!（连写按多次阶乘算）
        private Node ParsePostfix()
        {
            var node = ParsePrimary();
            while (true)
            {
                SkipWhitespace();
                if (_pos < _length && _text[_pos] == '!')
                {
                    _pos++;
                    node = new FactorialNode(node);
                    continue;
                }
                return node;
            }
        }

        // 数字 / 常量 / 变量 / 函数调用 / 括号
        private Node ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _length)
                throw new ExpressionSyntaxException("表达式不完整：这里还缺一个数、变量或函数", _pos);

            var ch = _text[_pos];

            if (ch == '(')
            {
                _pos++;
                var inner = ParseAdditive();
                SkipWhitespace();
                if (_pos >= _length || _text[_pos] != ')')
                    throw new ExpressionSyntaxException("括号没有闭合（少一个「)」）", _pos);
                _pos++;
                return inner;
            }

            if (char.IsDigit(ch) || ch == '.') return new NumberNode(ParseNumber());

            if (IsIdentifierStart(ch))
            {
                var nameStart = _pos;
                var name = ReadIdentifier();
                if (name.Length == 0)
                    throw new ExpressionSyntaxException($"不认识的符号「{ch}」", nameStart);

                SkipWhitespace();

                // 函数调用
                if (_pos < _length && _text[_pos] == '(')
                {
                    if (!MathFunctions.TryGetFunction(name, out var def))
                        throw new ExpressionSyntaxException($"未知函数「{name}」", nameStart);

                    _pos++;   // 吃掉 '('
                    var args = new List<Node>();
                    SkipWhitespace();
                    if (_pos < _length && _text[_pos] == ')')
                    {
                        _pos++;
                    }
                    else
                    {
                        while (true)
                        {
                            args.Add(ParseAdditive());
                            SkipWhitespace();
                            if (_pos < _length && _text[_pos] == ',')
                            {
                                _pos++;
                                SkipWhitespace();
                                continue;
                            }
                            if (_pos < _length && _text[_pos] == ')')
                            {
                                _pos++;
                                break;
                            }
                            throw new ExpressionSyntaxException(
                                $"函数「{name}」的参数之间应当是「,」，最后用「)」结束", _pos);
                        }
                    }

                    if (!def.AcceptsArity(args.Count))
                    {
                        var need = def.MinArgs == def.MaxArgs ? def.MinArgs.ToString() : $"{def.MinArgs}~{def.MaxArgs}";
                        throw new ExpressionSyntaxException(
                            $"函数「{name}」需要 {need} 个参数，实际给了 {args.Count} 个（写法：{def.Signature}）", nameStart);
                    }

                    return new FunctionNode(name, def, args);
                }

                // 常量
                if (MathFunctions.TryGetConstant(name, out var constant)) return new NumberNode(constant);

                // 变量
                if (string.Equals(name, "x", StringComparison.OrdinalIgnoreCase)) return VariableNode.Instance;

                // 常见笔误：写了函数名却没括号
                if (MathFunctions.IsFunction(name))
                    throw new ExpressionSyntaxException($"「{name}」是函数名，后面要跟括号，例如 {name}(…)", nameStart);

                throw new ExpressionSyntaxException(
                    $"未知符号「{name}」（变量只有 x；常量有 pi / e / tau；函数要带括号）", nameStart);
            }

            throw new ExpressionSyntaxException($"不认识的字符「{ch}」", _pos);
        }

        // 数字：123、1.5、.5、1e-3（注意 2e 是 2×e，不是指数）
        private double ParseNumber()
        {
            var start = _pos;

            while (_pos < _length && char.IsDigit(_text[_pos])) _pos++;
            if (_pos < _length && _text[_pos] == '.')
            {
                _pos++;
                while (_pos < _length && char.IsDigit(_text[_pos])) _pos++;
            }

            if (_pos < _length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
            {
                var save = _pos;
                var probe = _pos + 1;
                if (probe < _length && (_text[probe] == '+' || _text[probe] == '-')) probe++;
                if (probe < _length && char.IsDigit(_text[probe]))
                {
                    _pos = probe;
                    while (_pos < _length && char.IsDigit(_text[_pos])) _pos++;
                }
                else
                {
                    _pos = save;   // 交给隐式乘法：2e → 2 * e
                }
            }

            var raw = _text[start.._pos];
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new ExpressionSyntaxException($"数字格式不对：「{raw}」", start);
            return value;
        }

        // ---------- 字符工具 ----------

        private void SkipWhitespace()
        {
            while (_pos < _length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }

        private static bool IsIdentifierStart(char ch)
            => char.IsLetter(ch) || ch == '_' || ch == '∞';

        private static bool IsIdentifierPart(char ch)
            => char.IsLetterOrDigit(ch) || ch == '_' || ch == '∞';

        private string ReadIdentifier()
        {
            var start = _pos;
            while (_pos < _length && IsIdentifierPart(_text[_pos])) _pos++;
            return _text[start.._pos];
        }

        /// <summary>当前位置是不是整词 <paramref name="keyword"/>（忽略大小写，且后面不能紧跟标识符字符）。</summary>
        private bool MatchKeyword(string keyword)
        {
            if (_pos + keyword.Length > _length) return false;
            if (string.Compare(_text, _pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            var after = _pos + keyword.Length;
            if (after < _length && IsIdentifierPart(_text[after])) return false;
            _pos = after;
            return true;
        }

        /// <summary>下一个字符看起来能不能开始一个「主表达式」（用于省略乘号的判断）。</summary>
        private bool StartsPrimary()
        {
            if (_pos >= _length) return false;
            var ch = _text[_pos];
            return char.IsDigit(ch) || ch == '.' || ch == '(' || IsIdentifierStart(ch);
        }
    }
}
