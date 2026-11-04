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

    /// <summary>自定义菜单条目 Id 前缀（后面是小写动词键名，例如 <c>custom:yuliangtoolkit.open.abc</c>）。</summary>
    private const string CustomIdPrefix = "custom:";

    /// <summary>合并后的自定义菜单条目固定显示的位置文案。</summary>
    private const string CustomScopeLabel = "自定义菜单";

    /// <summary>自定义菜单条目的固定说明（告诉用户删了只影响菜单、不动 exe）。</summary>
    private const string CustomStatusNote = "本工具「添加打开菜单」创建；删除后菜单项消失，exe 文件本身不受影响。";

    /// <summary>动词显示名称的值名（优先于默认值）。</summary>
    private const string MuiVerbValueName = "MUIVerb";

    /// <summary>动词「打开命令」的子键名（默认值就是命令行）。</summary>
    private const string CommandSubKeyName = "command";

    /// <summary>菜单图标的值名（<see cref="CustomContextMenuRules.BuildIcon"/> 写的就是它）。</summary>
    private const string IconValueName = "Icon";

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
        // 本工具「添加打开菜单」创建的项按动词键名跨位置合并（同一个动词会同时挂在多个后缀 / 位置下）。
        var customGroups = new Dictionary<string, CustomMenuGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in ContextMenuScopes.All)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(scope.VerbRelativePath)) CollectVerbs(scope, entries, customGroups);
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

        // 按后缀添加的自定义菜单在 SystemFileAssociations 下（不在上面那批固定位置里），单独扫一遍。
        try
        {
            CollectSystemFileAssociationsCustomMenus(customGroups);
        }
        catch
        {
            // 后缀位置读取失败不影响其余扫描。
        }

        foreach (var group in handlerGroups.Values) entries.Add(BuildHandlerEntry(group));
        foreach (var group in customGroups.Values) entries.Add(BuildCustomEntry(group));

        entries.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(left.Id, right.Id);
        });

        return entries;
    }

    /// <summary>
    /// 收集一个位置下的菜单动词（<c>...\shell\&lt;动词&gt;</c>）。
    /// <para>本工具添加的自定义菜单（动词键带 <see cref="CustomContextMenuRules.VerbKeyPrefix"/> 前缀）
    /// 不在这里出普通行，而是记进 <paramref name="customGroups"/> 按「动词键名」跨位置合并成一行。</para>
    /// </summary>
    private void CollectVerbs(
        ContextMenuScope scope,
        List<ContextMenuEntry> entries,
        Dictionary<string, CustomMenuGroup> customGroups)
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

            // SourceKeys：这个动词在真实注册表里到底存在于哪些根（删除 / 备份 / 多键隐藏都按它来）。
            var sourceKeys = new List<ContextMenuKeyRef>(2);
            if (KeyExistsSafe(currentUserPath))
                sourceKeys.Add(new ContextMenuKeyRef(RegistryRoot.CurrentUser, ClassesFullPath(relativeVerbPath)));
            if (KeyExistsSafe(localMachinePath))
                sourceKeys.Add(new ContextMenuKeyRef(RegistryRoot.LocalMachine, ClassesFullPath(relativeVerbPath)));
            if (sourceKeys.Count == 0)
                sourceKeys.Add(new ContextMenuKeyRef(sourceRoot, ClassesFullPath(relativeVerbPath)));

            // 本工具添加的自定义菜单：不在普通行出现，合并进 Custom 组。
            if (CustomContextMenuRules.IsCustomVerbKey(item.Name))
            {
                if (!customGroups.TryGetValue(item.Name, out var customGroup))
                {
                    customGroup = new CustomMenuGroup(item.Name);
                    customGroups[item.Name] = customGroup;
                }

                customGroup.AddOccurrence(
                    sourceKeys,
                    FirstUsableDisplayText(
                        ReadValue(currentUserPath, ""),
                        ReadValue(localMachinePath, ""),
                        ReadValue(currentUserPath, MuiVerbValueName),
                        ReadValue(localMachinePath, MuiVerbValueName)),
                    detail);
                continue;
            }

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

            // 删除限制：open 是打开文件的主操作，删掉会让双击失效。
            var canDelete = !ContextMenuRegistryLayout.IsCriticalVerbName(item.Name);

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
                StatusNote: statusNote)
            {
                SourceKeys = sourceKeys,
                CanDelete = canDelete,
                DeleteBlockReason = canDelete
                    ? ""
                    : "这是打开文件的主操作（open），删除后双击文件会打不开，本工具不提供删除。",
            });
        }
    }

    /// <summary>
    /// 扫描 <c>HKCU/HKLM\Software\Classes\SystemFileAssociations\&lt;后缀&gt;\shell</c> 下由本工具
    /// 「添加打开菜单（按后缀）」创建的动词，合并进自定义菜单组。
    /// <para>只认 <see cref="CustomContextMenuRules.SystemFileAssociationsPath"/> 的直接子键这一层后缀
    /// （可能是 <c>.psd</c> / <c>*</c> 等）；SystemFileAssociations 下面的其它层级不递归。</para>
    /// </summary>
    private void CollectSystemFileAssociationsCustomMenus(Dictionary<string, CustomMenuGroup> customGroups)
    {
        foreach (var root in new[] { RegistryRoot.CurrentUser, RegistryRoot.LocalMachine })
        {
            var associationsPath = new RegistryPath(root, CustomContextMenuRules.SystemFileAssociationsPath);
            foreach (var suffix in ReadSubKeyNames(associationsPath))
            {
                if (string.IsNullOrWhiteSpace(suffix)) continue;

                var shellPath = new RegistryPath(
                    root,
                    CustomContextMenuRules.SystemFileAssociationsPath + "\\" + suffix + "\\shell");

                foreach (var verbKey in ReadSubKeyNames(shellPath))
                {
                    if (!CustomContextMenuRules.IsCustomVerbKey(verbKey)) continue;

                    var relativePath = CustomContextMenuRules.SystemFileAssociationsPath + "\\" + suffix + "\\shell\\" + verbKey;
                    var verbPath = new RegistryPath(root, relativePath);
                    if (!KeyExistsSafe(verbPath)) continue;

                    if (!customGroups.TryGetValue(verbKey, out var customGroup))
                    {
                        customGroup = new CustomMenuGroup(verbKey);
                        customGroups[verbKey] = customGroup;
                    }

                    customGroup.AddOccurrence(
                        new[] { new ContextMenuKeyRef(root, relativePath) },
                        FirstUsableDisplayText(
                            ReadValue(verbPath, ""),
                            ReadValue(verbPath, MuiVerbValueName)),
                        ReadValue(ChildKey(verbPath, CommandSubKeyName), ""));
                }
            }
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

            // SourceKeys：这个扩展在真实注册表里出现的每个 (根 + 相对路径) 都记下来（HKCU / HKLM 都算）。
            var sourceKeys = new List<ContextMenuKeyRef>(2);
            if (KeyExistsSafe(ClassesKey(RegistryRoot.CurrentUser, relativeHandlerPath)))
                sourceKeys.Add(new ContextMenuKeyRef(RegistryRoot.CurrentUser, ClassesFullPath(relativeHandlerPath)));
            if (KeyExistsSafe(ClassesKey(RegistryRoot.LocalMachine, relativeHandlerPath)))
                sourceKeys.Add(new ContextMenuKeyRef(RegistryRoot.LocalMachine, ClassesFullPath(relativeHandlerPath)));
            if (sourceKeys.Count == 0)
                sourceKeys.Add(new ContextMenuKeyRef(sourceRoot, ClassesFullPath(relativeHandlerPath)));

            group.Add(scope.Label, relativeHandlerPath, item.Name, sourceKeys);
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

        // 删除限制：系统关键菜单扩展（新建）删掉会让右键菜单残缺。
        var canDelete = !ContextMenuRegistryLayout.CriticalClsids.Contains(group.Clsid);

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
            StatusNote: statusNote)
        {
            SourceKeys = group.SourceKeys,
            CanDelete = canDelete,
            DeleteBlockReason = canDelete
                ? ""
                : "这是系统关键菜单扩展（例如「新建」），删除后系统右键菜单会残缺，本工具不提供删除。",
        };
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
                ContextMenuEntryKind.Custom => SetVerbHidden(entry, hidden),
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

    /// <summary>
    /// 隐藏 / 恢复普通菜单动词或自定义菜单：按 <see cref="ContextMenuEntry.SourceKeys"/> 的多条相对路径，
    /// 逐条写 / 删 HKCU 覆盖键上的 <c>ProgrammaticAccessOnly</c>。
    /// <para>隐藏时即使原键只在 HKLM，也会创建 HKCU 下的同名覆盖键（这正是注册表的覆盖语义）。
    /// 恢复时只删本工具写的值和「因此变空」的 HKCU 键，原来就有别的值的键不动。</para>
    /// </summary>
    private ContextMenuOperationResult SetVerbHidden(ContextMenuEntry entry, bool hidden)
    {
        var relativePaths = GetHiddenTargetRelativePaths(entry);
        if (relativePaths.Count == 0) return ContextMenuOperationResult.Fail("条目缺少注册表路径，无法操作。");

        foreach (var relativePath in relativePaths)
        {
            var currentUserKey = ClassesKey(RegistryRoot.CurrentUser, relativePath);

            if (hidden)
            {
                Registry.SetValue(currentUserKey, ContextMenuRegistryLayout.ProgrammaticAccessOnly, ContextMenuRegistryLayout.Marker);
                continue;
            }

            Registry.DeleteValue(currentUserKey, ContextMenuRegistryLayout.ProgrammaticAccessOnly);

            // 只删「本工具创建、现在已经完全空了」的覆盖键；原来就存在的 HKCU 键不动。
            if (IsEmptyKey(currentUserKey)) Registry.DeleteKey(currentUserKey);
        }

        return ContextMenuOperationResult.Ok(SuccessMessage(entry, hidden));
    }

    /// <summary>
    /// 取隐藏 / 恢复要操作的相对 <c>Software\Classes</c> 路径：优先用 <see cref="ContextMenuEntry.SourceKeys"/>
    /// 去重后的相对路径（普通动词 + 自定义菜单都适用）；老代码手工 new 的动词条目没有 SourceKeys 时
    /// 回退到 Id 里记录的路径。
    /// </summary>
    private static List<string> GetHiddenTargetRelativePaths(ContextMenuEntry entry)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceKey in entry.SourceKeys)
        {
            if (sourceKey is null || string.IsNullOrWhiteSpace(sourceKey.RelativePath)) continue;
            var relativePath = ToClassesRelativePath(sourceKey.RelativePath);
            if (relativePath.Length > 0 && seen.Add(relativePath)) result.Add(relativePath);
        }

        if (result.Count == 0 && entry.Kind == ContextMenuEntryKind.Verb)
        {
            var legacy = GetVerbRelativePath(entry);
            if (legacy.Length > 0) result.Add(legacy);
        }

        return result;
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
    // 删除
    // ================================================================

    /// <summary>
    /// 永久删除一个条目的注册表菜单键（<see cref="ContextMenuEntry.SourceKeys"/> 里逐键处理）。
    /// <para>HKCU 的键正常情况下直接删掉；HKLM 没权限删的放进
    /// <see cref="ContextMenuDeleteResult.PermissionDeniedKeys"/>，由界面再走一次 UAC 提权删除。</para>
    /// <para>系统关键项（open / 新建菜单）拒绝删除；删除后只清理「因此变空」的上层键，
    /// 最多清到 <c>Software\Classes</c> 之下，绝不删 <c>Software\Classes</c> 本身。全程不抛异常。</para>
    /// </summary>
    public ContextMenuDeleteResult Delete(ContextMenuEntry entry)
    {
        var deleted = new List<ContextMenuKeyRef>();
        var permissionDenied = new List<ContextMenuKeyRef>();
        var failures = new List<string>();

        if (entry is null)
        {
            failures.Add("没有选中要删除的右键菜单项。");
            return new ContextMenuDeleteResult(deleted, permissionDenied, failures);
        }

        if (!entry.CanDelete)
        {
            var reason = string.IsNullOrWhiteSpace(entry.DeleteBlockReason)
                ? "这是系统关键项，本工具不提供删除。"
                : entry.DeleteBlockReason;
            failures.Add($"「{entry.Name}」不能删除：{reason}");
            return new ContextMenuDeleteResult(deleted, permissionDenied, failures);
        }

        var targets = DistinctSourceKeys(entry.SourceKeys);
        if (targets.Count == 0)
        {
            failures.Add("找不到可以删除的注册表键（可能是系统项或读取失败）。");
            return new ContextMenuDeleteResult(deleted, permissionDenied, failures);
        }

        foreach (var key in targets)
        {
            try
            {
                if (!Registry.KeyExists(key.ToRegistryPath())) continue; // 已经不存在：跳过，不算失败

                var removed = false;
                Exception? failure = null;
                try
                {
                    removed = Registry.TryDeleteKey(key.ToRegistryPath());
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                if (removed)
                {
                    deleted.Add(key);
                    CleanupEmptyParents(key);
                }
                else if (key.Root == RegistryRoot.LocalMachine)
                {
                    // HKLM 失败通常是权限不足：界面收集这些键后走一次 UAC 提权删除。
                    permissionDenied.Add(key);
                }
                else
                {
                    failures.Add(failure is null
                        ? $"删除「{key}」失败：可能没有权限或该键正被占用。"
                        : $"删除「{key}」失败：{failure.Message}");
                }
            }
            catch (Exception ex)
            {
                // 钥匙检查 / 删除本身抛异常：同样按根分类，绝不让一条坏键炸掉整个删除。
                if (key.Root == RegistryRoot.LocalMachine) permissionDenied.Add(key);
                else failures.Add($"删除「{key}」失败：{ex.Message}");
            }
        }

        return new ContextMenuDeleteResult(deleted, permissionDenied, failures);
    }

    /// <summary>
    /// 删除成功后清理「因此变空」的上层键：从被删键的相对路径逐层往上，
    /// 只要父键已经空（没有值也没有子键）就删掉并继续；碰到非空父键或
    /// <c>Software\Classes</c> 就停（绝不删 <c>Software\Classes</c> 本身）。
    /// </summary>
    private void CleanupEmptyParents(ContextMenuKeyRef key)
    {
        try
        {
            var relativePath = (key.RelativePath ?? "").Trim().Trim('\\');
            var classesPrefix = ContextMenuRegistryLayout.ClassesPath + "\\";

            while (true)
            {
                var slash = relativePath.LastIndexOf('\\');
                if (slash <= 0) break;
                relativePath = relativePath[..slash];

                // 只清理 Software\Classes 之下；到 Software\Classes（或不在它下面）就停。
                if (relativePath.Length <= ContextMenuRegistryLayout.ClassesPath.Length) break;
                if (!relativePath.StartsWith(classesPrefix, StringComparison.OrdinalIgnoreCase)) break;

                var parent = new RegistryPath(key.Root, relativePath);
                if (!IsEmptyKey(parent)) break;
                Registry.DeleteKey(parent);
            }
        }
        catch
        {
            // 清理空键只是顺手，删不掉不影响菜单项已经被删掉的事实。
        }
    }

    // ================================================================
    // 添加自定义打开菜单
    // ================================================================

    /// <summary>
    /// 添加一个「用 XX 打开」的自定义右键菜单（只写 HKCU）。
    /// <para>内部会先做表单校验，失败返回中文原因。exe 是否存在用默认的
    /// <see cref="File.Exists(string)"/> 判断。</para>
    /// </summary>
    public ContextMenuAddResult AddCustomMenu(CustomContextMenuDefinition definition)
        => AddCustomMenu(definition, exeExists: null);

    /// <summary>
    /// 添加一个「用 XX 打开」的自定义右键菜单（可注入 exe 存在性判断，方便自检不依赖文件系统）。
    /// <para>逐个键写默认值（菜单名）、<c>Icon</c>、<c>command</c> 子键默认值（命令行）；
    /// 任何一步失败都尽量回滚本次写入的键，绝不半途留下半成品。</para>
    /// </summary>
    public ContextMenuAddResult AddCustomMenu(
        CustomContextMenuDefinition definition,
        Func<string, bool>? exeExists)
    {
        var validation = CustomContextMenuRules.Validate(definition, exeExists);
        if (!validation.IsValid) return ContextMenuAddResult.Fail(validation.ErrorSummary);

        var name = (definition.Name ?? "").Trim();
        var exePath = (definition.ExePath ?? "").Trim().Trim('"');
        var verbKey = CustomContextMenuRules.NewVerbKey();
        var keys = CustomContextMenuRules.BuildSourceKeys(
            verbKey,
            validation.NormalizedExtensions,
            definition.ApplyToFolders,
            definition.ApplyToFolderBackground,
            definition.ApplyToAllFiles);

        if (keys.Count == 0)
            return ContextMenuAddResult.Fail("没有可添加的右键菜单位置：请至少填一个文件后缀，或勾选「文件夹 / 文件夹空白处 / 所有文件」中的一项。");

        var attempted = new List<ContextMenuKeyRef>();
        try
        {
            foreach (var key in keys)
            {
                attempted.Add(key);
                var path = key.ToRegistryPath();

                // 菜单显示名：写在动词键默认值上（资源管理器优先显示它）。
                Registry.SetValue(path, "", name);
                // 图标：直接取 exe 自己的图标。
                Registry.SetValue(path, IconValueName, CustomContextMenuRules.BuildIcon(exePath));
                // 命令行：文件夹空白处用 %V（当前文件夹），其它用 %1（被选中的文件 / 文件夹）。
                Registry.SetValue(
                    ChildKey(path, CommandSubKeyName),
                    "",
                    CustomContextMenuRules.BuildCommand(exePath, IsFolderBackgroundKey(key)));
            }

            var message = $"已添加「{name}」，写入 {keys.Count} 个注册表位置（只改当前用户，不需要管理员权限）；"
                          + "通常立即生效，个别情况点「重启资源管理器」。";
            return ContextMenuAddResult.Ok(message, keys);
        }
        catch (Exception ex)
        {
            // 尽力回滚：动词键是刚生成的唯一键，整棵删掉不会误伤用户原有菜单。
            foreach (var key in attempted)
            {
                try
                {
                    Registry.DeleteKey(key.ToRegistryPath());
                    CleanupEmptyParents(key);
                }
                catch
                {
                    // 单个回滚失败不掩盖原始错误。
                }
            }

            return ContextMenuAddResult.Fail($"添加「{name}」失败：{ex.Message}（已尽可能回滚写入的注册表项）");
        }
    }

    /// <summary>
    /// 这个自定义菜单键是不是「文件夹空白处」位置（<c>Directory\Background\shell\&lt;动词&gt;</c>）：
    /// 只有这一处命令行用 <c>%V</c>，其它位置用 <c>%1</c>。
    /// </summary>
    public static bool IsFolderBackgroundKey(ContextMenuKeyRef key)
    {
        if (key is null) return false;

        var relativePath = (key.RelativePath ?? "").Trim().Trim('\\');
        var prefix = ContextMenuRegistryLayout.ClassesPath + @"\Directory\Background\shell\";
        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var verbKey = relativePath[prefix.Length..];
        return verbKey.Length > 0 && !verbKey.Contains('\\');
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

    // ================================================================
    // 自定义菜单（Kind=Custom）的合并 / 隐藏状态
    // ================================================================

    /// <summary>把按动词键名合并好的自定义菜单变成一条 <see cref="ContextMenuEntryKind.Custom"/> 条目。</summary>
    private ContextMenuEntry BuildCustomEntry(CustomMenuGroup group)
    {
        var name = group.Name;
        if (name.Length == 0)
        {
            // 默认值读不到：用命令行里的 exe 名生成「用 XX 打开」，最后退回动词键名。
            name = CustomContextMenuRules.BuildMenuName(ExtractExecutableFromCommand(group.Detail));
            if (name.Length == 0) name = group.VerbKey;
        }

        var (isHidden, hideSource) = AggregateCustomHideState(group.SourceKeys);

        return new ContextMenuEntry(
            Id: CustomIdPrefix + group.VerbKey.ToLowerInvariant(),
            Kind: ContextMenuEntryKind.Custom,
            Name: name,
            ScopeLabel: CustomScopeLabel,
            RegistryLocation: string.Join("；", group.SourceKeys.Select(static key => key.ToString())),
            Clsid: null,
            Detail: group.Detail,
            IsHidden: isHidden,
            HideSource: hideSource,
            CanToggle: true,
            StatusNote: CustomStatusNote)
        {
            SourceKeys = group.SourceKeys,
            CanDelete = true,
            DeleteBlockReason = "",
        };
    }

    /// <summary>
    /// 汇总自定义菜单的隐藏状态：对全部 SourceKeys 的 HKCU 路径读 <c>ProgrammaticAccessOnly</c>
    /// （HKLM 原键上的电脑级值也算「其他程序隐藏」）。
    /// <para>一条隐藏记录都没有 → None；全部是本工具 Marker → ThisTool；其余（含混合）→ OtherProgram。</para>
    /// </summary>
    private (bool IsHidden, ContextMenuHideSource HideSource) AggregateCustomHideState(
        IReadOnlyList<ContextMenuKeyRef> sourceKeys)
    {
        var relativePaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in sourceKeys)
        {
            if (key is null) continue;
            var relativePath = (key.RelativePath ?? "").Trim().Trim('\\');
            if (relativePath.Length > 0 && seen.Add(relativePath)) relativePaths.Add(relativePath);
        }

        var hasAnyFlag = false;
        var allThisTool = true;
        foreach (var relativePath in relativePaths)
        {
            var currentUserFlag = ReadValue(
                new RegistryPath(RegistryRoot.CurrentUser, relativePath),
                ContextMenuRegistryLayout.ProgrammaticAccessOnly);

            if (currentUserFlag is not null)
            {
                hasAnyFlag = true;
                if (!IsOurMarker(currentUserFlag)) allThisTool = false;
                continue;
            }

            // HKCU 没写覆盖值，但 HKLM 原键上有隐藏值：视为其他程序 / 系统隐藏。
            var localMachineFlag = ReadValue(
                new RegistryPath(RegistryRoot.LocalMachine, relativePath),
                ContextMenuRegistryLayout.ProgrammaticAccessOnly);
            if (localMachineFlag is not null)
            {
                hasAnyFlag = true;
                allThisTool = false;
            }
        }

        if (!hasAnyFlag) return (false, ContextMenuHideSource.None);
        return (true, allThisTool ? ContextMenuHideSource.ThisTool : ContextMenuHideSource.OtherProgram);
    }

    /// <summary>
    /// 从命令行里取 exe 路径：支持 <c>"C:\a b\x.exe" "%1"</c> 和 <c>x.exe %1</c> 两种写法。
    /// 取不到返回空串。
    /// </summary>
    private static string ExtractExecutableFromCommand(string? command)
    {
        var text = (command ?? "").Trim();
        if (text.Length == 0) return "";

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end].Trim() : "";
        }

        var space = text.IndexOfAny(new[] { ' ', '\t' });
        return (space > 0 ? text[..space] : text).Trim();
    }

    // ================================================================
    // 路径 / 集合小工具
    // ================================================================

    /// <summary>把 <c>Software\Classes\...</c> 形式（相对注册表根）的路径转成相对 <c>Software\Classes</c> 的路径。</summary>
    private static string ToClassesRelativePath(string? relativePath)
    {
        var trimmed = (relativePath ?? "").Trim().Trim('\\');
        var prefix = ContextMenuRegistryLayout.ClassesPath + "\\";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[prefix.Length..];
        return string.Equals(trimmed, ContextMenuRegistryLayout.ClassesPath, StringComparison.OrdinalIgnoreCase)
            ? ""
            : trimmed;
    }

    /// <summary>相对 <c>Software\Classes</c> 的路径 → 相对注册表根的完整路径（用于 <see cref="ContextMenuKeyRef"/>）。</summary>
    private static string ClassesFullPath(string classesRelativePath)
        => ContextMenuRegistryLayout.ClassesPath + "\\" + (classesRelativePath ?? "").Trim().Trim('\\');

    /// <summary>键是否存在；读取失败（无权限 / 坏注册表）当作不存在，绝不让枚举崩掉。</summary>
    private bool KeyExistsSafe(RegistryPath path)
    {
        try { return Registry.KeyExists(path); }
        catch { return false; }
    }

    /// <summary>取第一个可用（非空白、不是 <c>@资源串</c>）的显示文本；都没有返回 null。</summary>
    private static string? FirstUsableDisplayText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (IsUsableDisplayText(value)) return value!.Trim();
        }
        return null;
    }

    /// <summary>SourceKeys 去重（根 + 相对路径，忽略大小写）。</summary>
    private static List<ContextMenuKeyRef> DistinctSourceKeys(IReadOnlyList<ContextMenuKeyRef>? keys)
    {
        var result = new List<ContextMenuKeyRef>();
        if (keys is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (key is null || string.IsNullOrWhiteSpace(key.RelativePath)) continue;
            var relativePath = key.RelativePath.Trim().Trim('\\');
            if (seen.Add(key.Root + "|" + relativePath))
                result.Add(new ContextMenuKeyRef(key.Root, relativePath));
        }
        return result;
    }

    /// <summary>合并外壳扩展时用的累加器：同一个 CLSID 在多个位置出现时收集位置信息。</summary>
    private sealed class HandlerGroup
    {
        /// <summary>SourceKeys 去重用的键集合（根 + 相对路径）。</summary>
        private readonly HashSet<string> _sourceKeySet = new(StringComparer.OrdinalIgnoreCase);

        public HandlerGroup(string clsid) => Clsid = clsid;

        /// <summary>规范 CLSID（大写、带花括号）。</summary>
        public string Clsid { get; }

        /// <summary>出现位置的中文名（去重，按发现顺序）。</summary>
        public List<string> ScopeLabels { get; } = [];

        /// <summary>具体的外壳扩展键相对路径（去重，按发现顺序）。</summary>
        public List<string> RegistryKeys { get; } = [];

        /// <summary>外壳扩展子键名（CLSID 没有显示名时兜底用）。</summary>
        public List<string> HandlerNames { get; } = [];

        /// <summary>这个扩展实际存在的全部注册表键（HKCU / HKLM，按发现顺序去重）。</summary>
        public List<ContextMenuKeyRef> SourceKeys { get; } = [];

        /// <summary>记一个出现位置（连同该位置真实存在的键）。</summary>
        public void Add(string scopeLabel, string registryKey, string handlerName, IEnumerable<ContextMenuKeyRef> sourceKeys)
        {
            if (!ScopeLabels.Contains(scopeLabel, StringComparer.OrdinalIgnoreCase)) ScopeLabels.Add(scopeLabel);
            if (!RegistryKeys.Contains(registryKey, StringComparer.OrdinalIgnoreCase)) RegistryKeys.Add(registryKey);
            if (!HandlerNames.Contains(handlerName, StringComparer.OrdinalIgnoreCase)) HandlerNames.Add(handlerName);

            foreach (var key in sourceKeys)
            {
                if (key is null) continue;
                var relativePath = (key.RelativePath ?? "").Trim().Trim('\\');
                if (relativePath.Length == 0) continue;
                if (!_sourceKeySet.Add(key.Root + "|" + relativePath)) continue;
                SourceKeys.Add(new ContextMenuKeyRef(key.Root, relativePath));
            }
        }
    }

    /// <summary>
    /// 合并自定义菜单时用的累加器：同一个动词键名可能同时出现在多个标准位置 +
    /// <c>SystemFileAssociations\&lt;后缀&gt;\shell</c> 下，跨位置合并成一行。
    /// </summary>
    private sealed class CustomMenuGroup
    {
        /// <summary>SourceKeys 去重用的键集合（根 + 相对路径）。</summary>
        private readonly HashSet<string> _sourceKeySet = new(StringComparer.OrdinalIgnoreCase);

        public CustomMenuGroup(string verbKey) => VerbKey = verbKey;

        /// <summary>注册表里的动词键名（大小写以第一次发现为准）。</summary>
        public string VerbKey { get; }

        /// <summary>全部出现位置（按发现顺序，HKCU 在前）。</summary>
        public List<ContextMenuKeyRef> SourceKeys { get; } = [];

        /// <summary>菜单显示名（第一个可用默认值；读不到由外层兜底）。</summary>
        public string Name { get; private set; } = "";

        /// <summary>第一条 command 命令行。</summary>
        public string Detail { get; private set; } = "";

        /// <summary>记一个出现位置；名称 / 命令行只在第一次拿到时保留。</summary>
        public void AddOccurrence(
            IEnumerable<ContextMenuKeyRef> sourceKeys,
            string? displayText,
            string? command)
        {
            foreach (var key in sourceKeys)
            {
                if (key is null) continue;
                var relativePath = (key.RelativePath ?? "").Trim().Trim('\\');
                if (relativePath.Length == 0) continue;
                if (!_sourceKeySet.Add(key.Root + "|" + relativePath)) continue;
                SourceKeys.Add(new ContextMenuKeyRef(key.Root, relativePath));
            }

            if (Name.Length == 0 && IsUsableDisplayText(displayText)) Name = displayText!.Trim();
            if (Detail.Length == 0 && !string.IsNullOrWhiteSpace(command)) Detail = command.Trim();
        }
    }
}
