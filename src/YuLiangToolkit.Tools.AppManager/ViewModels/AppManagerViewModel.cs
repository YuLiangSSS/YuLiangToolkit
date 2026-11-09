using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.AppManager.Models;
using YuLiangToolkit.Tools.AppManager.Services;

namespace YuLiangToolkit.Tools.AppManager.ViewModels;

/// <summary>
/// 「应用管理器」的 ViewModel：把注册表里的已安装应用变成可筛 / 可排 / 可卸载的列表。
///
/// <para><b>数据流</b>：<see cref="ScanAsync"/>（后台线程）读注册表 → <see cref="RefreshVisible"/>
/// 按「搜索词 + 驱动器 + 是否隐藏无大小项」筛、按「排序依据 + 方向」排 → 填进 <see cref="VisibleApps"/>。
/// 筛选 / 排序只动可见集合，底层 <c>_all</c> 不动，所以改条件不会重扫注册表。</para>
///
/// <para><b>卸载 / 修改</b>：确认框 → <see cref="AppProcessLauncher"/> 启动安装程序自己的卸载入口 →
/// 监听进程退出 → 自动重扫。本工具不静默卸载、不删文件、不写注册表。</para>
/// </summary>
public sealed class AppManagerViewModel : ToolViewModelBase
{
    private readonly IAppCatalog _catalog;
    private readonly AppIconLoader _iconLoader;
    private readonly AppProcessLauncher _launcher;
    private readonly List<InstalledAppRow> _all = new();
    private CancellationTokenSource? _cts;
    private bool _loadingIcons;
    private bool _suppressConfigRefresh;
    private string _statsText = "";
    private string _emptyHint = "";
    private bool _hasApps;
    private InstalledAppRow? _selectedApp;

    public AppManagerViewModel()
        : this(new RegistryAppCatalog(), new AppIconLoader(), new AppProcessLauncher())
    {
    }

    /// <summary>可注入版本（自检用假扫描器 / 假启动器，不碰真实注册表、不启动任何程序）。</summary>
    public AppManagerViewModel(IAppCatalog catalog, AppIconLoader iconLoader, AppProcessLauncher launcher)
        : base(AppManagerConfig.ToolId)
    {
        _catalog = catalog;
        _iconLoader = iconLoader;
        _launcher = launcher;

        Cfg = LoadConfig<AppManagerConfig>();

        SortOptions = OptionItem.From(
            (AppCatalogRules.SortKey.Name, "名称"),
            (AppCatalogRules.SortKey.InstallDate, "安装日期"),
            (AppCatalogRules.SortKey.Size, "占用大小"),
            (AppCatalogRules.SortKey.Publisher, "发布者"));

        DriveOptions = new ObservableCollection<OptionItem<string>>
        {
            new(AppCatalogRules.AllDrives, "所有驱动器"),
        };
        RefreshDriveOptions();

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => CancelCurrent(), _ => IsBusy);
        ToggleSortDirectionCommand = new RelayCommand(_ => Cfg.SortDescending = !Cfg.SortDescending);
        ClearSearchCommand = new RelayCommand(_ => SearchText = "");
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        UninstallCommand = new RelayCommand(p => RunCommandFor(p as InstalledAppRow, isModify: false));
        ModifyCommand = new RelayCommand(p => RunCommandFor(p as InstalledAppRow, isModify: true));

        Cfg.PropertyChanged += OnConfigChanged;
        RefreshVisible();

        Log($"配置文件：{Cfg.FilePath}");
        Log("数据来源：注册表卸载项（HKLM / HKLM-WOW6432Node / HKCU 三处），只读读取，不写注册表。");
        Log("卸载 / 修改 = 启动安装程序自己登记的入口（不静默卸载、不删文件），确认后才会执行。");
        StatusText = "就绪：点「刷新」读取本机已安装的桌面应用。";

