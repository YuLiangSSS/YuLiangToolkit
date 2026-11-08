using YuLiangToolkit.App.Models;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「主页设置 → 清理缓存 / 重建配置」的纯逻辑自检（v0.39.0）：
/// Cache 路径约定 / <see cref="ToolCache"/> 门面 / <see cref="CacheCleaner"/> 汇总与清理 /
/// <see cref="ConfigResetter"/> 现状列表 / <see cref="ToolConfigStore"/> 重建（内存对象原地回默认 + 磁盘 JSON 回默认）/
/// <see cref="ToolConfigPeek"/>（读状态不建文件）。
///
/// <para><b>为什么值得单独一组</b>：清理缓存 / 重建配置动的是用户磁盘上的 Data / Config / Cache，
/// 出错就是「删了不该删的」或「重建完又被自动保存写回老值」。这里把三条承诺钉死：</para>
/// <list type="number">
///   <item>清理缓存只动 <c>Cache\</c>，<b>不碰 Data / Config</b>；</item>
///   <item>重建配置后<b>内存里的同一个对象</b>回到默认值，磁盘 JSON 也回默认（否则下一秒自动保存又写回老值）；</item>
///   <item>只读维护列表不会为了「看一眼」把配置目录 / 文件建出来。</item>
/// </list>
///
/// <para><b>隔离</b>：自检跑在临时 <c>YLT_HOME</c> 里，本模块只用<b>唯一临时工具 Id</b>（带 GUID 后缀）造数据，
/// 不依赖真实 Data / Config；不弹窗（不调 <c>OpenInExplorer</c>）；结束把造出来的目录 / 文件全部删掉。</para>
/// </summary>
public static class MaintenanceChecks
{
    /// <summary>跑完整组断言（签名与其它 Checks 一致：带 detail 的 check + 不带 detail 的 simpleCheck）。</summary>
    public static void Run(Action<string, bool, string> check, Action<string, bool> simpleCheck)
    {
        using var scratch = new Scratch();
        RunOnScratch(check, simpleCheck, scratch);
    }

