using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Tools.SoftwareUpdater.Winget;

namespace YuLiangToolkit.Tools.SoftwareUpdater;

/// <summary>
/// 「软件升级器」的强类型配置 → <c>Config\software-updater\config.json</c>。
/// 和其它工具一样：可读写属性一改就自动落盘，不需要保存按钮。
///
/// <para>这里每个属性都走 <c>Set(ref 字段, value)</c> 而不是自动属性：界面要在选项变化的
/// <b>当下</b>立刻重算筛选 / 统计（<c>ViewModel</c> 订阅了 <c>PropertyChanged</c>），
/// 同时 <c>ToolConfig</c> 收到通知后会排一次防抖保存。</para>
/// </summary>
public sealed class SoftwareUpdaterConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名，自检会核对它与 ITool.Id 一致）。</summary>
    [JsonIgnore]
    public static string ToolId => "software-updater";

    private string _scanMode = WingetCommand.AllApps;
    /// <summary>扫描范围：<see cref="WingetCommand.AllApps"/>（全部已安装）或 <see cref="WingetCommand.UpgradableOnly"/>（仅可更新）。</summary>
    public string ScanMode
    {
        get => _scanMode;
        set => Set(ref _scanMode, value);
    }

    private bool _onlyUpgradable = true;
    /// <summary>列表里只显示有可用更新的应用（关掉就能看到扫描到的全部已安装应用）。</summary>
    public bool OnlyUpgradable
    {
        get => _onlyUpgradable;
        set => Set(ref _onlyUpgradable, value);
    }

    private bool _autoScanOnOpen = true;
    /// <summary>打开工具页时自动扫描一次（截图冒烟模式 YLT_SHOT 下会自动跳过）。</summary>
    public bool AutoScanOnOpen
    {
        get => _autoScanOnOpen;
        set => Set(ref _autoScanOnOpen, value);
    }

    private bool _silentInstall;
    /// <summary>给 winget 加 --silent：静默安装，不弹安装程序界面。</summary>
    public bool SilentInstall
    {
        get => _silentInstall;
        set => Set(ref _silentInstall, value);
    }

    private bool _includeUnknown;
    /// <summary>给 winget 加 --include-unknown：连当前版本未知的包也一起列 / 升级。</summary>
    public bool IncludeUnknown
    {
        get => _includeUnknown;
        set => Set(ref _includeUnknown, value);
    }

    private bool _confirmBeforeUpgrade = true;
    /// <summary>批量升级前弹一次确认框（列出要升级的应用）。</summary>
    public bool ConfirmBeforeUpgrade
    {
        get => _confirmBeforeUpgrade;
        set => Set(ref _confirmBeforeUpgrade, value);
    }

    private int _upgradeTimeoutMinutes = 10;
    /// <summary>单个应用升级的最长等待时间（分钟），超时就终止这一项、继续下一项。</summary>
    public int UpgradeTimeoutMinutes
    {
        get => _upgradeTimeoutMinutes;
        set => Set(ref _upgradeTimeoutMinutes, value);
    }

    /// <summary>夹到合法区间，避免手改配置文件写出负数 / 超大值。</summary>
    [JsonIgnore]
    public int SafeTimeoutMinutes => Math.Clamp(UpgradeTimeoutMinutes, 1, 120);
}
