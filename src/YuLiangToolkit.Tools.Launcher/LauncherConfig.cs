using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Launcher;

/// <summary>
/// 「软件启动器」的强类型配置 → <c>Config\launcher\config.json</c>。
///
/// <para>启动项列表放在配置里（<see cref="ObservableCollection{T}"/> 方便界面增删；
/// 与「邮箱代收」的账号列表同一套做法），增删 / 改名 / 换图标后最多 400ms 自动落盘，
/// 不需要「保存」按钮。自定义图标文件本体放在 <c>Data\launcher\icons\</c>。</para>
/// </summary>
public sealed class LauncherConfig : ToolConfig
{
    /// <summary>图标墙上的启动项（顺序 = 导入顺序）。</summary>
    public ObservableCollection<Models.LauncherItemConfig> Items { get; set; } = new();

    /// <summary>打开工具页时自动检查一次失效项（关闭后只能手点「刷新状态」）。</summary>
    public bool AutoRefreshStatus { get; set; } = true;

    /// <summary>上次选中的启动项 Id（重新打开工具时恢复选中）。</summary>
    public string LastSelectedId { get; set; } = "";

    /// <summary>启动项数量（只读计算属性，不落盘）。</summary>
    [JsonIgnore]
    public int ItemCount => Items.Count;
}
