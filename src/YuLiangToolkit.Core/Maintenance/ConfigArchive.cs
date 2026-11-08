using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Core.Maintenance;

/// <summary>配置包里的清单（<c>manifest.json</c>）：导入时用来确认「这是本程序的配置包」并展示摘要。</summary>
public sealed class ConfigArchiveManifest
{
    /// <summary>包类型标记（不是这个值就不是本程序的配置包）。</summary>
    public string Kind { get; set; } = "yuLiangToolkit-config";

    /// <summary>包格式版本（以后结构变化时用来迁移）。</summary>
    public int Version { get; set; } = ConfigArchive.CurrentVersion;

    /// <summary>导出时的程序版本（仅展示用，导入不校验）。</summary>
    public string? AppVersion { get; set; }

    /// <summary>导出时间。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>包里有多少个配置 JSON。</summary>
    public int ConfigFileCount { get; set; }

    /// <summary>包里有多少个数据文件（表情图 / 背景图 / 图标…）。</summary>
    public int DataFileCount { get; set; }

    /// <summary>配置涉及的功能 Id（「主页 / 外壳设置」是 shell；布局是 layout）。</summary>
    public List<string> ConfigToolIds { get; set; } = new();

    /// <summary>数据文件涉及的功能 Id。</summary>
    public List<string> DataToolIds { get; set; } = new();
}

/// <summary>导入时对 zip 的预览结果（<see cref="ConfigArchive.Probe"/>）。</summary>
public sealed class ConfigArchiveProbe
{
    /// <summary>是否是有效的配置包（false 时看 <see cref="Error"/>）。</summary>
    public bool Ok { get; init; }

    /// <summary>无效时的中文原因（「不是本程序的配置包」「文件读不出来」…）。</summary>
    public string? Error { get; init; }

    /// <summary>包里的清单（有效时才有）。</summary>
    public ConfigArchiveManifest? Manifest { get; init; }

    /// <summary>包的大小（字节）。</summary>
    public long ZipBytes { get; init; }
}

/// <summary>导出 / 导入的结果统计（引擎只报事实，文案由界面拼）。</summary>
public sealed class ConfigArchiveResult
{
    /// <summary>是否成功（false 时看 <see cref="Error"/>）。</summary>
    public bool Ok { get; init; }

    /// <summary>失败时的中文原因。</summary>
    public string? Error { get; init; }

    /// <summary>写进 / 读出的配置 JSON 数量。</summary>
    public int ConfigFileCount { get; init; }

    /// <summary>写进 / 读出的数据文件（表情图 / 背景图 / 图标…）数量。</summary>
    public int DataFileCount { get; init; }

    /// <summary>导出时：zip 文件大小（字节）；导入时为 0。</summary>
    public long ZipBytes { get; init; }

    /// <summary>导入时：因为本地已有同名文件而自动编号 <c>名字 (1).ext</c> 的数据文件数量。</summary>
    public int RenamedCount { get; init; }

    /// <summary>导入时：外来包里带了凭据类配置（按约定不导入）的功能 Id。</summary>
    public IReadOnlyList<string> SkippedCredentialToolIds { get; init; } = Array.Empty<string>();

    /// <summary>导入时：既不是配置也不是白名单数据、被跳过的条目数量（外来包里的陌生内容）。</summary>
    public int SkippedUnknownCount { get; init; }

    /// <summary>导入时：配置被覆盖的功能 Id（shell = 主页 / 外壳设置）。</summary>
    public IReadOnlyList<string> AffectedConfigToolIds { get; init; } = Array.Empty<string>();

    /// <summary>导入时：界面布局记忆（Config\layout.json）是否被覆盖。</summary>
    public bool LayoutImported { get; init; }
}

