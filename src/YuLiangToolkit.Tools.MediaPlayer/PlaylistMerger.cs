namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>一次「往列表里加东西」的结果统计（状态栏文案用，自检直接断言）。</summary>
public sealed class MergeReport
{
    public MergeReport(IReadOnlyList<MediaItem> items, int added, int skipped, int displaced)
    {
        Items = items;
        Added = added;
        Skipped = skipped;
        Displaced = displaced;
    }

    /// <summary>合并后的整份列表（已经去过重）。</summary>
    public IReadOnlyList<MediaItem> Items { get; }

    /// <summary>真正新加进来的条数。</summary>
    public int Added { get; }

    /// <summary>因为列表里已经有（同一个路径）而跳过的条数。</summary>
    public int Skipped { get; }

    /// <summary>因为选了「覆盖」而被顶掉的原有条数（选「追加」时是 0）。</summary>
    public int Displaced { get; }

    /// <summary>合并后总数。</summary>
    public int Total => Items.Count;

    /// <summary>给状态栏用的一句话。</summary>
    public string Describe(bool append)
    {
        var text = append
            ? $"已添加 {Added} 个（列表共 {Total} 个）"
            : $"已替换列表：{Added} 个（原有 {Displaced} 个被清掉）";
        if (Skipped > 0) text += $"，跳过重复 {Skipped} 个";
        return text;
    }
}

/// <summary>
/// 把「新扫到的文件」并进现有播放列表（**纯逻辑**，自检直接断言）。
///
/// <para>两个设置项就落在这里：</para>
/// <list type="bullet">
///   <item><b>覆盖 / 追加</b>（<c>AppendWhenOpeningFolder</c>）：覆盖 = 从新扫到的这批重新开始；
///   追加 = 保留原有，只把没见过的补进去。</item>
///   <item><b>去重</b>：按完整路径（大小写不敏感）判重 —— 同一个文件夹打开两次不会变成两份，
///   但如果同一首歌放在两个目录里（路径不同）就会各留一条（这是故意的：用户可能就想要两份）。</item>
/// </list>
/// </summary>
public static class PlaylistMerger
{
    public static MergeReport Merge(IReadOnlyCollection<MediaItem> existing, IEnumerable<MediaItem> incoming, bool append)
    {
        var result = new List<MediaItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (append)
        {
            foreach (var item in existing)
            {
                if (seen.Add(item.FullPath)) result.Add(item);
            }
        }

        var before = result.Count;
        var skipped = 0;
        foreach (var item in incoming)
        {
            if (!seen.Add(item.FullPath))
            {
                skipped++;
                continue;
            }
            result.Add(item);
        }

        var added = result.Count - before;
        var displaced = append ? 0 : existing.Count;
        return new MergeReport(result, added, skipped, displaced);
    }
}
