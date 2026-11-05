using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.SoftwareUpdater.Winget;

namespace YuLiangToolkit.Tools.SoftwareUpdater.ViewModels;

/// <summary>一次升级在这一行上的结果。</summary>
public enum UpgradeState
{
    /// <summary>还没处理（或刚扫描出来）。</summary>
    Pending,

    /// <summary>winget 正在装这一个。</summary>
    Running,

    /// <summary>winget 退出码 0。</summary>
    Done,

    /// <summary>winget 报错 / 超时。</summary>
    Failed,

    /// <summary>用户取消批量升级后剩下的项。</summary>
    Skipped,
}

/// <summary>
/// 列表里的一行：把纯数据 <see cref="WingetPackage"/> 包成可勾选、可显示进度的界面模型。
/// <para>「能不能升级」只在构造时算一次（<see cref="WingetCommand.CanUpgrade"/>）：
/// 有可用版本 + ID 没被 winget 截断 + ID 过白名单。注册表来源的 <c>ARP\Machine\…</c>
/// 一律不可勾选，避免把不能升级的应用送进 <c>winget upgrade --id</c>。</para>
/// </summary>
public sealed class SoftwareUpdaterItem : ObservableObject
{
    private bool _isChecked;
    private UpgradeState _state = UpgradeState.Pending;
    private string _resultText = "";

    public SoftwareUpdaterItem(WingetPackage package)
    {
        Package = package;
        CanUpgrade = WingetCommand.CanUpgrade(package);
    }

    public WingetPackage Package { get; }

    public string Name => Package.Name;
    public string Id => Package.Id;
    public string Version => Package.Version;
    public string AvailableVersion => Package.AvailableVersion;
    public string Source => Package.Source;
    public bool HasUpdate => Package.HasUpdate;

    /// <summary>这一行能否送进 winget 升级（决定复选框是否可用）。</summary>
    public bool CanUpgrade { get; }

    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "未知" : Version;

    public string AvailableText => HasUpdate ? AvailableVersion : "—";

    public string SourceText => string.IsNullOrWhiteSpace(Source) ? "本机 / 注册表" : Source;

    /// <summary>详情面板里的一行版本对比（避免 XAML 里往 Run 上做绑定）。</summary>
    public string VersionSummaryText => $"当前 {VersionText}　→　可用 {AvailableText}";

    /// <summary>没新版本 / ID 被截断 / 不安全 ID 的原因，显示成灰色小标签。</summary>
    public string UpdateBadgeText
    {
        get
        {
            if (!HasUpdate) return "已是最新";
            if (Package.IsIdTruncated) return "ID 被截断";
            if (!WingetCommand.IsSafePackageId(Id)) return "无法用 ID 升级";
            return "有新版本";
        }
    }

    /// <summary>是否参与本次批量升级。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public UpgradeState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateKey));
            OnPropertyChanged(nameof(HintText));
        }
    }

    /// <summary>升级结果的一句话（失败原因 / 完成）。</summary>
    public string ResultText
    {
        get => _resultText;
        set
        {
            if (!Set(ref _resultText, value)) return;
            OnPropertyChanged(nameof(HintText));
        }
    }

    /// <summary>给 XAML 的 DataTrigger 用（字符串比枚举更好触发）。</summary>
    public string StateKey => State.ToString();

    /// <summary>还没开始升级、但可以升级（界面把「可升级」标成强调色）。</summary>
    public bool IsPendingUpgradable => State == UpgradeState.Pending && CanUpgrade;

    public string StateText => State switch
    {
        UpgradeState.Running => "升级中…",
        UpgradeState.Done => "✓ 已升级",
        UpgradeState.Failed => "✗ 失败",
        UpgradeState.Skipped => "已跳过",
        _ => CanUpgrade ? "可升级" : "不可升级",
    };

    /// <summary>搜索 / 只看可更新：返回这一行当前是否该显示。</summary>
    public bool Matches(string keyword, bool onlyUpgradable)
    {
        if (onlyUpgradable && !HasUpdate) return false;
        if (keyword.Length == 0) return true;
        return Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || Source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>鼠标悬停时显示的完整信息（表格里放不下 ID / 来源）。</summary>
    public string HintText =>
        $"{Name}{Environment.NewLine}ID：{Id}{Environment.NewLine}"
        + $"当前版本：{VersionText}{Environment.NewLine}可用版本：{AvailableText}{Environment.NewLine}"
        + $"来源：{SourceText}{Environment.NewLine}{StateText}"
        + (ResultText.Length > 0 ? Environment.NewLine + ResultText : "");
}
