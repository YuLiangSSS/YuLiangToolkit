namespace YuLiangToolkit.Core.Mvvm;

/// <summary>
/// 浏览器式的「后退 / 前进」历史（**纯逻辑、无界面**，自检可以直接断言）。
///
/// <para><b>行为（和浏览器一致）</b>：</para>
/// <list type="bullet">
///   <item>每访问一个页面 <see cref="Push"/> 一条；与**当前**位置相同则不重复记录（点同一个导航项不会越点越多）；</item>
///   <item><see cref="TryGoBack"/> / <see cref="TryGoForward"/> 只移动“当前指针”，不删记录；</item>
///   <item>从历史中间（后退过几次）再访问新页面时，**丢掉后面的“前进”记录**，
///   历史变成从当前位置往后只有新页面 —— 这也是浏览器行为；</item>
///   <item>最多保留 <see cref="MaxEntries"/> 条（超出丢最老的），避免长时间使用无限增长。</item>
/// </list>
///
/// <para>本类不碰 WPF：外壳那边用 <c>NavigationHistory&lt;ShellLocation&gt;</c>，
/// 自检用 <c>NavigationHistory&lt;string&gt;</c> 就能把上面每条行为钉住。</para>
/// </summary>
/// <typeparam name="T">一条历史记录（外壳里是页面位置；相等性由构造时给的比较器决定）。</typeparam>
public sealed class NavigationHistory<T> where T : notnull
{
    /// <summary>最多保留多少条历史（超出丢最老的）。</summary>
    public const int MaxEntries = 100;

    private readonly List<T> _entries = new();
    private readonly IEqualityComparer<T> _comparer;

    /// <summary>当前位置在 <see cref="Entries"/> 里的下标；-1 = 还没有任何记录。</summary>
    private int _index = -1;

    /// <summary>创建一份空历史。</summary>
    /// <param name="comparer">判断“是不是同一个页面”用的比较器；不给就用默认相等性。</param>
    public NavigationHistory(IEqualityComparer<T>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    /// <summary>历史记录条数（含“前进”那部分）。</summary>
    public int Count => _entries.Count;

    /// <summary>全部历史记录的只读视图（从最老到最新；调试 / 自检用，别直接改）。</summary>
    public IReadOnlyList<T> Entries => _entries;

    /// <summary>当前页在历史里的下标（0 起；空历史为 -1）。</summary>
    public int Index => _index;

    /// <summary>能不能后退（已经不在最老的那条上）。</summary>
    public bool CanGoBack => _index > 0;

    /// <summary>能不能前进（后面还有记录）。</summary>
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    /// <summary>当前页对应的记录；空历史为 <c>null</c>。</summary>
    public T? Current => _index >= 0 && _index < _entries.Count ? _entries[_index] : default;

    /// <summary>
    /// 记录一次新的页面访问。与当前位置相同则不记录并返回 <c>false</c>；
    /// 若当前在历史中间（可以前进），会先丢弃后面的“前进”记录（浏览器行为）。
    /// </summary>
    public bool Push(T entry)
    {
        if (_index >= 0 && _comparer.Equals(_entries[_index], entry)) return false;

        if (_index < _entries.Count - 1)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(entry);
        _index = _entries.Count - 1;

        // 上限保护：丢最老的一条，指针跟着前移（当前位置永远是最新记录）
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
            _index = _entries.Count - 1;
        }

        return true;
    }

    /// <summary>后退一格；没有可后退的记录时返回 <c>false</c>（不改状态）。</summary>
    public bool TryGoBack(out T entry)
    {
        if (!CanGoBack)
        {
            entry = default!;
            return false;
        }

        _index--;
        entry = _entries[_index];
        return true;
    }

    /// <summary>前进一格；没有可前进的记录时返回 <c>false</c>（不改状态）。</summary>
    public bool TryGoForward(out T entry)
    {
        if (!CanGoForward)
        {
            entry = default!;
            return false;
        }

        _index++;
        entry = _entries[_index];
        return true;
    }

    /// <summary>清空历史。</summary>
    public void Clear()
    {
        _entries.Clear();
        _index = -1;
    }
}
