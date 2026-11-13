using System.Text;

namespace YuLiangToolkit.Core.Input;

/// <summary>
/// 一个「按键集合」—— 快捷键的最小单元：若干修饰键 + 若干主键，要求**这些键同时按下**。
///
/// <para>例如 <c>Ctrl+F9</c>（修饰键 Ctrl + 主键 F9）、<c>Ctrl+U+K</c>（修饰键 Ctrl + 主键 U、K）。</para>
///
/// <para><b>为什么用「集合」而不是「修饰键 + 一个主键」</b>：分步快捷键「合并」之后会得到
/// <c>Ctrl+U+K</c> 这种 1 个修饰键 + 2 个主键的组合，Win32 的 <c>RegisterHotKey</c> 表达不了，
/// 但它是本项目的合法数据（由键盘钩子引擎识别）。所以底层类型必须能装下任意集合，
/// 至于「这条组合用哪种引擎注册」由 <see cref="GlobalShortcutEngine"/> 按主键个数决定。</para>
///
/// <para><b>不可变</b>：所有「改一下」的方法都返回新实例，避免注册表里共享的实例被就地改坏。</para>
///
/// <para><b>文本形式</b>：<see cref="ToString"/> 输出规范写法（修饰键按 Ctrl → Alt → Shift → Win 排序、
/// 主键保持录入顺序），<see cref="TryParse"/> 一定能把它原样解析回来（往返一致）。</para>
/// </summary>
public sealed class KeyCombo : IEquatable<KeyCombo>
{
    /// <summary>
    /// 一条组合最多几个键（修饰键 + 主键）。上限 4：再多人类手指也按不过来，
    /// 而且很容易和别的软件撞（合并时用它拦住「做出按不出来的快捷键」）。
    /// </summary>
    public const int MaxTotalKeys = 4;

    /// <summary>空组合（没有任何键）—— 表示「这条快捷键没设置」。</summary>
    public static KeyCombo Empty { get; } = new(Array.Empty<int>(), Array.Empty<int>());

    private readonly int[] _modifiers;
    private readonly int[] _keys;

    private KeyCombo(IEnumerable<int> modifiers, IEnumerable<int> keys)
    {
        // 修饰键：归一（左右同键合并到通用键）+ 去重 + 固定顺序 Ctrl → Alt → Shift → Win，
        // 这样 "Shift+Ctrl+A" 与 "Ctrl+Shift+A" 打印出来是同一个规范写法。
        _modifiers = modifiers
            .Where(k => k != 0)
            .Select(KeyComboNames.NormalizeModifier)
            .Distinct()
            .OrderBy(ModifierOrder)
            .ToArray();

        // 主键：**保留录入顺序**（只去重）。
        // 为什么不像修饰键那样排序：用户录的是 Ctrl+U+K，就应该继续显示 Ctrl+U+K；
        // 排成 Ctrl+K+U 会让人怀疑「我录的键是不是被改了」。相等比较仍然与顺序无关（见 Equals）。
        _keys = keys.Where(k => k != 0).Distinct().ToArray();
    }

    /// <summary>
    /// 构造一个按键集合。<paramref name="modifiers"/> 里的主键会被自动挪进主键集合，
    /// <paramref name="keys"/> 里的修饰键会被自动挪进修饰键集合 —— 调用方不用先把它们分开。
    /// </summary>
    public static KeyCombo FromKeys(IEnumerable<int> keys)
    {
        var modifiers = new List<int>();
        var main = new List<int>();
        foreach (var key in keys)
        {
            if (key == 0) continue;
            if (KeyComboNames.IsModifier(key)) modifiers.Add(key);
            else main.Add(key);
        }
        return new KeyCombo(modifiers, main);
    }

    /// <summary>从「修饰键集合 + 主键集合」构造（两边都宽松处理，见 <see cref="FromKeys"/>）。</summary>
    public static KeyCombo FromParts(IEnumerable<int> modifiers, IEnumerable<int> keys)
        => FromKeys(modifiers.Concat(keys));

    /// <summary>顺序：Ctrl → Alt → Shift → Win（其余修饰键排最后）。</summary>
    private static int ModifierOrder(int modifier)
    {
        var normalized = KeyComboNames.NormalizeModifier(modifier);
        return normalized switch
        {
            0x11 => 0,
            0x12 => 1,
            0x10 => 2,
            0x5B => 3,
            _ => 9,
        };
    }

    /// <summary>修饰键集合（已归一、去重、按 Ctrl → Alt → Shift → Win 排序）。</summary>
    public IReadOnlyList<int> Modifiers => _modifiers;

    /// <summary>主键集合（已去重，保留录入顺序）。</summary>
    public IReadOnlyList<int> Keys => _keys;

    /// <summary>没有任何键 = 没设置。</summary>
    public bool IsEmpty => _keys.Length == 0;

    /// <summary>总键数（修饰键 + 主键）。</summary>
    public int KeyCount => _modifiers.Length + _keys.Length;

    /// <summary>
    /// 能不能直接交给 Win32 <c>RegisterHotKey</c>：必须**恰好一个主键**且有修饰键
    /// （单独一个 F 键也可以）。多主键组合只能走键盘钩子。
    /// </summary>
    public bool IsRegisterHotKeyCompatible
        => _keys.Length == 1
           && (_modifiers.Length > 0 || IsFunctionKey(_keys[0]));

    /// <summary>是不是单独一个 F1~F24（这类键允许不带修饰键）。</summary>
    public static bool IsFunctionKey(int virtualKey) => virtualKey is >= 0x70 and <= 0x87;

