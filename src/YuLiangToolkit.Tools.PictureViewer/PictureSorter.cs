using System.IO;

namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 页清单的排序方式（工具条下拉框 + 「一键转 PDF」共用的同一套顺序）：
/// 拼接 / 导出顺序由它决定，切换后长带立即重排。
/// </summary>
public enum PictureSortMode
{
    /// <summary>页序（默认）：扫描时的文件名自然序（2.png 在 10.png 前面）。</summary>
    PageOrder = 0,

    /// <summary>按文件名（自然序，数字段按数值比）。</summary>
    Name = 1,

    /// <summary>按修改时间（旧 → 新；逆序 = 新 → 旧）。</summary>
    ModifiedTime = 2,

    /// <summary>按文件大小（小 → 大；逆序 = 大 → 小）。</summary>
    FileSize = 3,
}

/// <summary>「排序方式」的纯逻辑小工具（界面 / 自检都用它）。</summary>
public static class PictureSortModes
{
    /// <summary>把任意枚举值（手改坏了的配置）归一化到合法值。</summary>
    public static PictureSortMode Normalize(PictureSortMode mode)
        => Enum.IsDefined(typeof(PictureSortMode), mode) ? mode : PictureSortMode.PageOrder;

    /// <summary>给人看的名字（工具条下拉框用）。</summary>
    public static string Describe(PictureSortMode mode) => mode switch
    {
        PictureSortMode.Name => "按名称",
        PictureSortMode.ModifiedTime => "按修改日期",
        PictureSortMode.FileSize => "按大小",
        _ => "页序（文件名自然序）",
    };

    /// <summary>一句说明（ToolTip 用）。</summary>
    public static string Hint(PictureSortMode mode) => mode switch
    {
        PictureSortMode.Name => "按文件名自然序（数字段按数值比，2 在 10 前面）",
        PictureSortMode.ModifiedTime => "按文件修改时间：旧 → 新（配合逆序按钮变 新 → 旧）",
        PictureSortMode.FileSize => "按文件大小：小 → 大（配合逆序按钮变 大 → 小）",
        _ => "保持扫描时的页序（文件名自然序），逆序按钮整体反过来",
    };
}

/// <summary>排序要用的文件信息（修改时间 + 大小）；读不到时为 null → 该页永远排在最后。</summary>
public readonly record struct PictureSortInfo(DateTime ModifiedUtc, long Bytes);

/// <summary>
/// 页清单排序：给「转 PDF」与长带重排用的同一套纯逻辑。
///
/// <para>规则：</para>
/// <list type="bullet">
///   <item><see cref="PictureSortMode.PageOrder"/>：保持传入顺序（即扫描的自然页序），逆序 = 整体反转；</item>
///   <item>名称：自然序（复用 <see cref="PictureScanner.CompareNatural"/>）；</item>
///   <item>修改日期 / 大小：比较 <see cref="PictureSortInfo"/>；同值时按文件名自然序兜底（正逆序一致，结果可预期）；</item>
///   <item>文件信息读不到（文件被删 / 无权限）的页<b>永远沉底</b>（正逆序都一样，不会被翻到最前）。</item>
/// </list>
/// </summary>
public static class PictureSorter
{
    /// <summary>
    /// 按规则排出一份新清单（不改传入列表）。文件信息由 <paramref name="probe"/> 批量读取
    /// （一次 <c>FileInfo</c> 很便宜，几百张图毫秒级；传 null 时按「全部缺失」处理，即页序 / 反转页序）。
    /// </summary>
    public static List<PicturePage> Sort(
        IReadOnlyList<PicturePage> pages,
        PictureSortMode mode,
        bool descending,
        Func<string, PictureSortInfo?>? probe = null)
    {
        var result = new List<PicturePage>(pages);

        if (mode == PictureSortMode.PageOrder)
        {
            if (descending) result.Reverse();
            return result;
        }

        // 文件信息读一次缓存住（排序比较器里不能反复碰磁盘）
        var infos = new Dictionary<string, PictureSortInfo?>(StringComparer.OrdinalIgnoreCase);
        if (probe is not null)
        {
            foreach (var page in result)
            {
                if (infos.ContainsKey(page.FilePath)) continue;
                try { infos[page.FilePath] = probe(page.FilePath); }
                catch { infos[page.FilePath] = null; }
            }
        }

        result.Sort((a, b) => Compare(mode, descending, a, b, GetInfo(infos, a), GetInfo(infos, b)));
        return result;
    }

    /// <summary>两条页的比较（纯函数；缺失信息沉底，同值按文件名自然序兜底）。</summary>
    public static int Compare(
        PictureSortMode mode,
        bool descending,
        PicturePage a,
        PicturePage b,
        PictureSortInfo? aInfo,
        PictureSortInfo? bInfo)
    {
        // 缺失信息的一侧永远排最后：a 缺 → a 在后；b 缺 → b 在后；都缺按文件名
        if (aInfo is null || bInfo is null)
        {
            if (aInfo is null && bInfo is null) return Tie(a, b);
            return aInfo is null ? 1 : -1;
        }

        var primary = mode switch
        {
            PictureSortMode.ModifiedTime => aInfo.Value.ModifiedUtc.CompareTo(bInfo.Value.ModifiedUtc),
            PictureSortMode.FileSize => aInfo.Value.Bytes.CompareTo(bInfo.Value.Bytes),
            _ => PictureScanner.CompareNatural(a.FileName, b.FileName),
        };

        if (primary != 0) return descending ? -primary : primary;
        // 同值兜底：按文件名自然序固定（不随逆序翻转，两个键同值的相对顺序可预期）
        return Tie(a, b);
    }

    private static int Tie(PicturePage a, PicturePage b)
        => PictureScanner.CompareNatural(a.FileName, b.FileName);

    private static PictureSortInfo? GetInfo(Dictionary<string, PictureSortInfo?> cache, PicturePage page)
        => cache.TryGetValue(page.FilePath, out var info) ? info : null;
}
