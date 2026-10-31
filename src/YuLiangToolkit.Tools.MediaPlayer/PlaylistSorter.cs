namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>播放列表的排序方式（跟设置页 / 列表上方那个下拉是同一个值，按字符串存进 <c>config.json</c>）。</summary>
public enum PlaylistSortKey
{
    /// <summary>默认顺序 = 条目第一次进列表的先后（「添加顺序」），换过别的排序也回得来。</summary>
    Added,

    /// <summary>名称：去掉扩展名的标题，自然排序（<c>2.mp3</c> 在 <c>10.mp3</c> 前面）。</summary>
    Name,

    /// <summary>修改日期：磁盘上的修改时间；读不到时间的条目固定排最后。</summary>
    Modified,

    /// <summary>文件大小：按字节数（小→大 / 大→小）。</summary>
    Size,
}

/// <summary>
/// 播放列表的排序算术（**纯逻辑**，不碰磁盘、不碰播放器，自检直接断言）。
///
/// <para>排序只重排「列表怎么显示」，<b>不碰文件</b>。调用方（ViewModel）排完必须顺手
/// 把播放队列对齐：<c>_queue.Reset()</c> + <c>SetCurrent(Items.IndexOf(当前曲目))</c>，
/// 否则列表循环会按旧下标跳到错的歌、随机的一轮顺序作废（见 docs/dev/pitfalls.md）。</para>
///
/// <para>排序键里有并列时统一用「名称自然序 → 添加顺序」兜底，保证同样的列表重复排出来
/// 顺序一致（资源管理器式的可预期结果，也方便自检断言）。</para>
/// </summary>
public static class PlaylistSorter
{
    /// <summary>
    /// 按 <paramref name="key"/> / <paramref name="descending"/> 排一份新列表。
    /// 空列表 / 单条原样返回（内容相同的副本）。
    /// </summary>
    public static IReadOnlyList<MediaItem> Sort(
        IReadOnlyList<MediaItem> items, PlaylistSortKey key, bool descending)
    {
        if (items.Count <= 1) return items.ToList();

        return key switch
        {
            PlaylistSortKey.Added => SortByAdded(items, descending),
            PlaylistSortKey.Name => SortByName(items, descending),
            PlaylistSortKey.Modified => SortByModified(items, descending),
            PlaylistSortKey.Size => SortBySize(items, descending),
            _ => items.ToList(),
        };
    }

    /// <summary>「默认顺序」：按添加顺序；没分配过的（<c>-1</c>）放最后。</summary>
    private static IReadOnlyList<MediaItem> SortByAdded(IReadOnlyList<MediaItem> items, bool descending)
        => (descending
                ? items.OrderBy(i => i.AddedIndex < 0 ? 1 : 0).ThenByDescending(i => i.AddedIndex)
                : items.OrderBy(i => i.AddedIndex < 0 ? 1 : 0).ThenBy(i => i.AddedIndex))
            .ToList();

    /// <summary>「名称」：标题自然序，同名再比带扩展名的文件名。</summary>
    private static IReadOnlyList<MediaItem> SortByName(IReadOnlyList<MediaItem> items, bool descending)
        => (descending
                ? items.OrderByDescending(i => i.Title, NaturalStringComparer.Instance)
                    .ThenByDescending(i => i.FileName, NaturalStringComparer.Instance)
                    .ThenByDescending(i => i.AddedIndex)
                : items.OrderBy(i => i.Title, NaturalStringComparer.Instance)
                    .ThenBy(i => i.FileName, NaturalStringComparer.Instance)
                    .ThenBy(i => i.AddedIndex))
            .ToList();

    /// <summary>
    /// 「修改日期」：读不到时间的条目**永远排最后**（正序 / 反序都一样），
    /// 否则那些文件不在的条目会在大头小头之间乱跳。
    /// </summary>
    private static IReadOnlyList<MediaItem> SortByModified(IReadOnlyList<MediaItem> items, bool descending)
    {
        // ① 先按「有没有时间」分组（有时间 = 0 在前、没时间 = 1 在后）；
        //    ThenByDescending 只作用于有时间的组，不会把没时间的翻到前面去。
        var grouped = items.OrderBy(i => i.Modified is null ? 1 : 0);
        return (descending
                ? grouped.ThenByDescending(i => i.Modified ?? DateTime.MinValue)
                : grouped.ThenBy(i => i.Modified ?? DateTime.MinValue))
            .ThenBy(i => i.Title, NaturalStringComparer.Instance)
            .ThenBy(i => i.AddedIndex)
            .ToList();
    }

    /// <summary>「文件大小」：按字节数，同大小再按名称自然序。</summary>
    private static IReadOnlyList<MediaItem> SortBySize(IReadOnlyList<MediaItem> items, bool descending)
        => (descending
                ? items.OrderByDescending(i => i.Size)
                : items.OrderBy(i => i.Size))
            .ThenBy(i => i.Title, NaturalStringComparer.Instance)
            .ThenBy(i => i.AddedIndex)
            .ToList();

    // ---------- 展示文案（下拉框 / 按钮 / 状态栏共用，别在别处另写一套） ----------

    /// <summary>排序方式的中文名（下拉框标签）。</summary>
    public static string KeyText(PlaylistSortKey key) => key switch
    {
        PlaylistSortKey.Name => "名称",
        PlaylistSortKey.Modified => "修改日期",
        PlaylistSortKey.Size => "文件大小",
        _ => "默认顺序",
    };

    /// <summary>正序 / 反序各自是什么意思（给人看的方向说明）。</summary>
    public static string DirectionHint(PlaylistSortKey key, bool descending) => key switch
    {
        PlaylistSortKey.Name => descending ? "Z→A" : "A→Z",
        PlaylistSortKey.Modified => descending ? "新→旧" : "旧→新",
        PlaylistSortKey.Size => descending ? "大→小" : "小→大",
        _ => descending ? "晚→早" : "早→晚",
    };

    /// <summary>方向按钮上的字（↑↓ 是普通箭头字符，不是 🔁 那类 emoji，字体不会缺字形）。</summary>
    public static string DirectionText(bool descending) => descending ? "↓ 反序" : "↑ 正序";

    /// <summary>状态栏 / 悬停提示用的一句话，「「修改日期」新→旧」这种。</summary>
    public static string Describe(PlaylistSortKey key, bool descending)
        => $"「{KeyText(key)}」{DirectionHint(key, descending)}";
}
