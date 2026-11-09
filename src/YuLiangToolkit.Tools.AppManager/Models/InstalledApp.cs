namespace YuLiangToolkit.Tools.AppManager.Models;

/// <summary>
/// 注册表里一条「已安装应用」（ARP = Add / Remove Programs，即 Windows「设置 → 应用 → 安装的应用」的数据源）。
///
/// <para>本类型是<b>只读的纯数据</b>：扫描时由 <c>RegistryAppCatalog</c> 一次性填好，
/// 之后界面只读它、不再回写注册表（本工具不写任何注册表键）。</para>
/// </summary>
public sealed class InstalledApp
{
    /// <summary>应用名（注册表 <c>DisplayName</c>，已去掉首尾空白）。</summary>
    public string Name { get; init; } = "";

    /// <summary>发布者 / 厂商（<c>Publisher</c>，可能为空）。</summary>
    public string Publisher { get; init; } = "";

    /// <summary>版本号（<c>DisplayVersion</c>，可能是 <c>2026.6</c> 这种非标准写法，原样展示）。</summary>
    public string Version { get; init; } = "";

    /// <summary>安装日期（<c>InstallDate</c> 原始串，例如 <c>20260626</c>；空 = 注册表没记）。</summary>
    public string InstallDateRaw { get; init; } = "";

    /// <summary>占用大小（KB，<c>EstimatedSize</c>；&lt;= 0 = 注册表没记）。</summary>
    public long SizeInKilobytes { get; init; }

    /// <summary>安装目录（<c>InstallLocation</c>，可能为空；用来推断应用装在哪个盘）。</summary>
    public string InstallLocation { get; init; } = "";

    /// <summary>卸载命令（<c>QuietUninstallString</c> 优先，否则 <c>UninstallString</c>；空 = 没有卸载入口）。</summary>
    public string UninstallCommand { get; init; } = "";

    /// <summary>修改 / 修复命令（<c>ModifyPath</c>；空 = 这个应用不支持「修改」）。</summary>
    public string ModifyCommand { get; init; } = "";

    /// <summary>图标来源（<c>DisplayIcon</c>，可能是 <c>xxx.exe</c>、<c>xxx.exe,0</c> 或 <c>xxx.ico</c>）。</summary>
    public string IconSource { get; init; } = "";

    /// <summary>这条记录的注册表来源（HKLM / HKLM-WOW64 / HKCU），用于同名去重时的优先级判断。</summary>
    public string RegistryScope { get; init; } = "";

    /// <summary>注册表子键名（<c>{GUID}</c> 或产品名），用于稳定去重 / 排序兜底。</summary>
    public string RegistryKeyName { get; init; } = "";

    /// <summary>箭头标记：注册表里明确写了「是更新 / 补丁」的项（正常已经在扫描时过滤掉）。</summary>
    public bool IsUpdate { get; init; }

    /// <summary>应用装在哪（盘符；推断不出来是空串 = 未记录位置）。</summary>
    public string DriveLetter { get; init; } = "";

    /// <summary>解析后的安装日期（注册表没写 / 写不对 = null，排序时统一排最后）。</summary>
    public DateOnly? InstallDate { get; init; }

    /// <summary>是否记了安装日期。</summary>
    public bool HasInstallDate => InstallDate is not null;

    /// <summary>是否记了占用大小。</summary>
    public bool HasSize => SizeInKilobytes > 0;

    /// <summary>能不能卸载（有卸载命令）。</summary>
    public bool CanUninstall => UninstallCommand.Length > 0;

    /// <summary>能不能修改（有修改命令）。</summary>
    public bool CanModify => ModifyCommand.Length > 0;
}
