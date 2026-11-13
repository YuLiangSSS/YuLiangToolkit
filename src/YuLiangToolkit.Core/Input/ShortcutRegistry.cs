using System.Collections.ObjectModel;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Core.Input;

/// <summary>
/// 快捷键汇总页的一个分组节点：要么是「分类」（下含工具），要么是「工具」（下含快捷键）。
/// 树结构 + 折叠状态都放在这里，界面只负责绑定渲染（折叠箭头 ▼ / ▶ 绑 <see cref="IsExpanded"/>）。
/// </summary>
public sealed class ShortcutGroupNode : ObservableObject
{
    private bool _isExpanded;

    /// <summary>分类分组（下挂工具）。</summary>
    public ShortcutGroupNode(string categoryName)
    {
        CategoryName = categoryName;
        IsToolGroup = false;
    }

    /// <summary>工具分组（下挂快捷键）。</summary>
    public ShortcutGroupNode(IShortcutProvider provider, string toolName, string categoryPathText)
    {
        Provider = provider;
        ToolName = toolName;
        CategoryPathText = categoryPathText;
        IsToolGroup = true;
    }

    /// <summary>分类名（工具分组为空）。</summary>
    public string CategoryName { get; } = "";

    /// <summary>工具名（分类分组为空）。</summary>
    public string ToolName { get; } = "";

    /// <summary>工具分组的完整分类路径文字（「音游工具 / 谱面处理」）。</summary>
    public string CategoryPathText { get; } = "";

    /// <summary>是不是工具分组（否则是分类分组）。</summary>
    public bool IsToolGroup { get; }

    /// <summary>工具分组对应的 provider（分类分组为 null）。</summary>
    public IShortcutProvider? Provider { get; }

    /// <summary>子节点：分类分组装工具，工具分组装快捷键行。</summary>
    public ObservableCollection<object> Children { get; } = new();

    /// <summary>展开状态（▼ = true / ▶ = false）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(ArrowGlyph));
        }
    }

    /// <summary>折叠箭头：展开是 ▼、收起是 ▶。</summary>
    public string ArrowGlyph => IsExpanded ? "▼" : "▶";

    /// <summary>这个工具分组里启用的条数 / 总条数（标题右侧显示）。</summary>
    public string CountText
    {
        get
        {
            var rows = Children.OfType<ShortcutRow>().ToList();
            if (rows.Count == 0) return "";
            var enabled = rows.Count(r => r.Definition.Enabled);
            return $"{enabled} / {rows.Count} 条已启用";
        }
    }

    /// <summary>
    /// 工具级总开关（分类分组的行不显示它，值恒为 true）。
    /// <para>⚠ 必须可写：XAML 里 <c>CheckBox.IsChecked</c> 默认是 TwoWay 绑定，
    /// 绑到只读属性上会在模板加载时直接抛 <see cref="InvalidOperationException"/>（整个页面崩掉）。</para>
    /// </summary>
    public bool ToolShortcutsEnabled
    {
        get => Provider?.ShortcutsEnabled ?? true;
        set
        {
            if (Provider is null || Provider.ShortcutsEnabled == value) return;
            Provider.ShortcutsEnabled = value;
            OnPropertyChanged();
            RefreshCounts();
        }
    }

    /// <summary>刷新标题上的统计与总开关显示。</summary>
    internal void RefreshCounts()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ToolShortcutsEnabled));
    }
}

/// <summary>
/// 汇总页里的一行快捷键（一条 <see cref="ShortcutDefinition"/> 的界面包装）。
///
/// <para>改键 / 拆分 / 合并 / 启停都走这里，改完立刻通知 provider 重新注册并刷新整棵树。</para>
/// </summary>
public sealed class ShortcutRow : ObservableObject
{
    private readonly ShortcutRegistry _registry;

    internal ShortcutRow(ShortcutRegistry registry, ShortcutDefinition definition)
    {
        _registry = registry;
        Definition = definition;
    }

    /// <summary>底层定义（渲染时读它）。</summary>
    public ShortcutDefinition Definition { get; private set; }

    /// <summary>快捷键名。</summary>
    public string Name => Definition.Name;

