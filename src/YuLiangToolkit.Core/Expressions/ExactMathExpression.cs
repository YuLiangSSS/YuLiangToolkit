using System.Globalization;
using System.Numerics;
using YuLiangToolkit.Core.Numbers;

namespace YuLiangToolkit.Core.Expressions;

/// <summary>
/// 「精确表达式」求值：只处理能表示成有限位十进制数的算式（整数 / 有限小数、
/// <c>+ - * / ^ ! mod</c>、括号、一元正负号），结果用 <see cref="ExactDecimal"/>（BigInteger 尾数）算，
/// 所以 <c>100!</c>、<c>2^1000</c>、<c>12345678901234567890+1</c> 都能给出每一位都对的完整数字。
///
/// <para><b>什么时候不接管</b>（<see cref="TryEvaluate"/> 返回 false，调用方回退到 double 引擎
/// <see cref="MathExpression"/>）：出现函数 / 常量 / 变量、除不尽（<c>1/3</c>）、负指数幂、
/// 非整数阶乘、结果位数失控，以及写法本身不对。这样科学计算与函数绘图完全不受影响。</para>
///
/// <para>纯逻辑、无状态，不碰界面 / 存储，可直接被自检断言。</para>
/// </summary>
public static class ExactMathExpression
{
    /// <summary>阶乘允许的最大入参（再大结果位数会失控，交给 double 显示 ∞）。</summary>
    public const int MaxFactorial = 10000;

    /// <summary>乘方允许的最大指数（配合结果位数上限防手滑卡死）。</summary>
    public const int MaxExponent = 1_000_000;

    /// <summary>
    /// 试求精确值。成功时 <paramref name="text"/> 是完整十进制文本（不截断、不用科学计数法）。
    /// 任何「精确不了 / 太大 / 写法不认识」的情况都返回 false，由调用方回退。
    /// </summary>
    public static bool TryEvaluate(string? expression, out ExactDecimal value, out string text)
    {
        value = ExactDecimal.Zero;
        text = "";
        if (string.IsNullOrWhiteSpace(expression)) return false;

        try
        {
            var normalized = MathExpression.Normalize(expression);
            var parser = new Parser(normalized);
            var root = parser.ParseFull();
            var result = root.Evaluate();
            var resultText = result.ToPlainString();
            if (resultText.Length > ExactDecimal.MaxTextLength) return false;

            value = result;
            text = resultText;
            return true;
        }
        catch (NotExactException) { return false; }
        catch (ExpressionSyntaxException) { return false; }   // 写法不归精确引擎管（交给 double 引擎报错）
        catch (ArithmeticException) { return false; }         // 位数 / 指数失控（含 OverflowException）
    }

    /// <summary>「这个式子精确引擎管不了」的内部信号（不是用户可见错误）。</summary>
    private sealed class NotExactException : Exception;

    // ---------- 语法树 ----------

    private abstract class Node
    {
        internal abstract ExactDecimal Evaluate();
    }

    private sealed class NumberNode(ExactDecimal value) : Node
    {
        internal override ExactDecimal Evaluate() => value;
    }

    private sealed class NegateNode(Node operand) : Node
    {
        internal override ExactDecimal Evaluate() => -operand.Evaluate();
    }

    private sealed class FactorialNode(Node operand) : Node
    {
        internal override ExactDecimal Evaluate()
        {
            var v = operand.Evaluate();
            if (!v.IsInteger || v.IsNegative || !v.TryToInt32(out var n) || n > MaxFactorial)
                throw new NotExactException();

            var result = BigInteger.One;
            for (var i = 2; i <= n; i++)
            {
                result *= i;
                if ((i & 0xFF) == 0 && result.ToString(CultureInfo.InvariantCulture).Length > ExactDecimal.MaxTextLength)
                    throw new ArithmeticException("阶乘结果过大");
            }
            return ExactDecimal.FromInteger(result);
        }
    }

    private sealed class BinaryNode(char op, Node left, Node right) : Node
    {
        internal override ExactDecimal Evaluate()
        {
            var a = left.Evaluate();
            var b = right.Evaluate();
            return op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => Divide(a, b),
                '^' => Power(a, b),
                '%' => Modulo(a, b),
                _ => throw new NotExactException(),
            };
        }

