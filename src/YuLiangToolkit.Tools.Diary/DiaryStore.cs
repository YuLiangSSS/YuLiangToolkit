using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Diary;

/// <summary>
/// 日记的持久化：每篇日记一个 JSON，放在 <c>Data\diary\entries\&lt;Id&gt;.json</c>。
///
/// <para>为什么一篇一个文件（而不是塞进一个大 JSON）：</para>
/// <list type="bullet">
///   <item>写一篇只动一个文件，不会因为一次保存把几百篇全重写；</item>
///   <item>某篇文件坏了只丢这一篇，其它的还在（<see cref="JsonFileStore"/> 会把坏文件备份成 <c>*.bad-*</c>）；</item>
///   <item>用户可以自己进文件夹看 / 备份 / 手改，跟「一篇篇日记」的心智一致。</item>
/// </list>
///
/// <para>所有 IO 都不抛异常：读失败的篇目跳过、写失败返回 false，界面自行提示。</para>
/// </summary>
public sealed class DiaryStore
{
    /// <summary>日记文件所在子目录（相对 <c>Data\diary\</c>）。</summary>
    public const string FolderName = "entries";

    private readonly ToolStorage _storage;

    public DiaryStore(ToolStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _storage.EnsureCreated();
    }

    /// <summary>数据目录：<c>Data\diary\entries\</c>（不存在会自动建）。</summary>
    public string EntriesDirectory
    {
        get
        {
            _storage.EnsureCreated();
            var dir = _storage.PathOf(FolderName);
            try { System.IO.Directory.CreateDirectory(dir); } catch { /* 只读环境忽略 */ }
            return dir;
        }
    }

    /// <summary>读回全部日记（按创建时间倒序，最新在前）；文件坏了跳过、不抛异常。</summary>
    public IReadOnlyList<DiaryEntry> LoadAll()
    {
        var result = new List<DiaryEntry>();
        foreach (var file in _storage.List("*.json"))
        {
            if (!file.RelativePath.Replace('\\', '/').StartsWith(FolderName + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            // 损坏备份（*.bad-*.json）不当作日记再读一遍，否则会反复备份、越滚越多
            if (file.Name.Contains(".bad-", StringComparison.OrdinalIgnoreCase)) continue;

            var entry = JsonFileStore.Read<DiaryEntry>(file.FullPath);
            if (entry is null) continue;

            entry.Normalize();
            if (string.IsNullOrEmpty(entry.Id))
                entry.Id = System.IO.Path.GetFileNameWithoutExtension(file.Name);

            if (entry.CreatedAt == default) entry.CreatedAt = file.LastWriteTime;
            if (entry.UpdatedAt == default) entry.UpdatedAt = entry.CreatedAt;
            result.Add(entry);
        }

        return result
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.UpdatedAt)
            .ToList();
    }

    /// <summary>写一篇日记（新建或覆盖）。返回是否成功。</summary>
    public bool Save(DiaryEntry entry)
    {
        if (entry is null) return false;
        entry.Normalize();

        if (string.IsNullOrEmpty(entry.Id)) entry.Id = NewId();
        if (entry.CreatedAt == default) entry.CreatedAt = DateTime.Now;
        if (entry.UpdatedAt == default) entry.UpdatedAt = entry.CreatedAt;

        var path = _storage.PathOf(FolderName, entry.Id + ".json");
        return JsonFileStore.Write(path, entry);
    }

    /// <summary>删一篇日记。文件不存在也算成功。</summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        return _storage.Delete(System.IO.Path.Combine(FolderName, id + ".json"));
    }

    /// <summary>生成一个不重名的日记 Id（时间戳 + 随机后缀，既是文件名也是排序依据）。</summary>
    public static string NewId()
        => $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..22];
}