    /// <summary>说明文字（空串则不显示）。</summary>
    public string Description => Definition.Description;

    /// <summary>形态标签：分步 / 同时。</summary>
    public string KindLabel => Definition.Kind == ShortcutKind.Sequence ? "分步" : "同时";

    /// <summary>是不是分步形态（界面据此显示两个小框 + 合并）。</summary>
    public bool IsSequence => Definition.Kind == ShortcutKind.Sequence;

    /// <summary>是不是同时形态（界面据此显示一个大框 + 拆分）。</summary>
    public bool IsSingle => !IsSequence;

    /// <summary>第一步文本（同时形态就是唯一那一步）。</summary>
    public string PrimaryText => Definition.Primary.IsEmpty ? "（未设置）" : Definition.Primary.ToString();

    /// <summary>第二步文本（同时形态为空）。</summary>
    public string SecondaryText => Definition.Secondary is { IsEmpty: false } s ? s.ToString() : "（未设置）";

    /// <summary>
    /// 改键框双向绑定的第一步（<see cref="HotkeyBox"/> 录完后写这里 → 立即写回工具配置并重注册）。
    /// 读出来的永远是配置里的规范写法（空串 = 未设置，界面用占位文字显示）。
    /// </summary>
    public string PrimaryBinding
    {
        get => Definition.Primary.IsEmpty ? "" : Definition.Primary.ToString();
        set
        {
            if (!KeyCombo.TryParse(value, out var combo) || combo == Definition.Primary) return;
            SetPrimary(combo);
        }
    }

    /// <summary>改键框双向绑定的第二步（分步形态才有意义）。</summary>
    public string SecondaryBinding
    {
        get => Definition.Secondary is { IsEmpty: false } s ? s.ToString() : "";
        set
        {
            if (!IsSequence) return;
            if (!KeyCombo.TryParse(value, out var combo) || combo == Definition.Secondary) return;
            SetSecondary(combo);
        }
    }

    /// <summary>整条快捷键的文本（分步显示成 <c>Ctrl+U → Ctrl+K</c>）。</summary>
    public string DisplayText => Definition.DisplayText;

    /// <summary>启用状态。</summary>
    public bool Enabled => Definition.Enabled;

    /// <summary>能不能拆分（只有同时形态、且工具允许时才显示拆分按钮）。</summary>
    public bool CanSplit => Definition.CanSplit && IsSingle && Definition.Primary.KeyCount >= 2;

    /// <summary>能不能合并（只有分步形态才能合并回同时）。</summary>
    public bool CanMerge => IsSequence;

    /// <summary>状态文字：「已启用」/「已禁用」。</summary>
    public string StateText => Definition.Enabled ? "已启用" : "已禁用";

    /// <summary>状态角标：✅ / ❌。</summary>
    public string StateGlyph => Definition.Enabled ? "✅" : "❌";

    /// <summary>「禁用 / 启用」按钮上的文字（已启用时显示「禁用」）。</summary>
    public string ActionText => Definition.Enabled ? "禁用" : "启用";

    /// <summary>注册失败 / 冲突原因（空 = 一切正常）。</summary>
    public string ErrorText => _registry.GetError(Definition.ToolId, Definition.Id);

    /// <summary>有没有错误要显示。</summary>
    public bool HasError => ErrorText.Length > 0;

    /// <summary>改第一步（同时形态就是改成新的唯一组合）。</summary>
    public void SetPrimary(KeyCombo combo)
    {
        var secondary = Definition.Secondary;
        Definition.SetCombo(combo, secondary);
        _registry.SoftRefresh();
    }

    /// <summary>改第二步（分步形态）。</summary>
    public void SetSecondary(KeyCombo combo)
    {
        if (!IsSequence) return;
        Definition.SetCombo(Definition.Primary, combo);
        _registry.SoftRefresh();
    }

    /// <summary>启用 / 禁用这一条。</summary>
    public void ToggleEnabled()
    {
        Definition.SetEnabled(!Definition.Enabled);
        _registry.SoftRefresh();
    }

    /// <summary>把底层定义换成最新快照（原地刷新时用：控件实例不换，只换数据）。</summary>
    internal void SyncDefinition(ShortcutDefinition definition)
    {
        Definition = definition;
        RefreshState();
    }