/// <summary>
/// 配置导入导出（「主页设置 → 配置导入导出」的引擎，纯逻辑、不碰界面，自检可直接调用）。
///
/// <para><b>包里有什么</b>：一个 zip = <c>manifest.json</c>（清单） + <c>Config/...</c>（所有配置 JSON，
/// 含外壳 config、各功能 config、界面布局 layout.json） + <c>Data/...</c>（白名单里的个人数据：
/// 表情包收藏图、外壳背景图 / 光标图、启动器自定义图标、KPS 按键图）。
/// <b>Cache、聊天记录、下载的游戏、播放列表这类大数据 / 隐私数据都不进包</b>。</para>
///
/// <para><b>凭据类配置整目录排除</b>（见 <see cref="CredentialConfigToolIds"/>）：邮箱授权码是 DPAPI 密文
/// （绑当前 Windows 用户，换机器解不开）、Spug 推送地址 / osu! 密钥是明文凭证 —— 项目文档本来就要求不要外传这些文件。</para>
///
/// <para><b>导入语义</b>：配置 JSON 整文件覆盖（该功能的选项整体替换成包里的）；数据文件<b>同名不覆盖</b>，
/// 自动编号 <c>名字 (1).ext</c>（与 <see cref="ToolStorage.Import"/> 的重名规则一致）；zip 里没有的本地文件保留。</para>
///
/// <para><b>路径安全</b>：zip 条目名先规范化（拒绝 <c>..</c> / 盘符 / 绝对路径 / 非法字符，规则与压缩包工具的
/// ArchivePathSafety 一致 —— Core 引用不到工具工程，这里是有意复制同一条规则），再拼到
/// <see cref="AppPaths.ConfigRoot"/> / <see cref="AppPaths.DataRoot"/> 下并做完整路径前缀复核；
/// 导入只放行 <c>Config/</c>、<c>Data/</c> 白名单与 manifest，包里的任何路径段都不被信任。</para>
///
/// <para><b>落盘并发</b>：导出前先把防抖窗口里未落盘的配置全部 <see cref="ToolConfigStore.Flush"/>（不用
/// FlushAll —— 那会把后台自动保存停掉）；导入对已加载的配置对象走
/// <see cref="ToolConfigStore.ApplyImportedJson"/>（占写盘锁 + 代际自增，同「重建配置」骨架），防止自动保存把旧值写回。</para>
/// </summary>
public static class ConfigArchive
{
    /// <summary>包格式当前版本（manifest.Version 必须等于它才接受导入）。</summary>
    public const int CurrentVersion = 1;

    /// <summary>清单条目名（zip 根下）。</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>配置条目前缀（zip 内一律用 <c>/</c> 分隔）。</summary>
    public const string ConfigPrefix = "Config/";

    /// <summary>数据条目前缀。</summary>
    public const string DataPrefix = "Data/";

    /// <summary>
    /// 凭据类配置（<c>Config\&lt;Id&gt;\</c> 整目录不导出、导入时跳过）：
    /// mail-collector = 邮箱代收的授权码密文（DPAPI）；spug-captcha = 推送地址是明文调用凭证；
    /// map-downloader = osu! 的 ClientSecret / AccessToken。
    /// </summary>
    public static readonly IReadOnlyList<string> CredentialConfigToolIds = new[]
    {
        "mail-collector",
        "spug-captcha",
        "map-downloader",
    };

    /// <summary>
    /// 会随包导出的数据白名单（<see cref="DataPrefix"/> 下的相对路径前缀，小写比较）：
    /// 都是「个人习惯」级别的小文件；表情图是需求点名要带的，背景 / 光标 / 图标不带走导入后就会失效。
    /// </summary>
    public static readonly IReadOnlyList<string> ExportableDataPrefixes = new[]
    {
        "meme-board/images/",   // 表情包收藏夹：收藏的图片
        "shell/background/",    // 外壳：背景图副本
        "shell/cursor/",        // 外壳：自定义光标图副本
        "launcher/icons/",      // 软件启动器：自定义图标
        "kps/",                 // KPS 按键统计：按键图片
    };

    // ==================== 导出 ====================

