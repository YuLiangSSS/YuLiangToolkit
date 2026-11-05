using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.WordViewer;

/// <summary>
/// 「Word 阅读器」的设置 → <c>Config\word-viewer\config.json</c>。
///
/// <para>记的是「下次打开还要接着用」的选项：缩放百分比、大纲栏开没开、最近打开上限、
/// 最近打开列表、上次选文件所在文件夹。改一下自动落盘，没有保存按钮（见 AGENTS.md）。</para>
///
/// <para>最近列表的增删逻辑全部走 Core 的 <see cref="RecentFiles"/>（Word / Excel 两个阅读器共用），
/// 这里只负责把列表存进 JSON 并在改动后发一次通知触发落盘。</para>
/// </summary>
public sealed class WordViewerConfig : ToolConfig
{
    /// <summary>功能 Id（= <c>Config\word-viewer\</c> 与 <c>Data\word-viewer\</c> 的文件夹名）。</summary>
    public const string ToolId = "word-viewer";

    /// <summary>默认缩放百分比。</summary>
    public const int DefaultZoomPercent = 100;

    /// <summary>最小缩放百分比（再小就看不清正文了）。</summary>
    public const int MinZoomPercent = 50;

    /// <summary>最大缩放百分比。</summary>
    public const int MaxZoomPercent = 300;

    /// <summary>缩放一步的百分比（工具条 +/-、Ctrl+滚轮）。</summary>
    public const int ZoomStepPercent = 10;

    /// <summary>最近打开默认记多少条。</summary>
    public const int DefaultRecentLimit = 50;

    /// <summary>最近打开上限的最大值（0 ~ 100，0 = 不记录）。</summary>
    public const int MaxRecentLimit = RecentFiles.MaxLimit;

    /// <summary>最多记住多少份文档的阅读位置（超出按保存时间淘汰最老的，避免配置文件无限长）。</summary>
    public const int MaxReadingPositions = 200;

    // ---------------------------------------------------------------- 持久化字段

    /// <summary>缩放百分比：默认 100%，允许 50 ~ 300（手改坏配置会被 <see cref="ZoomPercentEffective"/> 夹回来）。</summary>
    public int ZoomPercent { get; set; } = DefaultZoomPercent;

    /// <summary>左侧大纲栏是否显示（默认显示）。</summary>
    public bool ShowOutline { get; set; } = true;

    /// <summary>是否显示页眉 / 页脚（默认显示；单节文档两处通常来自同一个 sectPr）。</summary>
    public bool ShowHeaderFooter { get; set; } = true;

    /// <summary>
    /// 是否记住每份文档的阅读位置（默认开）。关掉只是不再写新位置，
    /// **不会清掉已有记录**（重新打开开关还能接着用）。
    /// <para>属性名和「写位置」的方法 <see cref="RememberReadingPosition"/> 重名会冲突，
    /// 所以属性叫 <c>*Enabled</c>，JSON 键仍是 <c>rememberReadingPosition</c>。</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("rememberReadingPosition")]
    public bool RememberReadingPositionEnabled { get; set; } = true;

    /// <summary>查找是否区分大小写（默认不区分；改开关时已有查询会立即重算命中）。</summary>
    public bool SearchMatchCase { get; set; }

    /// <summary>
    /// 每份文档的上次阅读位置：key = <see cref="NormalizePositionKey"/> 规范化后的完整路径（小写），
    /// value = 滚动比例 + 保存时间。打开同一份文档时自动回到这里。
    /// </summary>
    public Dictionary<string, WordReadingPosition> ReadingPositions { get; set; } = new();

    /// <summary>最近打开最多记多少条：0 = 不记录（并清空列表），默认 50。</summary>
    public int RecentLimit { get; set; } = DefaultRecentLimit;

    /// <summary>最近打开过的文档（新 → 旧；同一路径只留一条）。</summary>
    public List<RecentFileEntry> RecentEntries { get; set; } = new();

    /// <summary>上次选文件所在文件夹（只当文件对话框的起始目录，不会自动打开文件）。</summary>
    public string LastFolder { get; set; } = "";

    // ---------------------------------------------------------------- 计算属性（不落盘）

    /// <summary>生效的缩放百分比（夹到 50 ~ 300）。</summary>
    [JsonIgnore]
    public int ZoomPercentEffective => Math.Clamp(ZoomPercent, MinZoomPercent, MaxZoomPercent);

    /// <summary>缩放系数（100% → 1.0，200% → 2.0），直接绑 <c>FlowDocumentScrollViewer.Zoom</c>。</summary>
    [JsonIgnore]
    public double ZoomScale => ZoomPercentEffective / 100.0;

    /// <summary>生效的最近打开上限（夹到 0 ~ 100）。</summary>
    [JsonIgnore]
    public int RecentLimitEffective => RecentFiles.ClampLimit(RecentLimit);

    /// <summary>现在要不要记录最近打开（上限 0 = 不记录）。</summary>
    [JsonIgnore]
    public bool RecordsRecent => RecentLimitEffective > 0;

    // ---------------------------------------------------------------- 最近打开

