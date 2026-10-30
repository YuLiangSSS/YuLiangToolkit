using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.Core.Maintenance;

/// <summary>
/// <b>可选接口</b>：缓存存在 Config / 其它地方、需要跟着「主页设置 → 清理缓存」一起清掉时实现它。
///
/// <para><b>新工具要不要实现</b>：</para>
/// <list type="bullet">
///   <item>缓存写在 <c>Cache\&lt;工具Id&gt;\</c>（<see cref="Storage.ToolCache"/>）→ <b>不用实现</b>，
///         框架按工具 Id 自动清理，新加的工具自动出现在列表里；</item>
///   <item>缓存混在配置里（「最近浏览」这类列表）→ 实现本接口，返回
///         <see cref="ToolCacheItem"/>（显示名 + 数量提示 + 清理动作）即可，设置页不用改。</item>
/// </list>
///
/// <para><b>实现约定</b>：</para>
/// <list type="number">
///   <item>不要为了“读一下状态”就把配置文件建出来 —— 用 <see cref="ToolConfigPeek.Read{T}"/> /
///         <see cref="ToolConfigPeek.Mutate{T}"/>：已加载就读内存里的同一对象，没加载就读磁盘、不创建文件；</item>
///   <item>清理动作要自己落盘（推荐直接用 <see cref="ToolConfigPeek.Mutate{T}"/>）；</item>
///   <item>没有可清内容时返回空列表（或让 <see cref="ToolCacheItem.Detail"/> 为空），设置页会自动隐藏。</item>
/// </list>
///
/// <example>
/// <code>
/// public sealed class MyTool : ITool, IToolCacheSource
/// {
///     public IReadOnlyList&lt;ToolCacheItem&gt; DescribeCacheItems()
///     {
///         var count = ToolConfigPeek.Read&lt;MyConfig&gt;("my-tool")?.RecentEntries?.Count ?? 0;
///         if (count == 0) return Array.Empty&lt;ToolCacheItem&gt;();
///         return new[]
///         {
///             new ToolCacheItem
///             {
///                 Id = "recent",
///                 Name = "最近打开记录",
///                 Detail = $"{count} 条",
///                 Clear = () =&gt; ToolConfigPeek.Mutate&lt;MyConfig&gt;("my-tool", cfg =&gt; cfg.RecentEntries!.Clear()),
///             },
///         };
///     }
///     // …ITool 的成员…
/// }
/// </code>
/// </example>
/// </summary>
public interface IToolCacheSource
{
    /// <summary>
    /// 该功能**额外**的缓存项（Cache 文件夹里的东西不用报，框架自动清）。
    /// 每次刷新清理列表都会调用；没有可清内容就返回空列表。
    /// </summary>
    IReadOnlyList<ToolCacheItem> DescribeCacheItems();
}
