namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 扫文件夹 → 媒体文件清单（**只有一个地方碰磁盘**，自检自己造临时目录真扫一遍）。
///
/// <para>规则：</para>
/// <list type="bullet">
///   <item>按 <see cref="MediaIncludeMode"/> 过滤（音频 + 视频 / 仅音频 / 仅视频）；</item>
///   <item><c>recursive</c> = 要不要进子文件夹（设置项「读取子文件夹」）；</item>
///   <item>**读不动的子目录直接跳过**，不抛异常（权限不足 / 正好被别的程序占用时，
///       宁可少列几个文件，也不要整个工具打不开）；</item>
///   <item>按「自然顺序」排序：<c>2.mp3</c> 排在 <c>10.mp3</c> 前面（不是字典序）。</item>
/// </list>
/// </summary>
public static class MediaScanner
{
    /// <summary>扫一个文件夹，返回排序后的绝对路径清单。</summary>
    public static IReadOnlyList<string> EnumerateFiles(string? folder, bool recursive, MediaIncludeMode include)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(folder)) return result;

        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch
        {
            return result;
        }

        if (!Directory.Exists(full)) return result;

        var pending = new Stack<string>();
        pending.Push(full);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = Array.Empty<string>(); }

            foreach (var file in files)
            {
                if (MediaTypes.Accept(file, include)) result.Add(file);
            }

            if (!recursive) continue;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { subDirs = Array.Empty<string>(); }

            foreach (var sub in subDirs) pending.Push(sub);
        }

        result.Sort(NaturalStringComparer.Instance);
        return result;
    }

    /// <summary>扫一个文件夹并包成播放列表条目。</summary>
    public static IReadOnlyList<MediaItem> BuildItems(string? folder, bool recursive, MediaIncludeMode include)
        => FromFiles(EnumerateFiles(folder, recursive, include), include);

    /// <summary>一批现成的路径（打开文件 / 拖放进来）→ 列表条目；按设置过滤、去重、自然排序。</summary>
    public static IReadOnlyList<MediaItem> FromFiles(IEnumerable<string> files, MediaIncludeMode include)
    {
        var items = new List<MediaItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            if (!MediaTypes.Accept(file, include)) continue;
            if (!seen.Add(file)) continue;
            var kind = MediaTypes.KindOf(file);
            if (kind is null) continue;
            items.Add(new MediaItem(file, kind.Value));
        }

        items.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.FullPath, b.FullPath));
        return items;
    }
}

/// <summary>
/// 「自然顺序」比较器：把文件名里的数字段按数值比，所以 <c>2.mp3</c> &lt; <c>10.mp3</c>，
/// <c>01 - 序章.mp3</c> &lt; <c>02 - 第二章.mp3</c>。
///
/// <para>为什么不用 <c>string.Compare</c>：字典序下 "10" 会排到 "2" 前面，一个文件夹里
/// <c>1.mp3 … 10.mp3</c> 就会变成 1、10、11、2、3…（资源管理器不是这么排的，用户一眼就看出不对）。
/// 数字段长度不同时先比「去前导零后的长度」，再比字面值；非数字段大小写不敏感地逐字符比。
/// </para>
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var numX = x.Substring(startX, i - startX).TrimStart('0');
                var numY = y.Substring(startY, j - startY).TrimStart('0');

                if (numX.Length != numY.Length) return numX.Length.CompareTo(numY.Length);
                var byValue = string.CompareOrdinal(numX, numY);
                if (byValue != 0) return byValue;
                continue; // 数值相等（例如 "01" 与 "1"）：接着往后比
            }

            var cx = char.ToUpperInvariant(x[i]);
            var cy = char.ToUpperInvariant(y[j]);
            if (cx != cy) return cx.CompareTo(cy);
            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