    /// <summary>
    /// 记一次「打开过的文档」：放到最前面、同一路径去重、超过上限砍掉最旧的；
    /// 上限 0 按「不记录」处理（顺手清空列表）。
    /// </summary>
    public void RememberRecent(string? path, DateTime? lastOpenedUtc = null)
    {
        RecentEntries ??= new List<RecentFileEntry>();
        RecentFiles.Remember(RecentEntries, path, RecentLimitEffective, lastOpenedUtc);
        OnPropertyChanged(nameof(RecentEntries));   // 配置靠属性变更自动落盘
    }

    /// <summary>按当前上限裁剪列表（上限 0 = 清空）。</summary>
    public void TrimRecentToLimit()
    {
        RecentEntries ??= new List<RecentFileEntry>();
        RecentFiles.Trim(RecentEntries, RecentLimitEffective);
        OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>从最近列表里去掉某个文档（文件已不存在时用）。</summary>
    public void RemoveRecent(string? path)
    {
        RecentEntries ??= new List<RecentFileEntry>();
        RecentFiles.Remove(RecentEntries, path);
        OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>清空最近打开记录（只删记录，不删本地文件）。</summary>
    public void ClearRecent()
    {
        RecentEntries ??= new List<RecentFileEntry>();
        if (RecentEntries.Count == 0) return;
        RecentEntries.Clear();
        OnPropertyChanged(nameof(RecentEntries));
    }

    // ---------------------------------------------------------------- 阅读位置（记住上次读到哪）

    /// <summary>
    /// 把路径规范成阅读位置字典的 key：完整路径 + 小写（Windows 路径不区分大小写）。
    /// 非法路径返回空串（调用方据此跳过，不写脏数据）。
    /// </summary>
    public static string NormalizePositionKey(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return "";
        try { return Path.GetFullPath(filePath.Trim().Trim('"')).ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>查某份文档记住的阅读位置；没有记录 / 路径非法返回 <c>null</c>。</summary>
    public WordReadingPosition? FindReadingPosition(string? filePath)
    {
        var key = NormalizePositionKey(filePath);
        if (key.Length == 0 || ReadingPositions is null) return null;
        return ReadingPositions.TryGetValue(key, out var position) ? position : null;
    }

    /// <summary>
    /// 记住某份文档的阅读位置（同一份覆盖旧值；超过 <see cref="MaxReadingPositions"/> 份时淘汰最老的）。
    /// 改完主动通知属性变更 → 配置自动落盘（见 AGENTS.md：没有保存按钮）。
    /// </summary>
    public void RememberReadingPosition(string? filePath, WordReadingPosition? position)
    {
        var key = NormalizePositionKey(filePath);
        if (key.Length == 0 || position is null) return;

        ReadingPositions ??= new Dictionary<string, WordReadingPosition>();
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
    }

    /// <summary>忘掉某份文档的阅读位置（暂时没有清理入口，留给以后 / 自检用）。</summary>
    public void ForgetReadingPosition(string? filePath)
    {
        var key = NormalizePositionKey(filePath);
        if (key.Length == 0 || ReadingPositions is null) return;
        if (ReadingPositions.Remove(key)) OnPropertyChanged(nameof(ReadingPositions));
    }

    /// <summary>
    /// 清空所有阅读位置记录（主页「清理缓存」里的「阅读位置记录」用）。
    /// 只删「读到哪」的记录，不动缩放 / 页眉页脚 / 最近打开等其他设置。
    /// </summary>
    public void ClearReadingPositions()
    {
        ReadingPositions ??= new Dictionary<string, WordReadingPosition>();
        if (ReadingPositions.Count == 0) return;
        ReadingPositions.Clear();
        OnPropertyChanged(nameof(ReadingPositions));
    }

    // ---------------------------------------------------------------- 其它

    /// <summary>
    /// 「恢复默认设置」：缩放回 100%、大纲开、显示页眉页脚、记住阅读位置、不区分大小写、最近上限回 50。
    /// <para><b>不动</b>「上次打开的文件夹」「最近记录」「已有的阅读位置」—— 那属于用户的使用痕迹，
    /// 清掉只会给用户添麻烦（最近记录有工具条里的清空入口）。</para>
    /// </summary>
    public void ResetToDefaults()
    {
        ZoomPercent = DefaultZoomPercent;
        ShowOutline = true;
        ShowHeaderFooter = true;
        RememberReadingPositionEnabled = true;
        SearchMatchCase = false;
        RecentLimit = DefaultRecentLimit;
        TrimRecentToLimit();   // 只按新上限裁剪，不主动清掉用户的最近记录
    }

    /// <summary>把当前缩放百分比按步数增减并夹好（放大 / 缩小按钮、Ctrl+滚轮用）。</summary>
    public static int StepZoomPercent(int currentPercent, int steps, int stepPercent = ZoomStepPercent)
        => Math.Clamp(
            Math.Clamp(currentPercent, MinZoomPercent, MaxZoomPercent) + steps * stepPercent,
            MinZoomPercent,
            MaxZoomPercent);
}
