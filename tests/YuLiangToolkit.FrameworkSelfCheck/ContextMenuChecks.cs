using YuLiangToolkit.Tools.ContextMenu;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「右键菜单管理」的纯逻辑自检：枚举 / 隐藏 / 恢复 / 恢复全部 / 筛选 / CLSID 规范化。
///
/// <para><b>硬性约束</b>：这里<b>绝不碰</b>用户真实的注册表 —— 引擎 <see cref="ContextMenuService"/>
/// 的所有读写都走 <see cref="IContextMenuRegistry"/>，本文件用内存假实现
/// <see cref="FakeContextMenuRegistry"/> 注入，断言的是「值有没有按约定写进 HKCU 路径 / 有没有删干净」，
/// 不落盘、不需要管理员权限、可在任何机器上重复跑。跑一次自检就在用户右键菜单里留垃圾，
/// 是不可接受的副作用（恢复全部也只清本工具 Marker，不能靠它擦屁股）。</para>
///
/// <para>「真实注册表路径」只作为字符串断言对象：<see cref="RegistryPath"/> 是纯 record struct，
/// 假实现按 根 + 路径 存字典，和 <c>RegistryContextMenuStore</c> 的映射关系一一对应。</para>
/// </summary>
internal static class ContextMenuChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> checkPlain)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);

        // ================================================================
        // 1. 常量与纯逻辑：Marker / 注册表位置 / CLSID 规范化 / 关键项
        // ================================================================
        Check("常量：Marker = YuLiangToolkit.ContextMenuManager（恢复全部只认它）",
            ContextMenuRegistryLayout.Marker == "YuLiangToolkit.ContextMenuManager"
            && ContextMenuRegistryLayout.Marker.Trim().Length > 0,
            ContextMenuRegistryLayout.Marker);

        Check("常量：ProgrammaticAccessOnly / ClassesPath / BlockedListPath 路径写死正确",
            ContextMenuRegistryLayout.ProgrammaticAccessOnly == "ProgrammaticAccessOnly"
            && ContextMenuRegistryLayout.ClassesPath == @"Software\Classes"
            && ContextMenuRegistryLayout.BlockedListPath == @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked",
            $"{ContextMenuRegistryLayout.ProgrammaticAccessOnly} / {ContextMenuRegistryLayout.ClassesPath} / {ContextMenuRegistryLayout.BlockedListPath}");

        Check("常量：新建菜单 CLSID 文本格式正确、能规范化成自身的大写形式",
            ContextMenuRegistryLayout.NewMenuClsid == "{D969A300-E7FF-11d0-A93B-00A0C90F2719}"
            && ContextMenuRegistryLayout.CanonicalClsid(ContextMenuRegistryLayout.NewMenuClsid)
               == "{D969A300-E7FF-11D0-A93B-00A0C90F2719}",
            ContextMenuRegistryLayout.NewMenuClsid);

        Check("CanonicalClsid：无花括号 / 小写 GUID → 补花括号并全部大写",
            ContextMenuRegistryLayout.CanonicalClsid("d969a300-e7ff-11d0-a93b-00a0c90f2719")
            == "{D969A300-E7FF-11D0-A93B-00A0C90F2719}",
            ContextMenuRegistryLayout.CanonicalClsid("d969a300-e7ff-11d0-a93b-00a0c90f2719"));

        Check("CanonicalClsid：前后空白 / 大小写混合 / 带花括号都能规范化，且幂等",
            ContextMenuRegistryLayout.CanonicalClsid("  {AbCdEf01-2345-6789-AbCd-Ef0123456789}  ")
            == "{ABCDEF01-2345-6789-ABCD-EF0123456789}"
            && ContextMenuRegistryLayout.CanonicalClsid(
                   ContextMenuRegistryLayout.CanonicalClsid("abcdef01-2345-6789-abcd-ef0123456789"))
               == "{ABCDEF01-2345-6789-ABCD-EF0123456789}");

        Check("CanonicalClsid：垃圾 / @资源串 / 空串 / null 一律返回空串（不能拿去写黑名单）",
            ContextMenuRegistryLayout.CanonicalClsid("not-a-guid") == ""
            && ContextMenuRegistryLayout.CanonicalClsid("@shell32.dll,-123") == ""
            && ContextMenuRegistryLayout.CanonicalClsid("") == ""
            && ContextMenuRegistryLayout.CanonicalClsid("   ") == ""
            && ContextMenuRegistryLayout.CanonicalClsid(null) == "");

        Check("IsValidClsid：合法 GUID（带 / 不带花括号）为真，@资源串与垃圾为假",
            ContextMenuRegistryLayout.IsValidClsid("{D969A300-E7FF-11d0-A93B-00A0C90F2719}")
            && ContextMenuRegistryLayout.IsValidClsid("d969a300-e7ff-11d0-a93b-00a0c90f2719")
            && ContextMenuRegistryLayout.IsValidClsid("  {d969a300-e7ff-11d0-a93b-00a0c90f2719}  ")
            && !ContextMenuRegistryLayout.IsValidClsid("@shell32.dll,-123")
            && !ContextMenuRegistryLayout.IsValidClsid("garbage")
            && !ContextMenuRegistryLayout.IsValidClsid("")
            && !ContextMenuRegistryLayout.IsValidClsid("   ")
            && !ContextMenuRegistryLayout.IsValidClsid(null));

        Check("CriticalClsids：含新建菜单 CLSID、非空、匹配不区分大小写",
            ContextMenuRegistryLayout.CriticalClsids.Count >= 1
            && ContextMenuRegistryLayout.CriticalClsids.Contains(ContextMenuRegistryLayout.NewMenuClsid)
            && ContextMenuRegistryLayout.CriticalClsids.Contains("{d969a300-e7ff-11d0-a93b-00a0c90f2719}"),
            string.Join(",", ContextMenuRegistryLayout.CriticalClsids));

        Check("IsCriticalVerbName：open / OPEN / ' open ' 为真；openwith / print / 空 / 空白 / null 为假",
            ContextMenuRegistryLayout.IsCriticalVerbName("open")
            && ContextMenuRegistryLayout.IsCriticalVerbName("OPEN")
            && ContextMenuRegistryLayout.IsCriticalVerbName(" open ")
            && !ContextMenuRegistryLayout.IsCriticalVerbName("openwith")
            && !ContextMenuRegistryLayout.IsCriticalVerbName("print")
            && !ContextMenuRegistryLayout.IsCriticalVerbName("")
            && !ContextMenuRegistryLayout.IsCriticalVerbName("   ")
            && !ContextMenuRegistryLayout.IsCriticalVerbName(null));

        var scopes = ContextMenuScopes.All;
        var scopeKeys = scopes.Select(s => s.Key).ToList();
        Check("扫描位置：覆盖所有文件 / 文件夹 / 文件夹空白处 / 驱动器 / 桌面背景（Key 都在）",
            scopeKeys.Contains("files") && scopeKeys.Contains("directory")
            && scopeKeys.Contains("directory-background") && scopeKeys.Contains("drive")
            && scopeKeys.Contains("desktop"),
            string.Join(",", scopeKeys));

        Check("扫描位置：Key 无重复、Label 无重复、每个位置至少有一种条目路径",
            scopeKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == scopeKeys.Count
            && scopes.Select(s => s.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() == scopes.Count
            && scopes.All(s => !string.IsNullOrWhiteSpace(s.VerbRelativePath)
                            || !string.IsNullOrWhiteSpace(s.HandlerRelativePath))
            && scopes.Count >= 5,
            string.Join(" / ", scopes.Select(s => s.Key + ":" + s.Label)));

        Check("扫描位置：所有相对路径都不带 Software\\Classes 前缀（由引擎统一拼）",
            scopes.All(s =>
                (s.VerbRelativePath ?? "").IndexOf("Software", StringComparison.OrdinalIgnoreCase) < 0
                && (s.HandlerRelativePath ?? "").IndexOf("Software", StringComparison.OrdinalIgnoreCase) < 0));

        // 条目展示属性（纯 record 计算属性，不碰注册表）
        var displayEntry = new ContextMenuEntry(
            "verb:*\\shell\\x", ContextMenuEntryKind.Verb, "测试项", "所有文件",
            @"HKCU\Software\Classes\*\shell\x", null, "cmd.exe", true,
            ContextMenuHideSource.ThisTool, true, "");
        Check("条目展示属性：类型 / 状态 / 按钮文案 / 来源文案与 IsHiddenByThisTool 自洽",
            displayEntry.KindLabel == "菜单命令"
            && displayEntry.StatusLabel == "已隐藏"
            && displayEntry.ToggleActionLabel == "恢复显示"
            && displayEntry.SourceLabel == "本工具隐藏"
            && displayEntry.IsHiddenByThisTool,
            $"{displayEntry.KindLabel} / {displayEntry.StatusLabel} / {displayEntry.SourceLabel}");

        Check("条目展示属性：扩展模块类型、显示中、其他程序隐藏各自的文案",
            new ContextMenuEntry("handler:{X}", ContextMenuEntryKind.ShellExtension, "扩展", "文件夹",
                "*\\shellex", "{X}", "", false, ContextMenuHideSource.None, true, "").KindLabel == "扩展模块"
            && !new ContextMenuEntry("handler:{X}", ContextMenuEntryKind.ShellExtension, "扩展", "文件夹",
                "*\\shellex", "{X}", "", false, ContextMenuHideSource.None, true, "").IsHiddenByThisTool
            && new ContextMenuEntry("verb:a", ContextMenuEntryKind.Verb, "隐藏项", "所有文件",
                "HKCU\\a", null, "", true, ContextMenuHideSource.OtherProgram, true, "").SourceLabel == "其他程序隐藏");

        Check("结果记录：Ok / Fail 的 Success 与消息原样保留；RestoreResult 有失败项就不算成功",
            ContextMenuOperationResult.Ok("好了").Success
            && ContextMenuOperationResult.Ok("好了").Message == "好了"
            && !ContextMenuOperationResult.Fail("坏了").Success
            && ContextMenuOperationResult.Fail("坏了").Message == "坏了"
            && new ContextMenuRestoreResult(2, Array.Empty<string>()).Success
            && !new ContextMenuRestoreResult(1, new[] { "失败项" }).Success
            && new ContextMenuRestoreResult(1, new[] { "失败项" }).RestoredCount == 1);

        // ================================================================
        // 2. 枚举：HKCU / HKLM 合并、名称回退、command、CLSID 合并、Id、排序
        // ================================================================

        // 2.1 空注册表：不抛、返回 0 条
        var emptyService = new ContextMenuService(new FakeContextMenuRegistry());
        IReadOnlyList<ContextMenuEntry> emptyEntries = Array.Empty<ContextMenuEntry>();
        var emptyThrew = false;
        try { emptyEntries = emptyService.Enumerate(); } catch { emptyThrew = true; }
        Check("枚举：空注册表不抛异常、返回 0 条", !emptyThrew && emptyEntries.Count == 0);

        // 2.2 HKCU / HKLM 合并：两个根各放一个动词，结果都出现
        var mergeRegistry = new FakeContextMenuRegistry();
        SeedVerb(mergeRegistry, RegistryRoot.CurrentUser, @"*\shell", "UserVerb", muiVerb: "用户菜单项");
        SeedVerb(mergeRegistry, RegistryRoot.LocalMachine, @"*\shell", "MachineVerb", muiVerb: "电脑菜单项");
        var merged = new ContextMenuService(mergeRegistry).Enumerate();
        Check("枚举：HKCU 与 HKLM 的动词合并成一份清单（两个都在）",
            merged.Any(e => e.Name == "用户菜单项") && merged.Any(e => e.Name == "电脑菜单项")
            && merged.Count == 2,
            string.Join(" / ", merged.Select(e => e.Name)));

        // 2.3 同名（大小写不同）HKCU 覆盖 HKLM
        var overrideRegistry = new FakeContextMenuRegistry();
        SeedVerb(overrideRegistry, RegistryRoot.LocalMachine, @"*\shell", "SomeTool", muiVerb: "HKLM 显示名");
        SeedVerb(overrideRegistry, RegistryRoot.CurrentUser, @"*\shell", "sometool", muiVerb: "HKCU 显示名");
        var overridden = new ContextMenuService(overrideRegistry).Enumerate();
        var overriddenEntry = overridden.FirstOrDefault(e => e.Id.EndsWith(@"\shell\sometool", StringComparison.OrdinalIgnoreCase));
        Check("枚举：同名子键（大小写不同）HKCU 覆盖 HKLM，只保留一行且名称取 HKCU",
            overridden.Count == 1
            && overriddenEntry is not null
            && overriddenEntry.Name == "HKCU 显示名"
            && overriddenEntry.RegistryLocation.StartsWith("HKCU\\", StringComparison.Ordinal),
            $"{overridden.Count} 行 / {overriddenEntry?.Name}");

        // 2.4 动词名称回退链：MUIVerb → 默认值 → 子键名
        var nameRegistry = new FakeContextMenuRegistry();
        SeedVerb(nameRegistry, RegistryRoot.LocalMachine, @"*\shell", "mui-test",
            muiVerb: "MUIVerb 名称", displayDefault: "默认值名称");
        SeedVerb(nameRegistry, RegistryRoot.LocalMachine, @"*\shell", "default-test",
            displayDefault: "默认值名称");
        SeedVerb(nameRegistry, RegistryRoot.LocalMachine, @"*\shell", "keyname-test");
        var named = new ContextMenuService(nameRegistry).Enumerate();
        Check("枚举：名称优先级 MUIVerb > 默认值 > 子键名（三种情况各一行）",
            named.Count == 3
            && named.Count(e => e.Name == "MUIVerb 名称") == 1
            && named.Count(e => e.Name == "默认值名称") == 1
            && named.Count(e => e.Name == "keyname-test") == 1,
            string.Join(" / ", named.Select(e => e.Name)));

        // 2.5 @ 资源串不算显示名，要回退
        var resourceRegistry = new FakeContextMenuRegistry();
        SeedVerb(resourceRegistry, RegistryRoot.LocalMachine, @"*\shell", "res-mui",
            muiVerb: "@shell32.dll,-123", displayDefault: "回退后的默认名称");
        SeedVerb(resourceRegistry, RegistryRoot.LocalMachine, @"*\shell", "res-all",
            muiVerb: "@shell32.dll,-123", displayDefault: "@shell32.dll,-456");
        var resourceNamed = new ContextMenuService(resourceRegistry).Enumerate();
        Check("枚举：MUIVerb 是 @资源串时回退到默认值；默认值也是资源串时回退到子键名",
            resourceNamed.Count(e => e.Name == "回退后的默认名称") == 1
            && resourceNamed.Count(e => e.Name == "res-all") == 1,
            string.Join(" / ", resourceNamed.Select(e => e.Name)));

        // 2.6 command 进 Detail，HKCU 优先、缺了回退 HKLM
        var detailRegistry = new FakeContextMenuRegistry();
        SeedVerb(detailRegistry, RegistryRoot.LocalMachine, @"*\shell", "cmd-both",
            muiVerb: "命令项", command: @"C:\HKLM.exe %1");
        SeedVerb(detailRegistry, RegistryRoot.CurrentUser, @"*\shell", "cmd-both",
            muiVerb: "命令项", command: @"C:\HKCU.exe %1");
        SeedVerb(detailRegistry, RegistryRoot.LocalMachine, @"*\shell", "cmd-missing",
            muiVerb: "只有 HKLM 命令", command: "hklm-only.exe");
        var detailed = new ContextMenuService(detailRegistry).Enumerate();
        Check("枚举：command 默认值进 Detail，HKCU 优先；缺 HKCU 命令时回退 HKLM",
            detailed.FirstOrDefault(e => e.Name == "命令项")?.Detail == @"C:\HKCU.exe %1"
            && detailed.FirstOrDefault(e => e.Name == "只有 HKLM 命令")?.Detail == "hklm-only.exe",
            detailed.FirstOrDefault(e => e.Name == "命令项")?.Detail ?? "(null)");

        // 2.7 外壳扩展：多位置同 CLSID 合并、名称 / Detail / 位置拼接
        var handlerRegistry = new FakeContextMenuRegistry();
        const string handlerClsid = "{11111111-2222-3333-4444-555555555555}";
        SeedHandler(handlerRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "WinRAR",
            handlerClsid.ToLowerInvariant(), displayName: "WinRAR 压缩", dllPath: @"C:\Program Files\WinRAR\ShellExt.dll");
        SeedHandler(handlerRegistry, RegistryRoot.LocalMachine, @"Directory\shellex\ContextMenuHandlers", "WinRAR",
            handlerClsid, displayName: "WinRAR 压缩", dllPath: @"C:\Program Files\WinRAR\ShellExt.dll");
        var handlerEntries = new ContextMenuService(handlerRegistry).Enumerate();
        var handlerEntry = handlerEntries.FirstOrDefault(e => e.Kind == ContextMenuEntryKind.ShellExtension);
        Check("枚举：同一 CLSID 在多个位置出现合并为一行，ScopeLabel 用「、」连接",
            handlerEntries.Count == 1
            && handlerEntry is not null
            && handlerEntry.ScopeLabel == "所有文件、文件夹",
            handlerEntry?.ScopeLabel ?? "(null)");

        Check("枚举：扩展的 RegistryLocation 用「；」连接、含具体扩展子键名、不带 HKCU/HKLM 前缀",
            handlerEntry is not null
            && handlerEntry.RegistryLocation
               == @"*\shellex\ContextMenuHandlers\WinRAR；Directory\shellex\ContextMenuHandlers\WinRAR",
            handlerEntry?.RegistryLocation ?? "(null)");

        Check("枚举：扩展名称优先取 CLSID 键默认值，Detail 取 InprocServer32 的 DLL 路径",
            handlerEntry?.Name == "WinRAR 压缩"
            && handlerEntry?.Detail == @"C:\Program Files\WinRAR\ShellExt.dll"
            && handlerEntry?.Clsid == handlerClsid,
            $"{handlerEntry?.Name} / {handlerEntry?.Detail}");

        // 2.8 无效 CLSID 跳过 / HKCU 空值回退 HKLM
        var badClsidRegistry = new FakeContextMenuRegistry();
        SeedHandler(badClsidRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "BadResource",
            "@shell32.dll,-123");
        SeedHandler(badClsidRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "BadGarbage",
            "not-a-guid");
        SeedHandler(badClsidRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "BadEmpty", "");
        SeedHandler(badClsidRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "Fallback",
            handlerClsid);
        // HKCU 只建空键（没有默认值）：应当回退到 HKLM 读 CLSID
        badClsidRegistry.SeedKey(RegistryRoot.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\Fallback");
        var badClsidEntries = new ContextMenuService(badClsidRegistry).Enumerate();
        Check("枚举：默认值不是合法 CLSID（@资源串 / 垃圾 / 空）的扩展跳过，不进清单",
            badClsidEntries.Count == 1,
            string.Join(" / ", badClsidEntries.Select(e => e.Name)));

        Check("枚举：HKCU 只有空键、HKLM 有合法 CLSID 时回退到 HKLM（仍只 1 行）",
            badClsidEntries.Count(e => e.Clsid == handlerClsid) == 1
            && badClsidEntries[0].Name == "Fallback");

        // HKCU 写了非法 CLSID 时不回退 HKLM（视为「这个键自己坏了」，跳过）
        var badOverrideRegistry = new FakeContextMenuRegistry();
        SeedHandler(badOverrideRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "BrokenOverride",
            handlerClsid);
        badOverrideRegistry.Seed(RegistryRoot.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\BrokenOverride", "", "@broken.dll,-1");
        Check("枚举：HKCU 覆盖键写了非法 CLSID 时跳过（不回退 HKLM，避免误认）",
            new ContextMenuService(badOverrideRegistry).Enumerate().Count == 0);

        // 2.9 Id 规范 + 唯一 + 排序
        var idRegistry = new FakeContextMenuRegistry();
        SeedVerb(idRegistry, RegistryRoot.LocalMachine, @"*\shell", "BaiduNetdisk",
            muiVerb: "上传到百度网盘", command: @"C:\Baidu\Cloud.exe %1");
        SeedVerb(idRegistry, RegistryRoot.LocalMachine, @"Directory\shell", "BaiduNetdisk",
            muiVerb: "上传到百度网盘");
        SeedHandler(idRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "Ext",
            handlerClsid, displayName: "扩展模块");
        var idEntries = new ContextMenuService(idRegistry).Enumerate();
        var verbEntry = idEntries.FirstOrDefault(e => e.Kind == ContextMenuEntryKind.Verb && e.RegistryLocation.Contains(@"*\shell"));
        var handlerOnly = idEntries.FirstOrDefault(e => e.Kind == ContextMenuEntryKind.ShellExtension);
        Check("Id 规范：动词 = verb:+小写相对路径；扩展 = handler:+规范大写 CLSID",
            verbEntry?.Id == @"verb:*\shell\baidunetdisk"
            && handlerOnly?.Id == "handler:" + handlerClsid,
            $"{verbEntry?.Id} / {handlerOnly?.Id}");

        Check("Id 唯一：每个条目的 Id 都不重复（列表不会出现两行同 Id）",
            idEntries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() == idEntries.Count,
            string.Join(" | ", idEntries.Select(e => e.Id)));

        var sortedOnce = new ContextMenuService(idRegistry).Enumerate();
        var sortedTwice = new ContextMenuService(idRegistry).Enumerate();
        Check("枚举：排序稳定 —— 按名称忽略大小写升序，两次枚举顺序完全一致",
            sortedOnce.Select(e => e.Name)
                .SequenceEqual(sortedOnce.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => e.Name))
            && sortedOnce.Select(e => e.Id).SequenceEqual(sortedTwice.Select(e => e.Id)),
            string.Join(" / ", sortedOnce.Select(e => e.Name)));

        // 同名按 Id 兜底
        var tieRegistry = new FakeContextMenuRegistry();
        SeedVerb(tieRegistry, RegistryRoot.LocalMachine, @"*\shell", "zzz", muiVerb: "同名项");
        SeedVerb(tieRegistry, RegistryRoot.LocalMachine, @"*\shell", "aaa", muiVerb: "同名项");
        var tieEntries = new ContextMenuService(tieRegistry).Enumerate();
        Check("枚举：同名条目按 Id 的序数顺序兜底（aaa 在 zzz 前）",
            tieEntries.Count == 2
            && tieEntries[0].Id == @"verb:*\shell\aaa"
            && tieEntries[1].Id == @"verb:*\shell\zzz",
            string.Join(" / ", tieEntries.Select(e => e.Id)));

        // 2.10 动词 RegistryLocation / ScopeLabel
        var locationRegistry = new FakeContextMenuRegistry();
        SeedVerb(locationRegistry, RegistryRoot.LocalMachine, @"Directory\shell", "OnlyMachine",
            muiVerb: "只在电脑级");
        var locationEntries = new ContextMenuService(locationRegistry).Enumerate();
        Check("枚举：只在 HKLM 的动词，生效位置写 HKLM 前缀、ScopeLabel 取该位置中文名",
            locationEntries.Count == 1
            && locationEntries[0].RegistryLocation == @"HKLM\Software\Classes\Directory\shell\OnlyMachine"
            && locationEntries[0].ScopeLabel == "文件夹",
            $"{locationEntries.FirstOrDefault()?.RegistryLocation} / {locationEntries.FirstOrDefault()?.ScopeLabel}");

        // ================================================================
        // 3. 隐藏来源：本工具 / 其他程序 / 电脑级 / 关键项
        // ================================================================
        var hiddenRegistry = new FakeContextMenuRegistry();
        SeedVerb(hiddenRegistry, RegistryRoot.LocalMachine, @"*\shell", "MineHidden",
            muiVerb: "我隐藏的", programmaticAccessOnly: ContextMenuRegistryLayout.Marker);
        hiddenRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\MineHidden",
            ContextMenuRegistryLayout.ProgrammaticAccessOnly, ContextMenuRegistryLayout.Marker);
        SeedVerb(hiddenRegistry, RegistryRoot.LocalMachine, @"*\shell", "OtherHidden",
            muiVerb: "别的程序隐藏的");
        hiddenRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\OtherHidden",
            ContextMenuRegistryLayout.ProgrammaticAccessOnly, "SomeOtherProgram");
        SeedVerb(hiddenRegistry, RegistryRoot.LocalMachine, @"*\shell", "EmptyFlag",
            muiVerb: "空数据隐藏的");
        hiddenRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\EmptyFlag",
            ContextMenuRegistryLayout.ProgrammaticAccessOnly, "");
        SeedVerb(hiddenRegistry, RegistryRoot.LocalMachine, @"*\shell", "MachineHidden",
            muiVerb: "电脑级隐藏的", programmaticAccessOnly: "1");
        var hiddenEntries = new ContextMenuService(hiddenRegistry).Enumerate();

        var mineHidden = hiddenEntries.FirstOrDefault(e => e.Name == "我隐藏的");
        Check("隐藏来源：HKCU 的 ProgrammaticAccessOnly 数据 == Marker → 本工具隐藏、可操作",
            mineHidden is not null && mineHidden.IsHidden
            && mineHidden.HideSource == ContextMenuHideSource.ThisTool
            && mineHidden.CanToggle && mineHidden.IsHiddenByThisTool,
            $"{mineHidden?.HideSource} / CanToggle={mineHidden?.CanToggle}");

        var otherHidden = hiddenEntries.FirstOrDefault(e => e.Name == "别的程序隐藏的");
        Check("隐藏来源：HKCU 的 ProgrammaticAccessOnly 是其他数据 → 其他程序隐藏、仍可操作",
            otherHidden is not null && otherHidden.IsHidden
            && otherHidden.HideSource == ContextMenuHideSource.OtherProgram
            && !otherHidden.IsHiddenByThisTool
            && otherHidden.CanToggle,
            $"{otherHidden?.HideSource} / CanToggle={otherHidden?.CanToggle}");

        var emptyFlagHidden = hiddenEntries.FirstOrDefault(e => e.Name == "空数据隐藏的");
        Check("隐藏来源：HKCU 的 ProgrammaticAccessOnly 数据为空串也算「值存在」→ 其他程序隐藏",
            emptyFlagHidden is not null && emptyFlagHidden.IsHidden
            && emptyFlagHidden.HideSource == ContextMenuHideSource.OtherProgram,
            emptyFlagHidden?.HideSource.ToString() ?? "(null)");

        var machineHidden = hiddenEntries.FirstOrDefault(e => e.Name == "电脑级隐藏的");
        Check("隐藏来源：只有 HKLM 电脑级隐藏 → 其他程序隐藏、CanToggle=false、说明提到电脑级",
            machineHidden is not null && machineHidden.IsHidden
            && machineHidden.HideSource == ContextMenuHideSource.OtherProgram
            && !machineHidden.CanToggle
            && machineHidden.StatusNote.Contains("电脑级", StringComparison.Ordinal),
            $"{machineHidden?.HideSource} / {machineHidden?.StatusNote}");

        // open 动词
        var openRegistry = new FakeContextMenuRegistry();
        SeedVerb(openRegistry, RegistryRoot.LocalMachine, @"*\shell", "open", muiVerb: "打开");
        var openEntry = new ContextMenuService(openRegistry).Enumerate().FirstOrDefault();
        Check("关键项：open 动词 CanToggle=false，说明提到主操作（隐藏后双击打不开）",
            openEntry is not null && !openEntry.CanToggle
            && openEntry.StatusNote.Contains("open", StringComparison.Ordinal),
            openEntry?.StatusNote ?? "(null)");

        // 外壳扩展的隐藏来源
        var blockedRegistry = new FakeContextMenuRegistry();
        SeedHandler(blockedRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "MineExt",
            handlerClsid, displayName: "我隐藏的扩展");
        SeedHandler(blockedRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "OtherExt",
            "{22222222-3333-4444-5555-666666666666}", displayName: "别人隐藏的扩展");
        blockedRegistry.Seed(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath,
            handlerClsid, ContextMenuRegistryLayout.Marker);
        blockedRegistry.Seed(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath,
            "{22222222-3333-4444-5555-666666666666}", "OtherProgram");
        var blockedEntries = new ContextMenuService(blockedRegistry).Enumerate();
        Check("隐藏来源：Blocked 值数据 == Marker → 扩展是本工具隐藏、可操作",
            blockedEntries.FirstOrDefault(e => e.Name == "我隐藏的扩展") is { IsHidden: true, CanToggle: true } mineExt
            && mineExt.HideSource == ContextMenuHideSource.ThisTool,
            blockedEntries.FirstOrDefault(e => e.Name == "我隐藏的扩展")?.HideSource.ToString() ?? "(null)");

        Check("隐藏来源：Blocked 值数据是其他程序写的 → 其他程序隐藏、不是本工具隐藏",
            blockedEntries.FirstOrDefault(e => e.Name == "别人隐藏的扩展") is { IsHidden: true } otherExt
            && otherExt.HideSource == ContextMenuHideSource.OtherProgram
            && !otherExt.IsHiddenByThisTool,
            blockedEntries.FirstOrDefault(e => e.Name == "别人隐藏的扩展")?.HideSource.ToString() ?? "(null)");

        // Blocked 值名大小写 / 无花括号写法容错
        var caseBlockedRegistry = new FakeContextMenuRegistry();
        SeedHandler(caseBlockedRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "CaseExt",
            handlerClsid, displayName: "写法不同的扩展");
        caseBlockedRegistry.Seed(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath,
            handlerClsid.ToLowerInvariant().Trim('{', '}'), ContextMenuRegistryLayout.Marker);
        var caseBlockedEntry = new ContextMenuService(caseBlockedRegistry).Enumerate().FirstOrDefault();
        Check("隐藏来源：Blocked 值名大小写 / 无花括号写法也能识别为本工具隐藏（规范化匹配）",
            caseBlockedEntry is not null && caseBlockedEntry.IsHidden
            && caseBlockedEntry.HideSource == ContextMenuHideSource.ThisTool,
            caseBlockedEntry?.HideSource.ToString() ?? "(null)");

        // ================================================================
        // 4. SetHidden：只写 HKCU、只删自己、失败不崩
        // ================================================================

        // 4.1 隐藏动词 → 写 HKCU 覆盖值，重新枚举为本工具隐藏
        var hideVerbRegistry = new FakeContextMenuRegistry();
        SeedVerb(hideVerbRegistry, RegistryRoot.LocalMachine, @"*\shell", "YunPan",
            muiVerb: "上传到百度网盘", command: @"C:\Baidu\Cloud.exe %1");
        var hideVerbService = new ContextMenuService(hideVerbRegistry);
        var yunPan = hideVerbService.Enumerate().FirstOrDefault(e => e.Name == "上传到百度网盘");
        var hideVerbResult = hideVerbService.SetHidden(yunPan!, hidden: true);
        var afterHideVerb = hideVerbService.Enumerate().FirstOrDefault(e => e.Name == "上传到百度网盘");
        Check("SetHidden：隐藏动词只写 HKCU 的 ProgrammaticAccessOnly = Marker（原 HKLM 键不动）",
            hideVerbResult.Success
            && hideVerbRegistry.GetValue(
                   new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\YunPan"),
                   ContextMenuRegistryLayout.ProgrammaticAccessOnly) == ContextMenuRegistryLayout.Marker
            && hideVerbRegistry.GetValue(
                   new RegistryPath(RegistryRoot.LocalMachine, @"Software\Classes\*\shell\YunPan"),
                   ContextMenuRegistryLayout.ProgrammaticAccessOnly) is null,
            hideVerbResult.Message);

        Check("SetHidden：隐藏后重新枚举变为「已隐藏 / 本工具隐藏」，Id 不变、成功消息带条目名",
            afterHideVerb is { IsHidden: true }
            && afterHideVerb.HideSource == ContextMenuHideSource.ThisTool
            && afterHideVerb.Id == yunPan?.Id
            && hideVerbResult.Message.Contains("上传到百度网盘", StringComparison.Ordinal),
            $"{afterHideVerb?.StatusLabel} / {hideVerbResult.Message}");

        // 4.2 恢复动词 → 值消失、空覆盖键被删、重新枚举恢复显示
        var restoreVerbResult = hideVerbService.SetHidden(afterHideVerb!, hidden: false);
        var afterRestoreVerb = hideVerbService.Enumerate().FirstOrDefault(e => e.Name == "上传到百度网盘");
        Check("SetHidden：恢复动词删掉 HKCU 覆盖值，并清掉变空的覆盖键（只删空键）",
            restoreVerbResult.Success
            && hideVerbRegistry.GetValue(
                   new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\YunPan"),
                   ContextMenuRegistryLayout.ProgrammaticAccessOnly) is null
            && !hideVerbRegistry.KeyExists(
                   new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\YunPan")),
            restoreVerbResult.Message);

        Check("SetHidden：恢复后重新枚举是「显示中 / 无隐藏来源」（HKLM 原菜单仍能扫到）",
            afterRestoreVerb is { IsHidden: false }
            && afterRestoreVerb.HideSource == ContextMenuHideSource.None
            && afterRestoreVerb.CanToggle
            && afterRestoreVerb.Id == yunPan?.Id,
            $"{afterRestoreVerb?.StatusLabel} / {afterRestoreVerb?.SourceLabel}");

        // 4.3 恢复时不动「原本就有别的值」的 HKCU 键
        var keepKeyRegistry = new FakeContextMenuRegistry();
        SeedVerb(keepKeyRegistry, RegistryRoot.LocalMachine, @"*\shell", "KeepKey", muiVerb: "保留键");
        keepKeyRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\KeepKey",
            ContextMenuRegistryLayout.ProgrammaticAccessOnly, ContextMenuRegistryLayout.Marker);
        keepKeyRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\KeepKey", "Icon", "shell32.dll,1");
        var keepKeyService = new ContextMenuService(keepKeyRegistry);
        var keepKeyEntry = keepKeyService.Enumerate().FirstOrDefault(e => e.Name == "保留键");
        var keepKeyResult = keepKeyService.SetHidden(keepKeyEntry!, hidden: false);
        Check("SetHidden：恢复时 HKCU 键还有别的值 → 只删自己的覆盖值、键和别的值都保留",
            keepKeyResult.Success
            && keepKeyRegistry.GetValue(
                   new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\KeepKey"),
                   ContextMenuRegistryLayout.ProgrammaticAccessOnly) is null
            && keepKeyRegistry.GetValue(
                   new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\KeepKey"),
                   "Icon") == "shell32.dll,1"
            && keepKeyRegistry.KeyExists(
                   new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\KeepKey")),
            keepKeyResult.Message);

        // 4.4 隐藏 / 恢复外壳扩展
        var extendRegistry = new FakeContextMenuRegistry();
        SeedHandler(extendRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "ToggleExt",
            handlerClsid, displayName: "可切换扩展");
        var extendService = new ContextMenuService(extendRegistry);
        var toggleExt = extendService.Enumerate().FirstOrDefault(e => e.Name == "可切换扩展");
        var hideExtResult = extendService.SetHidden(toggleExt!, hidden: true);
        var blockedKey = new RegistryPath(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath);
        Check("SetHidden：隐藏扩展在 HKCU 的 Blocked 写「规范大写 CLSID = Marker」",
            hideExtResult.Success
            && extendRegistry.GetValue(blockedKey, handlerClsid) == ContextMenuRegistryLayout.Marker,
            hideExtResult.Message);

        var hiddenExtEntry = extendService.Enumerate().FirstOrDefault(e => e.Name == "可切换扩展");
        Check("SetHidden：扩展隐藏后重新枚举为「已隐藏 / 本工具隐藏」",
            hiddenExtEntry is { IsHidden: true }
            && hiddenExtEntry.HideSource == ContextMenuHideSource.ThisTool,
            hiddenExtEntry?.HideSource.ToString() ?? "(null)");

        var showExtResult = extendService.SetHidden(hiddenExtEntry!, hidden: false);
        var shownExtEntry = extendService.Enumerate().FirstOrDefault(e => e.Name == "可切换扩展");
        Check("SetHidden：恢复扩展删掉 Blocked 值、Blocked 空键被清掉、重新枚举恢复显示",
            showExtResult.Success
            && extendRegistry.GetValue(blockedKey, handlerClsid) is null
            && !extendRegistry.KeyExists(blockedKey)
            && shownExtEntry is { IsHidden: false } && shownExtEntry.HideSource == ContextMenuHideSource.None,
            showExtResult.Message);

        // 4.5 关键项不可操作：拒绝且不写任何东西
        var criticalRegistry = new FakeContextMenuRegistry();
        SeedHandler(criticalRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "New",
            ContextMenuRegistryLayout.NewMenuClsid);
        SeedVerb(criticalRegistry, RegistryRoot.LocalMachine, @"*\shell", "open", muiVerb: "打开");
        var criticalService = new ContextMenuService(criticalRegistry);
        var criticalEntries = criticalService.Enumerate();
        var newMenuEntry = criticalEntries.FirstOrDefault(
            e => e.Clsid == ContextMenuRegistryLayout.CanonicalClsid(ContextMenuRegistryLayout.NewMenuClsid));
        var criticalOpenEntry = criticalEntries.FirstOrDefault(e => e.Name == "打开");
        Check("关键项：新建菜单扩展 CanToggle=false，说明点明「新建」/ 系统关键菜单",
            newMenuEntry is not null && !newMenuEntry.CanToggle
            && newMenuEntry.StatusNote.Contains("新建", StringComparison.Ordinal),
            newMenuEntry?.StatusNote ?? "(null)");

        var hideCriticalExt = criticalService.SetHidden(newMenuEntry!, hidden: true);
        Check("关键项：对不可操作的「新建」扩展调用 SetHidden 返回 Fail、不抛异常、也不写 Blocked",
            !hideCriticalExt.Success
            && hideCriticalExt.Message.Trim().Length > 0
            && criticalRegistry.GetValues(
                   new RegistryPath(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath)).Count == 0,
            hideCriticalExt.Message);

        var hideCriticalOpen = criticalService.SetHidden(criticalOpenEntry!, hidden: true);
        Check("关键项：对 open 动词调用 SetHidden 返回 Fail、不抛异常、不写覆盖值",
            !hideCriticalOpen.Success
            && criticalRegistry.GetValue(
                   new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\open"),
                   ContextMenuRegistryLayout.ProgrammaticAccessOnly) is null,
            hideCriticalOpen.Message);

        // 空条目 / 写入抛异常 → Fail 不崩
        Check("SetHidden(null) → 返回 Fail，不抛异常",
            !criticalService.SetHidden(null!, hidden: true).Success);

        var failingService = new ContextMenuService(new FailOnWriteRegistry(extendRegistry));
        var failRegistryEntry = new ContextMenuService(extendRegistry).Enumerate()
            .FirstOrDefault(e => e.Name == "可切换扩展");
        ContextMenuOperationResult failWriteResult;
        var failWriteThrew = false;
        try { failWriteResult = failingService.SetHidden(failRegistryEntry!, hidden: true); }
        catch { failWriteResult = ContextMenuOperationResult.Ok("不该走到这里"); failWriteThrew = true; }
        Check("SetHidden：注册表写入抛异常时返回 Fail（中文原因）而不是把自检炸掉",
            !failWriteThrew
            && !failWriteResult.Success
            && failWriteResult.Message.Contains("失败", StringComparison.Ordinal)
            && extendRegistry.GetValue(blockedKey, handlerClsid) is null,
            failWriteResult.Message);

        // 枚举遇到整体坏掉的注册表：不抛、返回空
        var brokenService = new ContextMenuService(new ThrowingRegistry());
        IReadOnlyList<ContextMenuEntry> brokenEntries = Array.Empty<ContextMenuEntry>();
        var brokenThrew = false;
        try { brokenEntries = brokenService.Enumerate(); } catch { brokenThrew = true; }
        Check("枚举：注册表读写整体抛异常时 Enumerate 不崩、返回空清单（单键坏了不拖垮整个扫描）",
            !brokenThrew && brokenEntries.Count == 0);

        // ================================================================
        // 5. RestoreAllHidden：只恢复本工具隐藏的
        // ================================================================
        var restoreAllRegistry = new FakeContextMenuRegistry();
        SeedVerb(restoreAllRegistry, RegistryRoot.LocalMachine, @"*\shell", "MineA",
            muiVerb: "本工具隐藏的A");
        restoreAllRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\MineA",
            ContextMenuRegistryLayout.ProgrammaticAccessOnly, ContextMenuRegistryLayout.Marker);
        SeedVerb(restoreAllRegistry, RegistryRoot.LocalMachine, @"*\shell", "OtherB",
            muiVerb: "别人隐藏的B");
        restoreAllRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\OtherB",
            ContextMenuRegistryLayout.ProgrammaticAccessOnly, "OtherProgram");
        SeedHandler(restoreAllRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "MineExt",
            handlerClsid, displayName: "本工具隐藏的扩展");
        SeedHandler(restoreAllRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "OtherExt",
            "{33333333-4444-5555-6666-777777777777}", displayName: "别人隐藏的扩展");
        restoreAllRegistry.Seed(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath,
            handlerClsid, ContextMenuRegistryLayout.Marker);
        restoreAllRegistry.Seed(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath,
            "{33333333-4444-5555-6666-777777777777}", "OtherProgram");
        var restoreAllService = new ContextMenuService(restoreAllRegistry);
        var restoreAllResult = restoreAllService.RestoreAllHidden();
        Check("RestoreAllHidden：只恢复本工具隐藏的（1 个动词 + 1 个扩展），共 2 条、无失败",
            restoreAllResult.RestoredCount == 2
            && restoreAllResult.Success
            && restoreAllResult.Failures.Count == 0,
            $"{restoreAllResult.RestoredCount} / 失败 {restoreAllResult.Failures.Count}");

        Check("RestoreAllHidden：别家写的隐藏值原样保留（HKCU 覆盖值与 Blocked 非 Marker 值都在）",
            restoreAllRegistry.GetValue(
                new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\OtherB"),
                ContextMenuRegistryLayout.ProgrammaticAccessOnly) == "OtherProgram"
            && restoreAllRegistry.GetValue(
                new RegistryPath(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath),
                "{33333333-4444-5555-6666-777777777777}") == "OtherProgram"
            && restoreAllRegistry.GetValue(
                new RegistryPath(RegistryRoot.CurrentUser, ContextMenuRegistryLayout.BlockedListPath),
                handlerClsid) is null,
            "其余隐藏值保留");

        var afterRestoreAll = restoreAllService.Enumerate();
        Check("RestoreAllHidden：恢复后重新枚举，本工具隐藏的清空、别人隐藏的仍是「其他程序隐藏」",
            afterRestoreAll.Count(e => e.IsHiddenByThisTool) == 0
            && afterRestoreAll.Count(e => e.IsHidden) == 2
            && afterRestoreAll.Where(e => e.IsHidden).All(e => e.HideSource == ContextMenuHideSource.OtherProgram),
            string.Join(" / ", afterRestoreAll.Where(e => e.IsHidden).Select(e => e.Name)));

        Check("RestoreAllHidden：空注册表返回 0 条恢复、无失败、不抛",
            new ContextMenuService(new FakeContextMenuRegistry()).RestoreAllHidden() is { RestoredCount: 0, Success: true });

        // ================================================================
        // 6. ContextMenuFilter：关键词 / 位置 / 类型 / 只看已隐藏
        // ================================================================
        var filterRegistry = new FakeContextMenuRegistry();
        SeedVerb(filterRegistry, RegistryRoot.LocalMachine, @"*\shell", "AlphaTool",
            muiVerb: "云盘上传", command: @"C:\Cloud\cloud.exe %1");
        SeedVerb(filterRegistry, RegistryRoot.LocalMachine, @"*\shell", "BetaTool",
            muiVerb: "WinRAR 压缩");
        filterRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\*\shell\BetaTool",
            ContextMenuRegistryLayout.ProgrammaticAccessOnly, ContextMenuRegistryLayout.Marker);
        SeedVerb(filterRegistry, RegistryRoot.LocalMachine, @"Directory\shell", "DirOnly",
            muiVerb: "文件夹专有项");
        SeedHandler(filterRegistry, RegistryRoot.LocalMachine, @"*\shellex\ContextMenuHandlers", "Ext",
            handlerClsid, displayName: "扩展模块");
        var filterEntries = new ContextMenuService(filterRegistry).Enumerate();
        var all = filterEntries;

        Check("筛选：关键词命中名称、忽略大小写（「winrar」→ WinRAR 压缩）",
            ContextMenuFilter.Apply(all, "winrar", null, null, false).Count == 1
            && ContextMenuFilter.Apply(all, "winrar", null, null, false)[0].Name == "WinRAR 压缩");

        Check("筛选：关键词命中注册表路径（HTML 无关，词在路径里也能搜到）",
            ContextMenuFilter.Apply(all, "alphatool", null, null, false).Count == 1
            && ContextMenuFilter.Apply(all, "Directory\\shell\\DirOnly", null, null, false).Count == 1,
            ContextMenuFilter.Apply(all, "alphatool", null, null, false).FirstOrDefault()?.RegistryLocation ?? "(null)");

        Check("筛选：关键词命中 Detail（命令行 / DLL 路径）",
            ContextMenuFilter.Apply(all, "cloud.exe", null, null, false).Count == 1
            && ContextMenuFilter.Apply(all, "cloud.exe", null, null, false)[0].Name == "云盘上传");

        Check("筛选：关键词也命中状态文案（搜「已隐藏」→ 只剩隐藏项）",
            ContextMenuFilter.Apply(all, "已隐藏", null, null, false).Count == all.Count(e => e.IsHidden)
            && ContextMenuFilter.Apply(all, "已隐藏", null, null, false).All(e => e.IsHidden),
            $"{all.Count(e => e.IsHidden)} 条");

        Check("筛选：位置筛选只保留该位置（「文件夹」→ 只有文件夹位置的动词；多位置合并行不算）",
            ContextMenuFilter.Apply(all, null, "文件夹", null, false).Count == 1
            && ContextMenuFilter.Apply(all, null, "文件夹", null, false)[0].Name == "文件夹专有项");

        Check("筛选：类型筛选（菜单命令 / 扩展模块）互斥且数量对得上",
            ContextMenuFilter.Apply(all, null, null, ContextMenuEntryKind.Verb, false).All(e => e.Kind == ContextMenuEntryKind.Verb)
            && ContextMenuFilter.Apply(all, null, null, ContextMenuEntryKind.ShellExtension, false).Count == 1
            && ContextMenuFilter.Apply(all, null, null, null, false).Count == all.Count);

        Check("筛选：只看已隐藏只返回 IsHidden 的条目",
            ContextMenuFilter.Apply(all, null, null, null, true).Count == all.Count(e => e.IsHidden)
            && ContextMenuFilter.Apply(all, null, null, null, true).All(e => e.IsHidden));

        Check("筛选：「全部」/ 空串 / null / 纯空白的位置都表示不限",
            ContextMenuFilter.Apply(all, null, "全部", null, false).Count == all.Count
            && ContextMenuFilter.Apply(all, null, "", null, false).Count == all.Count
            && ContextMenuFilter.Apply(all, null, "   ", null, false).Count == all.Count
            && ContextMenuFilter.Apply(all, "   ", null, null, false).Count == all.Count);

        Check("筛选：null 集合 / 空集合返回空、不崩；搜不到返回 0 条",
            ContextMenuFilter.Apply(null, "x", null, null, false).Count == 0
            && ContextMenuFilter.Apply(Array.Empty<ContextMenuEntry>(), null, null, null, true).Count == 0
            && ContextMenuFilter.Apply(all, "不存在的关键词", null, null, false).Count == 0);

        Check("筛选：组合条件取交集（位置 + 类型 + 关键词 + 只看已隐藏）",
            ContextMenuFilter.Apply(all, "winrar", "所有文件", ContextMenuEntryKind.Verb, true).Count == 1
            && ContextMenuFilter.Apply(all, "winrar", "文件夹", ContextMenuEntryKind.Verb, true).Count == 0);

        // ================================================================
        // 7. 假实现自身（排序 / 删除语义）自证：假注册表不可靠，上面所有断言都白搭
        // ================================================================
        var selfRegistry = new FakeContextMenuRegistry();
        selfRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\Test\A", "v", "1");
        selfRegistry.Seed(RegistryRoot.CurrentUser, @"Software\Classes\Test\A\Child", "v", "2");
        Check("假注册表：KeyExists 认父键 / 子键，GetSubKeyNames 只返回直接子键",
            selfRegistry.KeyExists(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\Test"))
            && selfRegistry.GetSubKeyNames(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\Test"))
                .SequenceEqual(new[] { "A" })
            && selfRegistry.GetSubKeyNames(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\Test\A"))
                .SequenceEqual(new[] { "Child" }),
            "Test → A → Child");

        selfRegistry.DeleteKey(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\Test\A"));
        Check("假注册表：DeleteKey 删整棵子树（键与子键都不再存在）",
            !selfRegistry.KeyExists(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\Test\A"))
            && !selfRegistry.KeyExists(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\Test\A\Child")));

        selfRegistry.SetValue(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\New\Deep"), "x", "y");
        Check("假注册表：SetValue 自动补父键（父键 KeyExists 为真）、DeleteValue 不存在时静默",
            selfRegistry.KeyExists(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\New"))
            && selfRegistry.KeyExists(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\New\Deep"))
            && selfRegistry.GetValue(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\New\Deep"), "x") == "y");

        selfRegistry.DeleteValue(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\不存在\值"), "whatever");
        selfRegistry.DeleteKey(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\不存在"));
        Check("假注册表：删除不存在的值 / 键静默忽略（和真实注册表语义一致）",
            !selfRegistry.KeyExists(new RegistryPath(RegistryRoot.CurrentUser, @"Software\Classes\不存在")));
    }

    // ================================================================
    // 辅助：造数据（全部只写内存假注册表）
    // ================================================================

    /// <summary>往假注册表里种一个「普通菜单动词」（可选 MUIVerb / 默认值 / command / 隐藏标志）。</summary>
    private static void SeedVerb(
        FakeContextMenuRegistry registry,
        RegistryRoot root,
        string scopeVerbPath,
        string verbName,
        string? muiVerb = null,
        string? displayDefault = null,
        string? command = null,
        string? programmaticAccessOnly = null)
    {
        var path = @"Software\Classes\" + scopeVerbPath + "\\" + verbName;
        registry.SeedKey(root, path);
        if (muiVerb is not null) registry.Seed(root, path, "MUIVerb", muiVerb);
        if (displayDefault is not null) registry.Seed(root, path, "", displayDefault);
        if (command is not null) registry.Seed(root, path + @"\command", "", command);
        if (programmaticAccessOnly is not null)
            registry.Seed(root, path, ContextMenuRegistryLayout.ProgrammaticAccessOnly, programmaticAccessOnly);
    }

    /// <summary>往假注册表里种一个「COM 外壳扩展」（默认值是 CLSID；可选 CLSID 键显示名与 DLL 路径）。</summary>
    private static void SeedHandler(
        FakeContextMenuRegistry registry,
        RegistryRoot root,
        string scopeHandlerPath,
        string handlerName,
        string rawClsid,
        string? displayName = null,
        string? dllPath = null)
    {
        var path = @"Software\Classes\" + scopeHandlerPath + "\\" + handlerName;
        registry.Seed(root, path, "", rawClsid);

        var canonical = ContextMenuRegistryLayout.CanonicalClsid(rawClsid);
        if (canonical.Length == 0) return; // 故意写的非法 CLSID：只种扩展键，不种 CLSID 信息
        if (displayName is not null) registry.Seed(root, @"Software\Classes\CLSID\" + canonical, "", displayName);
        if (dllPath is not null)
            registry.Seed(root, @"Software\Classes\CLSID\" + canonical + @"\InprocServer32", "", dllPath);
    }

    // ================================================================
    // 内存假注册表：按「根 + 路径」存字典，键名 / 值名都不区分大小写
    // ================================================================

    /// <summary>
    /// <see cref="IContextMenuRegistry"/> 的内存实现。
    /// <para>路径统一按「根 + 相对路径」拼字典键；<see cref="KeyExists"/> 会把「只有子键的父键」也算存在，
    /// <see cref="GetSubKeyNames"/> 只返回直接子键，<see cref="SetValue"/> 自动补父键，
    /// <see cref="DeleteKey"/> 删整棵子树 —— 与 <c>RegistryContextMenuStore</c> 的语义对齐。</para>
    /// </summary>
    private sealed class FakeContextMenuRegistry : IContextMenuRegistry
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public bool KeyExists(RegistryPath path)
        {
            var normalized = Normalize(path.Path);
            if (normalized.Length == 0) return true;
            var prefix = MakePrefix(path.Root, normalized);
            foreach (var key in _keys.Keys)
            {
                if (key.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                if (key.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public IReadOnlyList<string> GetSubKeyNames(RegistryPath path)
        {
            var normalized = Normalize(path.Path);
            var prefix = MakePrefix(path.Root, normalized) + "\\";
            var names = new List<string>();
            foreach (var key in _keys.Keys)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = key[prefix.Length..];
                var slash = rest.IndexOf('\\');
                var name = slash >= 0 ? rest[..slash] : rest;
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public string? GetValue(RegistryPath path, string valueName)
        {
            if (!_keys.TryGetValue(MakeKey(path), out var values)) return null;
            return values.TryGetValue(valueName ?? "", out var value) ? value : null;
        }

        public IReadOnlyList<RegistryValue> GetValues(RegistryPath path)
        {
            if (!_keys.TryGetValue(MakeKey(path), out var values)) return Array.Empty<RegistryValue>();
            return values.Select(pair => new RegistryValue(pair.Key, pair.Value)).ToArray();
        }

        public void SetValue(RegistryPath path, string valueName, string value)
        {
            EnsureKey(path);
            _keys[MakeKey(path)][valueName ?? ""] = value ?? "";
        }

        public void DeleteValue(RegistryPath path, string valueName)
        {
            if (_keys.TryGetValue(MakeKey(path), out var values)) values.Remove(valueName ?? "");
        }

        public void DeleteKey(RegistryPath path)
        {
            var normalized = Normalize(path.Path);
            var prefix = MakePrefix(path.Root, normalized);
            var doomed = _keys.Keys
                .Where(key => key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                              || key.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in doomed) _keys.Remove(key);
        }

        /// <summary>直接种一个值（自动补父键）。</summary>
        public void Seed(RegistryRoot root, string path, string valueName, string value)
            => SetValue(new RegistryPath(root, path), valueName, value);

        /// <summary>直接种一个键（没有值也行）。</summary>
        public void SeedKey(RegistryRoot root, string path) => EnsureKey(new RegistryPath(root, path));

        private void EnsureKey(RegistryPath path)
        {
            var normalized = Normalize(path.Path);
            if (normalized.Length == 0) return;
            var parts = normalized.Split('\\');
            var current = "";
            foreach (var part in parts)
            {
                current = current.Length == 0 ? part : current + "\\" + part;
                var key = MakePrefix(path.Root, current);
                if (!_keys.ContainsKey(key)) _keys[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string Normalize(string? path) => (path ?? "").Trim().Trim('\\');

        private static string MakePrefix(RegistryRoot root, string normalizedPath) => root + "|" + normalizedPath;

        private static string MakeKey(RegistryPath path) => MakePrefix(path.Root, Normalize(path.Path));
    }

    /// <summary>读走假实现、写一律抛异常的包装：验证 <c>SetHidden</c> 失败时返回 Fail 而不是崩。</summary>
    private sealed class FailOnWriteRegistry : IContextMenuRegistry
    {
        private readonly IContextMenuRegistry _inner;

        public FailOnWriteRegistry(IContextMenuRegistry inner) => _inner = inner;

        public bool KeyExists(RegistryPath path) => _inner.KeyExists(path);

        public IReadOnlyList<string> GetSubKeyNames(RegistryPath path) => _inner.GetSubKeyNames(path);

        public string? GetValue(RegistryPath path, string valueName) => _inner.GetValue(path, valueName);

        public IReadOnlyList<RegistryValue> GetValues(RegistryPath path) => _inner.GetValues(path);

        public void SetValue(RegistryPath path, string valueName, string value)
            => throw new IOException("模拟注册表写入被拒绝（自检专用，不碰真实注册表）");

        public void DeleteValue(RegistryPath path, string valueName)
            => throw new IOException("模拟注册表删除被拒绝（自检专用，不碰真实注册表）");

        public void DeleteKey(RegistryPath path)
            => throw new IOException("模拟注册表删键被拒绝（自检专用，不碰真实注册表）");
    }

    /// <summary>所有读写都抛异常的坏注册表：验证枚举的容错（单个坏位置不拖垮扫描）。</summary>
    private sealed class ThrowingRegistry : IContextMenuRegistry
    {
        public bool KeyExists(RegistryPath path) => throw new InvalidOperationException("坏注册表：键查询失败");

        public IReadOnlyList<string> GetSubKeyNames(RegistryPath path)
            => throw new InvalidOperationException("坏注册表：子键读取失败");

        public string? GetValue(RegistryPath path, string valueName)
            => throw new InvalidOperationException("坏注册表：值读取失败");

        public IReadOnlyList<RegistryValue> GetValues(RegistryPath path)
            => throw new InvalidOperationException("坏注册表：值列表读取失败");

        public void SetValue(RegistryPath path, string valueName, string value)
            => throw new InvalidOperationException("坏注册表：写入失败");

        public void DeleteValue(RegistryPath path, string valueName)
            => throw new InvalidOperationException("坏注册表：删除值失败");

        public void DeleteKey(RegistryPath path)
            => throw new InvalidOperationException("坏注册表：删键失败");
    }
}