    /// <summary>把「会随定义变化而变的所有只读属性」通知一遍。</summary>
    internal void RefreshState()
    {
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IsSequence));
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(SecondaryText));
        OnPropertyChanged(nameof(PrimaryBinding));
        OnPropertyChanged(nameof(SecondaryBinding));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(CanSplit));
        OnPropertyChanged(nameof(CanMerge));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>合并：两段按键取并集变成一条「同时按」。合不出来（超过键数上限）时返回 false。</summary>
    public bool Merge(out string message)
    {
        message = "";
        if (Definition.Secondary is not { } second)
        {
            message = "这条不是分步快捷键，不需要合并";
            return false;
        }

        var merged = Definition.Primary.Union(second);
        var validation = merged.Validate();
        if (validation.Length > 0)
        {
            message = $"合并成「{merged}」不行：{validation}";
            return false;
        }

        Definition.SetCombo(merged, null);
        _registry.SoftRefresh();
        message = $"已合并成同时按「{merged}」";
        return true;
    }

    /// <summary>
    /// 拆分：把一条「同时按」拆成两步。
    /// <para>第二步先填成**第一步的副本**（<c>Ctrl+K</c> → <c>Ctrl+K → Ctrl+K</c>），
    /// 用户紧接着录新的就会覆盖它。为什么不用「留空」：空第二步 = 单步，
    /// 刚拆开就退回单步的话，界面会把「拆分」按钮又亮出来，拆了等于没拆。</para>
    /// </summary>
    public bool Split(out string message)
    {
        message = "";
        if (!CanSplit)
        {
            message = Definition.CanSplit
                ? "这条只有一个键，拆不出两步"
                : "这个工具不支持把这条快捷键拆成分步";
            return false;
        }

        Definition.SetCombo(Definition.Primary, Definition.Primary);
        _registry.SoftRefresh();
        message = "已拆分成两步：接着录第二步（会覆盖临时的第二步）";
        return true;
    }
}

/// <summary>
/// 全局快捷键注册表：把各工具声明的快捷键汇总成「分类 → 工具 → 快捷键」三层，
/// 并统一负责冲突检测、启用状态与向引擎注册。
///
/// <para><b>它不持有任何业务状态</b>：组合与启用位都存在各工具自己的配置里，
/// 这里只是「读一遍 provider、渲染成树、需要时把最新值推给引擎」。</para>
///
/// <para><b>冲突规则</b>：同一个组合（或某条分步的第一步）被两个启用中的快捷键占用 → 判冲突，
/// 两条都在界面上标红、都<b>不注册</b>（避免「谁生效看运气」）。分步的第一步也算占用：
/// 否则「单步 Ctrl+U」和「分步 Ctrl+U → Ctrl+K」会互相抢，用户更莫名其妙。</para>
/// </summary>
public sealed class ShortcutRegistry : ObservableObject
{
    private readonly List<IShortcutProvider> _providers = new();
    private readonly Dictionary<string, string> _errors = new();   // "toolId/shortcutId" → 原因

    /// <summary>展开着的分类名（重建树后按它恢复）/ 展开着的「分类/工具」键。</summary>
    private readonly HashSet<string> _expandedCategories = new(StringComparer.CurrentCulture);

    /// <summary>见 <see cref="_expandedCategories"/>。</summary>
    private readonly HashSet<string> _expandedTools = new(StringComparer.CurrentCulture);
    private int _registrationSeed = 1;

    /// <summary>分组树（顶层是分类）。</summary>
    public ObservableCollection<ShortcutGroupNode> Groups { get; } = new();

    /// <summary>树有变化（改键 / 启停 / 重新扫描）时触发，界面据此重新展开统计。</summary>
    public event EventHandler? Changed;

    /// <summary>一个分组都没有（空状态提示用）。</summary>
    public bool HasNoGroups => Groups.Count == 0;

    /// <summary>工具发通知用（冲突 / 注册失败时提示用户），由外壳注入。</summary>
    public Action<string, string>? ReportProblem { get; set; }