    private static void RunOnScratch(Action<string, bool, string> check, Action<string, bool> simpleCheck, Scratch scratch)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);
        void P(string name, bool ok) => simpleCheck(name, ok);

        // 每次运行用唯一后缀：既不会撞上其它模块的临时 Id，也避免同一进程里重复注册同名 store。
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var cacheRoot = AppPaths.CacheRoot;

        // ===================== 1. Cache 路径约定 =====================
        C("Cache 根目录约定：<数据根>\\Cache，名字来自 AppPaths.CacheFolderName",
            AppPaths.CacheFolderName == "Cache"
            && string.Equals(Path.GetFileName(cacheRoot), "Cache", StringComparison.Ordinal)
            && string.Equals(cacheRoot, Path.Combine(AppPaths.Root, "Cache"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetDirectoryName(cacheRoot)?.TrimEnd(Path.DirectorySeparatorChar),
                AppPaths.Root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase),
            cacheRoot);

        var cacheToolId = "maint-cache-" + suffix;
        var cacheDir = AppPaths.ToolCacheDir(cacheToolId);
        P("工具缓存目录 = Cache\\<工具Id>\\（在 CacheRoot 下面，只拼路径不创建）",
            string.Equals(cacheDir, Path.Combine(cacheRoot, cacheToolId), StringComparison.OrdinalIgnoreCase)
            && cacheDir.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(cacheDir) == cacheToolId
            && !Directory.Exists(cacheDir));

        AppPaths.EnsureCreated();
        C("AppPaths.EnsureCreated 建出 Data / Config / Cache 三个根文件夹",
            Directory.Exists(AppPaths.DataRoot) && Directory.Exists(AppPaths.ConfigRoot) && Directory.Exists(cacheRoot),
            $"Data={Directory.Exists(AppPaths.DataRoot)}, Config={Directory.Exists(AppPaths.ConfigRoot)}, Cache={Directory.Exists(cacheRoot)}");

        // ===================== 2. ToolCache 门面 =====================
        var cache = ToolCache.For(cacheToolId);
        scratch.TrackDirectory(cacheDir);
        P("ToolCache.For：拿到的门面 Id / 目录正确，且不会顺手建目录",
            cache.ToolId == cacheToolId && cache.Directory == cacheDir && !cache.Exists);

        cache.EnsureCreated();
        C("ToolCache.EnsureCreated 建出缓存文件夹", cache.Exists && Directory.Exists(cacheDir), cacheDir);

        var cachedFile = cache.PathOf("a.bin");
        File.WriteAllBytes(cachedFile, new byte[] { 1, 2, 3, 4, 5 });
        C("ToolCache 写文件后 TotalSize / FileCount 统计到",
            cache.TotalSize() == 5 && cache.FileCount() == 1,
            $"{cache.TotalSize()}B / {cache.FileCount()}");

        var nestedFile = cache.PathOf("thumbs", "001.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedFile)!);
        File.WriteAllBytes(nestedFile, new byte[] { 9, 9, 9 });
        C("PathOf(params) 组子目录路径，大小 / 文件数按递归统计",
            cache.FileCount() == 2 && cache.TotalSize() == 8 && nestedFile.StartsWith(cacheDir, StringComparison.OrdinalIgnoreCase),
            $"{cache.TotalSize()}B / {cache.FileCount()}");

        C("ToolCache.PathOf(\"../../x\") 抛 ArgumentException（禁止 .. 越界）", ThrowsArgument(() => cache.PathOf("../../x")));
        C("ToolCache.PathOf 拒绝绝对路径", ThrowsArgument(() => cache.PathOf(@"C:\Windows\evil.bin")));
        C("ToolCache.PathOf 拒绝空路径", ThrowsArgument(() => cache.PathOf("")));
        C("ToolCache.PathOf 用子目录 + .. 绕出去也拦住", ThrowsArgument(() => cache.PathOf("thumbs", "..", "..", "逃逸.bin")));

        C("ToolCache.Clear 清空内容但保留文件夹本身（下次写入照常）",
            cache.Clear() && cache.TotalSize() == 0 && cache.FileCount() == 0 && cache.Exists && Directory.Exists(cacheDir));
        P("文件夹本来就不存在时 ToolCache.Clear 也返回 true", ToolCache.For("maint-absent-" + suffix).Clear());

        // 重新放两个文件给下面的 CacheCleaner 用（2 + 3 = 5 字节，共 2 个文件）
        File.WriteAllBytes(cache.PathOf("b.bin"), new byte[] { 1, 2 });
        var cacheNested = cache.PathOf("thumbs", "c.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheNested)!);
        File.WriteAllBytes(cacheNested, new byte[] { 3, 4, 5 });

        var cacheToolBId = "maint-cacheb-" + suffix;
        var cacheB = ToolCache.For(cacheToolBId);
        scratch.TrackDirectory(cacheB.Directory);
        cacheB.EnsureCreated();
        File.WriteAllBytes(cacheB.PathOf("x.bin"), new byte[] { 7 });

        // 只有空子目录（没有文件）的缓存文件夹不该出现在清理列表里
        var emptyCacheId = "maint-cacheempty-" + suffix;
        var emptyCacheDir = AppPaths.ToolCacheDir(emptyCacheId);
        scratch.TrackDirectory(emptyCacheDir);
        Directory.CreateDirectory(Path.Combine(emptyCacheDir, "empty-sub"));

        // ===================== 3. CacheCleaner.List =====================
        var folderTool = new MaintenanceTool(cacheToolId, "缓存测试工具");
        var secondTool = new MaintenanceTool(cacheToolBId, "缓存测试工具B");
        var emptyTool = new MaintenanceTool(emptyCacheId, "空缓存工具");
        var listed = CacheCleaner.List(new ITool[] { folderTool, secondTool, emptyTool });
        var folderEntry = listed.FirstOrDefault(entry => entry.ToolId == cacheToolId);

        C("CacheCleaner.List：Cache\\<工具Id>\\ 有文件 → 列出「缓存文件夹」并统计大小",
            folderEntry is { Name: "缓存文件夹" }
            && folderEntry.Key == "folder:" + cacheToolId
            && folderEntry.ToolName == "缓存测试工具"
            && folderEntry.SizeBytes == 5
            && folderEntry.Detail.Contains("2 个文件")
            && folderEntry.SizeText.Length > 0,
            folderEntry is null ? "(没列出)" : $"{folderEntry.SizeBytes}B / {folderEntry.Detail}");

        C("CacheCleaner.List：只有空子目录的缓存文件夹不列出",
            listed.All(entry => entry.ToolId != emptyCacheId));

        var folderIndex = listed.Select((entry, index) => (Entry: entry, Index: index))
            .First(item => item.Entry.ToolId == cacheToolId).Index;
        var secondIndex = listed.Select((entry, index) => (Entry: entry, Index: index))
            .First(item => item.Entry.ToolId == cacheToolBId).Index;
        P("CacheCleaner.List 顺序 = 传入工具的顺序（先列第一个工具的缓存）", folderIndex < secondIndex);

        // 工具自己报告额外缓存项：Detail 非空的列出、Detail 为空的隐藏
        var sourceToolId = "maint-source-" + suffix;
        var sourceClearCount = 0;
        var visibleItem = new ToolCacheItem
        {
            Id = "recent",
            Name = "最近浏览",
            Description = "图片阅读器的最近浏览记录",
            Detail = "12 条",
            Clear = () => sourceClearCount++,
        };
        var hiddenItem = new ToolCacheItem
        {
            Id = "empty",
            Name = "空条目",
            Detail = "",
            Clear = () => { },
        };
        var sourceTool = new MaintenanceCacheSourceTool(sourceToolId, "带缓存项的工具", new[] { visibleItem, hiddenItem });
        var sourceEntries = CacheCleaner.List(new ITool[] { sourceTool })
            .Where(entry => entry.ToolId == sourceToolId)
            .ToList();

        C("IToolCacheSource：Detail 非空的项列出（名称 / 数量 / 唯一键 / SizeText 走 Detail）",
            sourceEntries.Count == 1
            && sourceEntries[0].Key == sourceToolId + ":recent"
            && sourceEntries[0].Name == "最近浏览"
            && sourceEntries[0].ToolName == "带缓存项的工具"
            && sourceEntries[0].Detail == "12 条"
            && sourceEntries[0].SizeBytes == 0
            && sourceEntries[0].SizeText == "12 条",
            sourceEntries.Count == 0 ? "(没列出)" : $"{sourceEntries[0].Key} / {sourceEntries[0].SizeText}");

        C("IToolCacheSource：Detail 为空的项被隐藏（设置页不会出现一堆「0 条」）",
            sourceEntries.All(entry => entry.Name != "空条目"));

        // 单个工具报告失败不能拖垮整张列表
        var throwingToolId = "maint-throwing-" + suffix;
        var throwingTool = new MaintenanceThrowingCacheSourceTool(throwingToolId, "报告失败的工具");
        IReadOnlyList<CacheEntry> afterThrow = Array.Empty<CacheEntry>();
        var listThrew = false;
        try { afterThrow = CacheCleaner.List(new ITool[] { throwingTool }); }
        catch { listThrew = true; }
        C("某个工具 DescribeCacheItems 抛异常 → 列表照常返回，只是少了它那几条",
            !listThrew && afterThrow.All(entry => entry.ToolId != throwingToolId));

        // Cache 根下没有对应工具的残留目录
        var unknownFolder = "maint-unknown-" + suffix;
        var unknownDir = Path.Combine(cacheRoot, unknownFolder);
        scratch.TrackDirectory(unknownDir);
        Directory.CreateDirectory(unknownDir);
        File.WriteAllBytes(Path.Combine(unknownDir, "残留.bin"), new byte[] { 1, 2, 3, 4 });
        var unknownEntry = CacheCleaner.List(Array.Empty<ITool>())
            .FirstOrDefault(entry => entry.Key == "unknown:" + unknownFolder);
        C("Cache 根下没有对应工具的残留目录 → 列为「残留缓存文件夹」",
            unknownEntry is { Name: "残留缓存文件夹" }
            && unknownEntry.ToolId.Length == 0
            && unknownEntry.ToolName.Contains(unknownFolder)
            && unknownEntry.SizeBytes == 4
            && unknownEntry.Detail.Contains("1 个文件"),
            unknownEntry is null ? "(没列出)" : $"{unknownEntry.ToolName} / {unknownEntry.Detail}");

        // Cache 根目录散落的零散文件（程序自己写的说明文件不算缓存）
        var cacheReadmePath = Path.Combine(cacheRoot, RuntimeReadme.CacheReadmeFileName);
        scratch.TrackFile(cacheReadmePath);
        File.WriteAllText(cacheReadmePath, "# Cache\n程序自己写的说明，不是缓存");
        var loosePath = Path.Combine(cacheRoot, "maint-loose-" + suffix + ".tmp");
        scratch.TrackFile(loosePath);
        File.WriteAllBytes(loosePath, new byte[] { 1, 2, 3, 4, 5, 6 });
        var looseEntry = CacheCleaner.List(Array.Empty<ITool>())
            .FirstOrDefault(entry => entry.Key == "cache-root-files");
        C("Cache 根目录散落的零散文件 → 汇总成一条「散落的缓存文件」",
            looseEntry is { Name: "散落的缓存文件" }
            && looseEntry.SizeBytes == 6
            && looseEntry.Detail.Contains("1 个文件"),
            looseEntry is null ? "(没列出)" : $"{looseEntry.SizeBytes}B / {looseEntry.Detail}");
        C("Cache 根目录的说明文件不算缓存（不列出、不计入大小）",
            looseEntry is not null && !looseEntry.Description.Contains(RuntimeReadme.CacheReadmeFileName));

        C("CacheCleaner.List(null) 不抛异常（全部是 Cache 根目录残留项，ToolId 为空）",
            CacheCleaner.List(null).All(entry => entry.ToolId.Length == 0));

        // ===================== 4. CacheCleaner.Clear =====================
        // 先造一份用户数据 / 配置，验证「清缓存不动它们」
        var dataToolId = "maint-data-" + suffix;
        var dataDir = AppPaths.ToolDataDir(dataToolId);
        scratch.TrackDirectory(dataDir);
        Directory.CreateDirectory(dataDir);
        var userDataFile = Path.Combine(dataDir, "用户数据.txt");
        File.WriteAllText(userDataFile, "删缓存不该动这个");

        var configKeepId = "maint-cfgkeep-" + suffix;
        var configKeepDir = AppPaths.ToolConfigDir(configKeepId);
        scratch.TrackDirectory(configKeepDir);
        Directory.CreateDirectory(configKeepDir);
        var configKeepFile = Path.Combine(configKeepDir, "config.json");
        File.WriteAllText(configKeepFile, """{ "keep": true }""");

        var toClear = new[] { folderEntry, sourceEntries.FirstOrDefault(), unknownEntry, looseEntry }
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToArray();
        var expectedBytes = toClear.Sum(entry => entry.SizeBytes);
        var (clearCount, clearBytes) = CacheCleaner.Clear(toClear);

        C("CacheCleaner.Clear：返回条数正确、占用合计 = 各条已知大小之和",
            clearCount == 4 && clearBytes == expectedBytes && clearBytes > 0,
            $"cleared={clearCount}, bytes={clearBytes}, expected={expectedBytes}");

        C("CacheCleaner.Clear：缓存文件夹内容真的没了（文件夹本身保留，下次写入照常）",
            cache.FileCount() == 0 && cache.Exists && Directory.Exists(cacheDir));

        C("CacheCleaner.Clear：残留目录内容 / 散落文件也被清掉",
            CountFilesUnder(unknownDir) == 0 && !File.Exists(loosePath));
        C("清理缓存不删 Cache 根目录的程序说明文件", File.Exists(cacheReadmePath));

        C("清理缓存不碰 Data 里的用户数据", File.Exists(userDataFile) && File.ReadAllText(userDataFile) == "删缓存不该动这个");
        C("清理缓存不碰 Config 里的配置", File.Exists(configKeepFile));
        C("IToolCacheSource 的清理动作真的被调用了", sourceClearCount == 1, $"sourceClearCount={sourceClearCount}");

        var (nullClearCount, nullClearBytes) = CacheCleaner.Clear(null);
        var (emptyClearCount, emptyClearBytes) = CacheCleaner.Clear(Array.Empty<CacheEntry>());
        P("Clear(null) / 空集合 = 什么都不做，返回 (0, 0)",
            nullClearCount == 0 && nullClearBytes == 0 && emptyClearCount == 0 && emptyClearBytes == 0);

        // 单条失败：走 onFailed 回调，不算清理成功，也不中断别的条
        var failedEntries = new List<CacheEntry>();
        var failingEntry = new CacheEntry
        {
            Key = "maint-failing-" + suffix,
            ToolId = "",
            ToolName = "（自检）",
            Name = "会失败的清理项",
            Clear = () => throw new IOException("模拟删除失败"),
        };
        var (failCount, failBytes) = CacheCleaner.Clear(new[] { failingEntry }, entry => failedEntries.Add(entry));
        C("单条清理失败走 onFailed 回调，不中断其它条目",
            failCount == 0 && failBytes == 0 && failedEntries.Count == 1 && ReferenceEquals(failedEntries[0], failingEntry));

        var (nullEntryCount, nullEntryBytes) = CacheCleaner.Clear(new CacheEntry[] { null! });
        P("集合里混进 null 条目不抛异常（直接跳过）", nullEntryCount == 0 && nullEntryBytes == 0);

        // ===================== 5. ConfigResetter.List（重建配置的「现状」列表） =====================
        var cfgWithId = "maint-cfgwith-" + suffix;
        var cfgNoneId = "maint-cfgnone-" + suffix;
        var cfgBadId = "maint-cfgbad-" + suffix;

        var cfgWithDir = AppPaths.ToolConfigDir(cfgWithId);
        scratch.TrackDirectory(cfgWithDir);
        Directory.CreateDirectory(cfgWithDir);
        File.WriteAllText(Path.Combine(cfgWithDir, "config.json"), """{ "note": "有配置" }""");

        var cfgBadDir = AppPaths.ToolConfigDir(cfgBadId);
        scratch.TrackDirectory(cfgBadDir);
        Directory.CreateDirectory(cfgBadDir);
        var cfgBadBackup = Path.Combine(cfgBadDir, "config.bad-20250101-120000.json");
        File.WriteAllText(cfgBadBackup, """{ 损坏的旧配置 }""");

        var cfgNoneDir = AppPaths.ToolConfigDir(cfgNoneId);
        C("ConfigResetter.List 之前：没有配置的功能连目录都不存在（列表不能为了看一眼就建目录）",
            !Directory.Exists(cfgNoneDir), cfgNoneDir);

        var targets = ConfigResetter.List(new[]
        {
            new ToolRef(cfgWithId, "有配置的功能"),
            new ToolRef(cfgNoneId, "没配置的功能"),
            new ToolRef(cfgBadId, "只有坏备份的功能"),
        });

        C("ConfigResetter.List：按传入顺序返回三条现状（ToolId / ToolName 都带出来）",
            targets.Count == 3
            && targets[0].ToolId == cfgWithId && targets[0].ToolName == "有配置的功能"
            && targets[1].ToolId == cfgNoneId
            && targets[2].ToolId == cfgBadId,
            string.Join(" / ", targets.Select(target => target.ToolId)));

        var withTarget = targets[0];
        var noneTarget = targets[1];
        var badTarget = targets[2];

        C("有 config.json → HasConfigFile = true，Detail 写清文件名，统计大小",
            withTarget.HasConfigFile
            && withTarget.Files.Contains("config.json")
            && withTarget.SizeBytes > 0
            && withTarget.SizeText.Length > 0
            && withTarget.Detail.Contains("config.json")
            && withTarget.Directory == cfgWithDir,
            $"{withTarget.Detail} / {withTarget.SizeText}");

        C("没有配置文件 → HasConfigFile = false，Detail 说明下次打开会自动生成",
            !noneTarget.HasConfigFile
            && noneTarget.Files.Count == 0
            && noneTarget.SizeBytes == 0
            && noneTarget.SizeText == ""
            && noneTarget.Detail.Contains("还没有配置文件")
            && noneTarget.Directory == cfgNoneDir,
            noneTarget.Detail);

        C("ConfigResetter.List 只读：仍然没有创建这个功能的配置目录", !Directory.Exists(cfgNoneDir));

        C("只有 *.bad-*.json → HasConfigFile = false（损坏备份不算有效配置）",
            !badTarget.HasConfigFile
            && badTarget.Files.Count == 1
            && badTarget.SizeBytes > 0
            && badTarget.Detail.Contains("损坏备份"),
            badTarget.Detail);

        P("ConfigResetter.List(null) / 空 Id = 空列表（不会崩）",
            ConfigResetter.List(null).Count == 0
            && ConfigResetter.List(new[] { new ToolRef("", "空 Id") }).Count == 0);

        // ResetMany 就是「重建选中配置」的批量入口：同一功能去重
        var (manyReset, manyFailed) = ConfigResetter.ResetMany(new[] { cfgWithId, cfgWithId });
        C("ResetMany 去重：同一功能只重建一次，返回成功数",
            manyReset == 1 && manyFailed == 0, $"reset={manyReset}, failed={manyFailed}");
        C("重建后配置文件被删（下次打开自动生成默认值），目录本身保留",
            !File.Exists(Path.Combine(cfgWithDir, "config.json")) && Directory.Exists(cfgWithDir));
        C("重建配置不碰损坏备份（*.bad-*.json 是救命用的）", File.Exists(cfgBadBackup));

        var (nullReset, nullFailed) = ConfigResetter.ResetMany(null);
        P("ResetMany(null) = (0, 0)", nullReset == 0 && nullFailed == 0);

        P("ConfigResetter.Reset 委托给 ToolConfigStore.ResetTool（没配置也返回 true）",
            ConfigResetter.Reset(cfgNoneId));

        // ===================== 6. ToolConfigStore.ResetToDefaults（核心承诺：同一个对象原地回默认） =====================
        // 用自检 Program.cs 里的 TestCfg（全部自动属性），验证「重建后内存实例不换、值回默认、磁盘也回默认」。
        var resetDefaultId = "maint-resetdefault-" + suffix;
        var resetDefaultStore = scratch.TrackStore(ToolConfigStore.For(resetDefaultId));
        var resetDefaultDir = AppPaths.ToolConfigDir(resetDefaultId);
        scratch.TrackDirectory(resetDefaultDir);
        var resetCfg = resetDefaultStore.Load<TestCfg>();

        C("ResetToDefaults 前置：首次加载就是默认值且已落盘",
            resetCfg is { Note: "", Repeat: 3, Enable: true, Mode: "标准" }
            && File.Exists(resetDefaultStore.FilePath),
            resetDefaultStore.FilePath);

        resetCfg.Note = "改过";
        resetCfg.Repeat = 42;
        resetCfg.Enable = false;
        resetCfg.Mode = "极限";
        resetDefaultStore.SaveNow();
        C("ResetToDefaults 前置：磁盘上确实是非默认值",
            JsonFileStore.Read<TestCfg>(resetDefaultStore.FilePath) is { Note: "改过", Repeat: 42 });

        var resetDefaultOk = resetDefaultStore.ResetToDefaults();
        var afterReset = resetDefaultStore.Load<TestCfg>();
        C("ResetToDefaults：同一个实例（ReferenceEquals）就地回到默认值",
            resetDefaultOk
            && ReferenceEquals(afterReset, resetCfg)
            && resetCfg is { Note: "", Repeat: 3, Enable: true, Mode: "标准" }
            && Math.Abs(afterReset.Threshold - 0.5) < 1e-9,
            $"Note='{resetCfg.Note}', Repeat={resetCfg.Repeat}");

        C("ResetToDefaults：磁盘 JSON 也回到默认，文件仍在（下次打开读到默认值）",
            File.Exists(resetDefaultStore.FilePath)
            && JsonFileStore.Read<TestCfg>(resetDefaultStore.FilePath) is { Note: "", Repeat: 3, Enable: true, Mode: "标准" });

        // ===================== 7. ToolConfigStore.ResetTool（整目录重建 + 损坏备份保留） =====================
        var resetToolId = "maint-resettool-" + suffix;
        var resetToolStore = scratch.TrackStore(ToolConfigStore.For(resetToolId));
        var resetToolDir = AppPaths.ToolConfigDir(resetToolId);
        scratch.TrackDirectory(resetToolDir);
        var resetToolCfg = resetToolStore.Load<TestCfg>();
        resetToolCfg.Note = "要删掉的配置";
        resetToolCfg.Repeat = 8;
        resetToolStore.SaveNow();

        var otherJson = Path.Combine(resetToolDir, "other.json");
        File.WriteAllText(otherJson, """{ "other": 1 }""");
        var tmpFile = Path.Combine(resetToolDir, "options.tmp");
        File.WriteAllText(tmpFile, "tmp");
        var badBackup = Path.Combine(resetToolDir, "config.bad-20250101-120000.json");
        File.WriteAllText(badBackup, """{ 损坏的旧配置 }""");
        var notesFile = Path.Combine(resetToolDir, "notes.txt");
        File.WriteAllText(notesFile, "不是配置，别删");

        C("IsResettableConfigFile：*.json / *.tmp 会删，*.bad-*.json 与其它文件保留",
            ToolConfigStore.IsResettableConfigFile(resetToolStore.FilePath)
            && ToolConfigStore.IsResettableConfigFile(otherJson)
            && ToolConfigStore.IsResettableConfigFile(tmpFile)
            && !ToolConfigStore.IsResettableConfigFile(badBackup)
            && !ToolConfigStore.IsResettableConfigFile(notesFile));

        var resetToolOk = ToolConfigStore.ResetTool(resetToolId);
        P("ResetTool 返回 true", resetToolOk);
        C("ResetTool：other.json / 残留 *.tmp 被删", !File.Exists(otherJson) && !File.Exists(tmpFile));
        C("ResetTool：*.bad-*.json 损坏备份保留（用户可能还要从里面抄回设置）", File.Exists(badBackup));
        C("ResetTool：不是配置的文件（notes.txt）不动", File.Exists(notesFile));
        C("ResetTool：配置文件用默认值重新写回，内存实例仍是同一个且回默认",
            File.Exists(resetToolStore.FilePath)
            && ReferenceEquals(resetToolStore.Load<TestCfg>(), resetToolCfg)
            && resetToolCfg is { Note: "", Repeat: 3 }
            && JsonFileStore.Read<TestCfg>(resetToolStore.FilePath) is { Note: "", Repeat: 3 });

        // ===================== 8. ToolConfigPeek（只读 / 改配置都不建文件） =====================
        var peekId = "maint-peek-" + suffix;
        var peekDir = AppPaths.ToolConfigDir(peekId);
        scratch.TrackDirectory(peekDir);
        var peekPath = ToolConfigPeek.PathOf(peekId);

        C("ToolConfigPeek.PathOf：Config\\<功能Id>\\config.json（只拼路径，支持自定义文件名）",
            peekPath == Path.Combine(peekDir, "config.json")
            && peekPath.EndsWith("config.json", StringComparison.Ordinal)
            && ToolConfigPeek.PathOf(peekId, "options.json") == Path.Combine(peekDir, "options.json"));

        C("ToolConfigPeek.Read：没有配置文件返回 null（不是默认对象）",
            ToolConfigPeek.Read<TestCfg>(peekId) is null);
        C("ToolConfigPeek.Read：不会为了读状态把文件 / 目录建出来",
            !File.Exists(peekPath) && !Directory.Exists(peekDir));

        Directory.CreateDirectory(peekDir);
        File.WriteAllText(peekPath, """{ "note": "手写", "repeat": 7 }""");
        var peeked = ToolConfigPeek.Read<TestCfg>(peekId);
        C("ToolConfigPeek.Read：手写的 config.json 能读出来（缺失字段用默认值）",
            peeked is { Note: "手写", Repeat: 7, Enable: true });

        var mutateOk = ToolConfigPeek.Mutate<TestCfg>(peekId, cfg =>
        {
            cfg.Note = "改过";
            cfg.Repeat = 11;
        });
        C("ToolConfigPeek.Mutate：功能没加载时读盘 → 改 → 写盘",
            mutateOk && JsonFileStore.Read<TestCfg>(peekPath) is { Note: "改过", Repeat: 11 });

        var peekMissingId = "maint-peekmissing-" + suffix;
        var peekMissingPath = ToolConfigPeek.PathOf(peekMissingId);
        C("ToolConfigPeek.Mutate：文件不存在（= 本来就是默认值）返回 false，且不创建文件 / 目录",
            !ToolConfigPeek.Mutate<TestCfg>(peekMissingId, cfg => cfg.Note = "不该发生")
            && !File.Exists(peekMissingPath)
            && !Directory.Exists(AppPaths.ToolConfigDir(peekMissingId)));

        // 功能开着（配置已加载）：Mutate 改内存里的同一个对象，并立刻落盘
        var peekLiveId = "maint-peeklive-" + suffix;
        var peekLiveStore = scratch.TrackStore(ToolConfigStore.For(peekLiveId));
        scratch.TrackDirectory(AppPaths.ToolConfigDir(peekLiveId));
        var peekLiveCfg = peekLiveStore.Load<TestCfg>();
        var liveMutateOk = ToolConfigPeek.Mutate<TestCfg>(peekLiveId, cfg => cfg.Note = "内存里改");
        C("ToolConfigPeek.Mutate：功能已加载 → 改内存同一实例并落盘（界面能立刻看到）",
            liveMutateOk
            && ReferenceEquals(ToolConfigPeek.Read<TestCfg>(peekLiveId), peekLiveCfg)
            && peekLiveCfg.Note == "内存里改"
            && JsonFileStore.Read<TestCfg>(peekLiveStore.FilePath)?.Note == "内存里改");

        // ===================== 9. 外壳配置重建（ShellConfig：排序 / 标签墙 / 最近使用） =====================
        var shellRebuildId = "maint-shell-" + suffix;
        var shellRebuildStore = scratch.TrackStore(ToolConfigStore.For(shellRebuildId));
        scratch.TrackDirectory(AppPaths.ToolConfigDir(shellRebuildId));
        var shellRebuild = shellRebuildStore.Load<ShellConfig>();

        C("外壳配置重建前置：默认排序 Default / 标签墙显示 / 没有最近使用记录",
            shellRebuild is { SortMode: ToolSortMode.Default, ShowHomeTags: true }
            && shellRebuild.LastUsed.Count == 0);

        shellRebuild.SortMode = ToolSortMode.RecentlyUsed;
        shellRebuild.ShowHomeTags = false;
        shellRebuild.MarkUsed("demo-tool");
        shellRebuildStore.SaveNow();

        var shellResetOk = shellRebuildStore.ResetToDefaults();
        var shellAfter = shellRebuildStore.Load<ShellConfig>();
        C("ShellConfig 重建：同一实例回默认（排序 / 标签墙 / 最近使用清单清空）",
            shellResetOk
            && ReferenceEquals(shellAfter, shellRebuild)
            && shellAfter.SortMode == ToolSortMode.Default
            && shellAfter.ShowHomeTags
            && shellAfter.LastUsed.Count == 0,
            $"SortMode={shellAfter.SortMode}, ShowHomeTags={shellAfter.ShowHomeTags}, LastUsed={shellAfter.LastUsed.Count}");

        var diskShell = JsonFileStore.Read<ShellConfig>(shellRebuildStore.FilePath);
        C("ShellConfig 重建：磁盘 JSON 也回默认，文件仍在",
            File.Exists(shellRebuildStore.FilePath)
            && diskShell is { ShowHomeTags: true }
            && diskShell.SortMode == ToolSortMode.Default
            && diskShell.LastUsed.Count == 0);

        // ===================== 8. 界面布局记忆（v0.41.0：工具页分隔条 / 恢复默认布局） =====================
        RunLayoutChecks(C, P);

        // ===================== 10. 配置导入导出（把设置 + 图片打包成 zip） =====================
        RunConfigArchiveChecks(C, P, scratch);
    }

    /// <summary>
    /// 「主页设置 → 配置导入导出」的底层：<see cref="ConfigArchive"/> 的导出 / 预览 / 导入。
    ///
    /// <para><b>钉死的三条承诺</b>：</para>
    /// <list type="number">
    ///   <item>导出包里<b>没有</b>凭据类配置（邮箱授权码 / Spug 推送地址 / osu! 密钥），导入时也跳过；</item>
    ///   <item>导入后<b>同一个配置对象</b>换成包里的值（不是新对象），否则界面绑的旧对象不动、后台自动保存还会写回旧值；</item>
    ///   <item>恶意压缩包（<c>../</c> 逃逸 / 盘符 / 陌生目录）一律不落盘。</item>
    /// </list>
    /// </summary>
    private static void RunConfigArchiveChecks(
        Action<string, bool, string> check, Action<string, bool> simpleCheck, Scratch scratch)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);
        void P(string name, bool ok) => simpleCheck(name, ok);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var toolId = "maint-archive-" + suffix;
        var configDir = AppPaths.ToolConfigDir(toolId);
        scratch.TrackDirectory(configDir);

        // ---- 造一份可识别的配置 + 一张「收藏的表情图」 ----
        var store = scratch.TrackStore(ToolConfigStore.For(toolId));
        var cfg = store.Load<TestCfg>();
        cfg.Note = "导出时的值";
        cfg.Repeat = 7;
        cfg.Enable = false;
        store.SaveNow();

        var imageDir = Path.Combine(AppPaths.DataRoot, "meme-board", "images");
        Directory.CreateDirectory(imageDir);
        var imagePath = Path.Combine(imageDir, $"selfcheck-{suffix}.png");
        File.WriteAllBytes(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        scratch.TrackFile(imagePath);

        // 凭据类配置：导出 / 导入都必须跳过
        var credentialDir = AppPaths.ToolConfigDir("mail-collector");
        scratch.TrackDirectory(credentialDir);
        var credentialStore = scratch.TrackStore(ToolConfigStore.For("mail-collector"));
        var credentialConfig = credentialStore.Load<TestCfg>();
        credentialConfig.Note = "授权码密文（不该进包）";
        credentialStore.SaveNow();

        // ---- 导出 ----
        var zipPath = SelfCheckScratch.MakeFile("config-archive.zip");
        scratch.TrackFile(zipPath);
        var export = ConfigArchive.Export(zipPath);
        C("ConfigArchive.Export：成功、统计到配置、文件落在指定位置",
            export.Ok && export.ConfigFileCount > 0 && export.ZipBytes > 0 && File.Exists(zipPath),
            $"ok={export.Ok}, config={export.ConfigFileCount}, data={export.DataFileCount}, bytes={export.ZipBytes}, err={export.Error}");

        var probe = ConfigArchive.Probe(zipPath);
        C("Probe：读出清单、认出是本程序的配置包",
            probe.Ok && probe.Manifest is not null
            && string.Equals(probe.Manifest.Kind, "yuLiangToolkit-config", StringComparison.Ordinal)
            && probe.Manifest.Version == ConfigArchive.CurrentVersion,
            $"ok={probe.Ok}, err={probe.Error}");

        C("导出包含刚造的功能配置，但<b>不含</b>凭据类配置（mail-collector）",
            probe.Manifest is not null
            && probe.Manifest.ConfigToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase)
            && !probe.Manifest.ConfigToolIds.Contains("mail-collector", StringComparer.OrdinalIgnoreCase),
            probe.Manifest is null ? "清单为空" : string.Join(",", probe.Manifest.ConfigToolIds));

        C("导出带上了白名单里的表情包图片",
            probe.Manifest is not null && probe.Manifest.DataFileCount > 0
            && probe.Manifest.DataToolIds.Contains("meme-board", StringComparer.OrdinalIgnoreCase),
            probe.Manifest is null ? "清单为空" : $"data={probe.Manifest.DataFileCount}");

        P("白名单判定：表情图 / 背景图在包内，Cache 与聊天记录不在",
            ConfigArchive.IsExportableDataPath("meme-board/images/a.png")
            && ConfigArchive.IsExportableDataPath("shell/background/bg.png")
            && !ConfigArchive.IsExportableDataPath("chat-room/history.json")
            && !ConfigArchive.IsExportableDataPath("meme-board/videos/a.mp4"));

        // ---- 改本地值，再导入（覆盖式还原） ----
        cfg.Note = "本地改过的值";
        cfg.Repeat = 99;
        store.SaveNow();

        var import = ConfigArchive.Import(zipPath);
        C("ConfigArchive.Import：成功并回报被覆盖配置的功能 Id",
            import.Ok && import.ConfigFileCount > 0
            && import.AffectedConfigToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase),
            $"ok={import.Ok}, config={import.ConfigFileCount}, affected={string.Join(",", import.AffectedConfigToolIds)}, err={import.Error}");

        C("导入后<b>同一个配置对象</b>换成包里的值（热重载，界面绑定不用重建）",
            ReferenceEquals(store.Load<TestCfg>(), cfg)
            && cfg.Note == "导出时的值" && cfg.Repeat == 7 && !cfg.Enable,
            $"Note={cfg.Note}, Repeat={cfg.Repeat}, Enable={cfg.Enable}");

        var diskCfg = JsonFileStore.Read<TestCfg>(store.FilePath);
        C("导入后磁盘 JSON 也是包里的值（后台自动保存不会把旧值写回）",
            diskCfg is { Repeat: 7 } && diskCfg.Note == "导出时的值",
            $"disk.Note={diskCfg?.Note}, disk.Repeat={diskCfg?.Repeat}");

        C("导入跳过凭据类配置：本地那份没被包里的内容盖掉",
            JsonFileStore.Read<TestCfg>(credentialStore.FilePath)?.Note == "授权码密文（不该进包）");

        C("导入时同名的表情图不覆盖、自动编号一份（RenamedCount 统计到）",
            import.RenamedCount >= 1 && File.Exists(imagePath),
            $"renamed={import.RenamedCount}");

        // ---- 布局记忆也随包走 ----
        var layoutKey = "archive-check-" + suffix + ".left.px";
        LayoutMemory.SetPixel(layoutKey, 321);
        var layoutZip = SelfCheckScratch.MakeFile("config-archive-layout.zip");
        scratch.TrackFile(layoutZip);
        var layoutExport = ConfigArchive.Export(layoutZip);
        LayoutMemory.ResetAll();
        var layoutImport = ConfigArchive.Import(layoutZip);
        C("布局记忆随包导出 / 导入（LayoutImported 且值回来了）",
            layoutExport.Ok && layoutImport.Ok && layoutImport.LayoutImported
            && LayoutMemory.GetPixel(layoutKey) == 321,
            $"export={layoutExport.Ok}, import={layoutImport.Ok}, layout={layoutImport.LayoutImported}, px={LayoutMemory.GetPixel(layoutKey)}");
        LayoutMemory.Remove(layoutKey);

        // ---- 坏包 / 外来包一律拒绝 ----
        var notZip = SelfCheckScratch.MakeFile("not-a-config.zip");
        scratch.TrackFile(notZip);
        File.WriteAllText(notZip, "这不是压缩包");
        var badProbe = ConfigArchive.Probe(notZip);
        C("Probe：不是 zip / 没有清单时明确拒绝并给出中文原因",
            !badProbe.Ok && !string.IsNullOrWhiteSpace(badProbe.Error),
            badProbe.Error ?? "");

        var badImport = ConfigArchive.Import(notZip);
        C("Import：坏包直接失败，不写任何文件",
            !badImport.Ok && !string.IsNullOrWhiteSpace(badImport.Error),
            badImport.Error ?? "");

        var missingProbe = ConfigArchive.Probe(Path.Combine(AppPaths.Root, $"不存在-{suffix}.zip"));
        C("Probe：文件不存在时说「文件不存在」", !missingProbe.Ok && missingProbe.Error == "文件不存在。", missingProbe.Error ?? "");

        // ---- 恶意压缩包：../../ 逃逸 / 盘符 / 陌生目录都不能落盘 ----
        var evilZip = SelfCheckScratch.MakeFile("config-archive-evil.zip");
        scratch.TrackFile(evilZip);
        WriteRawZip(evilZip, new (string Name, string Text)[]
        {
            ("manifest.json", JsonFileStore.Serialize(new ConfigArchiveManifest { CreatedAt = DateTime.Now })),
            ("../逃逸.txt", "不该出现在任何地方"),
            ("Config/../../逃逸2.json", "{}"),
            ("Data/../../逃逸3.txt", "不该出现在任何地方"),
            ("C:/Windows/逃逸4.txt", "不该出现"),
            ("Data/meme-board/images/../../../../逃逸5.txt", "不该出现"),
            ("Config/mail-collector/config.json", JsonFileStore.Serialize(new TestCfg { Note = "外来包里的授权码" })),
        });

        var evilProbe = ConfigArchive.Probe(evilZip);
        var evilImport = ConfigArchive.Import(evilZip);
        C("恶意包：认得出是本程序的包，导入不抛异常",
            evilProbe.Ok && evilImport.Ok, $"probe={evilProbe.Ok}, import={evilImport.Ok}, err={evilImport.Error}");

        C("恶意包：<c>..</c> 逃逸条目不落盘（根目录 / Data 上级都没多出文件）",
            !File.Exists(Path.Combine(AppPaths.Root, "逃逸.txt"))
            && !File.Exists(Path.Combine(AppPaths.Root, "逃逸2.json"))
            && !File.Exists(Path.Combine(AppPaths.DataRoot, "逃逸3.txt"))
            && !File.Exists(Path.Combine(Path.GetDirectoryName(AppPaths.DataRoot)!, "逃逸5.txt")));

        C("恶意包：陌生条目计入「已忽略」",
            evilImport.SkippedUnknownCount > 0, $"skipped={evilImport.SkippedUnknownCount}");

        C("恶意包：夹带的凭据类配置被跳过，本地那份没被改",
            evilImport.SkippedCredentialToolIds.Contains("mail-collector", StringComparer.OrdinalIgnoreCase)
            && JsonFileStore.Read<TestCfg>(credentialStore.FilePath)?.Note == "授权码密文（不该进包）",
            $"credential={string.Join(",", evilImport.SkippedCredentialToolIds)}");

        C("导入后凭据目录里没有多出文件（只保留原来那一份 config.json）",
            CountFilesUnder(credentialDir) == 1, $"files={CountFilesUnder(credentialDir)}");
    }

    /// <summary>按条目名原样写一个 zip（<see cref="System.IO.Compression.ZipArchive"/> 不会帮我们清洗路径，正好用来造恶意包）。</summary>
    private static void WriteRawZip(string path, IEnumerable<(string Name, string Text)> entries)
    {
        using var stream = File.Create(path);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(text);
        }
    }

    /// <summary>
    /// 「界面布局 → 恢复默认布局」的底层：<see cref="LayoutMemory"/> 的落盘 / 比例 / 分组 / 清空，
    /// 以及 <see cref="PaneSplitter.ComputeDrag"/> 的拖拽换算（纯函数）。
    /// <para>布局文件放在 <c>Config\layout.json</c>（外壳级，不放进某个功能目录），
    /// 这样「重建配置」不会顺手删掉布局，「恢复默认布局」能一次全清。</para>
    /// </summary>
    private static void RunLayoutChecks(Action<string, bool, string> check, Action<string, bool> simpleCheck)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);
        void P(string name, bool ok) => simpleCheck(name, ok);

        // 每次用唯一分组名，避免和真实布局键冲突（自检跑在临时 YLT_HOME 里，本来也互不影响）
        var group = "layout-check-" + Guid.NewGuid().ToString("N")[..8];

        C("LayoutMemory 文件路径 = Config\\layout.json（外壳级，不在功能目录里）",
            string.Equals(LayoutMemory.FilePath, Path.Combine(AppPaths.ConfigRoot, "layout.json"),
                StringComparison.OrdinalIgnoreCase),
            LayoutMemory.FilePath);

        LayoutMemory.ResetAll();
        P("恢复默认布局后：没有任何记住的布局", !LayoutMemory.HasSaved && LayoutMemory.SavedCount == 0);

        var pixelKey = group + ".left.px";
        P("SetPixel 写入成功并读回同一个值",
            LayoutMemory.SetPixel(pixelKey, 456.5) && LayoutMemory.GetPixel(pixelKey) == 456.5);
        P("布局文件已写盘", File.Exists(LayoutMemory.FilePath));
        P("HasSaved / SavedGroups 反映刚刚记住的页面",
            LayoutMemory.HasSaved && LayoutMemory.SavedGroups.Contains(group, StringComparer.OrdinalIgnoreCase));
        P("同一个值重复写不重复落盘（返回 false）", !LayoutMemory.SetPixel(pixelKey, 456.5));
        P("非法像素值被忽略（NaN / 0 / 负数）",
            !LayoutMemory.SetPixel(group + ".bad1", double.NaN)
            && !LayoutMemory.SetPixel(group + ".bad2", 0)
            && !LayoutMemory.SetPixel(group + ".bad3", -10)
            && LayoutMemory.GetPixel(group + ".bad1") is null);

        var ratioKey = group + ".cols.r";
        P("SetRatio / GetRatio 正常读回", LayoutMemory.SetRatio(ratioKey, 0.3) && LayoutMemory.GetRatio(ratioKey) == 0.3);
        P("SetRatio 夹到 0.02~0.98", LayoutMemory.SetRatio(ratioKey, 5) && LayoutMemory.GetRatio(ratioKey) == 0.98);
        P("Remove 删掉指定键", LayoutMemory.Remove(ratioKey) && LayoutMemory.GetRatio(ratioKey) is null);

        // 一处「两侧都固定像素」的分隔条会存 .a + .b 两个键，统计时应算 1 处
        var pair = group + ".rows";
        LayoutMemory.SetPixel(pair + ".a", 500);
        LayoutMemory.SetPixel(pair + ".b", 200);
        C("同一分隔条的 .a + .b 两个键按 1 处统计、分组正确",
            LayoutMemory.SavedCount == 2 && LayoutMemory.SavedGroups.Count == 1,
            $"count={LayoutMemory.SavedCount}, groups={string.Join(",", LayoutMemory.SavedGroups)}");

        var resetEvents = 0;
        EventHandler onReset = (_, _) => resetEvents++;
        LayoutMemory.ResetCompleted += onReset;
        try
        {
            var ok = LayoutMemory.ResetAll();
            C("ResetAll：清空内存 + 删文件 + 触发一次 ResetCompleted",
                ok && !LayoutMemory.HasSaved && LayoutMemory.SavedCount == 0
                && LayoutMemory.SavedGroups.Count == 0 && !File.Exists(LayoutMemory.FilePath)
                && resetEvents == 1,
                $"ok={ok}, events={resetEvents}, file={File.Exists(LayoutMemory.FilePath)}");
        }
        finally
        {
            LayoutMemory.ResetCompleted -= onReset;
        }

        // ---- PaneSplitter.ComputeDrag：拖拽换算（纯函数） ----
        var star = PaneSplitter.ComputeDrag(300, 500, 50, true, true, 100, 100);
        P("ComputeDrag：两侧都 * → 按比例分（前块变 350、后块 450）",
            star.Mode == PaneSplitter.PaneDragMode.Star && star.Prev == 350 && star.Next == 450);

        var prevPixel = PaneSplitter.ComputeDrag(300, 500, 50, false, true, 100, 100);
        P("ComputeDrag：前块固定、后块 * → 只改前块像素",
            prevPixel.Mode == PaneSplitter.PaneDragMode.PrevPixel && prevPixel.Prev == 350 && prevPixel.Next == 500);

        var nextPixel = PaneSplitter.ComputeDrag(300, 500, 50, true, false, 100, 100);
        P("ComputeDrag：前块 *、后块固定 → 只改后块像素（往右拖后块变小）",
            nextPixel.Mode == PaneSplitter.PaneDragMode.NextPixel && nextPixel.Prev == 300 && nextPixel.Next == 450);

        var bothPixel = PaneSplitter.ComputeDrag(300, 500, 50, false, false, 100, 100);
        P("ComputeDrag：两侧都固定 → 一块大多少另一块小多少（总和 800 不变）",
            bothPixel.Mode == PaneSplitter.PaneDragMode.BothPixel
            && bothPixel.Prev == 350 && bothPixel.Next == 450);

        var clampedHigh = PaneSplitter.ComputeDrag(300, 500, 1000, false, false, 100, 100);
        P("ComputeDrag：拖过头时两侧都夹在最小值（100 / 700）",
            clampedHigh.Prev == 700 && clampedHigh.Next == 100);

        var clampedLow = PaneSplitter.ComputeDrag(300, 500, -1000, false, false, 100, 100);
        P("ComputeDrag：往反方向拖过头也夹在最小值（100 / 700）",
            clampedLow.Prev == 100 && clampedLow.Next == 700);

        P("ComputeDrag：总量 <= 1（还没布局）返回 None",
            PaneSplitter.ComputeDrag(0, 1, 50, false, true, 10, 10).Mode == PaneSplitter.PaneDragMode.None);

        // ---- PaneSplitterSettings：分隔条「提示线透明度」（主页设置 → 界面布局 的可调项） ----
        var opacityEvents = 0;
        EventHandler onOpacityChanged = (_, _) => opacityEvents++;
        PaneSplitterSettings.RestOpacityChanged += onOpacityChanged;
        try
        {
            P("PaneSplitterSettings 默认提示线透明度 = 0.18",
                Math.Abs(PaneSplitterSettings.DefaultRestOpacity - 0.18) < 0.0001);

            PaneSplitterSettings.SetRestOpacity(0.5);
            C("SetRestOpacity(0.5) 生效并触发一次变化事件",
                Math.Abs(PaneSplitterSettings.RestOpacity - 0.5) < 0.0001 && opacityEvents == 1,
                $"opacity={PaneSplitterSettings.RestOpacity}, events={opacityEvents}");

            PaneSplitterSettings.SetRestOpacity(0.5);
            P("同一个值重复设置不重复触发事件", opacityEvents == 1);

            PaneSplitterSettings.SetRestOpacity(-1);
            P("SetRestOpacity 下限夹到 0（完全看不见）", PaneSplitterSettings.RestOpacity == 0);

            PaneSplitterSettings.SetRestOpacity(5);
            P("SetRestOpacity 上限夹到 1（一直显示实心线）", PaneSplitterSettings.RestOpacity == 1);
        }
        finally
        {
            PaneSplitterSettings.SetRestOpacity(PaneSplitterSettings.DefaultRestOpacity);
            PaneSplitterSettings.RestOpacityChanged -= onOpacityChanged;
        }
    }

    // ---------- 断言小工具 ----------

    private static bool ThrowsArgument(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static int CountFilesUnder(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 造完就收拾：停掉本模块创建的配置 store 的后台自动保存（否则删完目录它又写回来），
    /// 再删本模块造的临时文件 / 目录。清理失败不影响断言结果（临时目录里，删不掉也无害）。
    /// </summary>
    private sealed class Scratch : IDisposable
    {
        private readonly List<ToolConfigStore> _stores = new();
        private readonly List<string> _files = new();
        private readonly List<string> _directories = new();

        public void TrackDirectory(string directory) => _directories.Add(directory);

        public void TrackFile(string file) => _files.Add(file);

        public ToolConfigStore TrackStore(ToolConfigStore store)
        {
            _stores.Add(store);
            return store;
        }

        public void Dispose()
        {
            foreach (var store in _stores)
            {
                try { store.StopWatching(); } catch { /* 忽略 */ }
            }

            foreach (var file in _files)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { /* 忽略 */ }
            }

            foreach (var directory in _directories
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(path => path.Length))   // 先删深的目录
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { /* 忽略 */ }
            }
        }
    }

    // ---------- 假工具（只实现 ITool / IToolCacheSource，CreateView 不会被调用，不弹窗） ----------

    private sealed class MaintenanceTool : ITool
    {
        public MaintenanceTool(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => "自检用假工具";
        public string EmojiIcon => "🧪";
        public string Category => "自检";
        public int Order => 900;

        public System.Windows.FrameworkElement CreateView() => new System.Windows.Controls.TextBlock();
    }

    private sealed class MaintenanceCacheSourceTool : ITool, IToolCacheSource
    {
        private readonly IReadOnlyList<ToolCacheItem> _items;

        public MaintenanceCacheSourceTool(string id, string name, IReadOnlyList<ToolCacheItem> items)
        {
            Id = id;
            Name = name;
            _items = items;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => "自检用假工具（带额外缓存项）";
        public string EmojiIcon => "🧪";
        public string Category => "自检";
        public int Order => 901;

        public IReadOnlyList<ToolCacheItem> DescribeCacheItems() => _items;

        public System.Windows.FrameworkElement CreateView() => new System.Windows.Controls.TextBlock();
    }

    private sealed class MaintenanceThrowingCacheSourceTool : ITool, IToolCacheSource
    {
        public MaintenanceThrowingCacheSourceTool(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => "自检用假工具（报告缓存项时抛异常）";
        public string EmojiIcon => "🧪";
        public string Category => "自检";
        public int Order => 902;

        public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
            => throw new InvalidOperationException("模拟：单个工具读取缓存状态失败");

        public System.Windows.FrameworkElement CreateView() => new System.Windows.Controls.TextBlock();
    }
}
