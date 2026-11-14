using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Vsrg.Core;

/// <summary>
/// 一张谱面集的**缓存索引**（<c>mapset.json</c>）。由导入时生成，之后打开工具时只读它 ——
/// 不用每次重新解析所有 .osu（谱面集多了会很慢）。手改没意义（下次导入会覆盖）。
/// </summary>
public sealed class MapsetIndex
{
    /// <summary>谱面集文件夹名（= 文件夹名字，仅用于展示）。</summary>
    public string FolderName { get; set; } = "";

    /// <summary>标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>艺术家。</summary>
    public string Artist { get; set; } = "";

    /// <summary>导入时间。</summary>
    public DateTime ImportedAt { get; set; } = DateTime.Now;

    /// <summary>这张谱面集里的所有难度。</summary>
    public List<MapsetDifficulty> Difficulties { get; set; } = new();

    /// <summary>「艺术家 - 标题」（列表里显示的一行）。</summary>
    public string TitleLine => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";

    /// <summary>难度个数。</summary>
    public int DifficultyCount => Difficulties.Count;
}

/// <summary>谱面集里的一个难度（mapset.json 里的一行）。</summary>
public sealed class MapsetDifficulty
{
    /// <summary>难度名（osu! 的 <c>[Metadata] Version</c>）。</summary>
    public string Name { get; set; } = "";

    /// <summary>谱师。</summary>
    public string Creator { get; set; } = "";

    /// <summary>键数（1~10）。</summary>
    public int KeyCount { get; set; } = 4;

    /// <summary>谱面文件相对谱面集文件夹的路径（通常是文件名）。</summary>
    public string FileName { get; set; } = "";

    /// <summary>note 总数（普通键 + 面条）。</summary>
    public int NoteCount { get; set; }

    /// <summary>谱面长度（秒）。</summary>
    public double LengthSeconds { get; set; }

    /// <summary>「难度名 · 4K」这样的展示文本。</summary>
    public string DisplayLine
        => $"{(string.IsNullOrWhiteSpace(Name) ? "未命名难度" : Name)} · {KeyCount}K";

    /// <summary>note 数与长度的展示文本（<c>1234 键 · 2:05</c>）。</summary>
    public string DetailLine
    {
        get
        {
            var total = (int)Math.Round(LengthSeconds);
            return $"{NoteCount} 键 · {total / 60}:{total % 60:00}";
        }
    }
}

/// <summary>
/// **谱面集库**：<c>Data\vsrg\Mapset\</c> 下一个谱面集一个文件夹。负责
/// 扫描 / 导入 <c>.osz</c> / 删除 / 定位文件夹。
///
/// <para><b>目录结构</b>（用户要求的样子）：</para>
/// <code>
/// Data\vsrg\Mapset\
///   ├── Artist - Title\        ← 一首歌一个文件夹
///   │   ├── audio.ogg          ← 音频（谱面 [General] AudioFilename 指向的那个文件，原名）
///   │   ├── bg.jpg             ← 背景图（[Events] 里声明的，没有就取文件夹里第一张图）
///   │   ├── Artist - Title (Creator) [4K].osu   ← 各难度谱面
///   │   └── mapset.json        ← 程序读的索引（难度清单 / 键数 / 导入时间）
///   └── ...
/// </code>
///
/// <para><b>.osz 就是 zip</b>：只解出谱面 / 音频 / 图片（视频与 hitsound 跳过，省空间）；
/// 每条解压路径都要过安全检查，挡住 <c>..</c> / 绝对路径（压缩包路径穿越）。</para>
/// </summary>
public sealed class MapsetLibrary
{
    /// <summary>谱面集索引文件名。</summary>
    public const string IndexFileName = "mapset.json";

    /// <summary>能被当成音频带过来的扩展名。</summary>
    public static readonly string[] AudioExtensions = { ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".aac", ".wma", ".opus" };

    /// <summary>能被当成背景图带过来的扩展名。</summary>
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    /// <summary>归档里这些目录（osu! 惯例）不当作谱面内容。</summary>
    private static readonly string[] SkippedFolders = { "hitsound", "samples", "video" };

    private readonly ToolStorage _storage;

