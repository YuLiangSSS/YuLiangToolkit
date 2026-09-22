namespace YuLiangToolkit.Core.Mvvm;

/// <summary>
/// 下拉框 / 单选列表的选项：<typeparamref name="T"/> 是要持久化的真实值，
/// <see cref="Label"/> 是界面显示文本（两者分离，配置文件里存的是干净的值而不是中文标签）。
///
/// <para><b>用法</b>：</para>
/// <code>
/// public IReadOnlyList&lt;OptionItem&lt;int&gt;&gt; LevelOptions { get; } = OptionItem.From(
///     (0, "0（最快）"), (5, "5（推荐）"), (8, "8（最小）"));
///
/// // XAML：ComboBox 记得写 DisplayMemberPath="Label"
/// &lt;ComboBox ItemsSource="{Binding LevelOptions}" DisplayMemberPath="Label"
///           SelectedItem="{Binding SelectedLevel}" /&gt;
/// </code>
///
/// <para>已实现值相等（<see cref="Equals(OptionItem{T})"/>）：即使传入的是新实例，
/// ComboBox 也能正确选中同一项的现有实例。</para>
/// </summary>
public sealed class OptionItem<T> : IEquatable<OptionItem<T>>
{
    public OptionItem(T value, string label)
    {
        Value = value;
        Label = label;
    }

    /// <summary>真正要保存/参与逻辑的值。</summary>
    public T Value { get; }

    /// <summary>界面显示文本。</summary>
    public string Label { get; }

    /// <summary>字符串化时显示 Label（防止忘记写 DisplayMemberPath 时显示类型名）。</summary>
    public override string ToString() => Label;

    public bool Equals(OptionItem<T>? other)
        => other is not null && EqualityComparer<T>.Default.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is OptionItem<T> other && Equals(other);

    public override int GetHashCode() => EqualityComparer<T>.Default.GetHashCode(Value!);

    /// <summary>在选项集合里按值查找对应实例（值不存在时返回 null）。</summary>
    public static OptionItem<T>? Find(IEnumerable<OptionItem<T>> options, T value)
        => options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value));

    /// <summary>用 (值, 标签) 元组数组快速构造选项集合。</summary>
    public static OptionItem<T>[] From(params (T Value, string Label)[] items)
        => items.Select(i => new OptionItem<T>(i.Value, i.Label)).ToArray();
}

/// <summary>非泛型便捷构造（类型推断用）。</summary>
public static class OptionItem
{
    /// <summary>用 (值, 标签) 元组数组构造选项集合：<c>OptionItem.From((1, "一"), (2, "二"))</c>。</summary>
    public static OptionItem<T>[] From<T>(params (T Value, string Label)[] items)
        => OptionItem<T>.From(items);

    /// <summary>用一组值构造选项集合，标签默认取值的字符串形式。</summary>
    public static OptionItem<T>[] FromValues<T>(params T[] values)
        => values.Select(v => new OptionItem<T>(v, v?.ToString() ?? "")).ToArray();

    /// <summary>用一组值 + 标签生成函数构造选项集合。</summary>
    public static OptionItem<T>[] FromValues<T>(IEnumerable<T> values, Func<T, string> labelSelector)
        => values.Select(v => new OptionItem<T>(v, labelSelector(v))).ToArray();
}