    /// <summary>
    /// 把当前所有设置导出成一个配置包 zip：先把手头未落盘的配置冲盘，再按白名单打包。
    /// 失败时返回 <see cref="ConfigArchiveResult.Ok"/> = false，并清掉写了一半的文件。
    /// </summary>
    public static ConfigArchiveResult Export(string zipPath)
    {
        try
        {
            // ① 防抖窗口里的改动先落盘（不能用 FlushAll：它会把后台自动保存停掉）
            foreach (var store in ToolConfigStore.All)
            {
                try { store.Flush(); }
                catch { /* 单个 store 落盘失败不影响导出（它磁盘上是上一次的内容） */ }
            }

            // ② 列出要打包的文件
            var (configEntries, configToolIds) = CollectConfigEntries();
            if (configEntries.Count == 0)
                return Fail("还没有任何配置可以导出（先随便打开一个功能 / 改一个设置再试）。");
            var (dataEntries, dataToolIds) = CollectDataEntries();

            // ③ 写 zip（清单最后写，计数才有得填）
            var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var entry in configEntries) WriteEntry(archive, entry);
                foreach (var entry in dataEntries) WriteEntry(archive, entry);

                var manifest = new ConfigArchiveManifest
                {
                    CreatedAt = DateTime.Now,
                    AppVersion = typeof(ConfigArchive).Assembly.GetName().Version?.ToString(),
                    ConfigFileCount = configEntries.Count,
                    DataFileCount = dataEntries.Count,
                    ConfigToolIds = configToolIds,
                    DataToolIds = dataToolIds,
                };
                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using (var manifestStream = manifestEntry.Open())
                using (var writer = new StreamWriter(manifestStream))
                {
                    writer.Write(JsonFileStore.Serialize(manifest));
                }
            }

