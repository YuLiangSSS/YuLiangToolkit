using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Shell;

namespace YuLiangToolkit.Tools.OpenWith.ViewModels;

/// <summary>「打开方式」页面里的一个入口行（按文件类型 / 以文本方式 / 以压缩包方式）。</summary>
public sealed record OpenWithOpenerRow(string DisplayName, string Hint, bool IsAutoMode);

/// <summary>「按文件类型打开」映射表里的一行（工具 + 它认领的扩展名）。</summary>
public sealed record OpenWithTargetRow(
    string EmojiIcon, string ToolName, string Hint, string ExtensionsText, string ExtensionsCountText);

/// <summary>
/// 「打开方式」的 ViewModel：把 Core 的 <see cref="OpenWithRegistration"/>（读 / 写注册表 + 入口副本）
/// 包装成界面上的开关、状态说明和映射表。
///
/// <para><b>只写 HKCU</b>：启用 / 取消都在点按钮时发生，失败只写状态栏和日志，不弹窗。</para>
/// </summary>
public sealed class OpenWithViewModel : ToolViewModelBase
{
    private string _stateMessage = "";
    private string _upToDateText = "";
    private bool _isRegistered;
    private bool _isRefreshing;
    private string _simulationPath = "";
    private string _simulationResult = "还没有选择文件。";

    public OpenWithViewModel() : base(OpenWithConfig.ToolId)
    {
        Cfg = LoadConfig<OpenWithConfig>();

        MainExecutablePath = OpenWithRegistration.ResolveCanonicalExecutablePath(Environment.ProcessPath);
        CanRegister = !string.IsNullOrWhiteSpace(MainExecutablePath) && File.Exists(MainExecutablePath);

        OpenerRows = OpenWithRegistration.Openers
            .Select(opener => new OpenWithOpenerRow(opener.DisplayName, opener.Hint, opener.Mode == OpenWithMode.Auto))
            .ToArray();

        TargetRows = OpenWithCatalog.Targets
            .Select(target => new OpenWithTargetRow(
                target.EmojiIcon,
                target.ToolName,
                target.Hint,
                string.Join("  ", target.Extensions),
                $"{target.Extensions.Count} 种"))
            .ToArray();

        _simulationPath = Cfg.LastSimulatedPath;

        RefreshCommand = new RelayCommand(_ => RefreshState(announce: true));
        RepairCommand = new RelayCommand(_ => Repair());
        ClearAssociationCommand = new RelayCommand(_ => ClearAssociation());
        OpenDefaultAppsCommand = new RelayCommand(_ => OpenWithRegistration.OpenDefaultAppsSettings());
        BrowseSimulationFileCommand = new RelayCommand(_ => BrowseSimulationFile());
        SimulateCommand = new RelayCommand(_ => Simulate());
        ClearLogCommand = new RelayCommand(_ => ClearLog());

        RefreshState(announce: false);
        Log($"「打开方式」页面已打开，当前程序：{MainExecutablePath}");
        if (!IsBuildSupported) Log("[提示] " + OpenWithRegistration.UnsupportedBuildMessage);
    }

    /// <summary>强类型配置（<c>Config\open-with\config.json</c>）。</summary>
    public OpenWithConfig Cfg { get; }

    /// <summary>当前主程序 exe 的完整路径（入口副本启动时会自动还原成主 exe）。</summary>
    public string MainExecutablePath { get; }

    /// <summary>能不能登记（exe 路径存在）。</summary>
    public bool CanRegister { get; }

    /// <summary>
    /// 当前构建是否允许登记「打开方式」（只有 Release / 发布版；Debug 构建固定关闭，
    /// 开关在主页设置里，本页只做状态检查 / 修复 / 清理）。
    /// </summary>
    public bool IsBuildSupported => OpenWithRegistration.IsBuildSupported;

    /// <summary>Debug 构建下的不可用说明（Release 下为空）。</summary>
    public string BuildGateText => OpenWithRegistration.UnsupportedBuildMessage;

    /// <summary>三个入口的说明（界面逐条显示）。</summary>
    public IReadOnlyList<OpenWithOpenerRow> OpenerRows { get; }

