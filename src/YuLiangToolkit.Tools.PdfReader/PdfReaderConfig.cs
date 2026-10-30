using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 「PDF 阅读器」的设置 → <c>Config\pdf-reader\config.json</c>。
///
/// <para>存「下次打开还要接着用」的选项：阅读方式、拖动惯性、滚轮步长、缩放百分比、
/// 双页页面间距、上次打开 PDF 所在文件夹。改一下自动落盘，没有保存按钮（见 AGENTS.md）。</para>
/// </summary>
public sealed class PdfReaderConfig : ToolConfig
{
    /// <summary>功能 Id（= <c>Config\pdf-reader\</c> 与 <c>Data\pdf-reader\</c> 的文件夹名）。</summary>
    public const string ToolId = "pdf-reader";

    /// <summary>连续滚动模式下滚轮一格的默认步长（像素）。</summary>
    public const int DefaultWheelStepPixels = 120;

    /// <summary>缩放百分比：默认 100%（= 铺满基准，**不是** PDF 原始点数 1:1），允许 25 ~ 800。</summary>
    public const int DefaultZoomPercent = 100;
    public const int MinZoomPercent = 25;
    public const int MaxZoomPercent = 800;

    /// <summary>双页模式左右两页之间的默认间距（像素）：默认 2（贴近，像翻书）；允许 0 ~ 64。</summary>
    public const int DefaultSpreadGapPixels = 2;
    public const int MinSpreadGapPixels = 0;
    public const int MaxSpreadGapPixels = 64;

    /// <summary>连续滚动模式页与页之间的间距（像素）：固定 16，跟图片阅读器一致。</summary>
    public const int ContinuousPageGapPixels = 16;

    /// <summary>长带两端 / 铺满方向上留的边距（像素）：固定 24。</summary>
    public const int SideMarginPixels = 24;

    /// <summary>最多记住多少本 PDF 的阅读位置（超出按最后阅读时间淘汰最老的，避免配置文件无限长）。</summary>
    public const int MaxReadingPositions = 200;

    // ---------- 持久化字段 ----------

    /// <summary>阅读方式：纵向 上→下（默认）/ 横向 左→右 / 双页 翻页。枚举按 C# 原名存字符串。</summary>
    public PdfReadMode ReadMode { get; set; } = PdfReadMode.Vertical;

    /// <summary>左键拖动松手后的惯性（默认开）；关掉 = 松手即停。</summary>
    public bool InertiaEnabled { get; set; } = true;

    /// <summary>连续滚动模式滚轮一格的像素（20 ~ 600，默认 120）。</summary>
    public int WheelStepPixels { get; set; } = DefaultWheelStepPixels;

    /// <summary>
    /// 缩放百分比：默认 100%（= 程序默认的铺满基准，**不是** PDF 原始点数 1:1），允许 25 ~ 800。
    /// 工具条的百分比输入框 / 放大镜按钮 / Ctrl+滚轮都改它。
    /// </summary>
    public int ZoomPercent { get; set; } = DefaultZoomPercent;

    /// <summary>双页翻页时左右两页之间的间距（像素，0 ~ 64，默认 2 = 贴近）。</summary>
    public int SpreadGapPixels { get; set; } = DefaultSpreadGapPixels;

    /// <summary>上次打开 PDF 所在文件夹（只用来当文件对话框的起始目录，不会自动打开）。</summary>
    public string LastFolder { get; set; } = "";

    /// <summary>
    /// 每本 PDF 的上次阅读位置：key = <see cref="NormalizePositionKey"/> 规范化后的完整路径（小写），
    /// value = 页号 / 阅读比例 / 当时的阅读方式 / 保存时间。打开同一本 PDF 时自动回到这里。
    /// </summary>
    public Dictionary<string, PdfReadingPosition> ReadingPositions { get; set; } = new();

    /// <summary>「最近浏览」默认记多少条。</summary>
    public const int DefaultRecentLimit = 50;

    /// <summary>「最近浏览」上限的最大值；范围 0 ~ 100。</summary>
    public const int MaxRecentLimit = 100;

    /// <summary>「最近浏览」最多记多少条：0 = 不记录（并清空列表），默认 50，允许 0 ~ 100。</summary>
    public int RecentLimit { get; set; } = DefaultRecentLimit;

    /// <summary>最近打开过的 PDF（新 → 旧；同一路径只留一条，最多 <see cref="RecentLimitEffective"/> 条）。</summary>
    public List<PdfRecentEntry> RecentEntries { get; set; } = new();

    // ---------- 最近浏览 ----------

    /// <summary>生效的上限（夹到 0 ~ 100，手改坏配置也不会存出离谱的条数）。</summary>
    [JsonIgnore]
    public int RecentLimitEffective => Math.Clamp(RecentLimit, 0, MaxRecentLimit);

    /// <summary>现在要不要记录最近浏览（上限 0 = 不记录）。</summary>
    [JsonIgnore]
    public bool RecordsRecent => RecentLimitEffective > 0;

