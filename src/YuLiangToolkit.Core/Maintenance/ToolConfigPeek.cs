using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Core.Maintenance;

/// <summary>
/// 安全地「看一眼 / 改一下」某个功能的强类型配置，<b>不会为了读状态就把配置文件建出来</b>。
///
/// <para><b>为什么需要它</b>：<see cref="ToolConfigStore.Load{T}"/> 会在文件不存在时立刻创建并写盘；
/// 而维护 / 清理这类功能只是“列一下现在有什么”，在设置页刷新列表时给所有功能建一遍配置文件显然不对。
/// 本类规则：</para>
/// <list type="bullet">
///   <item>功能此刻已加载配置（工具开着）→ 直接返回 / 修改内存里的<b>同一个对象</b>，界面能立即看到变化；</item>
///   <item>没加载 → 从 <c>Config\&lt;功能Id&gt;\config.json</c> 读一份临时对象（文件不存在就返回 null / 什么都不做）。</item>
/// </list>
///
/// <example>
/// <code>
/// // 读（不创建文件）：「最近浏览」有多少条
/// var count = ToolConfigPeek.Read&lt;PictureViewerConfig&gt;("picture-viewer")?.RecentEntries?.Count ?? 0;
///
/// // 改（已加载就改内存对象并落盘；没加载就读盘→改→写盘）
/// ToolConfigPeek.Mutate&lt;PictureViewerConfig&gt;("picture-viewer", cfg =&gt; cfg.ClearRecent());
/// </code>
/// </example>
/// </summary>
public static class ToolConfigPeek
{
    /// <summary>配置文件完整路径：<c>Config\&lt;功能Id&gt;\&lt;文件名&gt;</c>（只拼路径）。</summary>
    public static string PathOf(string toolId, string fileName = "config.json")
        => Path.Combine(AppPaths.ToolConfigDir(toolId), fileName);

    /// <summary>
    /// 读配置：已加载返回内存里的实例；否则读磁盘；都没有返回 null（<b>不会创建文件</b>）。
    /// <para>⚠ 返回的可能就是工具正在用的实例，只读可以，别直接改（改请用 <see cref="Mutate{T}"/>）。</para>
    /// </summary>
    public static T? Read<T>(string toolId, string fileName = "config.json") where T : ToolConfig
    {
        if (string.IsNullOrWhiteSpace(toolId)) return null;

        var live = ToolConfigStore.TryGet(toolId, fileName)?.TryGetTracked<T>();
        if (live is not null) return live;

        return JsonFileStore.Read<T>(PathOf(toolId, fileName));
    }

    /// <summary>
    /// 改配置并落盘：已加载 → 改内存里的实例（自动保存会写回）；没加载 → 读盘 → 改 → 写盘。
    /// 文件不存在（= 本来就是默认值）时返回 false，不做任何事。
    /// </summary>
    public static bool Mutate<T>(string toolId, Action<T> change, string fileName = "config.json")
        where T : ToolConfig
    {
        if (change is null || string.IsNullOrWhiteSpace(toolId)) return false;

        var live = ToolConfigStore.TryGet(toolId, fileName)?.TryGetTracked<T>();
        if (live is not null)
        {
            change(live);
            live.SaveNow();
            return true;
        }

        var path = PathOf(toolId, fileName);
        var fromDisk = JsonFileStore.Read<T>(path);
        if (fromDisk is null) return false;   // 没有配置文件 = 本来就是默认值，没有缓存要清

        change(fromDisk);
        return JsonFileStore.Write(path, fromDisk);
    }
}
