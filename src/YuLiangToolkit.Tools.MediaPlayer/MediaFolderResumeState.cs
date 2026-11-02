namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 「每个文件夹上次听到哪首、听到哪儿」的快照 → <c>Data\media-player\folder-resume.json</c>（v0.56.0）。
///
/// <para>为什么单独存一份：<c>playlist.json</c> 只记住**整个播放列表**最后一次的状态（换文件夹会被覆盖），
/// 而用户要的是**每个文件夹各自记住**：G 盘那个音乐文件夹听到第 5 首 3 分钟，换去听别的文件夹，
/// 下次再打开 G 盘那个文件夹时应该从第 5 首 3 分钟接着放。所以按文件夹路径分别记一条。</para>
///
/// <para>只存路径与秒数，不存别的；最多保留 <see cref="MaxEntries"/> 个最近用过的文件夹
/// （按 <see cref="MediaFolderResumeEntry.UpdatedAtUtc"/> 丢最旧的），不会无限长。</para>
/// </summary>
public sealed class MediaFolderResumeState
{
    /// <summary>最多记住多少个文件夹（超了按更新时间丢最旧的）。</summary>
    public const int MaxEntries = 50;

    /// <summary>所有文件夹的记录（顺序无所谓，查找按路径比）。</summary>
    public List<MediaFolderResumeEntry> Entries { get; set; } = new();

    /// <summary>找某个文件夹的记录（路径大小写不敏感、末尾分隔符不算差异；没有返回 <c>null</c>）。</summary>
    public MediaFolderResumeEntry? Find(string? folder)
    {
        var key = Normalize(folder);
        if (key.Length == 0) return null;
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Folder, key, StringComparison.OrdinalIgnoreCase)) return entry;
        }
        return null;
    }

    /// <summary>
    /// 写入 / 覆盖一个文件夹的记录。<paramref name="folder"/> 空、<paramref name="currentPath"/> 空
    /// 或负位置都会被忽略 / 夹到 0（脏数据不该把快照写坏）。
    /// </summary>
    public void Set(string? folder, string? currentPath, double positionSeconds, DateTime updatedAtUtc)
    {
        var key = Normalize(folder);
        if (key.Length == 0 || string.IsNullOrWhiteSpace(currentPath)) return;

        var entry = Find(key);
        if (entry is null)
        {
            entry = new MediaFolderResumeEntry();
            Entries.Add(entry);
        }

        entry.Folder = key;
        entry.CurrentPath = currentPath;
        entry.PositionSeconds = double.IsNaN(positionSeconds) ? 0 : Math.Max(0, positionSeconds);
        entry.UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>只留最近用的 <see cref="MaxEntries"/> 个文件夹（按更新时间倒序）。</summary>
    public void Prune()
    {
        if (Entries.Count <= MaxEntries) return;
        foreach (var stale in Entries.OrderByDescending(e => e.UpdatedAtUtc).Skip(MaxEntries).ToList())
        {
            Entries.Remove(stale);
        }
    }

    /// <summary>
    /// 路径归一：转绝对路径 + 去掉末尾分隔符（<c>C:\Music</c> 和 <c>C:\Music\</c> 当同一个文件夹）。
    /// 坏路径（非法字符 / 空）返回空串，调用方据此跳过。
    /// </summary>
    public static string Normalize(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        var path = folder.Trim();
        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            // 读配置时可能遇到手改坏的老路径：按原样比较，至少别崩
        }
        return Path.TrimEndingDirectorySeparator(path);
    }
}

/// <summary>一个文件夹的上次播放记录：那首文件名 + 听到第几秒 + 什么时候记的（判断哪个文件夹更新）。</summary>
public sealed class MediaFolderResumeEntry
{
    /// <summary>文件夹完整路径（写入前已归一，见 <see cref="MediaFolderResumeState.Normalize"/>）。</summary>
    public string Folder { get; set; } = "";

    /// <summary>上次听到的那首文件的完整路径。</summary>
    public string CurrentPath { get; set; } = "";

    /// <summary>上次听到第几秒（0 = 从头）。</summary>
    public double PositionSeconds { get; set; }

    /// <summary>记录时间（UTC；只用来挑「最近更新的文件夹」和淘汰旧的）。</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