    /// <summary>「已启用 N / 共 M 条」。</summary>
    public string SummaryText
    {
        get
        {
            var rows = AllRows().ToList();
            if (rows.Count == 0) return "还没有任何工具声明全局快捷键";
            var enabled = rows.Count(r => r.Definition.Enabled);
            return $"已启用 {enabled} / 共 {rows.Count} 条快捷键";
        }
    }

    /// <summary>注册一个 provider（程序启动时由外壳对每个实现了 <see cref="IShortcutProvider"/> 的工具调用）。</summary>
    public void RegisterProvider(IShortcutProvider provider)
    {
        if (provider is null || _providers.Contains(provider)) return;
        _providers.Add(provider);
        provider.ShortcutsChanged += OnProviderChanged;
        Rebuild();
    }

    /// <summary>移除一个 provider（工具被卸载时用）。</summary>
    public void UnregisterProvider(IShortcutProvider provider)
    {
        if (!_providers.Remove(provider)) return;
        provider.ShortcutsChanged -= OnProviderChanged;
        Rebuild();
    }

    /// <summary>
    /// provider 说「我的定义变了」（通常是改了键 / 启停）：<b>原地刷新</b>，不重建整棵树。
    /// <para>⚠ 这条很重要：工具在 <c>SetCombo</c> / <c>SetEnabled</c> 回调里都会 <c>ApplyShortcuts()</c>，
    /// 如果这里走 <see cref="Rebuild"/>，用户正在录的那个改键框会被整棵换掉（丢焦点、录制中断），
    /// 界面也会跳一下。</para>
    /// </summary>
    private void OnProviderChanged(object? sender, EventArgs e) => SoftRefresh();

