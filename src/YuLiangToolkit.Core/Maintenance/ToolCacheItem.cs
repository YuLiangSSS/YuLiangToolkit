namespace YuLiangToolkit.Core.Maintenance;

/// <summary>
/// 一条**工具自己报告**的缓存项：用于缓存存在 <c>Config\&lt;功能Id&gt;\config.json</c> 里、
/// 没法靠「删 Cache 文件夹」清掉的情况（例如图片阅读器 / PDF 阅读器的「最近浏览」）。
///
/// <para><b>什么时候需要它</b>：新功能的缓存如果写在 <c>Cache\&lt;功能Id&gt;\</c>（推荐，见
/// <see cref="Storage.ToolCache"/>），<b>什么都不用做</b> —— 「清理缓存」会按工具 Id 自动认出来。
/// 只有当缓存混在配置里（最近浏览、最近会话这类），才让工具类实现
/// <see cref="IToolCacheSource"/> 报告一条清理项。</para>
/// </summary>
public sealed class ToolCacheItem
{
    /// <summary>功能内的稳定 Id（例如 "recent"），和工具 Id 一起构成唯一键。</summary>
    public required string Id { get; init; }

    /// <summary>显示名（例如「最近浏览记录」）。</summary>
    public required string Name { get; init; }

    /// <summary>一句话说明：清的是什么、会不会影响设置。</summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// 可读的数量 / 大小提示（例如「12 条」「3 个会话」）；为空表示没什么可清的，
    /// 引擎会把它从列表里筛掉（避免设置页出现一堆「0 条」）。
    /// </summary>
    public string Detail { get; init; } = "";

    /// <summary>真正执行清理的动作（引擎调用它；实现方自己负责落盘）。</summary>
    public required Action Clear { get; init; }
}