            var zipBytes = new FileInfo(zipPath).Length;
            return new ConfigArchiveResult
            {
                Ok = true,
                ConfigFileCount = configEntries.Count,
                DataFileCount = dataEntries.Count,
                ZipBytes = zipBytes,
            };
        }
        catch (Exception ex)
        {
            TryDelete(zipPath);
            return Fail($"导出失败：{ex.Message}");
        }
    }

    // ==================== 预览 / 导入 ====================

    /// <summary>读一个 zip 的清单做校验，给导入前的确认框用（不动任何本地文件）。</summary>
    public static ConfigArchiveProbe Probe(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath))
                return new ConfigArchiveProbe { Ok = false, Error = "文件不存在。" };

            var zipBytes = new FileInfo(zipPath).Length;
            using var stream = File.OpenRead(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var manifestEntry = archive.GetEntry(ManifestEntryName);
            if (manifestEntry is null)
                return new ConfigArchiveProbe { Ok = false, Error = "这不是本程序的配置包（zip 里没有 manifest.json 清单）。" };

            string json;
            using (var entryStream = manifestEntry.Open())
            using (var reader = new StreamReader(entryStream))
            {
                json = reader.ReadToEnd();
            }

            ConfigArchiveManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ConfigArchiveManifest>(json, JsonFileStore.Options);
            }
            catch
            {
                manifest = null;
            }

            if (manifest is null || !string.Equals(manifest.Kind, "yuLiangToolkit-config", StringComparison.Ordinal))
                return new ConfigArchiveProbe { Ok = false, Error = "这不是本程序的配置包（清单内容对不上）。" };
            if (manifest.Version > CurrentVersion)
                return new ConfigArchiveProbe
                {
                    Ok = false,
                    Error = $"配置包版本较新（v{manifest.Version}，本程序最高认 v{CurrentVersion}），请先升级本程序再导入。",
                };

            return new ConfigArchiveProbe
            {
                Ok = true,
                Manifest = manifest,
                ZipBytes = zipBytes,
            };
        }
        catch (Exception ex)
        {
            return new ConfigArchiveProbe { Ok = false, Error = $"读不出这个压缩包：{ex.Message}" };
        }
    }

    /// <summary>
    /// 导入一个配置包：配置 JSON 整文件覆盖（已加载的配置对象原地换值并热重载），
    /// 数据文件同名不覆盖、自动编号；凭据类配置与白名单之外的陌生条目跳过并计数。
    /// </summary>
    public static ConfigArchiveResult Import(string zipPath)
    {
        try
        {
            var probe = Probe(zipPath);
            if (!probe.Ok || probe.Manifest is null)
                return Fail(probe.Error ?? "这不是本程序的配置包。");

            var configCount = 0;
            var dataCount = 0;
            var renamedCount = 0;
            var unknownCount = 0;
            var credentialToolIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var affectedToolIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var layoutImported = false;

            using var stream = File.OpenRead(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                // 用 FullName（含包内目录）而不是 Name（只有文件名），否则 Config/ 前缀永远匹配不上
                var key = NormalizeEntryKey(entry.FullName, out var rejected);
                if (key is null)
                {
                    if (rejected) unknownCount++;   // 目录条目静默跳过；恶意条目计入「跳过」
                    continue;
                }

                // ---------- 配置 ----------
                if (key.StartsWith(ConfigPrefix, StringComparison.Ordinal))
                {
                    // 解析目标路径（白名单前缀 + 拒绝 .. / 盘符 / 非法字符 + 完整路径前缀复核）
                    if (ResolveEntryTarget(key, ConfigPrefix, AppPaths.ConfigRoot) is null)
                    {
                        unknownCount++;
                        continue;
                    }
                    var handled = ImportConfigEntry(key, out var isLayout, out var toolId, out var isCredential);
                    if (isCredential)
                    {
                        credentialToolIds.Add(toolId);
                        continue;
                    }
                    if (!handled)
                    {
                        unknownCount++;
                        continue;
                    }

                    if (WriteEntryToConfig(key, entry))
                    {
                        configCount++;
                        if (isLayout) layoutImported = true;
                        else affectedToolIds.Add(toolId);
                    }
                    else
                    {
                        return Fail("导入失败：某个配置文件写不进去（可能正被别的程序占用），本次导入已中止，之前的部分文件已生效。");
                    }
                    continue;
                }

                // ---------- 数据（白名单） ----------
                if (key.StartsWith(DataPrefix, StringComparison.Ordinal)
                    && IsExportableDataPath(key[DataPrefix.Length..]))
                {
                    if (ResolveEntryTarget(key, DataPrefix, AppPaths.DataRoot) is null)
                    {
                        unknownCount++;
                        continue;
                    }
                    if (WriteEntryToDataUnique(key, entry, out var renamed))
                    {
                        dataCount++;
                        if (renamed) renamedCount++;
                    }
                    else
                    {
                        return Fail("导入失败：某个数据文件写不进去（可能正被别的程序占用），本次导入已中止，之前的部分文件已生效。");
                    }
                    continue;
                }

                unknownCount++;
            }

            return new ConfigArchiveResult
            {
                Ok = true,
                ConfigFileCount = configCount,
                DataFileCount = dataCount,
                RenamedCount = renamedCount,
                SkippedCredentialToolIds = credentialToolIds.ToList(),
                SkippedUnknownCount = unknownCount,
                AffectedConfigToolIds = affectedToolIds.ToList(),
                LayoutImported = layoutImported,
            };
        }
        catch (Exception ex)
        {
            return Fail($"导入失败：{ex.Message}");
        }
    }

    /// <summary>这段 Data 相对路径（<c>meme-board/images/a.png</c>）是否在导出白名单里。</summary>
    public static bool IsExportableDataPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var candidate = relativePath.Replace('\\', '/').ToLowerInvariant();
        return ExportableDataPrefixes.Any(prefix => candidate.StartsWith(prefix, StringComparison.Ordinal));
    }

    // ---------- 导出：收集 ----------

    private static (List<(string Key, string Path, long Bytes)> Entries, List<string> ToolIds) CollectConfigEntries()
    {
        var entries = new List<(string, string, long)>();
        var toolIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = AppPaths.ConfigRoot;
        if (!Directory.Exists(root)) return (entries, toolIds.ToList());

        // 整树枚举：结构就是 Config\<功能Id>\*.json（外加根下的 layout.json），未来多一层也能兜住
        foreach (var path in EnumerateFilesSafe(root))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var name = Path.GetFileName(relative);
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains(".bad-", StringComparison.OrdinalIgnoreCase)) continue;   // 损坏备份不带走

            var segments = relative.Split('/');
            string toolId;
            if (segments.Length == 1)
            {
                // 根目录下只认 layout.json（界面布局记忆），其它散文件不是配置
                if (!name.Equals(LayoutMemory.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                toolId = "layout";
            }
            else
            {
                toolId = segments[0];
                if (CredentialConfigToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase)) continue;
            }

            try
            {
                var bytes = new FileInfo(path).Length;
                entries.Add((ConfigPrefix + relative, path, bytes));
                toolIds.Add(toolId);
            }
            catch { /* 单个文件取不到大小就跳过 */ }
        }

        return (entries, toolIds.ToList());
    }

    private static (List<(string Key, string Path, long Bytes)> Entries, List<string> ToolIds) CollectDataEntries()
    {
        var entries = new List<(string, string, long)>();
        var toolIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prefix in ExportableDataPrefixes)
        {
            // prefix 形如 "meme-board/images/"：前两段决定 Data 下的起始目录，其余段可能还有子目录
            var segments = prefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var dir = Path.Combine(new[] { AppPaths.DataRoot }.Concat(segments).ToArray());
            if (!Directory.Exists(dir)) continue;

            foreach (var path in EnumerateFilesSafe(dir))
            {
                var relative = Path.GetRelativePath(AppPaths.DataRoot, path).Replace('\\', '/');
                if (!IsExportableDataPath(relative)) continue;   // 目录里混进白名单外的文件（例如说明文件）不带

                try
                {
                    var bytes = new FileInfo(path).Length;
                    entries.Add((DataPrefix + relative, path, bytes));
                    toolIds.Add(segments[0]);
                }
                catch { /* 单个文件取不到大小就跳过 */ }
            }
        }

        return (entries, toolIds.ToList());
    }

    /// <summary>枚举目录里的文件；目录中途不可读就返回已枚举到的部分（导出 / 导入都不该因一个目录挂掉）。</summary>
    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        var found = new List<string>();
        try
        {
            found.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
        }
        catch { /* 拿到多少算多少 */ }
        return found;
    }

    private static void WriteEntry(ZipArchive archive, (string Key, string Path, long Bytes) entry)
    {
        var zipEntry = archive.CreateEntry(entry.Key, CompressionLevel.Optimal);
        using var entryStream = zipEntry.Open();
        using var source = File.OpenRead(entry.Path);
        source.CopyTo(entryStream);
    }

    // ---------- 导入：条目名安全 ----------

    /// <summary>
    /// zip 条目名 → 规范化的包内相对路径（<c>/</c> 分隔）。
    /// 目录条目返回 null 且 rejected = false（静默跳过）；带 <c>..</c> / 盘符 / 绝对路径 / 非法字符的
    /// 返回 null 且 rejected = true（按陌生条目计数）。规则与压缩包工具的 ArchivePathSafety 一致（有意复制）。
    /// </summary>
    private static string? NormalizeEntryKey(string entryName, out bool rejected)
    {
        rejected = false;
        try
        {
            if (string.IsNullOrWhiteSpace(entryName)) return null;
            var candidate = entryName.Trim().Replace('\\', '/');
            if (candidate.EndsWith('/')) return null;                       // 目录条目（ZipArchive 一般不会给，兜一下）
            if (candidate.Contains("//")) return null;                      // 空段
            if (candidate.Contains(':')) goto bad;                          // 盘符 / NTFS 数据流
            if (candidate.StartsWith('/')) goto bad;                        // 绝对路径

            foreach (var segment in candidate.Split('/'))
            {
                if (segment.Length == 0 || segment is "." or "..") goto bad;
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) goto bad;
            }

            return candidate;

            bad:
            rejected = true;
            return null;
        }
        catch
        {
            rejected = true;
            return null;
        }
    }

    // ---------- 导入：写盘 ----------

    /// <summary>分类一个 Config 条目：是不是 layout、属于哪个功能、是不是凭据类（凭据类只报不写）。</summary>
    private static bool ImportConfigEntry(string key, out bool isLayout, out string toolId, out bool isCredential)
    {
        isLayout = false;
        isCredential = false;
        toolId = "";

        var relative = key[ConfigPrefix.Length..];
        var segments = relative.Split('/');
        var name = segments[^1];

        // 损坏备份 / 临时文件不该出现在包里，带进来也不导入
        if (name.Contains(".bad-", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return false;

        if (segments.Length == 1)
        {
            if (!name.Equals(LayoutMemory.FileName, StringComparison.OrdinalIgnoreCase)) return false;
            isLayout = true;
            toolId = "layout";
            return true;
        }

        toolId = segments[0];
        if (CredentialConfigToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase)) isCredential = true;
        return true;
    }

    /// <summary>把一个 Config 条目写到本地（配置对象已加载 → 原地换值并写盘；否则直接写文件）。</summary>
    private static bool WriteEntryToConfig(string key, ZipArchiveEntry entry)
    {
        // 目标路径只由引擎按白名单前缀拼出来（不信任 zip 里的任何路径段），再做一次完整路径复核
        var target = ResolveEntryTarget(key, ConfigPrefix, AppPaths.ConfigRoot);
        if (target is null) return false;

        var json = ReadEntryText(entry);
        if (json is null) return false;

        // layout.json：先写文件再让 LayoutMemory 清缓存（下次取值重读，正开着的页面也会重排）
        var relative = key[ConfigPrefix.Length..];
        var segments = relative.Split('/');
        if (segments.Length == 1)
        {
            if (!JsonFileStore.WriteJson(target, json)) return false;
            LayoutMemory.ReloadFromDisk();
            return true;
        }

        var toolId = segments[0];
        var fileName = segments[^1];
        var store = ToolConfigStore.TryGet(toolId, fileName);
        if (store is not null && string.Equals(store.FilePath, target, StringComparison.OrdinalIgnoreCase))
        {
            return store.ApplyImportedJson(json);   // 已加载 → 原地换值 + 立刻写回（界面热重载）
        }

        return JsonFileStore.WriteJson(target, json);   // 没加载过 → 直接写盘，下次打开按文件内容来
    }

    /// <summary>
    /// 把一个 Data 条目写到本地：同名不覆盖，自动编号 <c>名字 (1).ext</c>（与 ToolStorage.Import 的重名规则一致）。
    /// <paramref name="renamed"/> 回报这次是否因重名而编号。返回是否写入成功。
    /// </summary>
    private static bool WriteEntryToDataUnique(string key, ZipArchiveEntry entry, out bool renamed)
    {
        renamed = false;
        var target = ResolveEntryTarget(key, DataPrefix, AppPaths.DataRoot);
        if (target is null) return false;

        if (File.Exists(target))
        {
            target = ToolStorage.MakeUniquePath(target);
            renamed = true;
        }

        try
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var entryStream = entry.Open();
            using var output = File.Create(target);
            entryStream.CopyTo(output);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadEntryText(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    // ---------- 内部：路径解析 / 条目名校验 ----------

    /// <summary>
    /// 把一个包内相对路径（<c>Config/shell/config.json</c> / <c>Data/meme-board/images/a.png</c>）
    /// 解析成目标根目录下的绝对路径，并阻止越出该根目录。
    /// 白名单前缀 + 前缀复核双保险，规则与 <see cref="ToolStorage"/> 的 ResolvePath 一致。
    /// </summary>
    private static string? ResolveEntryTarget(string key, string prefix, string root)
    {
        // 条目名规范化：目录条目（以 / 结尾）静默跳过；带 .. / 盘符 / 绝对路径 / 非法字符的直接拒绝
        try
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var candidate = key.Trim().Replace('\\', '/');
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) return null;
            if (candidate.EndsWith('/')) return null;                        // 目录条目（ZipArchive 一般不会给，兜一下）
            if (candidate.Contains("//")) return null;                       // 空段
            if (candidate.Contains(':')) return null;                        // 盘符 / NTFS 数据流
            if (candidate.StartsWith('/')) return null;                      // 绝对路径

            var relative = candidate[prefix.Length..];
            foreach (var segment in relative.Split('/'))
            {
                if (segment.Length == 0 || segment is "." or "..") return null;
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            }

            var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            return IsInsideRoot(root, target) ? target : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>目标完整路径必须落在根目录里面（不许 <c>..</c> / 换盘符逃出去，与 ToolStorage.ResolvePath 同一条约束）。</summary>
    private static bool IsInsideRoot(string root, string target)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            var targetFull = Path.GetFullPath(target);
            var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            return targetFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static ConfigArchiveResult Fail(string error) => new() { Ok = false, Error = error };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 删不掉就算了 */ }
    }
}