    /// <summary>用「Data\vsrg」这个功能数据文件夹造一个库（谱面集在它的 <c>Mapset</c> 子目录）。</summary>
    public MapsetLibrary(ToolStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <summary>
    /// 谱面集根目录（<b>完整路径</b>）。这是本类唯一允许出现绝对路径的地方：
    /// 谱面集是用户自己的数据，导入 / 删除都在这个目录下操作。
    /// </summary>
    public string MapsetRoot => Path.Combine(_storage.Directory, "Mapset");

    /// <summary>确保根目录存在，返回它的路径。</summary>
    public string EnsureRoot()
    {
        _storage.EnsureCreated();
        try { Directory.CreateDirectory(MapsetRoot); } catch { /* 只读环境忽略 */ }
        return MapsetRoot;
    }

    /// <summary>
    /// 扫描全部谱面集（按「艺术家 - 标题」排序）。
    /// 索引文件缺失 / 损坏的谱面集**现场重建**（用户手工往文件夹里塞过谱面时也不至于看不见）。
    /// </summary>
    public IReadOnlyList<MapsetEntry> Scan()
    {
        var result = new List<MapsetEntry>();
        if (!Directory.Exists(MapsetRoot)) return result;

        foreach (var dir in Directory.EnumerateDirectories(MapsetRoot))
        {
            try
            {
                var entry = LoadEntry(dir);
                if (entry is not null) result.Add(entry);
            }
            catch { /* 单个谱面集读不了就跳过，别让整个列表变空 */ }
        }

        return result
            .OrderBy(e => e.Index.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Index.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.FolderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>读一个谱面集文件夹；索引缺失时按文件夹里的 .osu 现场重建（不写盘，扫描要快）。</summary>
    private MapsetEntry? LoadEntry(string folderPath)
    {
        var index = ReadIndex(folderPath);
        if (index is null || index.Difficulties.Count == 0)
        {
            index = RebuildIndex(folderPath);
            if (index is null) return null;
        }

        // 索引里记的谱面文件可能被用户删了：丢掉这些行（免得列表点进去报错）
        var alive = index.Difficulties
            .Where(d => File.Exists(Path.Combine(folderPath, d.FileName)))
            .ToList();

        if (alive.Count == 0) return null;
        if (alive.Count != index.Difficulties.Count) index.Difficulties = alive;

        return new MapsetEntry(folderPath, index);
    }

    /// <summary>读 <c>mapset.json</c>（不存在 / 损坏返回 null）。</summary>
    private static MapsetIndex? ReadIndex(string folderPath)
    {
        var path = Path.Combine(folderPath, IndexFileName);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path, new UTF8Encoding(false));
            var index = JsonSerializer.Deserialize<MapsetIndex>(json, JsonFileStore.Options);
            if (index is null) return null;
            index.FolderName = Path.GetFileName(folderPath);
            return index;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按文件夹里现有的 .osu 现场重建一份索引（不写盘）。一个 mania 谱面都没有返回 null。
    /// </summary>
    public static MapsetIndex? RebuildIndex(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return null;

        var difficulties = new List<MapsetDifficulty>();
        string? title = null;
        string? artist = null;

        foreach (var file in Directory.EnumerateFiles(folderPath, "*.osu", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!OsuChartReader.TryLoad(file, out var chart, out _) || chart is null) continue;

            title ??= chart.Title;
            artist ??= chart.Artist;

            difficulties.Add(new MapsetDifficulty
            {
                Name = chart.DifficultyName,
                Creator = chart.Creator,
                KeyCount = chart.KeyCount,
                FileName = Path.GetFileName(file),
                NoteCount = chart.Notes.Count,
                LengthSeconds = chart.LengthSeconds,
            });
        }

        if (difficulties.Count == 0) return null;

        return new MapsetIndex
        {
            FolderName = Path.GetFileName(folderPath),
            Title = title ?? Path.GetFileName(folderPath),
            Artist = artist ?? "",
            ImportedAt = SafeCreationTime(folderPath),
            Difficulties = difficulties,
        };
    }

    private static DateTime SafeCreationTime(string folderPath)
    {
        try { return Directory.GetCreationTime(folderPath); } catch { return DateTime.Now; }
    }

    /// <summary>把一个谱面集文件夹里的某个难度读成可游玩的谱面（含音频与背景路径）。</summary>
    public static Chart? LoadChart(MapsetEntry entry, MapsetDifficulty difficulty)
    {
        var path = Path.Combine(entry.FolderPath, difficulty.FileName);
        return OsuChartReader.TryLoad(path, out var chart, out _) ? chart : null;
    }

    /// <summary>把索引写进谱面集文件夹（导入时用）。</summary>
    public static bool WriteIndex(string folderPath, MapsetIndex index)
        => JsonFileStore.Write(Path.Combine(folderPath, IndexFileName), index);

    /// <summary>
    /// 导入一个 <c>.osz</c>。
    ///
    /// <para>步骤：① 先在临时目录解出需要的文件；② 逐张判定 .osu 是不是 mania（mode 3）且键数 1~10；
    /// 一张都没有就直接失败（不留下半个谱面集）；③ 有的话把整个临时目录挪进
    /// <c>Mapset\&lt;艺术家 - 标题&gt;\</c>（重名自动加序号）；④ 生成索引。</para>
    /// </summary>
    public OszImportResult Import(string oszPath)
    {
        if (string.IsNullOrWhiteSpace(oszPath) || !File.Exists(oszPath))
            return OszImportResult.Failed("文件不存在。");

        if (!Path.GetExtension(oszPath).Equals(".osz", StringComparison.OrdinalIgnoreCase))
            return OszImportResult.Failed("只支持 .osz 文件。");

        EnsureRoot();

        string tempDir;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "ylt-vsrg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            return OszImportResult.Failed($"建临时目录失败（{ex.GetType().Name}）：{ex.Message}");
        }

        try
        {
            var skipped = ExtractRelevantFiles(oszPath, tempDir, out var foundAudio, out var foundImage);
            if (skipped is null) return OszImportResult.Failed("压缩包打不开（文件损坏或不是 zip 格式的 .osz）。");

            // ---- 逐张判定 .osu ----
            var charts = new List<(string File, Chart Chart)>();
            var rejected = new List<string>();

            foreach (var osuFile in Directory.EnumerateFiles(tempDir, "*.osu", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (!OsuChartReader.TryLoad(osuFile, out var chart, out var error) || chart is null)
                {
                    rejected.Add($"{Path.GetFileName(osuFile)}：{error}");
                    File.Delete(osuFile);      // 非 mania 的谱面不留（用户要的是 mania 专用库）
                    continue;
                }

                charts.Add((osuFile, chart));
            }

            if (charts.Count == 0)
            {
                var reason = rejected.Count > 0
                    ? "这个 .osz 里没有可以玩的 osu!mania 谱面：" + string.Join("；", rejected.Take(3))
                    : "这个 .osz 里没有 .osu 谱面文件。";
                return OszImportResult.Failed(reason);
            }

            // ---- 元数据：取第一张谱面的艺术家 / 标题 ----
            var first = charts[0].Chart;
            var folderName = SanitizeFolderName(
                string.IsNullOrWhiteSpace(first.Artist) ? first.Title : $"{first.Artist} - {first.Title}");
            if (folderName.Length == 0) folderName = "未命名单曲";

            var targetDir = MakeUniqueFolder(MapsetRoot, folderName);

            // 顺手拾一下音频 / 背景：谱面里声明的文件名可能大小写不一致（osu! 在 Windows 上不区分，
            // 但解压后文件名是原文），AudioPath / BackgroundPath 已经是「真实存在的完整路径」，
            // 直接按它的文件名把临时目录里的文件重命名过去更稳。
            NormalizeReferencedFiles(tempDir, charts, foundAudio, foundImage);

            Directory.Move(tempDir, targetDir);
            tempDir = "";   // 已经挪走，finally 里不要再删

            var index = new MapsetIndex
            {
                FolderName = Path.GetFileName(targetDir),
                Title = first.Title,
                Artist = first.Artist,
                ImportedAt = DateTime.Now,
                Difficulties = new List<MapsetDifficulty>(),
            };

            foreach (var (file, chart) in charts)
            {
                index.Difficulties.Add(new MapsetDifficulty
                {
                    Name = chart.DifficultyName,
                    Creator = chart.Creator,
                    KeyCount = chart.KeyCount,
                    FileName = Path.GetFileName(file),
                    NoteCount = chart.Notes.Count,
                    LengthSeconds = chart.LengthSeconds,
                });
            }

            WriteIndex(targetDir, index);

            return new OszImportResult
            {
                Success = true,
                FolderPath = targetDir,
                Index = index,
                RejectedCharts = rejected,
                SkippedFiles = skipped.Value,
            };
        }
        catch (Exception ex)
        {
            return OszImportResult.Failed($"导入失败（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            if (tempDir.Length > 0)
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch { /* 临时目录删不掉不影响结果 */ }
            }
        }
    }

    /// <summary>
    /// 从 <c>.osz</c> 里解出需要的文件（.osu / 音频 / 图片）。返回被跳过的文件数；打不开返回 null。
    /// </summary>
    private static int? ExtractRelevantFiles(string oszPath, string targetDir,
        out bool foundAudio, out bool foundImage)
    {
        foundAudio = false;
        foundImage = false;
        var skipped = 0;

        try
        {
            using var zip = ZipFile.OpenRead(oszPath);
            foreach (var entry in zip.Entries)
            {
                // 目录项（以 / 结尾、长度为 0）直接跳过
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("\\", StringComparison.Ordinal)) continue;

                if (!TryMapEntryName(entry.FullName, out var relativeName))
                {
                    skipped++;
                    continue;
                }

                var ext = Path.GetExtension(relativeName).ToLowerInvariant();
                if (!IsWantedExtension(ext))
                {
                    skipped++;
                    continue;
                }

                var destination = Path.Combine(targetDir, relativeName);
                try
                {
                    var parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    // 同名冲突（压缩包里有重名项）：保留第一个，其余的加序号
                    if (File.Exists(destination)) destination = ToolStorage.MakeUniquePath(destination);

                    entry.ExtractToFile(destination, overwrite: false);

                    if (AudioExtensions.Contains(ext)) foundAudio = true;
                    if (ImageExtensions.Contains(ext)) foundImage = true;
                }
                catch
                {
                    skipped++;
                }
            }

            return skipped;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 压缩包里的条目名 → 安全的相对路径（挡路径穿越）。
    ///
    /// <para>osu! 的 .osz 都是平铺的，但**不能假设**：恶意 / 损坏的压缩包可能带
    /// <c>../../evil.exe</c> 或绝对路径。这里统一：
    /// ① 任何一段是 <c>..</c> 的条目**整条拒绝**（不是取 basename 收下 —— 见下面的注释）；
    /// ② 只取文件名部分（.osz 里的内容都是平铺的，丢掉目录层级最省事）；
    /// ③ 空名 / 带冒号（备用数据流）/ 以点开头的隐藏名直接拒绝。</para>
    ///
    /// <para>⚠ 为什么不「只取 basename」收下：那样 <c>../../evil.exe</c> 会被静默改写成
    /// <c>evil.exe</c> 解出来 —— 虽然没逃出目录，但等于把攻击者放的文件塞进了用户谱面集，
    /// 而且日志上看起来像「导入成功」。宁可整条丢掉并在结果里报「跳过了 N 个文件」。</para>
    /// </summary>
    public static bool TryMapEntryName(string entryName, out string relativeName)
    {
        relativeName = "";

        if (string.IsNullOrWhiteSpace(entryName)) return false;

        var normalized = entryName.Replace('\\', '/').Trim();

        // ① 绝对路径 / 盘符 / UNC 整条拒绝（否则 basename 会把 "C:\a.txt" 收成 "a.txt"）
        if (normalized.StartsWith('/')) return false;
        if (normalized.Length >= 2 && normalized[1] == ':') return false;

        // ② 任何一段是 ".." → 整条拒绝（含 "../a.txt" / "a/../../b" 这类）
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Trim() == "..") return false;
        }

        var slash = normalized.LastIndexOf('/');
        var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        name = name.Trim();

        if (name.Length == 0) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name.Contains(':', StringComparison.Ordinal)) return false;
        if (name.StartsWith('.')) return false;
        if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)) return false;

