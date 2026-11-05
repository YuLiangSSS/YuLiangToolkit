using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.SoftwareUpdater.Winget;

namespace YuLiangToolkit.Tools.SoftwareUpdater.ViewModels;

/// <summary>
/// 「软件升级器」的 ViewModel：把 <c>winget list / upgrade</c> 的输出变成可勾选的列表，
/// 再把「多选 / 全选 / 全不选 / 反选」交给纯规则 <see cref="SelectionRules"/> 算，
/// 最后按勾选顺序逐个执行 <c>winget upgrade --id … --exact</c>。
///
/// <para>耗时操作一律走 <see cref="WingetRunner.RunAsync"/>（后台进程、可取消、可超时），
/// 界面状态通过基类的 <c>Log / Ui / IsBusy / Progress</c> 更新；winget 缺失时只提示、不崩。</para>
/// </summary>
public sealed class SoftwareUpdaterViewModel : ToolViewModelBase
{
    private readonly List<SoftwareUpdaterItem> _all = new();
    private readonly Dictionary<SoftwareUpdaterItem, int> _indices = new();
    private readonly HashSet<SoftwareUpdaterItem> _selectedRows = new();
    private CancellationTokenSource? _cts;
    private bool _applyingSelection;
    private bool _wingetMissing;
    private string _searchText = "";
    private string _statsText = "";
    private string _emptyHint = "";
    private bool _hasPackages;
    private bool? _headerState;
    private SoftwareUpdaterItem? _selectedPackage;

    public SoftwareUpdaterViewModel() : base(SoftwareUpdaterConfig.ToolId)
    {
        Cfg = LoadConfig<SoftwareUpdaterConfig>();

        ScanModeOptions = OptionItem.From(
            (WingetCommand.AllApps, "全部已安装应用（winget list）"),
            (WingetCommand.UpgradableOnly, "仅列出可更新（winget upgrade）"));
        TimeoutOptions = OptionItem.FromValues(new[] { 5, 10, 20, 30, 60 }, v => $"{v} 分钟");

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        UpgradeCheckedCommand = new AsyncRelayCommand(_ => UpgradeCheckedAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => CancelCurrent(), _ => IsBusy);
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());

        SelectAllCommand = new RelayCommand(_ => ApplySelection(SelectionAction.All), _ => !IsBusy);
        SelectNoneCommand = new RelayCommand(_ => ApplySelection(SelectionAction.None), _ => !IsBusy);
        InvertSelectionCommand = new RelayCommand(_ => ApplySelection(SelectionAction.Invert), _ => !IsBusy);
        CheckRowsCommand = new RelayCommand(_ => ApplySelection(SelectionAction.CheckRows), _ => !IsBusy);
        UncheckRowsCommand = new RelayCommand(_ => ApplySelection(SelectionAction.UncheckRows), _ => !IsBusy);
        ToggleHeaderCommand = new RelayCommand(_ => ToggleHeader(), _ => !IsBusy);

        Cfg.PropertyChanged += OnConfigChanged;

        Log($"配置文件：{Cfg.FilePath}");
        Log($"winget 路径：{WingetPathText}");
        Log("数据来源：winget list（全部应用）/ winget upgrade（仅可更新）；升级交给 winget upgrade --id … --exact。");
        RefreshVisible();