    /// <summary>把路径规范成完整路径（用于去重）；非法路径返回空串。</summary>
    public static string NormalizeRecentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return ""; }
    }

    /// <summary>
    /// 记一次「打开过的 PDF」：放到最前面、同一路径去重、超过上限就砍掉最旧的。
    /// 上限是 0 时按「不记录」处理（顺手清空已有列表）。
    /// </summary>
    public void RememberRecent(string? path, DateTime? lastOpenedUtc = null)
    {
        var full = NormalizeRecentPath(path);
        if (full.Length == 0) return;

        RecentEntries ??= new List<PdfRecentEntry>();
        if (!RecordsRecent)
        {
            ClearRecent();
            return;
        }

        RecentEntries.RemoveAll(entry => string.Equals(NormalizeRecentPath(entry.Path), full, StringComparison.OrdinalIgnoreCase));
        RecentEntries.Insert(0, new PdfRecentEntry { Path = full, LastOpenedUtc = lastOpenedUtc ?? DateTime.UtcNow });
        TrimRecentToLimit();
        OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>按当前上限裁剪列表；上限 0 = 清空。</summary>
    public void TrimRecentToLimit()
    {
        RecentEntries ??= new List<PdfRecentEntry>();

        var limit = RecentLimitEffective;
        if (limit == 0)
        {
            if (RecentEntries.Count > 0) RecentEntries.Clear();
            return;
        }

        if (RecentEntries.Count > limit) RecentEntries.RemoveRange(limit, RecentEntries.Count - limit);
    }

    /// <summary>从最近浏览里去掉某个 PDF（文件已不存在时用）。</summary>
    public void RemoveRecent(string? path)
    {
        var full = NormalizeRecentPath(path);
        if (full.Length == 0 || RecentEntries is null) return;

        var removed = RecentEntries.RemoveAll(entry => string.Equals(NormalizeRecentPath(entry.Path), full, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>清空最近浏览（工具条「最近」面板里的「清空」按钮）。</summary>
    public void ClearRecent()
    {
        RecentEntries ??= new List<PdfRecentEntry>();
        if (RecentEntries.Count == 0) return;
        RecentEntries.Clear();
        OnPropertyChanged(nameof(RecentEntries));
    }

    // ---------- 阅读位置（记住上次读到哪） ----------

    /// <summary>把路径规范成配置里的 key：完整路径 + 小写（Windows 路径不区分大小写）。非法路径返回空串。</summary>
    public static string NormalizePositionKey(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return "";
        try { return Path.GetFullPath(filePath.Trim().Trim('"')).ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>查某本 PDF 记住的阅读位置；没有记录 / 路径非法返回 null。</summary>
    public PdfReadingPosition? FindReadingPosition(string? filePath)
    {
        var key = NormalizePositionKey(filePath);
        if (key.Length == 0 || ReadingPositions is null) return null;
        return ReadingPositions.TryGetValue(key, out var position) ? position : null;
    }

    /// <summary>
    /// 记住某本 PDF 的阅读位置（同一本覆盖旧值；超过 <see cref="MaxReadingPositions"/> 本时淘汰最老的）。
    /// 改完主动通知属性变更 → 配置自动落盘（见 AGENTS.md：没有保存按钮）。
    /// </summary>
    public void RememberReadingPosition(string? filePath, PdfReadingPosition? position)
    {
        var key = NormalizePositionKey(filePath);
        if (key.Length == 0 || position is null) return;

        ReadingPositions ??= new Dictionary<string, PdfReadingPosition>();
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

    /// <summary>忘掉某本 PDF 的阅读位置（一般用不到，留着给以后清理入口）。</summary>
    public void ForgetReadingPosition(string? filePath)
    {
        var key = NormalizePositionKey(filePath);
        if (key.Length == 0 || ReadingPositions is null) return;
        if (ReadingPositions.Remove(key)) OnPropertyChanged(nameof(ReadingPositions));
    }

    // ---------- 计算属性（只读 → 不会被写进 JSON） ----------

    /// <summary>合法的阅读方式（手改坏配置时退回纵向）。</summary>
    [JsonIgnore]
    public PdfReadMode ReadModeEffective => PdfReadModes.Normalize(ReadMode);

    /// <summary>生效的滚轮步长（夹到 20 ~ 600，手改坏配置也不会滚飞）。</summary>
    [JsonIgnore]
    public int WheelStepEffective => Math.Clamp(WheelStepPixels, 20, 600);

    /// <summary>生效的缩放百分比（夹到 25 ~ 800）。</summary>
    [JsonIgnore]
    public int ZoomPercentEffective => Math.Clamp(ZoomPercent, MinZoomPercent, MaxZoomPercent);

    /// <summary>缩放系数（100% → 1.0，200% → 2.0）。</summary>
    [JsonIgnore]
    public double ZoomScale => ZoomPercentEffective / 100.0;

    /// <summary>生效的双页页面间距（夹到 0 ~ 64）。</summary>
    [JsonIgnore]
    public int SpreadGapEffective => Math.Clamp(SpreadGapPixels, MinSpreadGapPixels, MaxSpreadGapPixels);

    /// <summary>
    /// 「恢复默认设置」：阅读方式回纵向、惯性开、滚轮 120、缩放 100%、双页间距 2。
    /// **不动「上次打开的文件夹」**（那不是显示设置，清了反而给用户添麻烦）。
    /// </summary>
    public void ResetToDefaults()
    {
        ReadMode = PdfReadMode.Vertical;
        InertiaEnabled = true;
        WheelStepPixels = DefaultWheelStepPixels;
        ZoomPercent = DefaultZoomPercent;
        SpreadGapPixels = DefaultSpreadGapPixels;
        RecentLimit = DefaultRecentLimit;
        TrimRecentToLimit();   // 只按新上限裁剪，不主动清掉用户的最近浏览记录
    }

    /// <summary>把当前缩放百分比按步数增减并夹好（放大镜按钮 / Ctrl+滚轮用）。</summary>
    public static int StepZoomPercent(int currentPercent, int steps, int stepPercent = 10)
        => Math.Clamp(
            Math.Clamp(currentPercent, MinZoomPercent, MaxZoomPercent) + steps * stepPercent,
            MinZoomPercent,
            MaxZoomPercent);
}
