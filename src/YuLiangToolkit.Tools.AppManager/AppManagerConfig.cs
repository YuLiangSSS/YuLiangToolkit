using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.AppManager;

/// <summary>
/// 「应用管理器」的强类型配置 → <c>Config\app-manager\config.json</c>。
/// 和其它工具一样：可读写属性一改就自动落盘，没有保存按钮。
///
/// <para>这里每个属性都走 <c>Set(ref 字段, value)</c>：界面要在选项变化的<b>当下</b>
/// 立刻重算筛选 / 排序（<c>AppManagerViewModel</c> 订阅了 <c>PropertyChanged</c>），
/// 同时 <c>ToolConfig</c> 收到通知后会排一次防抖保存。</para>
/// </summary>
public sealed class AppManagerConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名，自检会核对它与 ITool.Id 一致）。</summary>
    [JsonIgnore]
    public static string ToolId => "app-manager";

    private string _searchText = "";
    /// <summary>搜索词（名称 / 发布者 / 版本）。记下来下次打开还在，方便接着筛。</summary>
    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value ?? "");
    }

    private string _driveFilter = "";
    /// <summary>驱动器筛选：空 = 所有驱动器，<c>?</c> = 未记录位置，否则是盘符（<c>C</c>）。</summary>
    public string DriveFilter
    {
        get => _driveFilter;
        set => Set(ref _driveFilter, value ?? "");
    }

    private string _sortKey = "name";
    /// <summary>排序依据（<c>name</c> / <c>installdate</c> / <c>size</c> / <c>publisher</c>）。</summary>
    public string SortKey
    {
        get => _sortKey;
        set => Set(ref _sortKey, value ?? "name");
    }

    private bool _sortDescending;
    /// <summary>逆序（大的 / 新的 / 从 Z 到 A 在前）。</summary>
    public bool SortDescending
    {
        get => _sortDescending;
        set => Set(ref _sortDescending, value);
    }

    private bool _autoScanOnOpen = true;
    /// <summary>打开工具页时自动扫描一次（截图冒烟模式 YLT_SHOT 下会自动跳过）。</summary>
    public bool AutoScanOnOpen
    {
        get => _autoScanOnOpen;
        set => Set(ref _autoScanOnOpen, value);
    }

    private bool _confirmBeforeRun = true;
    /// <summary>点「卸载 / 修改」前弹确认框，列出应用名与实际要执行的命令行。</summary>
    public bool ConfirmBeforeRun
    {
        get => _confirmBeforeRun;
        set => Set(ref _confirmBeforeRun, value);
    }

    private bool _rescanAfterRun = true;
    /// <summary>卸载 / 修改进程退出后自动重扫列表（卸载完成的项会自己消失）。</summary>
    public bool RescanAfterRun
    {
        get => _rescanAfterRun;
        set => Set(ref _rescanAfterRun, value);
    }

    private bool _showUnknownSize = true;
    /// <summary>显示注册表没记大小的应用（关掉后「按大小排序」时列表更干净）。</summary>
    public bool ShowUnknownSize
    {
        get => _showUnknownSize;
        set => Set(ref _showUnknownSize, value);
    }
}