        private static ExactDecimal Divide(ExactDecimal a, ExactDecimal b)
            => ExactDecimal.TryDivide(a, b, out var q) ? q : throw new NotExactException();

        private static ExactDecimal Power(ExactDecimal a, ExactDecimal b)
        {
            if (!b.IsInteger || b.IsNegative || !b.TryToInt32(out var exponent) || exponent > MaxExponent)
                throw new NotExactException();   // 负指数 / 分数指数交给 double
            return a.Pow(exponent);
        }

        private static ExactDecimal Modulo(ExactDecimal a, ExactDecimal b)
        {
            if (b.IsZero) throw new NotExactException();   // 模 0 交给 double 显示无定义
            // a - b * floor(a/b)：对齐后全是 BigInteger，结果仍是精确有限小数
            var scale = Math.Max(a.Scale, b.Scale);
            var x = a.Unscaled * ExactDecimal.Pow10(scale - a.Scale);
            var y = b.Unscaled * ExactDecimal.Pow10(scale - b.Scale);
            var q = BigInteger.DivRem(x, y, out var r);
            if (!r.IsZero && (r.Sign < 0) != (y.Sign < 0)) q -= 1;   // 截断 → floor
            var remainder = ExactDecimal.FromInteger(x - q * y);
            return ExactDecimal.TryDivide(remainder, ExactDecimal.FromInteger(ExactDecimal.Pow10(scale)), out var result)
                ? result
                : throw new NotExactException();
        }
    }

    // ---------- 递归下降解析器（优先级与 MathExpression 保持一致） ----------

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private readonly int _length = text.Length;
        private int _pos;

        internal Node ParseFull()
        {
            var root = ParseAdditive();
            SkipWhitespace();
            if (_pos < _length) throw new ExpressionSyntaxException("末尾有多余内容", _pos);
            return root;
        }

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
                else return left;
            }
        }

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
                if (MatchKeyword("mod"))
                {
                    left = new BinaryNode('%', left, ParseUnary());
                    continue;
                }
                // 省略乘号：2(3+4)、(1+2)(3+4) 等；出现字母说明是函数/常量/变量 → 交给 double 引擎
                if (StartsPrimary())
                {
                    left = new BinaryNode('*', left, ParseUnary());
                    continue;
                }
                return left;
            }
        }

        private Node ParseUnary()
        {
            SkipWhitespace();
            if (_pos < _length)
            {
                if (_text[_pos] == '+') { _pos++; return ParseUnary(); }
                if (_text[_pos] == '-') { _pos++; return new NegateNode(ParseUnary()); }
            }
            return ParsePower();
        }

        private Node ParsePower()
        {
            var left = ParsePostfix();
            SkipWhitespace();
            if (_pos < _length && _text[_pos] == '^')
            {
                _pos++;
                return new BinaryNode('^', left, ParseUnary());   // 右结合；2^-1 由 Power 判负指数回退
            }
            return left;
        }

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

        private Node ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _length) throw new ExpressionSyntaxException("缺数", _pos);
            var ch = _text[_pos];

            if (ch == '(')
            {
                _pos++;
                var inner = ParseAdditive();
                SkipWhitespace();
                if (_pos >= _length || _text[_pos] != ')') throw new ExpressionSyntaxException("括号没有闭合", _pos);
                _pos++;
                return inner;
            }

            if (char.IsDigit(ch) || ch == '.') return new NumberNode(ParseNumber());

            // 函数名 / 常量 / 变量 / 单字母 → 精确引擎不认识，回退
            throw new NotExactException();
        }

        private ExactDecimal ParseNumber()
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
                else _pos = save;   // 2e → 2 * e，交给 double
            }

            var raw = _text[start.._pos];
            if (!ExactDecimal.TryParse(raw, out var value))
                throw new ExpressionSyntaxException("数字格式不对", start);
            return value;
        }

        private void SkipWhitespace()
        {
            while (_pos < _length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }

        private bool MatchKeyword(string keyword)
        {
            if (_pos + keyword.Length > _length) return false;
            if (string.Compare(_text, _pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            var after = _pos + keyword.Length;
            if (after < _length && (char.IsLetterOrDigit(_text[after]) || _text[after] == '_')) return false;
            _pos = after;
            return true;
        }

        private bool StartsPrimary()
            => _pos < _length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.' || _text[_pos] == '(');
    }
}