        // 打开页面自动扫一次；截图冒烟默认不启动真实 winget（否则截图前会卡在扫描上），
        // 除非显式设 YLT_SCAN_IN_SHOT=1 做真实链路验收。
        if (Cfg.AutoScanOnOpen && (!IsScreenshotMode || ScanAllowedInScreenshot))
        {
            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => ScanCommand.Execute(null)));
        }
    }

    // ---------------------------------------------------------------- 配置 / 选项

    public SoftwareUpdaterConfig Cfg { get; }

    public IReadOnlyList<OptionItem<string>> ScanModeOptions { get; }

    public OptionItem<string> SelectedScanMode
    {
        get => OptionItem<string>.Find(ScanModeOptions, WingetCommand.NormalizeScanMode(Cfg.ScanMode)) ?? ScanModeOptions[0];
        set
        {
            if (value is null) return;
            Cfg.ScanMode = value.Value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<OptionItem<int>> TimeoutOptions { get; }

    public OptionItem<int> SelectedTimeout
    {
        get => OptionItem<int>.Find(TimeoutOptions, Cfg.UpgradeTimeoutMinutes) ?? TimeoutOptions[1];
        set
        {
            if (value is null) return;
            Cfg.UpgradeTimeoutMinutes = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>winget 可执行文件（优先当前用户的 App Execution Alias）。</summary>
    public string WingetPathText => WingetRunner.ExecutablePath();

    private static bool IsScreenshotMode =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_SHOT"));

    /// <summary>
    /// 调试用：截图模式默认不启动真实 winget（免得截图卡在扫描上）；设 <c>YLT_SCAN_IN_SHOT=1</c>
    /// 可以强制扫一次，用来验收「真实表格 → 解析 → 列表」整条链路。
    /// 配合 <c>YLT_SHOT_DELAY_MS=15000</c> 把截图等待时间拉长，等扫描跑完再截。
    /// </summary>
    private static bool ScanAllowedInScreenshot =>
        Environment.GetEnvironmentVariable("YLT_SCAN_IN_SHOT") == "1";

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SoftwareUpdaterConfig.OnlyUpgradable):
                RefreshVisible();
                break;
            case nameof(SoftwareUpdaterConfig.ScanMode):
                OnPropertyChanged(nameof(SelectedScanMode));
                StatusText = "扫描范围已改，点「刷新扫描」后生效";
                break;
            case nameof(SoftwareUpdaterConfig.UpgradeTimeoutMinutes):
                OnPropertyChanged(nameof(SelectedTimeout));
                break;
        }
        UpdateSummary();
    }

    // ---------------------------------------------------------------- 列表 / 筛选

    /// <summary>当前筛选后可见的行（与 <c>_all</c> 共用同一批对象，勾选状态不会因筛选丢失）。</summary>
    public ObservableCollection<SoftwareUpdaterItem> VisiblePackages { get; } = new();

    public SoftwareUpdaterItem? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (!Set(ref _selectedPackage, value)) return;
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedPackage is not null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            RefreshVisible();
        }
    }

    public bool HasPackages
    {
        get => _hasPackages;
        private set => Set(ref _hasPackages, value);
    }

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

    /// <summary>表头三态复选框：全勾 = true，全不勾 = false，部分 = null。</summary>
    public bool? HeaderState
    {
        get => _headerState;
        private set => Set(ref _headerState, value);
    }

    public int CheckedCount => _all.Count(item => item.IsChecked);

    public bool HasChecked => CheckedCount > 0;

    public string UpgradeButtonText => $"升级选中的 {CheckedCount} 个应用";

    public string SelectedRowsText => $"已选中 {_selectedRows.Count} 行";

    /// <summary>界面把 ListBox 的多选结果同步过来（「勾选选中行 / 取消勾选选中行」用）。</summary>
    public void UpdateSelection(IList? selectedItems)
    {
        _selectedRows.Clear();
        if (selectedItems is not null)
        {
            foreach (var entry in selectedItems)
                if (entry is SoftwareUpdaterItem item) _selectedRows.Add(item);
        }
        OnPropertyChanged(nameof(SelectedRowsText));
    }

    /// <summary>按搜索词 + 「只看可更新」重建可见列表，不改任何勾选状态。</summary>
    private void RefreshVisible()
    {
        Ui(() =>
        {
            var keyword = (_searchText ?? "").Trim();
            VisiblePackages.Clear();
            foreach (var item in _all.Where(item => item.Matches(keyword, Cfg.OnlyUpgradable)))
                VisiblePackages.Add(item);

            _selectedRows.RemoveWhere(item => !VisiblePackages.Contains(item));
            if (SelectedPackage is not null && !VisiblePackages.Contains(SelectedPackage)) SelectedPackage = null;

            HasPackages = VisiblePackages.Count > 0;
            UpdateSummary();
            OnPropertyChanged(nameof(SelectedRowsText));
        });
    }

    /// <summary>重算统计条 / 空状态提示 / 表头三态 / 升级按钮文字。</summary>
    private void UpdateSummary()
    {
        Ui(() =>
        {
            var updates = _all.Count(item => item.HasUpdate);
            StatsText = $"扫描到 {_all.Count} 个应用 · {updates} 个有可用版本 · "
                        + $"当前显示 {VisiblePackages.Count} 个 · 已勾选 {CheckedCount} 个";

            EmptyHint = _wingetMissing
                ? "没找到 winget（Windows 应用安装程序）。请到 Microsoft Store 更新「应用安装程序」，或确认 winget 在 PATH 里，再点「刷新扫描」。"
                : _all.Count == 0
                    ? "还没有扫描结果：点右上「刷新扫描」读出本机已安装的应用与可用更新。"
                    : "当前筛选下没有应用：关掉「只看可更新」或清空搜索词，就能看到扫描到的全部应用。";

            var current = _all.Select(item => item.IsChecked).ToArray();
            var visible = VisiblePackages.Select(item => IndexOf(item)).ToArray();
            var canUpgrade = _all.Select(item => item.CanUpgrade).ToArray();
            HeaderState = SelectionRules.HeaderState(current, visible, canUpgrade);

            OnPropertyChanged(nameof(CheckedCount));
            OnPropertyChanged(nameof(HasChecked));
            OnPropertyChanged(nameof(UpgradeButtonText));
        });
    }

    private int IndexOf(SoftwareUpdaterItem item)
        => _indices.TryGetValue(item, out var index) ? index : -1;

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand UpgradeCheckedCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand InvertSelectionCommand { get; }
    public RelayCommand CheckRowsCommand { get; }
    public RelayCommand UncheckRowsCommand { get; }
    public RelayCommand ToggleHeaderCommand { get; }

    // ---------------------------------------------------------------- 批量勾选规则

    /// <summary>把界面动作交给 <see cref="SelectionRules.Apply"/> 算出新的布尔数组，再写回每一项。</summary>
    private void ApplySelection(SelectionAction action)
    {
        var current = _all.Select(item => item.IsChecked).ToArray();
        var visible = VisiblePackages.Select(item => IndexOf(item)).ToArray();
        var canUpgrade = _all.Select(item => item.CanUpgrade).ToArray();
        var rowSelected = _all.Select(item => _selectedRows.Contains(item)).ToArray();

        var next = SelectionRules.Apply(current, visible, canUpgrade, action, rowSelected);

        _applyingSelection = true;
        try
        {
            for (var i = 0; i < _all.Count; i++)
                if (current[i] != next[i]) _all[i].IsChecked = next[i];
        }
        finally
        {
            _applyingSelection = false;
        }
        UpdateSummary();
    }

    /// <summary>点表头复选框：当前是全勾 → 全不选，否则（含「部分」）→ 全选可见且可升级的行。</summary>
    private void ToggleHeader()
    {
        var current = _all.Select(item => item.IsChecked).ToArray();
        var visible = VisiblePackages.Select(item => IndexOf(item)).ToArray();
        var canUpgrade = _all.Select(item => item.CanUpgrade).ToArray();
        var state = SelectionRules.HeaderState(current, visible, canUpgrade);
        ApplySelection(state == true ? SelectionAction.None : SelectionAction.All);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingSelection) return;
        if (e.PropertyName == nameof(SoftwareUpdaterItem.IsChecked)) UpdateSummary();
    }

    private void CancelCurrent()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* 任务刚好结束 */ }
        Log("===== 收到取消请求，正在结束当前 winget 进程 =====");
    }

    // ---------------------------------------------------------------- 扫描

    /// <summary>读出「有什么应用 / 哪些有可用更新」。</summary>
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
            var args = WingetCommand.BuildScanArguments(Cfg.ScanMode, Cfg.IncludeUnknown);
            StatusText = $"正在扫描：winget {args[0]} …";
            Log($"===== 扫描开始：winget {string.Join(' ', args)} =====");

            // 先探一下 winget 在不在，省得把「找不到文件」当成空列表。
            var available = await Task.Run(() => WingetRunner.IsAvailable(), token);
            if (!available)
            {
                _wingetMissing = true;
                StatusText = "没找到 winget：请安装 / 更新「Windows 应用安装程序」";
                Log("✗ 没找到 winget.exe（Windows 应用安装程序 / App Installer）");
                UpdateSummary();
                return;
            }
            _wingetMissing = false;

            var logged = 0;
            var result = await WingetRunner.RunAsync(
                args,
                line => { if (logged++ < 24) Log("  " + line); },
                token,
                TimeSpan.FromMinutes(3));

            if (result.Canceled)
            {
                StatusText = result.TimedOut ? "扫描超时（3 分钟），已终止" : "扫描已取消";
                Log($"===== {StatusText} =====");
                return;
            }
            if (result.Error is not null)
            {
                StatusText = "启动 winget 失败：" + result.Error;
                Log("✗ " + result.Error);
                return;
            }
            if (!result.Success && WingetOutputParser.Parse(result.Output).Count == 0)
            {
                StatusText = $"winget 退出码 {result.ExitCode}，且没读到表格";
                Log($"✗ {StatusText}。可能源尚未就绪，可稍后重试。");
                return;
            }

            var packages = WingetOutputParser.Parse(result.Output);
            foreach (var item in _all) item.PropertyChanged -= OnItemChanged;
            _all.Clear();
            _indices.Clear();
            _selectedRows.Clear();
            foreach (var package in packages)
            {
                var item = new SoftwareUpdaterItem(package);
                item.PropertyChanged += OnItemChanged;
                _indices[item] = _all.Count;
                _all.Add(item);
            }

            RefreshVisible();
            var summary = WingetOutputParser.FindSummary(result.Output);
            var updates = _all.Count(item => item.HasUpdate);
            StatusText = $"扫描完成：共 {_all.Count} 个应用，其中 {updates} 个有可用版本"
                         + (summary.Length > 0 ? $"（{summary}）" : "");
            Log($"===== {StatusText} =====");
            if (packages.Count == 0)
                Log("提示：没解析到表格行。winget 源可能还在初始化，或输出语言 / 列宽与预期不同，可点「刷新扫描」重试。");
        }
        finally
        {
            Progress = 1;
            ProgressText = "完成";
            IsBusy = false;
            ReleaseCancellation();
        }
    }

    /// <summary>任务结束后再释放 CTS：取消按钮 / OnClosed 都可能在任务中途访问同一个实例。</summary>
    private void ReleaseCancellation()
    {
        var cts = _cts;
        _cts = null;
        cts?.Dispose();
    }

    // ---------------------------------------------------------------- 批量升级

    /// <summary>按勾选顺序逐个升级；单项失败 / 超时不打断后面的项。</summary>
    private async Task UpgradeCheckedAsync()
    {
        if (IsBusy) return;

        var targets = _all.Where(item => item.IsChecked && item.CanUpgrade).ToList();
        if (targets.Count == 0)
        {
            StatusText = "还没有勾选可升级的应用（灰掉的行没有可用版本或 ID 不能用于 winget）";
            return;
        }

        if (Cfg.ConfirmBeforeUpgrade)
        {
            var preview = string.Join(Environment.NewLine,
                targets.Take(12).Select(item => $"· {item.Name}  {item.VersionText} → {item.AvailableVersion}"));
            if (targets.Count > 12) preview += $"{Environment.NewLine}… 其余 {targets.Count - 12} 个";
            var answer = MessageBox.Show(
                $"即将用 winget 依次升级下面 {targets.Count} 个应用：{Environment.NewLine}{Environment.NewLine}"
                + preview + Environment.NewLine + Environment.NewLine
                + "安装时可能弹出安装程序窗口、需要你手动确认 UAC；建议先保存好正在编辑的内容。",
                "确认批量升级", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                Log("已取消批量升级（用户在确认框点了取消）");
                return;
            }
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Progress = 0;
        ProgressText = $"0 / {targets.Count}";
        Log($"===== 批量升级开始：{targets.Count} 个应用（静默={Cfg.SilentInstall}，单项超时={Cfg.SafeTimeoutMinutes} 分钟）=====");

        var done = 0;
        var failed = 0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    foreach (var rest in targets.Skip(i)) rest.State = UpgradeState.Skipped;
                    break;
                }

                var item = targets[i];
                item.State = UpgradeState.Running;
                item.ResultText = "";
                StatusText = $"({i + 1}/{targets.Count}) 正在升级 {item.Name}";
                Log($"--- [{i + 1}/{targets.Count}] {item.Name}（{item.Id}）{item.VersionText} → {item.AvailableVersion} ---");

                IReadOnlyList<string> args;
                try
                {
                    args = WingetCommand.BuildUpgradeArguments(item.Id, Cfg.SilentInstall, Cfg.IncludeUnknown);
                }
                catch (ArgumentException ex)
                {
                    item.State = UpgradeState.Failed;
                    item.ResultText = ex.Message;
                    Log("✗ " + ex.Message);
                    failed++;
                    continue;
                }

                var lines = 0;
                var result = await WingetRunner.RunAsync(
                    args,
                    line => { if (lines++ < 30) Log("   " + line); },
                    token,
                    TimeSpan.FromMinutes(Cfg.SafeTimeoutMinutes));

                if (result.Canceled)
                {
                    item.State = UpgradeState.Skipped;
                    if (result.TimedOut)
                    {
                        item.ResultText = $"超过 {Cfg.SafeTimeoutMinutes} 分钟，已终止这一项";
                        Log("✗ " + item.ResultText);
                        failed++;
                    }
                    else
                    {
                        item.ResultText = "已取消";
                        Log("===== 用户取消，剩余项已跳过 =====");
                        foreach (var rest in targets.Skip(i + 1)) rest.State = UpgradeState.Skipped;
                        break;
                    }
                }
                else if (result.Success)
                {
                    item.State = UpgradeState.Done;
                    item.ResultText = "winget 已返回成功";
                    Log($"✓ {item.Name} 升级完成");
                    item.IsChecked = false;
                    done++;
                }
                else
                {
                    item.State = UpgradeState.Failed;
                    item.ResultText = result.Error ?? $"winget 退出码 {result.ExitCode}";
                    Log("✗ " + item.Name + "：" + item.ResultText);
                    failed++;
                }

                Progress = (double)(i + 1) / targets.Count;
                ProgressText = $"{i + 1} / {targets.Count}";
            }

            StatusText = $"批量升级结束：成功 {done} 个 · 失败 / 跳过 {failed} 个 · 点「刷新扫描」可看最新版本";
            Log($"===== {StatusText} =====");
        }
        finally
        {
            IsBusy = false;
            ReleaseCancellation();
            UpdateSummary();
        }
    }

    protected override void OnClosed()
    {
        CancelCurrent();
        Cfg.PropertyChanged -= OnConfigChanged;
        foreach (var item in _all) item.PropertyChanged -= OnItemChanged;
    }
}
