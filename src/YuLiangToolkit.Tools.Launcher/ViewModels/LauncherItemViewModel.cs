using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Launcher.Models;
using YuLiangToolkit.Tools.Launcher.Services;

namespace YuLiangToolkit.Tools.Launcher.ViewModels;

/// <summary>
/// 一次探测得到的启动项快照（全部是纯数据 + 已冻结的位图）：
/// 在后台线程算好，再整包交给界面线程 <see cref="LauncherItemViewModel.Apply"/>，
/// 避免后台线程直接改绑定属性导致 WPF 跨线程异常。
/// </summary>
/// <param name="Health">健康状态（正常 / 文件丢了 / 快捷方式坏了 / 目标被卸载）。</param>
/// <param name="Icon">图标（已 Freeze；可能为 null，界面显示占位图标）。</param>
/// <param name="FileDescription">版本信息里的「文件说明」（例如微信）。</param>
/// <param name="ProductName">版本信息里的「产品名称」。</param>
/// <param name="CompanyName">版本信息里的「公司名称」。</param>
/// <param name="FileVersion">文件版本（例如 1.2.3.4）。</param>
/// <param name="TypeText">类型文案（应用程序 / 快捷方式 / 文件夹）。</param>
/// <param name="SizeText">大小文案（快捷方式优先显示目标程序的大小）。</param>
/// <param name="ModifiedText">修改时间文案。</param>
/// <param name="TargetPath">快捷方式指向的程序路径（不是快捷方式时为空）。</param>
/// <param name="Arguments">快捷方式的启动参数（可能为空）。</param>
/// <param name="WorkingDirectory">快捷方式的起始位置（可能为空）。</param>
public sealed record LauncherItemSnapshot(
    LauncherHealth Health,
    ImageSource? Icon,
    string FileDescription,
    string ProductName,
    string CompanyName,
    string FileVersion,
    string TypeText,
    string SizeText,
    string ModifiedText,
    string TargetPath,
    string Arguments,
    string WorkingDirectory);

/// <summary>
/// 图标墙里的一项：包装配置里的 <see cref="LauncherItemConfig"/>，把「显示名 / 图标 / 健康状态 /
/// 版本信息 / 路径 / 大小 / 修改时间」这些界面要用的东西整理成可绑定属性。
///
/// <para><b>职责边界</b>：这里只管「一项」的数据与展示；导入 / 删除 / 重命名 / 换图标等跨项操作
/// 全部由 <see cref="LauncherViewModel"/> 统一提供（右键菜单和右侧按钮都绑父 VM 的命令）。</para>
///
/// <para><b>探测结果怎么进来</b>：<see cref="Probe"/> 在后台线程调用（内部会走 STA 线程读 Shell 图标），
/// 返回不可变快照；界面线程再调 <see cref="Apply"/> 一次性更新属性。</para>
/// </summary>
public sealed class LauncherItemViewModel : ObservableObject
{
    private readonly LauncherItemConfig _config;

    private string _name;
    private ImageSource? _icon;
    private LauncherHealth _health = LauncherHealth.Ok;
    private string _fileDescription = "";
    private string _productName = "";
    private string _companyName = "";
    private string _fileVersion = "";
    private string _typeText = "";
    private string _sizeText = "";
    private string _modifiedText = "";
    private string _targetPath = "";
    private string _arguments = "";
    private string _workingDirectory = "";
    private bool _hasCustomIcon;

