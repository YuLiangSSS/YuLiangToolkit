namespace YuLiangToolkit.Tools.ContextMenu;

/// <summary>
/// 右键菜单的枚举 / 隐藏 / 恢复逻辑（不碰界面，也不直接碰真实注册表）。
///
/// <para>所有注册表读写都经过构造时注入的 <see cref="IContextMenuRegistry"/>：
/// 界面用 <see cref="RegistryContextMenuStore"/>（<c>Microsoft.Win32.Registry</c>），
/// 自检用内存假实现。这样这一层的逻辑可以完整断言，而且自检<b>绝不写用户真实注册表</b>。</para>
///
/// <para><b>安全边界</b>：隐藏只写 <c>HKCU</c> 的覆盖值——普通菜单动词写
/// <c>ProgrammaticAccessOnly = Marker</c>，外壳扩展写
/// <c>HKCU\...\Shell Extensions\Blocked</c> 黑名单值；<b>从不写 HKLM、从不删除或修改原来的菜单键</b>。
/// 恢复只删本工具 / 用户点选的那条覆盖值，并且只在这个 HKCU 键变空时才删掉空键。
/// 值数据带着 <see cref="ContextMenuRegistryLayout.Marker"/> 标记，「恢复全部」据此只恢复本工具隐藏的项。</para>
/// </summary>
public sealed class ContextMenuService
{
    /// <summary>外壳扩展黑名单键（按约定只读 / 只写 HKCU）。</summary>
    private static readonly RegistryPath BlockedKey =
        new(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath);

    /// <summary>动词条目 Id 前缀（后面是相对 <c>Software\Classes</c> 的小写路径）。</summary>
    private const string VerbIdPrefix = "verb:";

    /// <summary>外壳扩展条目 Id 前缀（后面是规范 CLSID）。</summary>
    private const string HandlerIdPrefix = "handler:";

    /// <summary>动词显示名称的值名（优先于默认值）。</summary>
    private const string MuiVerbValueName = "MUIVerb";

    /// <summary>动词「打开命令」的子键名（默认值就是命令行）。</summary>
    private const string CommandSubKeyName = "command";

    /// <summary>外壳扩展 DLL 路径所在的子键名。</summary>
    private const string InProcServer32SubKeyName = "InprocServer32";