    /// <summary>
    /// 这条组合作为「全局快捷键」是否合法：不能为空、必须有主键、总键数不超上限；
    /// 单个字母 / 数字 / 空格 / 导航键必须至少带一个修饰键（否则会把正常打字吃掉）。
    /// </summary>
    public bool IsValidGlobalHotkey
    {
        get
        {
            if (IsEmpty) return false;
            if (KeyCount > MaxTotalKeys) return false;
            if (_modifiers.Length > 0) return true;
            return _keys.Length == 1 && IsFunctionKey(_keys[0]);
        }
    }

    /// <summary>不合法时的中文原因（合法时为空串），直接给界面显示。</summary>
    public string Validate()
    {
        if (IsEmpty) return "还没有按键";
        if (KeyCount > MaxTotalKeys) return $"最多 {MaxTotalKeys} 个键（现在 {KeyCount} 个），再多按不过来";
        if (_modifiers.Length == 0 && !(_keys.Length == 1 && IsFunctionKey(_keys[0])))
            return "字母 / 数字 / 空格必须配合 Ctrl、Alt、Shift、Win 之一使用（F1~F24 可以单独用）";
        return "";
    }

    /// <summary>
    /// 合并两条组合（分步 → 同时用）：取并集。
    /// 比如 <c>Ctrl+U</c> + <c>Ctrl+K</c> → <c>Ctrl+U+K</c>；
    /// 公共的修饰键只留一份（<c>Ctrl+Shift+A</c> + <c>Ctrl+B</c> → <c>Ctrl+Shift+A+B</c>）。
    /// </summary>
    public KeyCombo Union(KeyCombo other) => FromKeys(_modifiers.Concat(other._modifiers).Concat(_keys).Concat(other._keys));

    /// <summary>合并后的键数（用来在合并前判断会不会超过 <see cref="MaxTotalKeys"/>）。</summary>
    public int UnionKeyCount(KeyCombo other) => Union(other).KeyCount;

    /// <summary>规范文本，例如 <c>Ctrl+F9</c> / <c>Ctrl+U+K</c> / <c>F8</c>；空组合返回空字符串。</summary>
    public override string ToString()
    {
        if (IsEmpty) return "";
        var builder = new StringBuilder();
        foreach (var modifier in _modifiers)
        {
            builder.Append(KeyComboNames.ModifierDisplay(modifier));
            builder.Append('+');
        }
        builder.Append(string.Join("+", _keys.Select(KeyComboNames.Display)));
        return builder.ToString();
    }

    /// <summary>解析文本（<c>Ctrl+U+K</c> / <c>ctrl + u + k</c> / <c>控制+U+K</c>）；失败返回 false，不抛异常。</summary>
    public static bool TryParse(string? text, out KeyCombo combo)
    {
        combo = Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 全角加号统一成半角：中文输入法下也能直接敲
        var normalized = text.Trim().Replace('＋', '+');
        if (normalized is "无" or "none" or "-" or "—" or "禁用") return false;

        // 修掉「键名里自带加号」的括号写法（小键盘+ / 小键盘-），避免被 "+" 切坏
        var escaped = normalized
            .Replace("小键盘+", "(numpadadd)")
            .Replace("小键盘-", "(numpadsub)")
            .Replace("小键盘*", "(numpadmul)")
            .Replace("小键盘/", "(numpaddiv)");

        var keys = new List<int>();
        foreach (var raw in escaped.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) continue;   // 容忍结尾多余 '+' / "Ctrl + F9" 里的空格

            var segment = raw.Trim('(', ')');
            var lookup = segment switch
            {
                "numpadadd" => "numpad+",
                "numpadsub" => "numpad-",
                "numpadmul" => "numpad*",
                "numpaddiv" => "numpad/",
                _ => segment,
            };

            if (KeyComboNames.TryParseModifier(lookup, out var modifier))
            {
                keys.Add(modifier);
                continue;
            }

            if (!KeyComboNames.TryParseKey(lookup, out var key)) return false;
            keys.Add(key);
        }

        if (keys.Count == 0) return false;
        combo = FromKeys(keys);
        return !combo.IsEmpty;
    }

    /// <summary>解析并同时校验是不是合法的全局快捷键（界面上「确定」按钮用）。</summary>
    public static bool TryParseValid(string? text, out KeyCombo combo, out string error)
    {
        error = "";
        if (!TryParse(text, out combo))
        {
            error = string.IsNullOrWhiteSpace(text) ? "还没有按键" : "看不懂这个写法";
            return false;
        }

        error = combo.Validate();
        return error.Length == 0;
    }

    /// <summary>内容相等（修饰键与主键集合都一致，**与录入顺序无关**）。</summary>
    public bool Equals(KeyCombo? other)
    {
        if (other is null) return false;
        if (_modifiers.Length != other._modifiers.Length || _keys.Length != other._keys.Length) return false;
        return _modifiers.SequenceEqual(other._modifiers)
               && _keys.OrderBy(k => k).SequenceEqual(other._keys.OrderBy(k => k));
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as KeyCombo);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // 与顺序无关：主键先排序再算 hash（否则 U+K 与 K+U 相等却 hash 不同，放进字典会挂）
        var hash = new HashCode();
        foreach (var modifier in _modifiers) hash.Add(modifier);
        hash.Add(0x1F);   // 分隔，避免 {Ctrl}+{A} 与 {Ctrl,A} 撞 hash
        foreach (var key in _keys.OrderBy(k => k)) hash.Add(key);
        return hash.ToHashCode();
    }

    /// <summary>用 == / != 比较内容（引用比较会让「内容相同」的两条组合判成不同）。</summary>
    public static bool operator ==(KeyCombo? left, KeyCombo? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>见 <see cref="operator ==(KeyCombo, KeyCombo)"/>。</summary>
    public static bool operator !=(KeyCombo? left, KeyCombo? right) => !(left == right);
}
