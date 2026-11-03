using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.ContextMenu;

/// <summary>
/// 「右键菜单管理」的强类型配置 → <c>Config\context-menu-manager\config.json</c>。
///
/// <para>规则同其它工具：可读写属性 = 持久化项（改一下自动落盘，不需要保存按钮）；
/// 只读计算属性不落盘，带 setter 的计算属性必须显式标 <see cref="JsonIgnoreAttribute"/>。</para>
/// </summary>
public sealed class ContextMenuManagerConfig : ToolConfig
{
    /// <summary>只看已隐藏的条目（列表筛选）。</summary>
    public bool ShowOnlyHidden { get; set; }

    /// <summary>类型筛选：<c>"全部"</c> / <c>"菜单命令"</c> / <c>"扩展模块"</c>。</summary>
    public string KindFilter { get; set; } = "全部";

    /// <summary>位置筛选：<c>"全部"</c> 或条目里的 <see cref="ContextMenuEntry.ScopeLabel"/>。</summary>
    public string ScopeFilter { get; set; } = "全部";

    /// <summary>是否按位置筛选（只读计算属性，不写进 JSON）。</summary>
    [JsonIgnore]
    public bool HasScopeFilter
        => !string.IsNullOrWhiteSpace(ScopeFilter) && !string.Equals(ScopeFilter, "全部", StringComparison.Ordinal);

    /// <summary>是否按类型筛选（只读计算属性，不写进 JSON）。</summary>
    [JsonIgnore]
    public bool HasKindFilter
        => !string.IsNullOrWhiteSpace(KindFilter) && !string.Equals(KindFilter, "全部", StringComparison.Ordinal);

    /// <summary>当前是否处于任何筛选状态（只读计算属性，不写进 JSON）。</summary>
    [JsonIgnore]
    public bool IsFiltered => ShowOnlyHidden || HasScopeFilter || HasKindFilter;
}
