namespace YuLiangToolkit.Core.Storage;

/// <summary>
/// 界面布局记忆：把可拖拽分隔条（<c>Core/Mvvm/PaneSplitter</c>）拖出来的尺寸持久化到
/// <c>Config\layout.json</c>，下次打开同一个页面还是上次调好的大小。
///
/// <para><b>为什么放在 Config 根目录（不是某个工具目录）</b>：布局是**外壳级**的界面偏好，
/// 所有工具页共用一份文件；「主页设置 → 界面布局 → 恢复默认布局」一次清掉全部。
/// 各功能自己的「重建配置」只删 <c>Config\&lt;功能Id&gt;\</c>，不会顺手把布局也清掉
/// （想清布局就走专门的按钮，语义清楚）。</para>
///
/// <para><b>键名约定</b>：<c>"&lt;工具Id&gt;.&lt;区域名&gt;"</c>，例如
/// <c>flash-downloader.主区域左右</c>。工具 Id 里不能带 <c>.</c>（外壳自动按第一个 <c>.</c> 分组，
/// 用来在设置页显示「涉及哪些页面」）。分隔条自己再拼后缀：</para>
/// <list type="bullet">
///   <item><c>.px</c>：只有一侧是固定像素（另一侧 <c>*</c> 随窗口缩放）——存固定那一侧的像素值；</item>
///   <item><c>.a</c> / <c>.b</c>：两侧都是固定像素（例如滚动页面里的上下两块）——两侧都存；</item>
///   <item><c>.r</c>：两侧都是 <c>*</c>（按比例分）——存前一块占的比例 0~1。</item>
/// </list>
///
/// <para><b>线程</b>：只在 UI 线程用（尺寸来自界面 / 设置页按钮）。写盘走
/// <see cref="JsonFileStore"/> 的原子写，失败静默（只读环境只是记不住布局，不影响使用）。
/// 文件损坏会被自动备份成 <c>layout.bad-时间戳.json</c> 并回退默认布局。</para>
/// </summary>
public static class LayoutMemory
{
    /// <summary>布局文件名（在 <c>Config\</c> 根目录下）。</summary>
    public const string FileName = "layout.json";

    /// <summary>当前文件格式版本（以后改结构时用来迁移；现在只认 1）。</summary>
    public const int CurrentVersion = 1;

    private static readonly object Gate = new();

    /// <summary>内存里的布局表（<c>null</c> = 还没从磁盘加载过）。</summary>
    private static Dictionary<string, PaneEntry>? _panes;

    /// <summary>布局文件完整路径：<c>Config\layout.json</c>。</summary>
    public static string FilePath => Path.Combine(AppPaths.ConfigRoot, FileName);

    /// <summary>
    /// 布局被「恢复默认布局」整体清空后触发（UI 线程）。正开着的页面收到后可以立刻重排。
    /// 单个分隔条的双击复位不走这个事件（它只影响自己那一处）。
    /// </summary>
    public static event EventHandler? ResetCompleted;

    // ---------------- 读 ----------------

    /// <summary>取某个分隔条记住的像素尺寸；没记过返回 <c>null</c>。</summary>
    public static double? GetPixel(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var panes = Panes;
        lock (Gate)
        {
            return panes.TryGetValue(key.Trim(), out var entry) ? entry.Pixel : null;
        }
    }

    /// <summary>取某个分隔条记住的比例（两侧都是 <c>*</c> 时用）；没记过 / 越界返回 <c>null</c>。</summary>
    public static double? GetRatio(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var panes = Panes;
        lock (Gate)
        {
            if (!panes.TryGetValue(key.Trim(), out var entry) || entry.Ratio is not { } ratio) return null;
            return ratio is >= 0.02 and <= 0.98 ? ratio : null;
        }
    }

    // ---------------- 写 ----------------

