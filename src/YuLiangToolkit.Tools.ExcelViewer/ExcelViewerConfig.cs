using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.ExcelViewer;

/// <summary>
/// 「Excel 阅读器」的设置 → <c>Config\excel-viewer\config.json</c>。
///
/// <para>只存「下次打开还要接着用」的东西：网格线开关、最近打开列表与上限、上次打开文件所在文件夹。
/// 和所有工具一样：改一下自动落盘，<b>没有保存按钮</b>（见 AGENTS.md 硬性规则 3）。</para>
/// </summary>
public sealed class ExcelViewerConfig : ToolConfig
{
    /// <summary>功能 Id（= <c>Config\excel-viewer\</c> 的文件夹名，也是主程序发现工具用的 Id）。</summary>
    public const string ToolId = "excel-viewer";

    // ---------- 持久化字段 ----------

    /// <summary>DataGrid 是否显示网格线（默认显示；关掉后靠单元格间距区分行列）。</summary>
    public bool ShowGridLines { get; set; } = true;

    /// <summary>
    /// 打开工作簿时是否还原文件里的冻结窗格（默认开）。
    /// <b>关掉只是不冻结，不修改原文件</b>；冻结列用 DataGrid 的 FrozenColumnCount，
    /// 冻结行用主表上方的第二个只读表（见 ExcelViewerView.xaml.cs）。
    /// </summary>
    public bool RestoreFreezePanes { get; set; } = true;

    /// <summary>
    /// 是否按内容自动设置行高（默认开）：文件里写了 <c>ht</c> 的行用 ht（夹在 18 ~ 180 DIP），
    /// 没写的行按内容自动撑高（自动换行的长文本不再被裁）。关掉 = 统一 24 DIP，回到旧版行为。
    /// </summary>
    public bool AutoRowHeight { get; set; } = true;

    /// <summary>
    /// 是否显示公式原文（默认关）：开 = 有公式的格子显示 <c>=公式</c>，没有公式的格子仍显示值；
    /// 关 = 全部按正常值显示。
    /// <para>界面不复制 / 不重建模型，只在单元格模板里用 MultiBinding 按这个开关换文本，
    /// 所以切换时列宽 / 选中 / 冻结 / 行高都不会被重置。</para>
    /// </summary>
    public bool ShowFormulas { get; set; }

    /// <summary>
    /// 是否记住每本工作簿的阅读位置（默认开）。关掉后不再写入 / 恢复阅读位置，
    /// 但<b>已有记录不会被删除</b>（下次重新打开开关还能用）。
    /// <para>字段名用复数是为了避开同名方法 <see cref="RememberReadingPosition"/>（C# 不允许属性和方法重名），
    /// JSON 里的键是 <c>rememberReadingPositions</c>。</para>
    /// </summary>
    public bool RememberReadingPositions { get; set; } = true;

    /// <summary>最多记住多少本工作簿的阅读位置（超出按最后保存时间淘汰最老的，避免配置文件无限长）。</summary>
    public const int MaxReadingPositions = 200;

    /// <summary>
    /// 每本工作簿的上次阅读位置：key = <see cref="NormalizePositionKey"/> 规范化后的完整路径（小写），
    /// value = 工作表名 / 滚动比例 / 保存时间。打开同一本工作簿时自动回到这里。
    /// </summary>
    public Dictionary<string, ExcelReadingPosition> ReadingPositions { get; set; } = new();

    /// <summary>
    /// 「最近打开」最多记多少条：默认 50，允许 0 ~ 100。
    /// 0 = 不记录（并清空已有列表）；调小会立刻裁掉多余的老记录。
    /// </summary>
    private int _recentLimit = RecentFiles.DefaultLimit;

    /// <summary>「最近打开」上限（写入时夹到 0 ~ 100，手改坏配置也不会存出离谱的条数）。</summary>
    public int RecentLimit
    {
        get => _recentLimit;
        set
        {
            var clamped = RecentFiles.ClampLimit(value);
            if (Set(ref _recentLimit, clamped))
            {
                OnPropertyChanged(nameof(RecentLimitEffective));
                OnPropertyChanged(nameof(RecordsRecent));
            }
        }
    }

    /// <summary>最近打开过的工作簿（新 → 旧；同一路径只留一条，最多 <see cref="RecentLimitEffective"/> 条）。</summary>
    public List<RecentFileEntry> RecentEntries { get; set; } = new();

    /// <summary>上次打开文件所在文件夹（只用来当文件对话框的起始目录，不会自动打开）。</summary>
    public string LastFolder { get; set; } = "";

    // ---------- 计算属性（只读 / 带 setter 的都必须 [JsonIgnore]） ----------

    /// <summary>生效的最近上限（夹到 0 ~ 100）。</summary>
    [JsonIgnore]
    public int RecentLimitEffective => RecentFiles.ClampLimit(RecentLimit);

    /// <summary>现在要不要记录最近打开（上限 0 = 不记录）。</summary>
    [JsonIgnore]
    public bool RecordsRecent => RecentLimitEffective > 0;

    /// <summary>当前记住了多少本工作簿的阅读位置（「清理缓存」显示条数用；只读，不写进 JSON）。</summary>
    [JsonIgnore]
    public int ReadingPositionCount => ReadingPositions?.Count ?? 0;

