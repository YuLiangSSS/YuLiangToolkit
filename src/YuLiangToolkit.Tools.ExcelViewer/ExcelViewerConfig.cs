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
    /// 「恢复默认设置」：网格线开、最近上限回 50。
    /// <b>不清空最近记录</b>（只按新上限裁剪），也不动「上次打开的文件夹」——
    /// 那不是显示设置，清了反而给用户添麻烦。
    /// </summary>
    public void ResetToDefaults()
    {
        ShowGridLines = true;
        RecentLimit = RecentFiles.DefaultLimit;
        TrimRecentToLimit();
    }
}