    /// <summary>包装一个配置项；显示名为空时按默认规则补一个。</summary>
    public LauncherItemViewModel(LauncherItemConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(_config.Name))
            _config.Name = LauncherRules.DefaultDisplayName(_config.Path);
        _name = _config.Name;
        _hasCustomIcon = _config.HasCustomIcon;
    }

    // ---------------------------------------------------------------- 配置 / 基本信息

    /// <summary>对应的配置项（改名 / 换图标会直接写回它，自动保存由配置存储负责）。</summary>
    public LauncherItemConfig Config => _config;

    /// <summary>稳定 Id（GUID）。</summary>
    public string Id => _config.Id;

    /// <summary>导入的那个 <c>.exe</c> / <c>.lnk</c> 的完整路径。</summary>
    public string Path => _config.Path;

    /// <summary>文件名（不含目录）。</summary>
    public string FileName => _config.FileName;

    /// <summary>是不是快捷方式（只读）。</summary>
    public bool IsShortcut => _config.IsShortcut;

    /// <summary>
    /// 图标下面显示的名字：可直接改（右侧内联重命名），赋值时会用
    /// <see cref="LauncherRules.SanitizeDisplayName"/> 清理并写回配置。
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            var clean = LauncherRules.SanitizeDisplayName(value, _config.FileName);
            if (!Set(ref _name, clean)) return;
            _config.Name = clean;
            OnPropertyChanged(nameof(HintText));
        }
    }

    /// <summary>悬停提示：名字 + 路径 + 状态（图标墙上一眼看不全的信息在这里补）。</summary>
    public string HintText => $"{Name}{Environment.NewLine}{Path}{Environment.NewLine}{StatusText}";

    /// <summary>导入时间文案。</summary>
    public string AddedAtText => _config.AddedAt.ToString("yyyy-MM-dd HH:mm");

    // ---------------------------------------------------------------- 图标

    /// <summary>当前显示的图标（可能为 null = 显示占位图标）。位图已 Freeze，可在任意线程赋值。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (!Set(ref _icon, value)) return;
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    /// <summary>有没有图标（没有时界面显示 📦 占位图标）。</summary>
    public bool HasIcon => _icon is not null;

    /// <summary>有没有设置自定义图标。</summary>
    public bool HasCustomIcon
    {
        get => _hasCustomIcon;
        private set => Set(ref _hasCustomIcon, value);
    }

    // ---------------------------------------------------------------- 健康状态

    /// <summary>当前健康状态（打开页面 / 点「刷新状态」后更新）。</summary>
    public LauncherHealth Health
    {
        get => _health;
        private set
        {
            if (!Set(ref _health, value)) return;
            OnPropertyChanged(nameof(IsHealthy));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HealthBadgeText));
            OnPropertyChanged(nameof(HintText));
        }
    }

    /// <summary>是否正常可用。</summary>
    public bool IsHealthy => _health == LauncherHealth.Ok;

    /// <summary>健康状态的一句话说明（右侧详情 / 图标墙提示用）。</summary>
    public string StatusText => LauncherRules.DescribeHealth(_health);

    /// <summary>右侧状态徽标上的短文本。</summary>
    public string HealthBadgeText => IsHealthy ? "正常" : "失效";

    // ---------------------------------------------------------------- 程序信息（刷新状态后填充）

    /// <summary>文件说明（版本信息里的 FileDescription，例如「微信」）。</summary>
    public string FileDescription
    {
        get => _fileDescription;
        private set
        {
            if (Set(ref _fileDescription, value)) OnPropertyChanged(nameof(HasVersionInfo));
        }
    }

    /// <summary>产品名称。</summary>
    public string ProductName
    {
        get => _productName;
        private set
        {
            if (Set(ref _productName, value)) OnPropertyChanged(nameof(HasVersionInfo));
        }
    }

    /// <summary>公司名称。</summary>
    public string CompanyName
    {
        get => _companyName;
        private set
        {
            if (Set(ref _companyName, value)) OnPropertyChanged(nameof(HasVersionInfo));
        }
    }

    /// <summary>文件版本。</summary>
    public string FileVersion
    {
        get => _fileVersion;
        private set
        {
            if (Set(ref _fileVersion, value)) OnPropertyChanged(nameof(HasVersionInfo));
        }
    }

    /// <summary>有没有读到任何版本信息（没有时详情里隐藏这几行）。</summary>
    public bool HasVersionInfo =>
        !string.IsNullOrWhiteSpace(_fileDescription)
        || !string.IsNullOrWhiteSpace(_productName)
        || !string.IsNullOrWhiteSpace(_companyName)
        || !string.IsNullOrWhiteSpace(_fileVersion);

    /// <summary>类型文案（应用程序 / 快捷方式 / 文件夹）。</summary>
    public string TypeText
    {
        get => _typeText;
        private set => Set(ref _typeText, value);
    }

    /// <summary>大小文案（快捷方式优先显示目标程序的大小）。</summary>
    public string SizeText
    {
        get => _sizeText;
        private set => Set(ref _sizeText, value);
    }

    /// <summary>修改时间文案。</summary>
    public string ModifiedText
    {
        get => _modifiedText;
        private set => Set(ref _modifiedText, value);
    }

    // ---------------------------------------------------------------- 快捷方式目标信息

    /// <summary>快捷方式指向的程序 / 文件夹路径（不是快捷方式时为空）。</summary>
    public string TargetPath
    {
        get => _targetPath;
        private set
        {
            if (!Set(ref _targetPath, value)) return;
            OnPropertyChanged(nameof(HasTargetPath));
            OnPropertyChanged(nameof(HintText));
        }
    }

    /// <summary>有没有可显示的快捷方式目标。</summary>
    public bool HasTargetPath => !string.IsNullOrWhiteSpace(_targetPath);

    /// <summary>快捷方式启动参数。</summary>
    public string Arguments
    {
        get => _arguments;
        private set
        {
            if (!Set(ref _arguments, value)) return;
            OnPropertyChanged(nameof(HasArguments));
        }
    }

    /// <summary>有没有启动参数。</summary>
    public bool HasArguments => !string.IsNullOrWhiteSpace(_arguments);

    /// <summary>快捷方式起始位置。</summary>
    public string WorkingDirectory
    {
        get => _workingDirectory;
        private set
        {
            if (!Set(ref _workingDirectory, value)) return;
            OnPropertyChanged(nameof(HasWorkingDirectory));
        }
    }

    /// <summary>有没有起始位置。</summary>
    public bool HasWorkingDirectory => !string.IsNullOrWhiteSpace(_workingDirectory);

    // ---------------------------------------------------------------- 探测 / 应用

    /// <summary>
    /// 在后台线程探测这一项（文件是否存在 / 快捷方式目标 / 版本信息 / 大小 / 图标）。
    /// 不抛异常；任何意外都退回当前已显示的值。返回的快照可安全跨线程传回界面。
    /// </summary>
    /// <param name="probe">文件与快捷方式探测实现。</param>
    /// <param name="iconLoader">图标加载器。</param>
    /// <param name="includeHealth">是否重新检查失效状态（false = 沿用上次结果，只刷新信息与图标）。</param>
    public LauncherItemSnapshot Probe(ILauncherProbe probe, IconLoader iconLoader, bool includeHealth)
    {
        try
        {
            var health = includeHealth ? LauncherRules.CheckHealth(_config, probe) : _health;
            var importedPath = LauncherRules.NormalizePath(_config.Path);

            var targetPath = "";
            var arguments = "";
            var workingDirectory = "";
            if (LauncherRules.IsShortcutPath(importedPath))
            {
                var target = probe.ResolveShortcut(importedPath);
                if (target.Resolved)
                {
                    targetPath = LauncherRules.NormalizePath(target.TargetPath);
                    arguments = (target.Arguments ?? "").Trim();
                    workingDirectory = (target.WorkingDirectory ?? "").Trim();
                }
            }

            // 详情里的版本 / 大小：快捷方式优先看它指向的程序，普通 exe 就看导入的文件本身。
            var infoPath = targetPath.Length > 0 && File.Exists(targetPath) ? targetPath : importedPath;
            var version = TryGetVersionInfo(infoPath);
            var (sizeText, modifiedText) = DescribeFile(infoPath);

            var exists = importedPath.Length > 0 && (File.Exists(importedPath) || Directory.Exists(importedPath));
            var icon = exists || _config.HasCustomIcon ? iconLoader.LoadForItem(_config) : null;

            return new LauncherItemSnapshot(
                health,
                icon,
                version?.FileDescription?.Trim() ?? "",
                version?.ProductName?.Trim() ?? "",
                version?.CompanyName?.Trim() ?? "",
                version?.FileVersion?.Trim() ?? "",
                DescribeType(importedPath, targetPath),
                sizeText,
                modifiedText,
                targetPath,
                arguments,
                workingDirectory);
        }
        catch
        {
            return CurrentSnapshot();
        }
    }

    /// <summary>把探测结果应用到界面（必须在 UI 线程调用；图标已 Freeze，直接赋值即可）。</summary>
    public void Apply(LauncherItemSnapshot snapshot)
    {
        if (snapshot is null) return;

        Health = snapshot.Health;
        Icon = snapshot.Icon;
        FileDescription = snapshot.FileDescription;
        ProductName = snapshot.ProductName;
        CompanyName = snapshot.CompanyName;
        FileVersion = snapshot.FileVersion;
        TypeText = snapshot.TypeText;
        SizeText = snapshot.SizeText;
        ModifiedText = snapshot.ModifiedText;
        TargetPath = snapshot.TargetPath;
        Arguments = snapshot.Arguments;
        WorkingDirectory = snapshot.WorkingDirectory;
        HasCustomIcon = _config.HasCustomIcon;
    }

    /// <summary>当前界面上的值打包成快照（探测失败时的兜底）。</summary>
    public LauncherItemSnapshot CurrentSnapshot() => new(
        _health,
        _icon,
        _fileDescription,
        _productName,
        _companyName,
        _fileVersion,
        _typeText,
        _sizeText,
        _modifiedText,
        _targetPath,
        _arguments,
        _workingDirectory);

    /// <summary>换 / 恢复自定义图标后更新界面状态（配置里的 <c>CustomIconPath</c> 由调用方先写好）。</summary>
    /// <param name="icon">新的图标（null = 暂无，由调用方随后补程序图标）。</param>
    public void RefreshCustomIconState(ImageSource? icon)
    {
        HasCustomIcon = _config.HasCustomIcon;
        Icon = icon;
    }

    /// <summary>只更新图标（恢复默认图标后重新取程序图标时用）。</summary>
    public void SetIcon(ImageSource? icon) => Icon = icon;

    // ---------------------------------------------------------------- 内部工具

    /// <summary>取版本信息；文件不存在 / 不是 PE 文件时返回 null。</summary>
    private static FileVersionInfo? TryGetVersionInfo(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return FileVersionInfo.GetVersionInfo(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>大小 + 修改时间；文件夹显示「文件夹」，取不到就留空。</summary>
    private static (string SizeText, string ModifiedText) DescribeFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return ("", "");

            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                return ("文件夹", directory.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
            }

            if (!File.Exists(path)) return ("", "");

            var file = new FileInfo(path);
            return (FileSize.Format(file.Length), file.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
        }
        catch
        {
            return ("", "");
        }
    }

    /// <summary>类型文案：快捷方式会区分「指向程序」还是「指向文件夹」。</summary>
    private static string DescribeType(string importedPath, string targetPath)
    {
        if (LauncherRules.IsShortcutPath(importedPath))
        {
            if (targetPath.Length == 0) return "快捷方式（.lnk）";
            if (Directory.Exists(targetPath)) return "快捷方式 → 文件夹";
            return "快捷方式（.lnk）";
        }

        return string.Equals(LauncherRules.ExtensionOf(importedPath), ".exe", StringComparison.OrdinalIgnoreCase)
            ? "应用程序（.exe）"
            : "文件";
    }
}