        relativeName = name;
        return true;
    }

    /// <summary>
    /// 看起来像「没声明名字」的音频 / 背景文件？导入时可以兜底当成整张谱面集的音频 / 背景。
    /// </summary>
    private static bool IsWantedExtension(string extension)
    {
        if (extension == ".osu") return true;
        if (AudioExtensions.Contains(extension)) return true;
        if (ImageExtensions.Contains(extension)) return true;
        return false;
    }

    /// <summary>
    /// 谱面里声明的音频 / 背景文件名可能和实际文件名大小写不同（osu! 在 Windows 上不区分）。
    /// 这里把临时目录里的文件改名成「谱面里声明的那个名字」，保证之后按名字能找到。
    /// </summary>
    private static void NormalizeReferencedFiles(string tempDir, List<(string File, Chart Chart)> charts,
        bool foundAudio, bool foundImage)
    {
        // 音频：只在恰好一个音频文件时改名（多个的话猜不出哪个是主音频，靠谱面声明的名字也能对上）
        try
        {
            var audioFiles = Directory.EnumerateFiles(tempDir)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            if (foundAudio && audioFiles.Count == 1)
            {
                var declared = charts
                    .Select(c => Path.GetFileName(c.Chart.AudioFileName.Replace('\\', '/')))
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

                if (!string.IsNullOrWhiteSpace(declared))
                {
                    var target = Path.Combine(tempDir, declared);
                    if (!File.Exists(target) &&
                        !string.Equals(Path.GetFileName(audioFiles[0]), declared, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(audioFiles[0], target);
                    }
                }
            }

            // 背景：同理，只有一个图片文件时按谱面声明的名字改名
            var imageFiles = Directory.EnumerateFiles(tempDir)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            if (foundImage && imageFiles.Count == 1)
            {
                var declared = charts
                    .Select(c => Path.GetFileName(c.Chart.BackgroundFileName.Replace('\\', '/')))
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

                if (!string.IsNullOrWhiteSpace(declared))
                {
                    var target = Path.Combine(tempDir, declared);
                    if (!File.Exists(target) &&
                        !string.Equals(Path.GetFileName(imageFiles[0]), declared, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(imageFiles[0], target);
                    }
                }
            }
        }
        catch
        {
            // 改名失败不影响导入（谱面自己按原名找不到音频就是「缺音频」，界面上会提示）
        }
    }

    /// <summary>
    /// 谱面集文件夹名：去掉 Windows 非法字符，截到 80 字，去掉首尾的点和空格（不许 <c>.</c> / <c>..</c>）。
    /// </summary>
    public static string SanitizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (Array.IndexOf(invalid, ch) >= 0) continue;
            builder.Append(ch);
        }

        var cleaned = builder.ToString().Trim().Trim('.', ' ');
        if (cleaned.Length > 80) cleaned = cleaned[..80].Trim();
        return cleaned;
    }

    /// <summary>在 <paramref name="root"/> 下找一个不重名的文件夹名（<c>a</c> → <c>a (2)</c>）。</summary>
    public static string MakeUniqueFolder(string root, string folderName)
    {
        var candidate = Path.Combine(root, folderName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;

        for (var i = 2; i < 10000; i++)
        {
            candidate = Path.Combine(root, $"{folderName} ({i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }

        return Path.Combine(root, $"{folderName} ({Guid.NewGuid():N})");
    }

    /// <summary>删除一个谱面集（只删这一个文件夹；调用方负责二次确认）。</summary>
    public bool Delete(MapsetEntry entry)
    {
        try
        {
            // 只允许删 Mapset 根目录下的直接子目录（防手改索引里的路径把别处删掉）
            var full = Path.GetFullPath(entry.FolderPath);
            var root = Path.GetFullPath(MapsetRoot) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(MapsetRoot).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在资源管理器里打开谱面集根目录（不存在则先建）。</summary>
    public bool OpenRootInExplorer()
    {
        EnsureRoot();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = MapsetRoot,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在资源管理器里定位到某个谱面集文件夹。</summary>
    public bool RevealInExplorer(MapsetEntry entry)
    {
        try
        {
            if (!Directory.Exists(entry.FolderPath)) return OpenRootInExplorer();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{entry.FolderPath}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>扫描到的一个谱面集（文件夹 + 索引）。</summary>
public sealed class MapsetEntry
{
    /// <summary>构造。</summary>
    public MapsetEntry(string folderPath, MapsetIndex index)
    {
        FolderPath = folderPath;
        Index = index;
    }

    /// <summary>谱面集文件夹的完整路径。</summary>
    public string FolderPath { get; }

    /// <summary>索引（标题 / 艺术家 / 难度清单）。</summary>
    public MapsetIndex Index { get; }

    /// <summary>文件夹名。</summary>
    public string FolderName => Path.GetFileName(FolderPath);

    /// <summary>列表里显示的一行。</summary>
    public string TitleLine => Index.TitleLine;

    /// <summary>列表里的副标题（难度数 + 键数范围）。</summary>
    public string SubtitleLine
    {
        get
        {
            if (Index.Difficulties.Count == 0) return "没有可玩的难度";

            var keys = Index.Difficulties.Select(d => d.KeyCount).Distinct().OrderBy(k => k).ToArray();
            var keysText = keys.Length == 1 ? $"{keys[0]}K" : $"{keys[0]}K~{keys[^1]}K";
            return $"{Index.Difficulties.Count} 个难度 · {keysText}";
        }
    }

    /// <summary>背景图完整路径（没有就是空串）：优先用第一张有背景的难度的声明。</summary>
    /// <remarks>结果缓存一次 —— 列表里每行都要显示封面，每次重新解析 .osu 会很慢。</remarks>
    public string BackgroundPath
    {
        get
        {
            if (_backgroundResolved) return _backgroundPath;
            _backgroundResolved = true;
            _backgroundPath = ResolveBackgroundPath();
            return _backgroundPath;
        }
    }

    private bool _backgroundResolved;
    private string _backgroundPath = "";

    private string ResolveBackgroundPath()
    {
        foreach (var difficulty in Index.Difficulties)
        {
            var path = Path.Combine(FolderPath, difficulty.FileName);
            if (!OsuChartReader.TryLoad(path, out var chart, out _) || chart is null) continue;
            if (chart.BackgroundPath.Length > 0) return chart.BackgroundPath;
        }

        // 谱面没声明背景：取文件夹里第一张图（osu! 的 bg 经常不写进 [Events]）
        try
        {
            foreach (var file in Directory.EnumerateFiles(FolderPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (MapsetLibrary.ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) return file;
            }
        }
        catch { /* 忽略 */ }

        return "";
    }
}

/// <summary>导入一个 .osz 的结果。</summary>
public sealed class OszImportResult
{
    /// <summary>导入成功没有。</summary>
    public bool Success { get; init; }

    /// <summary>失败原因（成功时为空）。</summary>
    public string Error { get; init; } = "";

    /// <summary>落地的谱面集文件夹（成功时）。</summary>
    public string FolderPath { get; init; } = "";

    /// <summary>新谱面集的索引（成功时）。</summary>
    public MapsetIndex? Index { get; init; }

    /// <summary>被拒的难度（非 mania / 键数不支持），中文说明。</summary>
    public IReadOnlyList<string> RejectedCharts { get; init; } = Array.Empty<string>();

    /// <summary>被跳过的文件数（视频 / hitsound / 不认识的东西）。</summary>
    public int SkippedFiles { get; init; }

    /// <summary>造一个失败结果。</summary>
    public static OszImportResult Failed(string error) => new() { Success = false, Error = error };

    /// <summary>给用户看的一句话。</summary>
    public string Describe()
    {
        if (!Success) return Error;

        var text = $"已导入「{Index?.TitleLine}」：{Index?.Difficulties.Count ?? 0} 个难度";
        if (RejectedCharts.Count > 0) text += $"，跳过 {RejectedCharts.Count} 张不支持的谱面";
        if (SkippedFiles > 0) text += $"（另有 {SkippedFiles} 个文件不是谱面 / 音频 / 图片，已忽略）";
        return text + "。";
    }
}