    /// <summary>记住一个像素尺寸（非有限值 / &lt;= 0 会被忽略）。返回是否真的写入。</summary>
    public static bool SetPixel(string key, double value)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!double.IsFinite(value) || value <= 0) return false;

        var panes = Panes;
        lock (Gate)
        {
            var k = key.Trim();
            if (!panes.TryGetValue(k, out var entry))
            {
                entry = new PaneEntry();
                panes[k] = entry;
            }
            if (entry.Pixel is { } old && Math.Abs(old - value) < 0.5) return false;   // 没变化就不写盘
            entry.Pixel = value;
            entry.Ratio = null;   // 同一个键只会有一种存法
        }
        Save();
        return true;
    }

    /// <summary>记住一个比例（两侧都是 <c>*</c> 的布局；会夹到 0.02~0.98）。返回是否真的写入。</summary>
    public static bool SetRatio(string key, double ratio)
    {
        if (string.IsNullOrWhiteSpace(key) || !double.IsFinite(ratio)) return false;
        ratio = Math.Clamp(ratio, 0.02, 0.98);

        var panes = Panes;
        lock (Gate)
        {
            var k = key.Trim();
            if (!panes.TryGetValue(k, out var entry))
            {
                entry = new PaneEntry();
                panes[k] = entry;
            }
            if (entry.Ratio is { } old && Math.Abs(old - ratio) < 0.0005) return false;
            entry.Ratio = ratio;
            entry.Pixel = null;
        }
        Save();
        return true;
    }

    /// <summary>删掉某个键（双击分隔条复位时用）。返回是否真的删了东西。</summary>
    public static bool Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var panes = Panes;
        bool removed;
        lock (Gate)
        {
            removed = panes.Remove(key.Trim());
        }
        if (removed) Save();
        return removed;
    }

    // ---------------- 汇总 / 恢复默认 ----------------

    /// <summary>是否记住过任何布局（= 用户调过大小）。</summary>
    public static bool HasSaved => SavedCount > 0;

    /// <summary>记住的分隔条数量（一个分隔条可能存 1 或 2 个键；这里按**分隔条**粗算，够设置页显示）。</summary>
    public static int SavedCount
    {
        get
        {
            var panes = Panes;
            lock (Gate) return panes.Count == 0 ? 0 : CountSplitters(panes);
        }
    }

    /// <summary>记住过布局的工具 / 页面 Id（按第一个 <c>.</c> 分组；给设置页显示用）。</summary>
    public static IReadOnlyList<string> SavedGroups
    {
        get
        {
            var panes = Panes;
            lock (Gate)
            {
                if (panes.Count == 0) return Array.Empty<string>();
                return panes.Keys
                    .Select(key => key.Split('.', 2)[0])
                    .Where(id => id.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// 「恢复默认布局」：清空内存里的布局 + 删掉 <c>Config\layout.json</c>，
    /// 然后触发 <see cref="ResetCompleted"/>（正开着的页面据此恢复默认尺寸）。
    /// </summary>
    public static bool ResetAll()
    {
        lock (Gate)
        {
            _panes = new Dictionary<string, PaneEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var ok = true;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            ok = false;
        }

        try { ResetCompleted?.Invoke(null, EventArgs.Empty); } catch { /* 订阅方异常不影响重置 */ }
        return ok;
    }

    // ---------------- 内部 ----------------

    /// <summary>内存布局表（第一次访问时读盘；读坏了自动备份并回退空表）。</summary>
    private static Dictionary<string, PaneEntry> Panes
    {
        get
        {
            lock (Gate)
            {
                if (_panes is not null) return _panes;

                var loaded = new Dictionary<string, PaneEntry>(StringComparer.OrdinalIgnoreCase);
                var file = JsonFileStore.Read<LayoutFile>(FilePath);
                if (file?.Panes is not null)
                {
                    foreach (var (key, entry) in file.Panes)
                    {
                        if (string.IsNullOrWhiteSpace(key) || entry is null) continue;
                        var pixel = entry.Pixel is { } p && double.IsFinite(p) && p > 0 ? p : (double?)null;
                        var ratio = entry.Ratio is { } r && double.IsFinite(r) && r is >= 0.02 and <= 0.98
                            ? r
                            : (double?)null;
                        if (pixel is null && ratio is null) continue;
                        loaded[key.Trim()] = new PaneEntry { Pixel = pixel, Ratio = ratio };
                    }
                }

                _panes = loaded;
                return _panes;
            }
        }
    }

    /// <summary>把内存布局表写回文件（失败静默：只读环境下只是记不住布局）。</summary>
    private static void Save()
    {
        LayoutFile snapshot;
        lock (Gate)
        {
            snapshot = new LayoutFile
            {
                Version = CurrentVersion,
                Panes = new Dictionary<string, PaneEntry>(_panes ?? new(), StringComparer.OrdinalIgnoreCase),
            };
        }
        JsonFileStore.Write(FilePath, snapshot);
    }

    /// <summary>
    /// 一个分隔条会存成 1 个键（<c>.px</c> / <c>.r</c>）或 2 个键（<c>.a</c> + <c>.b</c>）。
    /// 汇总时把同一前缀的两侧键算作一处。
    /// </summary>
    private static int CountSplitters(Dictionary<string, PaneEntry> panes)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var singles = 0;
        foreach (var key in panes.Keys)
        {
            var cut = key.LastIndexOf('.');
            var prefix = cut > 0 ? key[..cut] : key;
            var suffix = cut > 0 ? key[(cut + 1)..] : "";
            if (suffix is "a" or "b") groups.Add(prefix);
            else singles++;
        }
        return groups.Count + singles;
    }

    /// <summary>落盘用的模型（版本号留给以后迁移）。</summary>
    private sealed class LayoutFile
    {
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, PaneEntry> Panes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>单个分隔条记住的值：像素尺寸或比例，两者只会有一个。</summary>
    private sealed class PaneEntry
    {
        public double? Pixel { get; set; }
        public double? Ratio { get; set; }
    }
}