        // 截图冒烟模式默认不扫（免得截图前卡在扫描上）；YLT_SCAN_IN_SHOT=1 可以强制扫一次做真实验收。
        if (Cfg.AutoScanOnOpen && (!IsScreenshotMode || ScanAllowedInScreenshot))
        {
            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => ScanCommand.Execute(null)));
        }
    }

    // ---------------------------------------------------------------- 配置 / 选项

    public AppManagerConfig Cfg { get; }

    public IReadOnlyList<OptionItem<AppCatalogRules.SortKey>> SortOptions { get; }

    /// <summary>排序依据（读写都落到配置里）。</summary>
    public OptionItem<AppCatalogRules.SortKey> SelectedSort
    {
        get => OptionItem<AppCatalogRules.SortKey>.Find(SortOptions, AppCatalogRules.ParseSortKey(Cfg.SortKey))
               ?? SortOptions[0];
        set
        {
            if (value is null) return;
            Cfg.SortKey = AppCatalogRules.SortKeyToConfig(value.Value);
            OnPropertyChanged();
        }
    }

    /// <summary>驱动器下拉框：所有驱动器 + 每个应用实际落在的盘 + 未记录位置。</summary>
    public ObservableCollection<OptionItem<string>> DriveOptions { get; }

    /// <summary>当前选中的驱动器（值是盘符 / 空 / <c>?</c>）。</summary>
    public OptionItem<string> SelectedDrive
    {
        get => OptionItem<string>.Find(DriveOptions, Cfg.DriveFilter) ?? DriveOptions[0];
        set
        {
            if (value is null) return;
            Cfg.DriveFilter = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>正序 / 逆序切换按钮上的文字。</summary>
    public string SortDirectionText => Cfg.SortDescending ? "逆序 ↓" : "正序 ↑";

    /// <summary>正序 / 逆序切换按钮的悬停提示。</summary>
    public string SortDirectionHint => Cfg.SortDescending
        ? "当前：逆序（大的 / 新的在前）；点一下换成正序"
        : "当前：正序（小的 / 旧的在前）；点一下换成逆序";

    /// <summary>搜索词（一敲键就筛）。</summary>
    public string SearchText
    {
        get => Cfg.SearchText;
        set
        {
            if (Cfg.SearchText == value) return;
            Cfg.SearchText = value ?? "";
            OnPropertyChanged();
        }
    }

    /// <summary>当前筛选后可见的行。</summary>
    public ObservableCollection<InstalledAppRow> VisibleApps { get; } = new();

    /// <summary>列表里选中的那一行（右侧详情绑定它）。</summary>
    public InstalledAppRow? SelectedApp
    {
        get => _selectedApp;
        set
        {
            if (!Set(ref _selectedApp, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedDetailTitle));
            OnPropertyChanged(nameof(CanUninstallSelected));
            OnPropertyChanged(nameof(CanModifySelected));
            OnPropertyChanged(nameof(UninstallSelectedHint));
            OnPropertyChanged(nameof(ModifySelectedHint));
        }
    }

    public bool HasSelection => SelectedApp is not null;

    /// <summary>详情卡标题（选中项的名字）。</summary>
    public string SelectedDetailTitle => SelectedApp?.Name ?? "选中应用";

    public bool CanUninstallSelected => SelectedApp?.CanUninstall == true && SelectedApp.IsRunning == false;

    public bool CanModifySelected => SelectedApp?.CanModify == true && SelectedApp.IsRunning == false;

    public string UninstallSelectedHint => SelectedApp is null
        ? "先在左侧选一个应用"
        : SelectedApp.UninstallHintText;

    public string ModifySelectedHint => SelectedApp is null
        ? "先在左侧选一个应用"
        : SelectedApp.ModifyHintText;

    public bool HasApps
    {
        get => _hasApps;
        private set => Set(ref _hasApps, value);
    }

    /// <summary>「已找到 N 个应用 · 当前显示 M 个」这类统计。</summary>
    public string StatsText
    {
        get => _statsText;
        private set => Set(ref _statsText, value);
    }

    public string EmptyHint
    {
        get => _emptyHint;
        private set => Set(ref _emptyHint, value);
    }

    /// <summary>界面上显示的驱动器（盘符 → 卷标，例如 <c>C</c> → <c>Acer</c>）。</summary>
    public IReadOnlyDictionary<string, string> VolumeLabels { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static bool IsScreenshotMode =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_SHOT"));

    /// <summary>调试用：截图模式默认不扫（免得截图卡在扫描上）；设 <c>YLT_SCAN_IN_SHOT=1</c> 强制扫一次。</summary>
    private static bool ScanAllowedInScreenshot =>
        Environment.GetEnvironmentVariable("YLT_SCAN_IN_SHOT") == "1";

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppManagerConfig.SearchText):
                OnPropertyChanged(nameof(SearchText));
                RefreshVisible();
                break;
            case nameof(AppManagerConfig.DriveFilter):
                OnPropertyChanged(nameof(SelectedDrive));
                RefreshVisible();
                break;
            case nameof(AppManagerConfig.SortKey):
                OnPropertyChanged(nameof(SelectedSort));
                RefreshVisible();
                break;
            case nameof(AppManagerConfig.SortDescending):
                OnPropertyChanged(nameof(SortDirectionText));
                OnPropertyChanged(nameof(SortDirectionHint));
                RefreshVisible();
                break;
            case nameof(AppManagerConfig.ShowUnknownSize):
                RefreshVisible();
                break;
        }
    }

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ToggleSortDirectionCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand ModifyCommand { get; }

    // ---------------------------------------------------------------- 扫描

    /// <summary>重扫注册表（后台线程），然后重建可见列表与图标。</summary>
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        Progress = 0;
        ProgressText = "扫描中";
        try
        {
            StatusText = "正在读取注册表里的已安装应用…";
            Log("===== 开始扫描：注册表卸载项（HKLM / HKLM-WOW6432Node / HKCU）=====");

            var labels = await Task.Run(VolumeLabelReader.ReadAll, token);
            var apps = await Task.Run(() => _catalog.Load(), token);
            if (token.IsCancellationRequested)
            {
                StatusText = "扫描已取消";
                Log("===== 扫描已取消 =====");
                return;
            }

            VolumeLabels = labels;
            _iconLoader.ClearCache();

            _all.Clear();
            foreach (var app in apps) _all.Add(new InstalledAppRow(app));

            RefreshDriveOptions();
            RefreshVisible();
            StatusText = $"扫描完成：共 {_all.Count} 个应用，当前显示 {VisibleApps.Count} 个";
            Log($"===== {StatusText} =====");

            // 图标慢慢补：先出列表，图标在后台一个一个加载好再通知界面，避免扫描时卡住。
            _ = LoadIconsAsync(token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
            Log("✗ 扫描失败：" + ex);
        }
        finally
        {
            Progress = 1;
            ProgressText = "完成";
            IsBusy = false;
            ReleaseCancellation();
        }
    }

    /// <summary>后台补图标；界面已关闭 / 又扫了一次时自动放弃。</summary>
    private async Task LoadIconsAsync(CancellationToken token)
    {
        if (_loadingIcons) return;
        _loadingIcons = true;
        try
        {
            var rows = _all.ToArray();
            foreach (var row in rows)
            {
                if (token.IsCancellationRequested) return;
                var icon = await Task.Run(() => _iconLoader.Load(row.App), token);
                if (icon is null) continue;
                Ui(() => row.Icon = icon);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常：又扫了一次或页面已关闭。
        }
        catch (Exception ex)
        {
            Log("图标加载失败（不影响列表）：" + ex.Message);
        }
        finally
        {
            _loadingIcons = false;
        }
    }

    /// <summary>按当前扫描结果重建驱动器下拉框（保留用户已选的项）。</summary>
    private void RefreshDriveOptions()
    {
        var letters = _all
            .Select(row => row.App.DriveLetter)
            .Where(letter => letter.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(letter => letter, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var wanted = new List<OptionItem<string>>
        {
            new(AppCatalogRules.AllDrives, "所有驱动器"),
        };
        foreach (var letter in letters)
        {
            VolumeLabels.TryGetValue(letter, out var label);
            wanted.Add(new OptionItem<string>(letter, AppCatalogRules.DriveDisplayName(letter, label)));
        }
        if (_all.Any(row => row.App.DriveLetter.Length == 0))
            wanted.Add(new OptionItem<string>(AppCatalogRules.UnknownDrive, "未记录位置"));

        Ui(() =>
        {
            DriveOptions.Clear();
            foreach (var option in wanted) DriveOptions.Add(option);

            // 选的盘这台机器上不存在了（U 盘拔了 / 换了机器）：退回「所有驱动器」。
            if (OptionItem<string>.Find(DriveOptions, Cfg.DriveFilter) is null)
            {
                _suppressConfigRefresh = true;
                try { Cfg.DriveFilter = AppCatalogRules.AllDrives; }
                finally { _suppressConfigRefresh = false; }
            }
            OnPropertyChanged(nameof(SelectedDrive));
        });
    }

    /// <summary>按「搜索词 + 驱动器 + 隐藏无大小项」筛、按「依据 + 方向」排，重建可见列表。</summary>
    private void RefreshVisible()
    {
        if (_suppressConfigRefresh) return;

        Ui(() =>
        {
            var keyword = Cfg.SearchText;
            var drive = Cfg.DriveFilter;
            var filtered = _all.Where(row =>
                AppCatalogRules.MatchesKeyword(row.App, keyword)
                && AppCatalogRules.MatchesDrive(row.App, drive)
                && (Cfg.ShowUnknownSize || row.App.HasSize));

            var ordered = AppCatalogRules.Sort(
                filtered.Select(row => row.App),
                AppCatalogRules.ParseSortKey(Cfg.SortKey),
                Cfg.SortDescending);

            // 按排序结果重建行集合（行对象复用，图标 / 选中状态不丢）。
            var lookup = _all.ToDictionary(row => row.App);
            VisibleApps.Clear();
            foreach (var app in ordered)
                if (lookup.TryGetValue(app, out var row)) VisibleApps.Add(row);

            if (SelectedApp is not null && !VisibleApps.Contains(SelectedApp)) SelectedApp = null;

            HasApps = VisibleApps.Count > 0;
            EmptyHint = _all.Count == 0
                ? "还没有扫描结果：点右上「刷新」读出本机安装的应用。"
                : "当前筛选条件下没有应用：清空搜索词、把驱动器切回「所有驱动器」，或打开「显示未记录大小的应用」。";
            UpdateStats();
        });
    }

    /// <summary>统计条文本。</summary>
    private void UpdateStats()
    {
        var totalSize = _all.Where(row => row.App.HasSize).Sum(row => row.App.SizeInKilobytes);
        var named = Cfg.DriveFilter.Length == 0
            ? "所有驱动器"
            : OptionItem<string>.Find(DriveOptions, Cfg.DriveFilter)?.Label ?? "所有驱动器";

        StatsText = $"已找到 {_all.Count} 个应用 · 当前显示 {VisibleApps.Count} 个 · {named} · "
                    + $"记录到的大小合计 {AppCatalogRules.FormatSize(totalSize)}";
    }

    private void CancelCurrent()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* 任务刚好结束 */ }
        Log("===== 收到取消请求 =====");
    }

    /// <summary>任务结束后再释放 CTS（取消按钮 / OnClosed 都可能在任务中途访问同一个实例）。</summary>
    private void ReleaseCancellation()
    {
        var cts = _cts;
        _cts = null;
        cts?.Dispose();
    }

    // ---------------------------------------------------------------- 卸载 / 修改

    /// <summary>执行某一行的卸载（<paramref name="isModify"/> = false）或修改命令。</summary>
    private void RunCommandFor(InstalledAppRow? row, bool isModify)
    {
        if (row is null || row.IsRunning) return;

        var command = isModify ? row.App.ModifyCommand : row.App.UninstallCommand;
        var actionName = isModify ? "修改 / 修复" : "卸载";
        if (command.Trim().Length == 0)
        {
            StatusText = $"{row.Name} 没有登记{actionName}命令（注册表里没有对应的值）。";
            Log($"✗ {StatusText}");
            return;
        }

        if (Cfg.ConfirmBeforeRun)
        {
            var answer = MessageBox.Show(
                $"即将对「{row.Name}」执行{actionName}：{Environment.NewLine}{Environment.NewLine}"
                + command + Environment.NewLine + Environment.NewLine
                + "接下来会交给安装程序自己的界面，它会问你要不要真的" + (isModify ? "修改 / 修复" : "卸载") + "。"
                + "部分程序需要管理员权限，会弹 UAC；建议先保存好正在编辑的内容。",
                $"确认{actionName}", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                Log($"已取消{actionName}「{row.Name}」（用户在确认框点了取消）");
                return;
            }
        }

        var working = row.App.InstallLocation;
        var result = _launcher.Start(command, working);
        if (!result.Started)
        {
            StatusText = $"{actionName}「{row.Name}」没能启动：{result.Error}";
            Log($"✗ {StatusText}");
            Log("  命令：" + result.CommandLine);
            return;
        }

        row.IsRunning = true;
        OnPropertyChanged(nameof(CanUninstallSelected));
        OnPropertyChanged(nameof(CanModifySelected));
        StatusText = $"已启动「{row.Name}」的{actionName}程序，请在弹出的窗口里确认。";
        Log($"✓ {actionName}「{row.Name}」已启动（进程 {result.Process?.Id.ToString() ?? "由系统接管"}）");
        Log("  命令：" + result.CommandLine);

        WatchProcessAsync(row, result.Process, actionName);
    }

    /// <summary>等这个卸载 / 修改进程退出，然后（可选）自动重扫列表。</summary>
    private async void WatchProcessAsync(InstalledAppRow row, Process? process, string actionName)
    {
        var exitCode = "未知";
        try
        {
            if (process is not null)
            {
                await process.WaitForExitAsync();
                exitCode = process.ExitCode.ToString();
            }
        }
        catch
        {
            // 进程句柄失效（已经被系统接管 / 提权后换了进程）：不当作失败。
        }

        Ui(() =>
        {
            row.IsRunning = false;
            OnPropertyChanged(nameof(CanUninstallSelected));
            OnPropertyChanged(nameof(CanModifySelected));
        });

        Log($"{actionName}程序已退出（退出码 {exitCode}）。");

        if (!Cfg.RescanAfterRun) return;
        Ui(() =>
        {
            StatusText = $"{actionName}程序已退出，正在重新读取应用列表…";
            ScanCommand.Execute(null);
        });
    }

    protected override void OnClosed()
    {
        CancelCurrent();
        Cfg.PropertyChanged -= OnConfigChanged;
    }
}