    /// <summary>「按文件类型打开」会分流到哪些工具。</summary>
    public IReadOnlyList<OpenWithTargetRow> TargetRows { get; }

    /// <summary>
    /// 当前系统里是否已登记（只读）。真正的开关在 **主页设置 → 打开方式关联**；
    /// 这一页只显示状态、提供修复 / 清理，避免两个地方各写一份开关状态。
    /// </summary>
    public bool IsRegistered => _isRegistered;

    /// <summary>注册表 / 副本状态的详细说明。</summary>
    public string StateMessage
    {
        get => _stateMessage;
        private set => Set(ref _stateMessage, value);
    }

    /// <summary>入口副本版本状态（给界面的小字）。</summary>
    public string UpToDateText
    {
        get => _upToDateText;
        private set => Set(ref _upToDateText, value);
    }

    /// <summary>「模拟打开」输入的文件路径。</summary>
    public string SimulationPath
    {
        get => _simulationPath;
        set
        {
            if (!Set(ref _simulationPath, value)) return;
            Cfg.LastSimulatedPath = value;
        }
    }

    /// <summary>「模拟打开」的结果（自动模式会打开哪个工具）。</summary>
    public string SimulationResult
    {
        get => _simulationResult;
        private set => Set(ref _simulationResult, value);
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RepairCommand { get; }
    public RelayCommand ClearAssociationCommand { get; }
    public RelayCommand OpenDefaultAppsCommand { get; }
    public RelayCommand BrowseSimulationFileCommand { get; }
    public RelayCommand SimulateCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    /// <summary>重新读一次注册表 / 副本状态（页面重新显示时也调它）。</summary>
    public void RefreshState(bool announce)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var state = OpenWithRegistration.Describe(MainExecutablePath);

            if (_isRegistered != state.Registered)
            {
                _isRegistered = state.Registered;
                OnPropertyChanged(nameof(IsRegistered));
            }

            StateMessage = state.Message;
            UpToDateText = !IsBuildSupported
                ? "Debug 构建不登记「打开方式」；请用 Release 构建或发布版。"
                : !state.Registered
                    ? "未登记。开关在 主页设置 → 打开方式关联。"
                    : state.OpenersUpToDate
                        ? "入口副本：与当前程序版本一致。"
                        : "入口副本：版本不一致（可能刚更新过程序），点「修复 / 刷新」重建。";

            if (announce)
            {
                StatusText = state.Message;
                Log($"[刷新] {state.Message}");
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void Repair()
    {
        var result = OpenWithRegistration.EnsureUpToDate(MainExecutablePath);
        StatusText = result.Message;
        Log("[修复] " + result.Message);
        RefreshState(announce: false);
    }

    /// <summary>清理残留（Debug 也能用）：删入口副本 + 注册表条目；不会影响「主页设置」里的开关值。</summary>
    private void ClearAssociation()
    {
        var result = OpenWithRegistration.ClearAll(MainExecutablePath);
        StatusText = result.Message;
        Log("[清理] " + result.Message);
        RefreshState(announce: false);
    }

    private void BrowseSimulationFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择一个文件，看看自动模式会交给哪个工具",
            Filter = "所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        try
        {
            var initial = Path.GetDirectoryName(Cfg.LastSimulatedPath);
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        }
        catch
        {
            // 上次的路径不合法就算了
        }

        if (dialog.ShowDialog() == true) SimulationPath = dialog.FileName;
    }

    private void Simulate()
    {
        var path = SimulationPath?.Trim().Trim('"') ?? "";
        if (path.Length == 0)
        {
            SimulationResult = "先在左边填一个文件路径，或者点「浏览…」选一个文件。";
        }
        else if (!File.Exists(path))
        {
            SimulationResult = "这个路径不存在（自动模式仍然会按扩展名分流，但打开时会提示文件不存在）。"
                               + Environment.NewLine
                               + OpenWithCatalog.DescribeRoute(path);
        }
        else
        {
            SimulationResult = OpenWithCatalog.DescribeRoute(path);
        }

        StatusText = SimulationResult.Replace(Environment.NewLine, " ");
        Log("[模拟] " + SimulationResult.Replace(Environment.NewLine, " "));
    }
}
