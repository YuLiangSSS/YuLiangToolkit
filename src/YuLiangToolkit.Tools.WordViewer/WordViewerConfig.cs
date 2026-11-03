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

    // ---------------------------------------------------------------- 持久化字段

    /// <summary>缩放百分比：默认 100%，允许 50 ~ 300（手改坏配置会被 <see cref="ZoomPercentEffective"/> 夹回来）。</summary>
    public int ZoomPercent { get; set; } = DefaultZoomPercent;

    /// <summary>左侧大纲栏是否显示（默认显示）。</summary>
    public bool ShowOutline { get; set; } = true;

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

    // ---------------------------------------------------------------- 其它

    /// <summary>
    /// 「恢复默认设置」：缩放回 100%、大纲栏打开、最近上限回 50。
    /// <para><b>不动</b>「上次打开的文件夹」与现有最近记录 —— 那属于用户的使用痕迹，
    /// 清掉只会给用户添麻烦（清理入口在工具条「最近」面板里）。</para>
    /// </summary>
    public void ResetToDefaults()
    {
        ZoomPercent = DefaultZoomPercent;
        ShowOutline = true;
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
