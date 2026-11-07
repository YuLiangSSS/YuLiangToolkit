using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.Launcher.Models;

/// <summary>
/// 配置里的一条启动项 → <c>Config\launcher\config.json</c> 的 <c>items[]</c>。
///
/// <para><b>只存“必要且稳定”的信息</b>：显示名、导入的原文件路径、自定义图标（可选）。
/// 目标路径 / 版本号 / 大小 / 修改时间这些每次打开页面都会重新探测，不写进配置
/// （软件升级后版本号会变，存下来只会看到旧值）。</para>
///
/// <para>约定：<see cref="Path"/> 是导入时那个 <c>.exe</c> / <c>.lnk</c> 的完整路径；
/// 快捷方式的目标程序每次用 <c>IShellLink</c> 现读（Windows 自己知道怎么解析）。</para>
/// </summary>
public sealed class LauncherItemConfig
{
    /// <summary>稳定 Id（GUID）：自定义图标文件名、选中状态恢复都靠它。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>图标下面显示的名字（重命名只改这个，不会去改磁盘上的文件）。</summary>
    public string Name { get; set; } = "";

    /// <summary>导入的原文件完整路径：<c>.exe</c> 或 <c>.lnk</c>。</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// 自定义图标相对 <c>Data\launcher\</c> 的路径（例如 <c>icons\xxx.png</c>）；空 = 用程序自己的图标。
    /// 换图标时会把选中的图标复制 / 转成 PNG 存进数据文件夹，这样原图标文件删了也不影响。
    /// </summary>
    public string CustomIconPath { get; set; } = "";

    /// <summary>导入时间（详情页显示，也用于将来按导入顺序排序）。</summary>
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>是否是快捷方式（只读计算属性：不参与 JSON 落盘）。</summary>
    [JsonIgnore]
    public bool IsShortcut => LauncherRules.IsShortcutPath(Path);

    /// <summary>文件名（含扩展名；只读计算属性）。</summary>
    [JsonIgnore]
    public string FileName => LauncherRules.FileNameOf(Path);

    /// <summary>有没有自定义图标（只读计算属性）。</summary>
    [JsonIgnore]
    public bool HasCustomIcon => !string.IsNullOrWhiteSpace(CustomIconPath);
}
