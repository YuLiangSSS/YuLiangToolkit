using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.BatchRename;

/// <summary>一条改名记录：从哪个路径改到了哪个路径。</summary>
public sealed class RenameHistoryEntry
{
    /// <summary>改名前的完整路径。</summary>
    public string From { get; set; } = "";

    /// <summary>改名后的完整路径。</summary>
    public string To { get; set; } = "";
}

/// <summary>一次批量改名的记录（= 一次「撤销」能回退的范围）。</summary>
public sealed class RenameSession
{
    /// <summary>唯一 Id（界面按钮传参用）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>改名发生的时间。</summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>这批改名是否已经被撤销过（撤销过的不能重复撤销）。</summary>
    public bool Undone { get; set; }

    /// <summary>本次改了哪些文件。</summary>
    public List<RenameHistoryEntry> Entries { get; set; } = new();

    // 下面两个是只读计算属性 → JsonFileStore 的 IgnoreReadOnlyProperties 会把它们排除，不写进 JSON

    /// <summary>界面上显示的时间。</summary>
    public string TimeText => Time.ToString("MM-dd HH:mm");

    /// <summary>界面上显示的一句话说明。</summary>
    public string Summary => Undone
        ? $"{Entries.Count} 个文件 · 已撤销"
        : $"{Entries.Count} 个文件 · 可撤销";
}

/// <summary>改名流水账（存在 <c>Data\batch-rename\rename-history.json</c>，所以重启程序也能撤销）。</summary>
public sealed class RenameHistory
{
    /// <summary>最多保留多少批（超出的从最旧的开始丢）。</summary>
    public const int MaxSessions = 20;

    /// <summary>最近的改名记录，最新的排最前。</summary>
    public List<RenameSession> Sessions { get; set; } = new();

    /// <summary>最近一批「还没被撤销」的记录（撤销按钮用它）。</summary>
    public RenameSession? LastUndoable() => Sessions.FirstOrDefault(s => !s.Undone && s.Entries.Count > 0);

    /// <summary>插入一批新记录并裁剪到 <see cref="MaxSessions"/> 条。</summary>
    public void Push(RenameSession session)
    {
        Sessions.Insert(0, session);
        if (Sessions.Count > MaxSessions) Sessions.RemoveRange(MaxSessions, Sessions.Count - MaxSessions);
    }
}

/// <summary>
/// 改名流水账的读写（<c>Data\batch-rename\rename-history.json</c>）。
/// 用 <see cref="JsonFileStore"/> 读写：原子写 + 损坏自动备份回退，坏文件不会让工具打不开。
/// </summary>
public sealed class RenameHistoryStore
{
    private const string FileName = "rename-history.json";

    private readonly ToolStorage _storage;

    public RenameHistoryStore(ToolStorage storage) => _storage = storage;

    /// <summary>流水账文件路径。</summary>
    public string FilePath => _storage.PathOf(FileName);

    /// <summary>读取；文件不存在或损坏时返回空账本。</summary>
    public RenameHistory Load()
    {
        var history = JsonFileStore.Read<RenameHistory>(FilePath, new RenameHistory());
        history ??= new RenameHistory();
        history.Sessions ??= new List<RenameSession>();
        return history;
    }

    /// <summary>写回（失败返回 false，不影响改名本身已经完成的事实）。</summary>
    public bool Save(RenameHistory history)
    {
        if (history.Sessions.Count > RenameHistory.MaxSessions)
            history.Sessions.RemoveRange(RenameHistory.MaxSessions, history.Sessions.Count - RenameHistory.MaxSessions);
        return JsonFileStore.Write(FilePath, history);
    }
}
