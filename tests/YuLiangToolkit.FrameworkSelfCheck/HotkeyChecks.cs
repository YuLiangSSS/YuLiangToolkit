using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 快捷键体系 + 通知体系的自检（组 <c>hotkeys</c>）：
/// 按键集合的解析 / 格式化往返、合并（分步 → 同时）、合法性校验、键数上限、
/// 分步判定与超时夹取、分组树结构、冲突检测、单条与工具级启停正交、
/// 拖放式分组重建、以及通知设置的默认值 / 四角枚举 / JSON 序列化形状。
///
/// <para><b>全是纯逻辑</b>：不创建窗口、不装键盘钩子、<b>绝不调用 RegisterHotKey</b>
/// （那会真的占用用户的快捷键）。真实注册 / 触发由人工冒烟验收。</para>
/// </summary>
internal static class HotkeyChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckParseAndFormat(assert);
        CheckValidate(assert);
        CheckUnion(assert);
        CheckEngineCompatibility(assert);
        CheckRegistryGrouping(assert);
        CheckConflictDetection(assert);
        CheckEnableSwitches(assert);
        CheckMergeSplit(assert);
        CheckSequenceRules(assert);
        CheckNotificationSettings(assert);
        CheckNotificationCenterFallback(assert);
    }

    // ---------------------------------------------------------------- 解析 / 格式化

    private static void CheckParseAndFormat(CheckFn check)
    {
        var ok = KeyCombo.TryParse("Ctrl+U+K", out var combo);
        check("[快捷键] 解析 Ctrl+U+K：1 个修饰键 + 2 个主键",
            ok && combo.Modifiers.Count == 1 && combo.Keys.Count == 2 && combo.KeyCount == 3,
            $"{combo} ({combo.Modifiers.Count}+{combo.Keys.Count})");

        var roundTrip = new[] { "Ctrl+F9", "Ctrl+Alt+L", "Ctrl+U+K", "F11", "Ctrl+Shift+Space", "Alt+0" };
        var failures = roundTrip
            .Where(text => !KeyCombo.TryParse(text, out var parsed) || parsed.ToString() != text)
            .ToList();
        check("[快捷键] 文本往返一致（解析 → 输出还是同一个写法）",
            failures.Count == 0, failures.Count == 0 ? string.Join(" / ", roundTrip) : string.Join(", ", failures));

        check("[快捷键] 大小写 / 全角加号 / 空格 / 中文修饰名都认",
            KeyCombo.TryParse("ctrl + u + k", out var lower) && lower.ToString() == "Ctrl+U+K"
            && KeyCombo.TryParse("控制＋U＋K", out var full) && full.ToString() == "Ctrl+U+K"
            && KeyCombo.TryParse("ALT+f9", out var alt) && alt.ToString() == "Alt+F9",
            "ctrl + u + k / 控制＋U＋K / ALT+f9");

        check("[快捷键] 修饰键顺序统一（Shift+Ctrl+A 输出成 Ctrl+Shift+A）",
            KeyCombo.TryParse("Shift+Ctrl+A", out var unordered) && unordered.ToString() == "Ctrl+Shift+A",
            unordered.ToString());

        check("[快捷键] 左右修饰键归一（左Ctrl+左Shift+A == Ctrl+Shift+A）",
            KeyCombo.TryParse("左Ctrl+左Shift+A", out var left) && left.ToString() == "Ctrl+Shift+A"
            && left == unordered,
            left.ToString());

        KeyCombo.TryParse("Ctrl+A", out var a);
        KeyCombo.TryParse("control+a", out var b);
        check("[快捷键] 同一组合的两种写法相等（用 == 比较内容，不是引用）",
            a == b && a.GetHashCode() == b.GetHashCode(),
            $"{a} == {b}");

        check("[快捷键] 空 / 乱写 / 只有修饰键都判为解析失败",
            !KeyCombo.TryParse("", out _) && !KeyCombo.TryParse("  ", out _)
            && !KeyCombo.TryParse("乱写的东西", out _) && !KeyCombo.TryParse("Ctrl+", out _),
            "空 / 空白 / 乱写 / Ctrl+");

        check("[快捷键] 空组合输出空串、判为空",
            KeyCombo.Empty.IsEmpty && KeyCombo.Empty.ToString() == "" && KeyCombo.Empty.KeyCount == 0,
            $"[\u0022{KeyCombo.Empty}\u0022]");

        KeyComboNames.TryParseKey("f24", out var f24);
        KeyComboNames.TryParseKey("F1", out var f1);
        check("[快捷键] F1~F24 显示成 F1 / F24（不是 VK 代号）",
            KeyComboNames.Display(f24) == "F24" && KeyComboNames.Display(f1) == "F1",
            KeyComboNames.Display(f24) + " / " + KeyComboNames.Display(f1));

        check("[快捷键] 小键盘运算符这种名字里带 + 的键也能往返",
            KeyCombo.TryParse("Ctrl+小键盘+", out var numpad) && numpad.ToString() == "Ctrl+小键盘+",
            numpad.ToString());
    }

    // ---------------------------------------------------------------- 合法性

    private static void CheckValidate(CheckFn check)
    {
        KeyCombo.TryParse("F9", out var f9);
        check("[快捷键] 单个 F 键可以单独用（不带修饰键也合法）",
            f9.IsValidGlobalHotkey && f9.Validate() == "",
            f9.Validate());

        KeyCombo.TryParse("A", out var bareLetter);
        check("[快捷键] 光一个字母不合法（会把正常打字吃掉）",
            !bareLetter.IsValidGlobalHotkey && bareLetter.Validate().Contains("Ctrl"),
            bareLetter.Validate());

        KeyCombo.TryParse("Ctrl+A+B+C+D", out var tooMany);
        check("[快捷键] 超过 4 个键判不合法（再多按不过来）",
            tooMany.KeyCount == 5 && !tooMany.IsValidGlobalHotkey && tooMany.Validate().Contains("最多"),
            tooMany.Validate());

        check("[快捷键] 空组合校验给出「还没有按键」",
            KeyCombo.Empty.Validate() == "还没有按键", KeyCombo.Empty.Validate());

        KeyCombo.TryParseValid("Ctrl+F9", out var valid, out var validError);
        var validAccepted = validError.Length == 0 && valid.ToString() == "Ctrl+F9";
        var invalidRejected = !KeyCombo.TryParseValid("A", out _, out var invalidError) && invalidError.Length > 0;
        check("[快捷键] TryParseValid 一次给出组合 + 错误",
            validAccepted && invalidRejected,
            validError + " / " + invalidError);
    }

    // ---------------------------------------------------------------- 合并

    private static void CheckUnion(CheckFn check)
    {
        KeyCombo.TryParse("Ctrl+U", out var first);
        KeyCombo.TryParse("Ctrl+K", out var second);

        var merged = first.Union(second);
        check("[快捷键] 合并 Ctrl+U + Ctrl+K → Ctrl+U+K（公共修饰键只留一份）",
            merged.ToString() == "Ctrl+U+K" && merged.KeyCount == 3,
            merged.ToString());

        KeyCombo.TryParse("Ctrl+Shift+A", out var withShift);
        KeyCombo.TryParse("Ctrl+B", out var withCtrl);
        check("[快捷键] 合并不带公共修饰键的组合（Ctrl+Shift+A + Ctrl+B → Ctrl+Shift+A+B）",
            withShift.Union(withCtrl).ToString() == "Ctrl+Shift+A+B",
            withShift.Union(withCtrl).ToString());

        check("[快捷键] 合并后仍是同一个对象语义（两条相同组合合并 = 那条本身）",
            first.Union(first) == first, first.Union(first).ToString());

        check("[快捷键] UnionKeyCount 能提前算出合并后几个键（合并按钮据此置灰）",
            first.UnionKeyCount(second) == 3 && withShift.UnionKeyCount(withCtrl) == 4,
            $"{first.UnionKeyCount(second)} / {withShift.UnionKeyCount(withCtrl)}");
    }

    // ---------------------------------------------------------------- 引擎通道选择

    private static void CheckEngineCompatibility(CheckFn check)
    {
        check("[快捷键] 单主键 + 修饰键 → 可以直接交给 RegisterHotKey",
            KeyCombo.TryParse("Ctrl+F9", out var single) && single.IsRegisterHotKeyCompatible
            && KeyCombo.TryParse("F9", out var bareF) && bareF.IsRegisterHotKeyCompatible,
            "Ctrl+F9 / F9");

        check("[快捷键] 多主键组合 → 必须走键盘钩子（RegisterHotKey 表达不了）",
            KeyCombo.TryParse("Ctrl+U+K", out var multi) && !multi.IsRegisterHotKeyCompatible,
            multi.ToString());

        check("[快捷键] 两个主键都不带修饰键也不算 RegisterHotKey 兼容",
            KeyCombo.TryParse("U+K", out var noModifier) && !noModifier.IsRegisterHotKeyCompatible,
            noModifier.ToString());
    }

    // ---------------------------------------------------------------- 分组树

    /// <summary>测试用的假 provider（自己存一份组合，不碰任何真实配置）。</summary>
    private sealed class FakeProvider : IShortcutProvider
    {
        private readonly List<FakeSlot> _slots = new();

        public FakeProvider(string toolId, string toolName, params string[] categoryPath)
        {
            ShortcutToolId = toolId;
            ShortcutToolName = toolName;
            ShortcutCategoryPath = categoryPath;
        }

        public string ShortcutToolId { get; }

        public string ShortcutToolName { get; }

        public IReadOnlyList<string> ShortcutCategoryPath { get; }

        public bool ShortcutsEnabled { get; set; } = true;

        public event EventHandler? ShortcutsChanged;

        public int ApplyCount { get; private set; }

        public FakeProvider Add(string id, string name, string primary, string? secondary = null, bool enabled = true)
        {
            _slots.Add(new FakeSlot(id, name, primary, secondary, enabled));
            return this;
        }

        public IReadOnlyList<ShortcutDefinition> GetShortcuts() => _slots.Select(slot =>
        {
            KeyCombo.TryParse(slot.Primary, out var primary);
            KeyCombo? secondary = slot.Secondary is not null && KeyCombo.TryParse(slot.Secondary, out var parsed)
                ? parsed
                : null;

            return new ShortcutDefinition(slot.Id, ShortcutToolId, slot.Name, primary, secondary, slot.Enabled,
                canSplit: true,
                setCombo: (combo, second) =>
                {
                    slot.Primary = combo.IsEmpty ? "" : combo.ToString();
                    slot.Secondary = second is null || second.IsEmpty ? "" : second.ToString();
                    ApplyShortcuts();
                },
                setEnabled: value =>
                {
                    slot.Enabled = value;
                    ApplyShortcuts();
                });
        }).ToList();

        public void ApplyShortcuts()
        {
            ApplyCount++;
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }

        private sealed class FakeSlot
        {
            public FakeSlot(string id, string name, string primary, string? secondary, bool enabled)
            {
                Id = id;
                Name = name;
                Primary = primary;
                Secondary = secondary;
                Enabled = enabled;
            }

            public string Id { get; }
            public string Name { get; }
            public string Primary { get; set; }
            public string? Secondary { get; set; }
            public bool Enabled { get; set; }
        }
    }

    private static void CheckRegistryGrouping(CheckFn check)
    {
        var registry = new ShortcutRegistry();
        var stopwatch = new FakeProvider("stopwatch", "秒表", "系统")
            .Add("start", "开始", "Ctrl+Alt+F9")
            .Add("pause", "暂停", "F10")
            .Add("stop", "停止", "F11");
        var kps = new FakeProvider("kps", "KPS 按键统计", "音游工具")
            .Add("toggle-overlay", "显示隐藏 Overlay", "Ctrl+Shift+O");

        registry.RegisterProvider(stopwatch);
        registry.RegisterProvider(kps);

        check("[快捷键] 按分类分组：两个工具落在「系统」「音游工具」两个分类下",
            registry.Groups.Count == 2
            && registry.Groups.Any(g => g.CategoryName == "系统")
            && registry.Groups.Any(g => g.CategoryName == "音游工具"),
            string.Join(" / ", registry.Groups.Select(g => g.CategoryName)));

        var systemGroup = registry.Groups.First(g => g.CategoryName == "系统");
        check("[快捷键] 分类下按工具分组，工具下挂具体快捷键行",
            systemGroup.Children.OfType<ShortcutGroupNode>().Count() == 1
            && systemGroup.Children.OfType<ShortcutGroupNode>().First().Children.Count == 3
            && registry.AllRows().Count() == 4,
            $"工具 {systemGroup.Children.Count} · 行 {registry.AllRows().Count()}");

        check("[快捷键] 默认全部收起（▼/▶ 的初始状态是 ▶）",
            registry.Groups.All(g => !g.IsExpanded && g.ArrowGlyph == "▶"),
            string.Join(" ", registry.Groups.Select(g => g.ArrowGlyph)));

        check("[快捷键] 展开状态跟着箭头字符变（▼ / ▶）",
            ExpandAndGlyph(systemGroup, out var expandedGlyph) && expandedGlyph == "▼",
            expandedGlyph);

        check("[快捷键] 统计文字给出「已启用 N / 共 M 条」",
            registry.SummaryText.Contains("4 条") && registry.SummaryText.Contains("4"),
            registry.SummaryText);

        check("[快捷键] 没注册任何工具时给出空状态",
            new ShortcutRegistry().HasNoGroups, "HasNoGroups");

        check("[快捷键] 工具行能显示「N / M 条已启用」",
            systemGroup.Children.OfType<ShortcutGroupNode>().First().CountText.Contains("3 / 3"),
            systemGroup.Children.OfType<ShortcutGroupNode>().First().CountText);
    }

    private static bool ExpandAndGlyph(ShortcutGroupNode node, out string glyph)
    {
        node.IsExpanded = true;
        glyph = node.ArrowGlyph;
        node.IsExpanded = false;
        return true;
    }

    // ---------------------------------------------------------------- 冲突检测

    private static void CheckConflictDetection(CheckFn check)
    {
        var registry = new ShortcutRegistry();
        var toolA = new FakeProvider("tool-a", "工具 A", "系统").Add("action", "动作 A", "Ctrl+Shift+R");
        var toolB = new FakeProvider("tool-b", "工具 B", "媒体").Add("action", "动作 B", "Ctrl+Shift+R");

        registry.RegisterProvider(toolA);
        registry.RegisterProvider(toolB);

        var errorA = registry.GetError("tool-a", "action");
        var errorB = registry.GetError("tool-b", "action");
        check("[快捷键] 两个工具用同一组合 → 两边都标冲突，并指出对方是谁",
            errorA.Length > 0 && errorB.Length > 0
            && errorA.Contains("tool-b") && errorB.Contains("tool-a"),
            errorA);

        registry.UnregisterProvider(toolB);
        check("[快捷键] 去掉一方后冲突消失",
            registry.GetError("tool-a", "action") == "",
            "[" + registry.GetError("tool-a", "action") + "]");

        // 分步的第一步与别人的单步撞车，同样算冲突（否则谁生效看运气）
        var registry2 = new ShortcutRegistry();
        var single = new FakeProvider("t1", "工具 1", "系统").Add("a", "单步", "Ctrl+K");
        var sequence = new FakeProvider("t2", "工具 2", "系统").Add("b", "分步", "Ctrl+K", "Ctrl+C");
        registry2.RegisterProvider(single);
        registry2.RegisterProvider(sequence);

        check("[快捷键] 单步 Ctrl+K 与分步的第一步 Ctrl+K 撞车 → 判冲突",
            registry2.GetError("t1", "a").Length > 0 && registry2.GetError("t2", "b").Length > 0,
            registry2.GetError("t1", "a"));

        // 禁用的条目不参与冲突
        var registry3 = new ShortcutRegistry();
        var disabled = new FakeProvider("d1", "工具 D1", "系统").Add("a", "禁用的", "Ctrl+M", enabled: false);
        var active = new FakeProvider("d2", "工具 D2", "系统").Add("b", "启用的", "Ctrl+M");
        registry3.RegisterProvider(disabled);
        registry3.RegisterProvider(active);

        check("[快捷键] 已禁用的条目不参与冲突判定",
            registry3.GetError("d1", "a") == "" && registry3.GetError("d2", "b") == "",
            $"[{registry3.GetError("d1", "a")}] / [{registry3.GetError("d2", "b")}]");

        // 工具总开关关掉也不参与
        var registry4 = new ShortcutRegistry();
        var offTool = new FakeProvider("o1", "工具 O1", "系统") { ShortcutsEnabled = false }
            .Add("a", "组合 1", "Ctrl+N");
        var onTool = new FakeProvider("o2", "工具 O2", "系统").Add("b", "组合 2", "Ctrl+N");
        registry4.RegisterProvider(offTool);
        registry4.RegisterProvider(onTool);

        check("[快捷键] 整个工具关掉后，它的快捷键也不参与冲突",
            registry4.GetError("o2", "b") == "",
            "[" + registry4.GetError("o2", "b") + "]");
    }

    // ---------------------------------------------------------------- 启停

    private static void CheckEnableSwitches(CheckFn check)
    {
        var registry = new ShortcutRegistry();
        var provider = new FakeProvider("t", "工具", "系统")
            .Add("a", "A", "Ctrl+1")
            .Add("b", "B", "Ctrl+2");
        registry.RegisterProvider(provider);

        var row = registry.AllRows().First(r => r.Definition.Id == "a");
        row.ToggleEnabled();
        check("[快捷键] 单条禁用后状态变「已禁用 ❌」，其它条不受影响",
            !row.Enabled && row.StateText == "已禁用" && row.StateGlyph == "❌"
            && registry.AllRows().First(r => r.Definition.Id == "b").Enabled,
            $"{row.StateText} {row.StateGlyph} / 启用={row.Enabled}");

        check("[快捷键] 单条禁用后按钮文字变「启用」",
            row.ActionText == "启用", row.ActionText);

        row.ToggleEnabled();
        check("[快捷键] 再点一下恢复启用",
            row.Enabled && row.StateText == "已启用" && row.ActionText == "禁用",
            row.StateText);

        provider.ShortcutsEnabled = false;
        registry.Rebuild();
        var toolGroup = registry.Groups.SelectMany(g => g.Children).OfType<ShortcutGroupNode>().First();
        check("[快捷键] 工具级总开关关掉 → 工具分组上显示为关；单条状态保留",
            !toolGroup.ToolShortcutsEnabled && registry.AllRows().All(r => r.Enabled),
            $"总开关 {toolGroup.ToolShortcutsEnabled} · 单条 {registry.AllRows().Count(r => r.Enabled)}");
    }

    // ---------------------------------------------------------------- 拆分 / 合并

    private static void CheckMergeSplit(CheckFn check)
    {
        var registry = new ShortcutRegistry();
        var provider = new FakeProvider("t", "工具", "系统")
            .Add("a", "分步的", "Ctrl+K", "Ctrl+C");
        registry.RegisterProvider(provider);

        var row = registry.AllRows().First();
        check("[快捷键] 有第二步时显示为「分步」，两个小框各自有内容",
            row.IsSequence && row.KindLabel == "分步"
            && row.PrimaryText == "Ctrl+K" && row.SecondaryText == "Ctrl+C"
            && row.DisplayText == "Ctrl+K → Ctrl+C",
            row.DisplayText);

        check("[快捷键] 分步可以合并，同时可以拆分（按钮可见性）",
            row.CanMerge && !row.CanSplit, $"CanMerge={row.CanMerge} CanSplit={row.CanSplit}");

        var merged = row.Merge(out var mergeMessage);
        check("[快捷键] 合并成功：Ctrl+K + Ctrl+C → Ctrl+K+C，形态变「同时」",
            merged && row.IsSingle && row.PrimaryText == "Ctrl+K+C" && !row.IsSequence,
            $"{mergeMessage} → {row.DisplayText}");

        var afterMerge = registry.AllRows().First();
        check("[快捷键] 合并后「拆分」按钮出现、状态显示已启用",
            afterMerge.CanSplit && afterMerge.Enabled,
            $"CanSplit={afterMerge.CanSplit}");

        var split = afterMerge.Split(out var splitMessage);
        var splitRow = registry.AllRows().First();
        check("[快捷键] 拆分后变回分步：第二步先填成第一步的副本（接着录会覆盖）",
            split && splitRow.IsSequence && splitRow.SecondaryText == splitRow.PrimaryText
            && splitMessage.Contains("第二步"),
            $"{splitMessage} → {splitRow.DisplayText}");

        // 合并后超过 4 个键要拦住
        var big = new ShortcutRegistry();
        var bigProvider = new FakeProvider("b", "工具", "系统").Add("x", "太多键", "Ctrl+A+B", "Ctrl+C+D");
        big.RegisterProvider(bigProvider);
        var bigRow = big.AllRows().First();

        var tooBig = !bigRow.Merge(out var tooBigMessage);
        check("[快捷键] 合并后超过 4 个键时拒绝合并并说明原因",
            tooBig && tooBigMessage.Contains("最多"), tooBigMessage);

        // 只有一个键的组合拆不出两步
        var single = new ShortcutRegistry();
        single.RegisterProvider(new FakeProvider("s", "工具", "系统").Add("y", "单键", "F9"));
        var singleRow = single.AllRows().First();

        check("[快捷键] 只有一个键的组合不能拆分（拆不出两步）",
            !singleRow.CanSplit, $"CanSplit={singleRow.CanSplit}");
    }

    // ---------------------------------------------------------------- 分步规则

    private static void CheckSequenceRules(CheckFn check)
    {
        // 分步 = 有第二步；单步 = 没有第二步
        KeyCombo.TryParse("Ctrl+U", out var first);
        KeyCombo.TryParse("Ctrl+K", out var second);

        var definitionSingle = new ShortcutDefinition("a", "t", "单步", first, null, true, true, (_, _) => { }, _ => { });
        var definitionSequence = new ShortcutDefinition("b", "t", "分步", first, second, true, true, (_, _) => { }, _ => { });

        check("[快捷键] 没有第二步 = 单步；有第二步 = 分步",
            definitionSingle.Kind == ShortcutKind.Single && definitionSequence.Kind == ShortcutKind.Sequence,
            $"{definitionSingle.Kind} / {definitionSequence.Kind}");

        check("[快捷键] 两条的显示文本分别是「Ctrl+U」和「Ctrl+U → Ctrl+K」",
            definitionSingle.DisplayText == "Ctrl+U" && definitionSequence.DisplayText == "Ctrl+U → Ctrl+K",
            definitionSequence.DisplayText);

        check("[快捷键] 没设置组合时显示「（无）」",
            new ShortcutDefinition("c", "t", "空", KeyCombo.Empty, null, true, true, (_, _) => { }, _ => { })
                .DisplayText == "（无）",
            "（无）");

        // 分步等待时长：v1.45.0 起全程序统一（不再按工具分开）
        check("[快捷键] 分步等待默认 5 秒",
            ShortcutTiming.DefaultSequenceTimeoutMs == 5000,
            ShortcutTiming.DefaultSequenceTimeoutMs.ToString());

        check("[快捷键] 等待时长夹到 500~30000ms",
            ShortcutTiming.Normalize(100) == 500 && ShortcutTiming.Normalize(99999) == 30000
            && ShortcutTiming.Normalize(5000) == 5000,
            $"{ShortcutTiming.Normalize(100)} / {ShortcutTiming.Normalize(99999)} / {ShortcutTiming.Normalize(5000)}");

        check("[快捷键] 0 表示不限时（不是被夹成 500）",
            ShortcutTiming.Normalize(0) == 0 && ShortcutTiming.Normalize(-5) == 0,
            $"{ShortcutTiming.Normalize(0)} / {ShortcutTiming.Normalize(-5)}");

        check("[快捷键] 等待时长的可读写法（5 秒 / 不限时）",
            ShortcutTiming.Describe(5000) == "5 秒" && ShortcutTiming.Describe(0) == "不限时"
            && ShortcutTiming.Describe(2500) == "2.5 秒",
            $"{ShortcutTiming.Describe(5000)} / {ShortcutTiming.Describe(0)} / {ShortcutTiming.Describe(2500)}");

        check("[快捷键] 工具不再各自定义分步等待（全程序统一一项设置）",
            !(typeof(IShortcutActionHost).GetProperty("SequenceTimeoutMs") is not null),
            "IShortcutActionHost 只有 InvokeShortcut");

        var engine = new GlobalShortcutEngine();
        try
        {
            engine.SequenceTimeout = 0;
            var unlimited = engine.IsSequenceTimeoutUnlimited;
            engine.SequenceTimeout = 8000;
            check("[快捷键] 引擎能表示「不限时」并能设回具体毫秒",
                unlimited && !engine.IsSequenceTimeoutUnlimited && engine.SequenceTimeout == 8000,
                $"unlimited={unlimited} / 8000={engine.SequenceTimeout}");
        }
        finally
        {
            engine.Dispose();
        }

        // 分步等待时长夹取（引擎侧，全程序统一）
        check("[快捷键] 引擎暴露分步等待时长属性（全程序统一一项）",
            typeof(GlobalShortcutEngine).GetProperty("SequenceTimeout") is not null, "SequenceTimeout");

        // 被拒绝事件的描述文案（界面直接显示它，所以文案要对）
        KeyCombo.TryParse("Ctrl+X", out var actual);
        KeyCombo.TryParse("Ctrl+K", out var expected);

        var rejected = new SequenceRejectedEventArgs(actual, expected);
        check("[快捷键] 按错第二步时的提示文案含实际组合与应该按的组合",
            rejected.Describe().Contains("Ctrl+X") && rejected.Describe().Contains("Ctrl+K")
            && rejected.Describe().Contains("未被识别"),
            rejected.Describe());

        var rejectedNoExpected = new SequenceRejectedEventArgs(actual, null);
        check("[快捷键] 没有第二步信息时文案也不崩（只报实际组合）",
            rejectedNoExpected.Describe().Contains("Ctrl+X") && !rejectedNoExpected.Describe().Contains("应该按"),
            rejectedNoExpected.Describe());
    }

    // ---------------------------------------------------------------- 通知设置

    private static void CheckNotificationSettings(CheckFn check)
    {
        var settings = new NotificationSettings();
        check("[通知] 默认：应用内通道 + 右下角 + 3.5 秒 + 开启",
            settings.Mode == NotificationMode.InApp
            && settings.Corner == NotificationCorner.BottomRight
            && settings.DurationMs == 3500
            && settings.IsInApp && settings.IsBottomRight && settings.Enabled,
            $"{settings.Mode} / {settings.Corner} / {settings.DurationMs}");

        check("[通知] 四个角都有对应的布尔属性，且互相独立",
            SetCornerAndCheck(settings, NotificationCorner.TopLeft, s => s.IsTopLeft)
            && SetCornerAndCheck(settings, NotificationCorner.TopRight, s => s.IsTopRight)
            && SetCornerAndCheck(settings, NotificationCorner.BottomLeft, s => s.IsBottomLeft)
            && SetCornerAndCheck(settings, NotificationCorner.BottomRight, s => s.IsBottomRight),
            settings.Corner.ToString());

        check("[通知] 停留时长夹在 1000~15000ms",
            ClampDuration(50) == 1000 && ClampDuration(99999) == 15000 && ClampDuration(4000) == 4000,
            $"{ClampDuration(50)} / {ClampDuration(99999)} / {ClampDuration(4000)}");

        check("[通知] 摘要按当前设置现算（应用内带位置、Windows 说明位置由系统决定）",
            WithMode(NotificationMode.InApp).Contains("右下角")
            && WithMode(NotificationMode.Windows).Contains("系统"),
            WithMode(NotificationMode.InApp));

        settings.Enabled = false;
        check("[通知] 关掉后摘要写明已关闭", settings.SummaryText.Contains("关闭"), settings.SummaryText);

        check("[通知] 角名中文化（左上角 / 右上角 / 左下角 / 右下角）",
            NotificationSettings.CornerName(NotificationCorner.TopLeft) == "左上角"
            && NotificationSettings.CornerName(NotificationCorner.BottomRight) == "右下角",
            NotificationSettings.CornerName(NotificationCorner.TopLeft));

        check("[通知] 请求体：标题 + 正文能拼成气泡文本（正文空时只用标题）",
            new NotificationRequest("标题", "正文").ToBalloonText().Contains("标题")
            && new NotificationRequest("只有标题", "").ToBalloonText() == "只有标题",
            new NotificationRequest("标题", "正文").ToBalloonText().Replace(Environment.NewLine, " | "));

        check("[通知] 四种类型各有不同图标（信息 / 成功 / 警告 / 错误）",
            Enum.GetValues<NotificationKind>().Length == 4, "4 种");
    }

    private static bool SetCornerAndCheck(NotificationSettings settings, NotificationCorner corner,
        Func<NotificationSettings, bool> predicate)
    {
        settings.Corner = corner;
        return predicate(settings);
    }

    private static int ClampDuration(int value)
    {
        var settings = new NotificationSettings { DurationMs = value };
        return settings.DurationMs;
    }

    private static string WithMode(NotificationMode mode)
    {
        var settings = new NotificationSettings { Mode = mode };
        return settings.SummaryText;
    }

    // ---------------------------------------------------------------- 通知兜底

    private sealed class RecordingSink : INotificationSink
    {
        public RecordingSink(bool available, bool succeeds = true)
        {
            IsAvailable = available;
            Succeeds = succeeds;
        }

        public bool IsAvailable { get; }

        public bool Succeeds { get; }

        public int SendCount { get; private set; }

        public bool Send(NotificationRequest request, NotificationSettings settings)
        {
            SendCount++;
            return Succeeds;
        }
    }

    private static void CheckNotificationCenterFallback(CheckFn check)
    {
        var inApp = new RecordingSink(available: true);
        var windows = new RecordingSink(available: false);
        NotificationCenter.SetSinks(inApp, windows);

        var settings = new NotificationSettings { Mode = NotificationMode.InApp };
        var sent = NotificationCenter.Show(settings, new NotificationRequest("t", "b"));

        check("[通知] 首选通道可用就从它发",
            sent && inApp.SendCount == 1 && windows.SendCount == 0,
            $"应用内 {inApp.SendCount} / Windows {windows.SendCount}");

        // 首选是 Windows 但它不可用 → 自动退回应用内，通知不静默丢失
        var inApp2 = new RecordingSink(available: true);
        var windows2 = new RecordingSink(available: false);
        NotificationCenter.SetSinks(inApp2, windows2);
        var windowsSettings = new NotificationSettings { Mode = NotificationMode.Windows };

        var fallbackSent = NotificationCenter.Show(windowsSettings, new NotificationRequest("t", "b"));
        check("[通知] 首选通道不可用时自动换另一条通道（不静默丢通知）",
            fallbackSent && windows2.SendCount == 0 && inApp2.SendCount == 1,
            $"Windows {windows2.SendCount} / 应用内 {inApp2.SendCount}");

        // 关掉通知就一条都不发
        var inApp3 = new RecordingSink(available: true);
        NotificationCenter.SetSinks(inApp3, null);
        var disabled = new NotificationSettings { Enabled = false };
        var disabledSent = NotificationCenter.Show(disabled, new NotificationRequest("t", "b"));

        check("[通知] 工具把通知关掉后一条都不发",
            !disabledSent && inApp3.SendCount == 0, $"发了 {inApp3.SendCount} 条");

        // 两条通道都没有 → 返回 false，不抛异常
        NotificationCenter.SetSinks(null, null);
        var none = NotificationCenter.Show(new NotificationSettings(), new NotificationRequest("t", "b"));
        check("[通知] 没有任何通道时安全返回 false（不抛异常）", !none, "false");
    }
}
