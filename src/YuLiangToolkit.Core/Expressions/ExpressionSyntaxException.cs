namespace YuLiangToolkit.Core.Expressions;

/// <summary>
/// 表达式语法错误：带出错位置（0 起的字符下标），界面可以据此高亮 / 给出「第几个字符有问题」的提示。
/// <para>只表示「写法不对」；能解析但数学上无定义（<c>sqrt(-1)</c>、<c>1/0</c>）不会抛它，求值时返回
/// <see cref="double.NaN"/> / <see cref="double.PositiveInfinity"/>，由界面决定怎么显示。</para>
/// </summary>
public sealed class ExpressionSyntaxException : Exception
{
    /// <summary>出错位置（0 起的字符下标，相对规范化之后的文本）。</summary>
    public int Position { get; }

    public ExpressionSyntaxException(string message, int position) : base(message)
    {
        Position = position;
    }
}