    /// <summary>构造一个引擎；<paramref name="registry"/> 为空时抛 <see cref="ArgumentNullException"/>。</summary>
    public ContextMenuService(IContextMenuRegistry registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>注册表读写入口（自检会换成内存假注册表）。</summary>
    public IContextMenuRegistry Registry { get; }

    // ================================================================
    // 枚举
    // ================================================================

    /// <summary>
    /// 扫描 <see cref="ContextMenuScopes.All"/> 里的全部位置，返回条目清单（隐藏的也在里面，带状态）。
    ///
    /// <para>每个位置分别读 HKCU / HKLM 下的同名路径并按子键名合并（HKCU 覆盖 HKLM、忽略大小写）。
    /// 最终结果统一按显示名称排序（忽略大小写），同名时按稳定 Id 兜底，保证两次枚举顺序一致。</para>
    ///
    /// <para>容错：某个键读不到 / 没权限 / 类型怪异时只跳过那一条或那个位置，其余条目照常返回，绝不抛异常。</para>
    /// </summary>
    public IReadOnlyList<ContextMenuEntry> Enumerate()
    {
        var entries = new List<ContextMenuEntry>();
        // 外壳扩展按规范 CLSID 跨位置合并；同一个 CLSID 在多个位置出现只显示一行。
        var handlerGroups = new Dictionary<string, HandlerGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in ContextMenuScopes.All)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(scope.VerbRelativePath)) CollectVerbs(scope, entries);
            }
            catch
            {
                // 单个位置读取失败（权限 / 键损坏）不影响其它位置。
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(scope.HandlerRelativePath)) CollectHandlers(scope, handlerGroups);
            }
            catch
            {
                // 同上：外壳扩展位置读取失败不影响动词和其它位置。
            }
        }

        foreach (var group in handlerGroups.Values) entries.Add(BuildHandlerEntry(group));

        entries.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(left.Id, right.Id);
        });

        return entries;
    }

    /// <summary>收集一个位置下的普通菜单动词（<c>...\shell\&lt;动词&gt;</c>）。</summary>
    private void CollectVerbs(ContextMenuScope scope, List<ContextMenuEntry> entries)
    {
        var relativePath = scope.VerbRelativePath!;
        var mergedNames = MergeSubKeyNames(
            ClassesKey(RegistryRoot.CurrentUser, relativePath),
            ClassesKey(RegistryRoot.LocalMachine, relativePath));

        foreach (var item in mergedNames)
        {
            var relativeVerbPath = relativePath + "\\" + item.Name;
            // 「生效来源」：HKCU 有同名子键就用 HKCU（它能覆盖 HKLM），否则用 HKLM。
            var sourceRoot = item.FromCurrentUser ? RegistryRoot.CurrentUser : RegistryRoot.LocalMachine;

            var currentUserPath = ClassesKey(RegistryRoot.CurrentUser, relativeVerbPath);
            var localMachinePath = ClassesKey(RegistryRoot.LocalMachine, relativeVerbPath);

            var displayName = ReadVerbDisplayText(currentUserPath, localMachinePath) ?? item.Name;
            var detail = FirstNonEmpty(
                ReadValue(ChildKey(currentUserPath, CommandSubKeyName), ""),
                ReadValue(ChildKey(localMachinePath, CommandSubKeyName), ""));

            var currentUserFlag = ReadValue(currentUserPath, ContextMenuRegistryLayout.ProgrammaticAccessOnly);
            var localMachineFlag = ReadValue(localMachinePath, ContextMenuRegistryLayout.ProgrammaticAccessOnly);

            // 隐藏来源：HKCU 的值优先（数据带 Marker = 本工具隐藏）；HKCU 没有再看 HKLM 电脑级隐藏。
            var hideSource = ContextMenuHideSource.None;
            if (currentUserFlag is not null)
                hideSource = IsOurMarker(currentUserFlag) ? ContextMenuHideSource.ThisTool : ContextMenuHideSource.OtherProgram;
            else if (localMachineFlag is not null)
                hideSource = ContextMenuHideSource.OtherProgram;

            var isHidden = hideSource != ContextMenuHideSource.None;

            // 系统关键项 / 电脑级隐藏项不允许本工具操作，并给出中文说明。
            var canToggle = true;
            var statusNote = "";
            if (ContextMenuRegistryLayout.IsCriticalVerbName(item.Name))
            {
                canToggle = false;
                statusNote = "这是打开文件的主操作（open），隐藏后双击文件会打不开，本工具不提供隐藏。";
            }
            else if (localMachineFlag is not null && currentUserFlag is null)
            {
                canToggle = false;
                statusNote = "这一项由系统或其他程序在电脑级（HKLM）注册表隐藏；本工具只改当前用户注册表，无法恢复。";
            }

            entries.Add(new ContextMenuEntry(
                Id: VerbIdPrefix + relativeVerbPath.ToLowerInvariant(),
                Kind: ContextMenuEntryKind.Verb,
                Name: displayName,
                ScopeLabel: scope.Label,
                RegistryLocation: ClassesKey(sourceRoot, relativeVerbPath).ToString(),
                Clsid: null,
                Detail: detail,
                IsHidden: isHidden,
                HideSource: hideSource,
                CanToggle: canToggle,
                StatusNote: statusNote));
        }
    }

    /// <summary>收集一个位置下的外壳扩展（<c>...\shellex\ContextMenuHandlers\&lt;名字&gt;</c>，默认值 = CLSID）。</summary>
    private void CollectHandlers(ContextMenuScope scope, Dictionary<string, HandlerGroup> handlerGroups)
    {
        var relativePath = scope.HandlerRelativePath!;
        var mergedNames = MergeSubKeyNames(
            ClassesKey(RegistryRoot.CurrentUser, relativePath),
            ClassesKey(RegistryRoot.LocalMachine, relativePath));

        foreach (var item in mergedNames)
        {
            var relativeHandlerPath = relativePath + "\\" + item.Name;
            var sourceRoot = item.FromCurrentUser ? RegistryRoot.CurrentUser : RegistryRoot.LocalMachine;
            var sourcePath = ClassesKey(sourceRoot, relativeHandlerPath);
            // 某些键只是 HKCU 覆盖键、默认值为空；这时回退到另一个根再读一次 CLSID。
            var otherPath = ClassesKey(
                item.FromCurrentUser ? RegistryRoot.LocalMachine : RegistryRoot.CurrentUser,
                relativeHandlerPath);

            var rawClsid = ReadValue(sourcePath, "");
            var clsid = ContextMenuRegistryLayout.CanonicalClsid(rawClsid);
            // 只有「生效来源根本没写默认值」才回退到另一个根；写了非法 CLSID（资源串 / 垃圾）按下一条跳过。
            if (clsid.Length == 0 && string.IsNullOrWhiteSpace(rawClsid))
                clsid = ContextMenuRegistryLayout.CanonicalClsid(ReadValue(otherPath, ""));
            if (clsid.Length == 0) continue; // 默认值不是合法 CLSID（资源串 / 空 / 垃圾）的键直接跳过

            if (!handlerGroups.TryGetValue(clsid, out var group))
            {
                group = new HandlerGroup(clsid);
                handlerGroups[clsid] = group;
            }

            group.Add(scope.Label, relativeHandlerPath, item.Name);
        }
    }

    /// <summary>把合并用的 CLSID 分组变成一个条目。</summary>
    private ContextMenuEntry BuildHandlerEntry(HandlerGroup group)
    {
        var currentUserClsidKey = ClassesKey(RegistryRoot.CurrentUser, @"CLSID\" + group.Clsid);
        var localMachineClsidKey = ClassesKey(RegistryRoot.LocalMachine, @"CLSID\" + group.Clsid);

        // 名称优先取 CLSID 键的默认值（不是 @资源串），其次外壳扩展子键名，最后用 CLSID 本身。
        var displayName = ReadDefaultDisplayText(currentUserClsidKey, localMachineClsidKey);
        if (string.IsNullOrEmpty(displayName))
            displayName = group.HandlerNames.Count > 0 ? group.HandlerNames[0] : group.Clsid;

        var detail = FirstNonEmpty(
            ReadValue(ChildKey(currentUserClsidKey, InProcServer32SubKeyName), ""),
            ReadValue(ChildKey(localMachineClsidKey, InProcServer32SubKeyName), ""));

        var blocked = FindBlockedValue(group.Clsid);
        var hideSource = blocked.Found
            ? (IsOurMarker(blocked.Data) ? ContextMenuHideSource.ThisTool : ContextMenuHideSource.OtherProgram)
            : ContextMenuHideSource.None;

        var canToggle = !ContextMenuRegistryLayout.CriticalClsids.Contains(group.Clsid);
        var statusNote = canToggle
            ? ""
            : "这是系统关键菜单扩展（例如「新建」），隐藏后系统右键菜单会残缺，本工具不提供隐藏。";

        return new ContextMenuEntry(
            Id: HandlerIdPrefix + group.Clsid,
            Kind: ContextMenuEntryKind.ShellExtension,
            Name: displayName,
            ScopeLabel: string.Join("、", group.ScopeLabels),
            RegistryLocation: string.Join("；", group.RegistryKeys),
            Clsid: group.Clsid,
            Detail: detail,
            IsHidden: blocked.Found,
            HideSource: hideSource,
            CanToggle: canToggle,
            StatusNote: statusNote);
    }

    // ================================================================
    // 隐藏 / 恢复单个条目
    // ================================================================

    /// <summary>
    /// 隐藏 / 恢复一个条目（只写 HKCU，绝不写 HKLM、绝不删原菜单键）。
    /// <para>失败返回 <see cref="ContextMenuOperationResult.Fail"/>（中文原因），不抛异常。</para>
    /// </summary>
    public ContextMenuOperationResult SetHidden(ContextMenuEntry entry, bool hidden)
    {
        if (entry is null) return ContextMenuOperationResult.Fail("没有选中要操作的右键菜单项。");

        // 系统关键项 / 电脑级隐藏项：界面即使误操作点到这里也要明确拒绝。
        if (!entry.CanToggle)
        {
            var reason = string.IsNullOrWhiteSpace(entry.StatusNote)
                ? "这是系统关键项，本工具不提供隐藏或恢复。"
                : entry.StatusNote;
            return ContextMenuOperationResult.Fail($"「{entry.Name}」不能修改：{reason}");
        }

        try
        {
            return entry.Kind switch
            {
                ContextMenuEntryKind.Verb => SetVerbHidden(entry, hidden),
                ContextMenuEntryKind.ShellExtension => SetHandlerHidden(entry, hidden),
                _ => ContextMenuOperationResult.Fail($"「{entry.Name}」的类型未知，无法操作。"),
            };
        }
        catch (Exception ex)
        {
            var action = hidden ? "隐藏" : "恢复显示";
            return ContextMenuOperationResult.Fail(
                $"{action}「{entry.Name}」失败：{ex.Message}（本工具只修改当前用户注册表，通常不需要管理员权限）");
        }
    }

    /// <summary>隐藏 / 恢复普通菜单动词：写 / 删 HKCU 覆盖键上的 <c>ProgrammaticAccessOnly</c>。</summary>
    private ContextMenuOperationResult SetVerbHidden(ContextMenuEntry entry, bool hidden)
    {
        var relativePath = GetVerbRelativePath(entry);
        if (relativePath.Length == 0) return ContextMenuOperationResult.Fail("条目缺少注册表路径，无法操作。");

        var currentUserKey = ClassesKey(RegistryRoot.CurrentUser, relativePath);

        if (hidden)
        {
            Registry.SetValue(currentUserKey, ContextMenuRegistryLayout.ProgrammaticAccessOnly, ContextMenuRegistryLayout.Marker);
            return ContextMenuOperationResult.Ok(SuccessMessage(entry, hidden: true));
        }

        Registry.DeleteValue(currentUserKey, ContextMenuRegistryLayout.ProgrammaticAccessOnly);

        // 只删「本工具创建、现在已经完全空了」的覆盖键；原来就存在的 HKCU 键不动。
        if (IsEmptyKey(currentUserKey)) Registry.DeleteKey(currentUserKey);

        return ContextMenuOperationResult.Ok(SuccessMessage(entry, hidden: false));
    }

    /// <summary>隐藏 / 恢复外壳扩展：在 HKCU 的 <c>Shell Extensions\Blocked</c> 黑名单里加 / 删 CLSID 值。</summary>
    private ContextMenuOperationResult SetHandlerHidden(ContextMenuEntry entry, bool hidden)
    {
        var clsid = GetHandlerClsid(entry);
        if (clsid.Length == 0) return ContextMenuOperationResult.Fail("条目缺少有效的 CLSID，无法操作。");

        if (hidden)
        {
            Registry.SetValue(BlockedKey, clsid, ContextMenuRegistryLayout.Marker);
            return ContextMenuOperationResult.Ok(SuccessMessage(entry, hidden: true));
        }

        // 优先删注册表里实际的写法（大小写可能和规范 CLSID 不同），找不到就按规范 CLSID 删。
        var existing = FindBlockedValue(clsid);
        Registry.DeleteValue(BlockedKey, existing.Found ? existing.ValueName! : clsid);

        // 黑名单键空掉就顺手删掉（只删空键，不影响其它值）。
        if (IsEmptyKey(BlockedKey)) Registry.DeleteKey(BlockedKey);

        return ContextMenuOperationResult.Ok(SuccessMessage(entry, hidden: false));
    }

    // ================================================================
    // 恢复全部（只恢复本工具隐藏的）
    // ================================================================

    /// <summary>
    /// 把所有「本工具隐藏」的条目恢复显示（其他程序 / 系统隐藏的不动）。
    /// <para>失败的条目会记进 <see cref="ContextMenuRestoreResult.Failures"/> 并继续处理其余条目。</para>
    /// </summary>
    public ContextMenuRestoreResult RestoreAllHidden()
    {
        var failures = new List<string>();
        IReadOnlyList<ContextMenuEntry> entries;
        try
        {
            entries = Enumerate();
        }
        catch (Exception ex)
        {
            return new ContextMenuRestoreResult(0, new[] { $"扫描右键菜单失败：{ex.Message}" });
        }

        var restored = 0;
        foreach (var entry in entries)
        {
            if (!entry.IsHiddenByThisTool) continue;

            var result = SetHidden(entry, hidden: false);
            if (result.Success) restored++;
            else failures.Add(entry.Name);
        }

        return new ContextMenuRestoreResult(restored, failures);
    }

    // ================================================================
    // 注册表读取小工具（每个读取单独兜底，坏键不影响其它条目）
    // ================================================================

    /// <summary>读子键名；失败 / 无权限返回空列表。</summary>
    private IReadOnlyList<string> ReadSubKeyNames(RegistryPath path)
    {
        try { return Registry.GetSubKeyNames(path); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>读一个值；失败 / 无权限返回 null。</summary>
    private string? ReadValue(RegistryPath path, string valueName)
    {
        try { return Registry.GetValue(path, valueName); }
        catch { return null; }
    }

    /// <summary>读一个键下的全部值；失败 / 无权限返回空列表。</summary>
    private IReadOnlyList<RegistryValue> ReadValues(RegistryPath path)
    {
        try { return Registry.GetValues(path); }
        catch { return Array.Empty<RegistryValue>(); }
    }

    /// <summary>
    /// 合并 HKCU / HKLM 下的直接子键名：忽略大小写去重，HKCU 覆盖 HKLM，按子键名排序。
    /// </summary>
    private List<(string Name, bool FromCurrentUser)> MergeSubKeyNames(
        RegistryPath currentUserKey,
        RegistryPath localMachineKey)
    {
        var merged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ReadSubKeyNames(currentUserKey)) merged[name] = true;
        foreach (var name in ReadSubKeyNames(localMachineKey))
        {
            if (!merged.ContainsKey(name)) merged[name] = false;
        }

        var result = new List<(string Name, bool FromCurrentUser)>(merged.Count);
        foreach (var pair in merged) result.Add((pair.Key, pair.Value));
        result.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>读动词显示名称：HKCU → HKLM 的 <c>MUIVerb</c>，再退到默认值；<c>@资源串</c>不算可显示名称。</summary>
    private string? ReadVerbDisplayText(RegistryPath currentUserVerb, RegistryPath localMachineVerb)
    {
        foreach (var verbPath in new[] { currentUserVerb, localMachineVerb })
        {
            var muiVerb = ReadValue(verbPath, MuiVerbValueName);
            if (IsUsableDisplayText(muiVerb)) return muiVerb!.Trim();

            var defaultValue = ReadValue(verbPath, "");
            if (IsUsableDisplayText(defaultValue)) return defaultValue!.Trim();
        }
        return null;
    }

    /// <summary>读键的默认值作为显示名称（用于 CLSID 键）；<c>@资源串</c>不算可显示名称。</summary>
    private string? ReadDefaultDisplayText(RegistryPath first, RegistryPath second)
    {
        foreach (var path in new[] { first, second })
        {
            var text = ReadValue(path, "");
            if (IsUsableDisplayText(text)) return text!.Trim();
        }
        return null;
    }

    /// <summary>
    /// 在黑名单键里找某个规范 CLSID 对应的值（不区分大小写 / 花括号写法，
    /// 避免别的程序用不同写法写入时漏判隐藏状态）。
    /// </summary>
    private (bool Found, string? ValueName, string? Data) FindBlockedValue(string canonicalClsid)
    {
        foreach (var value in ReadValues(BlockedKey))
        {
            if (string.Equals(value.Name, canonicalClsid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    ContextMenuRegistryLayout.CanonicalClsid(value.Name),
                    canonicalClsid,
                    StringComparison.Ordinal))
            {
                return (true, value.Name, value.Value);
            }
        }
        return (false, null, null);
    }

    /// <summary>这个键是不是「既没有值也没有子键」的空键（恢复后用来决定要不要删掉自己创建的壳）。</summary>
    private bool IsEmptyKey(RegistryPath path)
    {
        try
        {
            return ReadValues(path).Count == 0 && ReadSubKeyNames(path).Count == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从 Id 里取动词相对路径（<c>verb:</c> 后面那段）。</summary>
    private static string GetVerbRelativePath(ContextMenuEntry entry)
    {
        if (entry.Id is null || !entry.Id.StartsWith(VerbIdPrefix, StringComparison.Ordinal)) return "";
        return entry.Id[VerbIdPrefix.Length..].Trim('\\');
    }

    /// <summary>从条目取规范 CLSID：优先 <see cref="ContextMenuEntry.Clsid"/>，其次从 Id 里解析。</summary>
    private static string GetHandlerClsid(ContextMenuEntry entry)
    {
        var fromEntry = ContextMenuRegistryLayout.CanonicalClsid(entry.Clsid);
        if (fromEntry.Length > 0) return fromEntry;

        if (entry.Id is not null && entry.Id.StartsWith(HandlerIdPrefix, StringComparison.Ordinal))
            return ContextMenuRegistryLayout.CanonicalClsid(entry.Id[HandlerIdPrefix.Length..]);

        return "";
    }

    /// <summary>是不是本工具写下的标记值。</summary>
    private static bool IsOurMarker(string? value)
        => string.Equals(value, ContextMenuRegistryLayout.Marker, StringComparison.Ordinal);

    /// <summary>显示名称是否可用：非空白且不是 <c>@dll,-123</c> 这类资源串。</summary>
    private static bool IsUsableDisplayText(string? text)
        => !string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith('@');

    /// <summary>取第一个非空白并 Trim 的文本。</summary>
    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return "";
    }

    /// <summary>成功消息（界面日志用；带条目名和「重启资源管理器」提示）。</summary>
    private static string SuccessMessage(ContextMenuEntry entry, bool hidden)
        => hidden
            ? $"已隐藏「{entry.Name}」，资源管理器里的右键菜单通常会立即刷新；如没变化可点「重启资源管理器」。"
            : $"已恢复显示「{entry.Name}」。";

    /// <summary><c>Software\Classes\&lt;相对路径&gt;</c> 的完整注册表路径。</summary>
    private static RegistryPath ClassesKey(RegistryRoot root, string relativePath)
        => new(root, ContextMenuRegistryLayout.ClassesPath + "\\" + relativePath);

    /// <summary>拼一个子键的完整路径。</summary>
    private static RegistryPath ChildKey(RegistryPath parent, string childName)
        => new(parent.Root, parent.Path + "\\" + childName);

    /// <summary>合并外壳扩展时用的累加器：同一个 CLSID 在多个位置出现时收集位置信息。</summary>
    private sealed class HandlerGroup
    {
        public HandlerGroup(string clsid) => Clsid = clsid;

        /// <summary>规范 CLSID（大写、带花括号）。</summary>
        public string Clsid { get; }

        /// <summary>出现位置的中文名（去重，按发现顺序）。</summary>
        public List<string> ScopeLabels { get; } = [];

        /// <summary>具体的外壳扩展键相对路径（去重，按发现顺序）。</summary>
        public List<string> RegistryKeys { get; } = [];

        /// <summary>外壳扩展子键名（CLSID 没有显示名时兜底用）。</summary>
        public List<string> HandlerNames { get; } = [];

        /// <summary>记一个出现位置。</summary>
        public void Add(string scopeLabel, string registryKey, string handlerName)
        {
            if (!ScopeLabels.Contains(scopeLabel, StringComparer.OrdinalIgnoreCase)) ScopeLabels.Add(scopeLabel);
            if (!RegistryKeys.Contains(registryKey, StringComparer.OrdinalIgnoreCase)) RegistryKeys.Add(registryKey);
            if (!HandlerNames.Contains(handlerName, StringComparer.OrdinalIgnoreCase)) HandlerNames.Add(handlerName);
        }
    }
}
