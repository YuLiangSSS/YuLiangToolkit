using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.App.Models;

/// <summary>主页功能卡片的排序方式（存在 <c>Config\shell\config.json</c>，重启后保持）。</summary>
public enum ToolSortMode
{
    /// <summary>默认顺序：按 <see cref="YuLiangToolkit.Core.Tools.ITool.Order"/>，即作者排的优先级。</summary>
    Default,

    /// <summary>最近使用：刚打开过的功能排最前面（从未用过的排最后）。</summary>
    RecentlyUsed,

    /// <summary>名称 A→Z（中文按当前系统区域的拼音顺序）。</summary>
    NameAsc,

    /// <summary>名称 Z→A。</summary>
    NameDesc,

    /// <summary>按分类归堆，同分类内再按默认优先级。</summary>
    Category,
}

/// <summary>
/// 程序外壳（主页 / 导航）自己的配置 → <c>Config\shell\config.json</c>。
///
/// <para>外壳也算一个“功能”，所以同样用 <see cref="ToolConfig"/>：改一下就自动落盘。
/// 这里存主页排序偏好，以及每个功能上次被打开的时间（“最近使用”排序的依据，
/// 也顺便让你在卡片上看到“3 天前用过”）。</para>
/// </summary>
public sealed class ShellConfig : ToolConfig
{
    /// <summary>主页排序方式。</summary>
    public ToolSortMode SortMode { get; set; } = ToolSortMode.Default;

    /// <summary>功能 Id → 上次打开时间（本地时间）。</summary>
    public Dictionary<string, DateTime> LastUsed { get; set; } = new();

    /// <summary>是否在界面上绘制鼠标粒子效果（主页设置里的开关，默认开）。</summary>
    public bool ShowParticles { get; set; } = true;

    /// <summary>取某个功能上次使用时间（没有记录返回 null）。</summary>
    public DateTime? GetLastUsed(string toolId)
        => LastUsed.TryGetValue(toolId, out var time) ? time : null;

    /// <summary>记录某个功能刚刚被使用（字典改动会被后台自动保存捡到）。</summary>
    public void MarkUsed(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return;
        LastUsed[toolId] = DateTime.Now;
    }
}