    /// <summary>
    /// 重新读一遍所有 provider，重建树。
    /// <para>展开状态按「分类名 / 工具名」沿用（<see cref="_expandedCategories"/> / <see cref="_expandedTools"/>），
    /// 所以点过「全部展开」之后再重建也不会被折叠回去。</para>
    /// </summary>
    public void Rebuild()
    {
        // 先把当前展开状态记下来，重建后按名字恢复
        RememberExpansion();

        Groups.Clear();
        _errors.Clear();
        DetectConflicts();

        var byCategory = new Dictionary<string, ShortcutGroupNode>(StringComparer.CurrentCulture);

        foreach (var provider in _providers)
        {
            var path = provider.ShortcutCategoryPath;
            var categoryName = path.Count > 0 ? string.Join(" / ", path) : "未分类";

            if (!byCategory.TryGetValue(categoryName, out var category))
            {
                category = new ShortcutGroupNode(categoryName)
                {
                    IsExpanded = _expandedCategories.Contains(categoryName),
                };
                byCategory[categoryName] = category;
                Groups.Add(category);
            }

            var toolKey = categoryName + "/" + provider.ShortcutToolName;
            var toolGroup = new ShortcutGroupNode(provider, provider.ShortcutToolName, categoryName)
            {
                IsExpanded = _expandedTools.Contains(toolKey),
            };

            foreach (var definition in provider.GetShortcuts())
            {
                toolGroup.Children.Add(new ShortcutRow(this, definition));
            }

            category.Children.Add(toolGroup);
        }

        // 有冲突的工具默认展开（否则用户看不到红字原因）
        foreach (var category in Groups)
        {
            foreach (var tool in category.Children.OfType<ShortcutGroupNode>())
            {
                if (tool.Children.OfType<ShortcutRow>().Any(r => r.HasError))
                {
                    tool.IsExpanded = true;
                    category.IsExpanded = true;
                }

                tool.RefreshCounts();
            }

            category.RefreshCounts();
        }

        RememberExpansion();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasNoGroups));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把「哪些分类 / 工具是展开的」记到集合里（名字做键，重建后还能沿用）。</summary>
    private void RememberExpansion()
    {
        foreach (var category in Groups)
        {
            if (category.IsExpanded) _expandedCategories.Add(category.CategoryName);
            else _expandedCategories.Remove(category.CategoryName);

            foreach (var tool in category.Children.OfType<ShortcutGroupNode>())
            {
                var key = category.CategoryName + "/" + tool.ToolName;
                if (tool.IsExpanded) _expandedTools.Add(key);
                else _expandedTools.Remove(key);
            }
        }
    }

    /// <summary>界面改完一条快捷键后调用：重扫 provider（provider 自己会先 ApplyShortcuts 并抛 Changed）。</summary>
    public void Refresh() => Rebuild();

    /// <summary>
    /// 原地刷新：重扫 provider 的所有定义，把值同步进**已存在的行对象**，再按需增删行。
    ///
    /// <para><b>为什么不直接 <see cref="Rebuild"/></b>：改键 / 启停是在汇总页里点的，
    /// 一重建整棵树，用户正在录的那个改键框就会被换掉（丢焦点、录制中断），
    /// 页面也会整个跳一下。原地刷新保住控件实例，只把值换掉。</para>
    /// </summary>
    public void SoftRefresh()
    {
        DetectConflicts();

        foreach (var category in Groups.ToList())
        {
            foreach (var toolGroup in category.Children.OfType<ShortcutGroupNode>().ToList())
            {
                var provider = toolGroup.Provider;
                if (provider is null) continue;

                var definitions = provider.GetShortcuts();

                // 按 Id 建索引（用**刷新前**的定义 Id，它不会变）
                var rows = toolGroup.Children.OfType<ShortcutRow>()
                    .ToDictionary(r => r.Definition.Id, StringComparer.Ordinal);

                // 原地更新：值同步进已有行；新出现的才新建（保住控件实例 = 保住焦点）
                var rebuilt = new List<object>(definitions.Count);
                foreach (var definition in definitions)
                {
                    if (rows.TryGetValue(definition.Id, out var row))
                    {
                        row.SyncDefinition(definition);
                        rebuilt.Add(row);
                    }
                    else
                    {
                        rebuilt.Add(new ShortcutRow(this, definition));
                    }
                }

                toolGroup.Children.Clear();
                foreach (var child in rebuilt) toolGroup.Children.Add(child);

                toolGroup.RefreshCounts();
            }
        }

        // 错误文字也可能变了（冲突新出现 / 消失）
        foreach (var row in AllRows()) row.RefreshState();

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasNoGroups));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>拿某条快捷键的错误 / 冲突原因（空串 = 正常）。</summary>
    public string GetError(string toolId, string shortcutId)
        => _errors.TryGetValue(Key(toolId, shortcutId), out var error) ? error : "";

    private static string Key(string toolId, string shortcutId) => toolId + "/" + shortcutId;

    private void DetectConflicts()
    {
        // 只看「工具总开关开着 + 单条启用 + 真的设置了按键」的条目
        var active = _providers
            .Where(p => p.ShortcutsEnabled)
            .SelectMany(p => p.GetShortcuts().Where(d => d.Enabled && !d.Primary.IsEmpty))
            .ToList();

        // 每条快捷键占用的组合：单步 = 第一步；分步 = 第一步 + 第二步（第一步要与别人抢）
        var owners = new Dictionary<KeyCombo, List<ShortcutDefinition>>();
        foreach (var definition in active)
        {
            foreach (var combo in new[] { definition.Primary, definition.Secondary })
            {
                if (combo is null || combo.IsEmpty) continue;
                if (!owners.TryGetValue(combo, out var list))
                {
                    list = new List<ShortcutDefinition>();
                    owners[combo] = list;
                }
                list.Add(definition);
            }
        }

        foreach (var (combo, definitions) in owners)
        {
            if (definitions.Count < 2) continue;

            foreach (var definition in definitions)
            {
                var others = definitions
                    .Where(d => !ReferenceEquals(d, definition))
                    .Select(d => $"{d.ToolId} · {d.Name}")
                    .Distinct()
                    .ToList();
                var text = $"「{combo}」和 {string.Join("、", others)} 冲突，改掉其中一个才能生效";
                var key = Key(definition.ToolId, definition.Id);
                _errors[key] = _errors.TryGetValue(key, out var existing) ? existing + "；" + text : text;
            }
        }
    }

    /// <summary>
    /// 分步快捷键的等待时长（毫秒，全程序统一；0 = 不限时）。由外壳从 <c>ShellConfig</c> 设进来，
    /// 之后每次 <see cref="ApplyTo"/> 都会带进引擎。
    /// </summary>
    public int SequenceTimeoutMs { get; set; } = ShortcutTiming.DefaultSequenceTimeoutMs;

    /// <summary>把每个 provider 当前启用的快捷键推给引擎（引擎由调用方持有，通常是 App）。</summary>
    /// <param name="engine">全局引擎。</param>
    public void ApplyTo(GlobalShortcutEngine engine)
    {
        if (engine is null) return;

        engine.UnregisterAll();
        _registrationSeed = 1;

        // 分步等待时长（全程序统一的一项设置）
        engine.SequenceTimeout = SequenceTimeoutMs;

        // 只清「上一次注册失败」的旧条目，冲突条目在 DetectConflicts 里已经算好
        var conflictKeys = _errors.Keys.ToHashSet();
        foreach (var stale in conflictKeys) _errors.Remove(stale);
        DetectConflicts();

        var failures = new List<string>();

        foreach (var provider in _providers)
        {
            if (!provider.ShortcutsEnabled) continue;

            foreach (var definition in provider.GetShortcuts())
            {
                if (!definition.Enabled || definition.Primary.IsEmpty) continue;
                if (GetError(definition.ToolId, definition.Id).Length > 0) continue;   // 冲突的先不注册

                var id = _registrationSeed++;
                var host = provider as IShortcutActionHost;

                var shortcutId = definition.Id;
                var ok = engine.Register(id, definition.Kind, definition.Primary, definition.Secondary,
                    _ => host?.InvokeShortcut(shortcutId), out var error);

                if (!ok)
                {
                    _errors[Key(definition.ToolId, shortcutId)] = error ?? "注册失败";
                    failures.Add($"{provider.ShortcutToolName} · {definition.Name}：{error}");
                }
            }
        }

        if (failures.Count > 0)
        {
            ReportProblem?.Invoke("部分快捷键没能注册", string.Join(Environment.NewLine, failures));
        }
    }

    /// <summary>树里所有行（统计 / 搜索用）。</summary>
    public IEnumerable<ShortcutRow> AllRows()
        => Groups.SelectMany(g => g.Children).OfType<ShortcutGroupNode>()
            .SelectMany(g => g.Children).OfType<ShortcutRow>();

    /// <summary>
    /// 只展开指定分类（其余分类与工具全部收起）—— 截图 / 调试用。
    /// </summary>
    public void ExpandOnly(string categoryName)
    {
        foreach (var category in Groups)
        {
            var match = string.Equals(category.CategoryName, categoryName, StringComparison.CurrentCulture);
            category.IsExpanded = match;
            foreach (var tool in category.Children.OfType<ShortcutGroupNode>()) tool.IsExpanded = match;
        }

        RememberExpansion();
    }

    /// <summary>全部展开 / 全部收起（界面按钮与截图调试用）。</summary>
    public void SetAllExpanded(bool expanded)
    {
        foreach (var category in Groups)
        {
            category.IsExpanded = expanded;
            foreach (var tool in category.Children.OfType<ShortcutGroupNode>()) tool.IsExpanded = expanded;
        }

        // 记下来：之后再 Rebuild（例如别的工具抛了 Changed）也不会被折叠回去
        RememberExpansion();
    }
}

/// <summary>
/// provider 的可选扩展：告诉注册表「某条快捷键被按下时调谁」。
///
/// <para>拆成可选接口而不是塞进 <see cref="IShortcutProvider"/>，是为了让只想暴露快捷键、
/// 触发逻辑写在别处的工具也能用（它只要自己实现 <see cref="IShortcutProvider"/> 就行）。</para>
///
/// <para>⚠ 分步等待时长**不在这里**：它是全程序统一的一项设置（主页设置 → 快捷键），
/// 存在 <c>ShellConfig</c> 里、写进引擎的 <see cref="GlobalShortcutEngine.SequenceTimeout"/>。
/// 让每个工具各设一份没有意义 —— 用户按第一步时根本不会去想「这是哪个工具的等待时长」。</para>
/// </summary>
public interface IShortcutActionHost
{
    /// <summary>某条快捷键被按下（<paramref name="shortcutId"/> 是 <see cref="ShortcutDefinition.Id"/>）。</summary>
    void InvokeShortcut(string shortcutId);
}