    // ---------- 最近打开 ----------

    /// <summary>记一次「打开过的工作簿」：置顶、去重、超上限裁掉最旧的（逻辑在 Core 的 <see cref="RecentFiles"/>）。</summary>
    public void RememberRecent(string? path)
    {
        RecentEntries ??= new List<RecentFileEntry>();
        RecentFiles.Remember(RecentEntries, path, RecentLimitEffective);
        OnPropertyChanged(nameof(RecentEntries));   // 通知界面 + 触发自动落盘
    }

    /// <summary>按当前上限裁剪列表（上限 0 = 清空）。</summary>
    public void TrimRecentToLimit()
    {
        RecentEntries ??= new List<RecentFileEntry>();
        RecentFiles.Trim(RecentEntries, RecentLimitEffective);
        OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>从最近列表里去掉某个文件（文件已不存在时用）。</summary>
    public void RemoveRecent(string? path)
    {
        RecentEntries ??= new List<RecentFileEntry>();
        RecentFiles.Remove(RecentEntries, path);
        OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>清空最近列表（只删记录，不动本地文件）。</summary>
    public void ClearRecent()
    {
        RecentEntries ??= new List<RecentFileEntry>();
        if (RecentEntries.Count == 0) return;
        RecentEntries.Clear();
        OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>
    /// 「恢复默认设置」：网格线开、冻结窗格还原开、自动行高开、不显示公式、记住阅读位置开、最近上限回 50。
    /// <b>不清空最近记录</b>（只按新上限裁剪），<b>也不清阅读位置</b>，不动「上次打开的文件夹」——
    /// 那些不是显示设置，清了反而给用户添麻烦。
    /// </summary>
    public void ResetToDefaults()
    {
        ShowGridLines = true;
        RestoreFreezePanes = true;
        AutoRowHeight = true;
        ShowFormulas = false;
        RememberReadingPositions = true;
        RecentLimit = RecentFiles.DefaultLimit;
        TrimRecentToLimit();
    }

    // ---------- 阅读位置（记住上次看到哪张表、滚到哪） ----------

    /// <summary>
    /// 把文件路径规范成配置里的 key：完整路径 + 小写（Windows 路径不区分大小写）。非法路径返回空串。
    /// </summary>
    public static string NormalizePositionKey(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return "";
        try { return Path.GetFullPath(filePath.Trim().Trim('"')).ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>查某本工作簿记住的阅读位置；没有记录 / 路径非法返回 null。</summary>
    public ExcelReadingPosition? FindReadingPosition(string? filePath)
    {
        var key = NormalizePositionKey(filePath);
        if (key.Length == 0 || ReadingPositions is null) return null;
        if (!ReadingPositions.TryGetValue(key, out var position) || position is null) return null;

        // 手改坏配置时也兜一下：比例在 ScrollRatio 的 setter 里夹取
        position.ScrollRatio = position.ScrollRatio;
        return position;
    }

    /// <summary>
    /// 记住某本工作簿的阅读位置（同一本覆盖旧值；超过 <see cref="MaxReadingPositions"/> 本时淘汰最老的）。
    /// 改完主动通知属性变更 → 配置自动落盘（见 AGENTS.md：没有保存按钮）。
    /// </summary>
    public void RememberReadingPosition(string? filePath, ExcelReadingPosition? position)
    {
        var key = NormalizePositionKey(filePath);
        if (key.Length == 0 || position is null) return;

        ReadingPositions ??= new Dictionary<string, ExcelReadingPosition>();
        position.ScrollRatio = position.ScrollRatio;   // 再夹一次（调用方绕过 setter 直接改字段也不会写坏）
        if (position.SavedAtUtc == default) position.SavedAtUtc = DateTime.UtcNow;
        ReadingPositions[key] = position;

        if (ReadingPositions.Count > MaxReadingPositions)
        {
            var staleKeys = ReadingPositions
                .OrderBy(pair => pair.Value?.SavedAtUtc ?? DateTime.MinValue)
                .Take(ReadingPositions.Count - MaxReadingPositions)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var stale in staleKeys) ReadingPositions.Remove(stale);
        }

        OnPropertyChanged(nameof(ReadingPositions));   // 配置靠属性变更自动落盘
        OnPropertyChanged(nameof(ReadingPositionCount));
    }

    /// <summary>忘掉某本工作簿的阅读位置（一般用不到，留给「清理缓存」/ 以后的手动清理入口）。</summary>
    public void ForgetReadingPosition(string? filePath)
    {
        var key = NormalizePositionKey(filePath);
        if (key.Length == 0 || ReadingPositions is null) return;
        if (!ReadingPositions.Remove(key)) return;
        OnPropertyChanged(nameof(ReadingPositions));
        OnPropertyChanged(nameof(ReadingPositionCount));
    }

    /// <summary>清空全部阅读位置（「主页设置 → 清理缓存」用；只删记录，不动工作簿）。</summary>
    public void ClearReadingPositions()
    {
        ReadingPositions ??= new Dictionary<string, ExcelReadingPosition>();
        if (ReadingPositions.Count == 0) return;
        ReadingPositions.Clear();
        OnPropertyChanged(nameof(ReadingPositions));
        OnPropertyChanged(nameof(ReadingPositionCount));
    }
}
