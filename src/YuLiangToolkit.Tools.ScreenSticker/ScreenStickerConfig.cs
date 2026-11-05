using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// 一张「贴在桌面上」的贴纸记录（存在 <c>Config\screen-sticker\config.json</c> 的 <c>stickers</c> 里）。
///
/// <para>一个实例对应一个透明无边框窗口。列表顺序 = 贴纸的层级顺序：
/// <b>下标 0 最靠下、最后一个最靠上</b>（跨 topmost 状态时，置顶的贴纸永远排在所有非置顶贴纸上面，
/// 这是 Windows 的窗口带规则，不归我们管）。</para>
///
/// <para>位置 / 大小都用 DIP（与 WPF 的 <c>Window.Left</c> / <c>Width</c> 同一坐标系），
/// 恢复时如果显示器数量 / 分辨率变了，会由 <see cref="StickerGeometry"/> 拉回可见区域。</para>
/// </summary>
public sealed class StickerEntry
{
    /// <summary>图片完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>窗口左边界（DIP）。</summary>
    public double Left { get; set; }

    /// <summary>窗口上边界（DIP）。</summary>
    public double Top { get; set; }

    /// <summary>窗口宽度（DIP）；高度按图片比例算（见 <see cref="Height"/>）。</summary>
    public double Width { get; set; } = ScreenStickerConfig.DefaultStickerWidth;

    /// <summary>窗口高度（DIP），与 <see cref="Width"/> 保持图片原始宽高比。</summary>
    public double Height { get; set; } = ScreenStickerConfig.DefaultStickerWidth;

    /// <summary>这张贴纸是否「强制置顶」（覆盖其它窗口）。</summary>
    public bool Topmost { get; set; }

    /// <summary>第一次贴到桌面的时间（UTC），只做排序 / 排查用。</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 「屏幕贴纸」的设置 → <c>Config\screen-sticker\config.json</c>。
///
/// <para>改动即时落盘、没有保存按钮（规则见 AGENTS.md）：最近使用的图片列表、贴到桌面上的贴纸状态、
/// 启动时要不要自动恢复、新贴纸默认宽度 / 默认是否置顶。</para>
/// </summary>
public sealed class ScreenStickerConfig : ToolConfig
{
    /// <summary>功能 Id（= <c>Config\screen-sticker\</c> 与 <c>Data\screen-sticker\</c> 的文件夹名）。</summary>
    public const string ToolId = "screen-sticker";

    /// <summary>「最近使用」默认记多少条。</summary>
    public const int DefaultRecentLimit = 50;

    /// <summary>「最近使用」上限的最大值；范围 0 ~ 100。</summary>
    public const int MaxRecentLimit = 100;

    /// <summary>新贴纸的默认宽度（DIP）；真正的合法范围在 <see cref="StickerGeometry"/> 里。</summary>
    public const double DefaultStickerWidth = 320;

    // ---------- 持久化字段 ----------

    /// <summary>最后一次选图 / 贴图时所在的文件夹（只给文件对话框当起始目录用）。</summary>
    public string LastFolder { get; set; } = "";

    /// <summary>「最近使用」最多记多少条：0 = 不记录（并清空列表），默认 50，允许 0 ~ 100。</summary>
    public int RecentLimit { get; set; } = DefaultRecentLimit;

    /// <summary>最近贴过的图片（新 → 旧；同一路径只留一条）。</summary>
    public List<RecentFileEntry> RecentEntries { get; set; } = new();

    /// <summary>当前桌面上的贴纸（顺序 = 层级，0 最靠下）。</summary>
    public List<StickerEntry> Stickers { get; set; } = new();

    /// <summary>程序启动时自动把上次的贴纸放回桌面（默认开）。</summary>
    public bool AutoRestoreOnStartup { get; set; } = true;

    /// <summary>新贴上去的贴纸默认「强制置顶」（默认关：先像桌面贴纸一样待在普通窗口层）。</summary>
    public bool DefaultTopmost { get; set; }

    /// <summary>新贴纸的默认宽度（DIP，48 ~ 2400）；恢复时按图片比例算高度。</summary>
    public double DefaultWidth { get; set; } = DefaultStickerWidth;

    // ---------- 计算属性（只读 → 不落盘） ----------

    /// <summary>生效的「最近使用」上限（夹到 0 ~ 100，手改坏配置也不会存出离谱的条数）。</summary>
    [JsonIgnore]
    public int RecentLimitEffective => RecentFiles.ClampLimit(RecentLimit);

    /// <summary>现在要不要记录最近使用（上限 0 = 不记录）。</summary>
    [JsonIgnore]
    public bool RecordsRecent => RecentLimitEffective > 0;

    /// <summary>生效的新贴纸默认宽度。</summary>
    [JsonIgnore]
    public double DefaultWidthEffective => StickerGeometry.ClampWidth(DefaultWidth);

    // ---------- 最近使用 ----------

    /// <summary>把路径规范成完整路径（去重 / 落盘都用它）；非法路径返回空串。</summary>
    public static string NormalizePath(string? path) => RecentFiles.NormalizePath(path);

    /// <summary>记一次「贴过 / 选过的图片」：放到最前面、同一路径去重、超上限砍掉最旧的。</summary>
    public void RememberRecent(string? path, DateTime? lastOpenedUtc = null)
    {
        RecentEntries ??= new List<RecentFileEntry>();
        RecentFiles.Remember(RecentEntries, path, RecentLimit, lastOpenedUtc);
        OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>按当前上限裁剪列表；上限 0 = 清空。</summary>
    public void TrimRecentToLimit()
    {
        RecentEntries ??= new List<RecentFileEntry>();
        RecentFiles.Trim(RecentEntries, RecentLimit);
        OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>从最近使用里去掉一张图（图片已被删除时用）。</summary>
    public void RemoveRecent(string? path)
    {
        if (RecentEntries is null) return;
        var removed = RecentEntries.RemoveAll(entry => string.Equals(
            NormalizePath(entry.Path), NormalizePath(path), StringComparison.OrdinalIgnoreCase));
        if (removed > 0) OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>清空最近使用（工具页「清空」按钮；不会删本地图片）。</summary>
    public void ClearRecent()
    {
        RecentEntries ??= new List<RecentFileEntry>();
        if (RecentEntries.Count == 0) return;
        RecentEntries.Clear();
        OnPropertyChanged(nameof(RecentEntries));
    }

    // ---------- 贴纸列表 ----------

    /// <summary>按路径找一条贴纸记录（恢复 / 去重用；找不到返回 null）。</summary>
    public StickerEntry? FindSticker(string? path)
    {
        var full = NormalizePath(path);
        if (full.Length == 0 || Stickers is null) return null;
        return Stickers.FirstOrDefault(entry => string.Equals(
            NormalizePath(entry.Path), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把所有贴纸记录从配置里去掉（窗口关闭时也由会话负责关窗）。</summary>
    public void ClearStickers()
    {
        if (Stickers is null || Stickers.Count == 0) return;
        Stickers.Clear();
        OnPropertyChanged(nameof(Stickers));
    }

    /// <summary>
    /// 工具页「恢复默认设置」：只把偏好项改回默认，不动已经贴在桌面上的贴纸与最近使用记录
    /// （那是用户当下的成果，不能因为点一下「恢复默认」就全没了）。
    /// </summary>
    public void ResetPreferences()
    {
        AutoRestoreOnStartup = true;
        DefaultTopmost = false;
        DefaultWidth = DefaultStickerWidth;
        RecentLimit = DefaultRecentLimit;
        TrimRecentToLimit();
    }
}
