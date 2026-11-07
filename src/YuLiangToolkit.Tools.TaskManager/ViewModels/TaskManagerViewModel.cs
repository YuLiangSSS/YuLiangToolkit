using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.TaskManager.Models;
using YuLiangToolkit.Tools.TaskManager.Native;

namespace YuLiangToolkit.Tools.TaskManager.ViewModels;

/// <summary>
/// 「任务管理器」的 ViewModel：
/// <list type="bullet">
///   <item>后台循环每 N 秒采样一次整机 + 进程（<see cref="SystemSampler"/> / <see cref="ProcessSampler"/>），
///         结果切回 UI 线程合并到界面；</item>
///   <item>进程行按 PID 复用对象、原地更新，配合 <c>ListCollectionView.IsLiveSorting</c>
///         实现「边刷新边按 CPU 重排」而不丢选中项 / 滚动位置；</item>
///   <item>结束任务走 <see cref="ProcessTerminator"/> 的三级后备（Process.Kill → taskkill → wmic），
///         系统关键进程提前拦下并说明「拒绝访问」；</item>
///   <item>开机启动项由 <see cref="StartupManager"/> 扫描 / 启用禁用 / 删除。</item>
/// </list>
/// </summary>
public sealed class TaskManagerViewModel : ToolViewModelBase
{
    private readonly StartupManager _startupManager = new();
    private readonly ProcessTerminator _terminator = new();

    /// <summary>PID → 行对象（同一个进程始终复用同一个 <see cref="ProcessInfo"/>）。</summary>
    private readonly Dictionary<int, ProcessInfo> _rows = new();

    /// <summary>当前出现在列表里的行（筛选后的可见集合），与 <see cref="Processes"/> 同步。</summary>
    private readonly HashSet<ProcessInfo> _visible = new();

    /// <summary>唤醒采样循环（手动刷新 / 选中进程要立刻取详情 / 暂停时单次刷新）。</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _disposed;

    /// <summary>采样循环读的「当前选中 PID」（由 UI 线程更新）。0 = 没选。</summary>
    private volatile int _detailsPid;

    /// <summary>1 = 下一次循环即使暂停也要采一次。</summary>
    private int _forceSample;

    private SystemSnapshot _overview = new();
    private ProcessInfo? _selectedProcess;
    private StartupEntry? _selectedStartup;
    private string _monitorStateText = "";
    private string _lastUpdatedText = "等待第一次采样…";
    private string _processSummaryText = "";
    private string _startupSummaryText = "";
    private string _selectedPortSummaryText = "选择一个进程查看它占用的端口";
    private string _selectedPathText = "";
    private string _selectedUserText = "";
    private string _selectedStartTimeText = "";
    private string _selectedPriorityText = "";
    private string _selectedWindowText = "";
    private string _selectedThreadText = "";
    private string _selectedHandleText = "";
    private string _selectedVirtualText = "";
    private string _selectedDetailsError = "";
    private bool _paused;
    private bool _isElevated;

    public TaskManagerViewModel() : base(TaskManagerConfig.ToolId)
    {
        Cfg = LoadConfig<TaskManagerConfig>();

        // 老配置 / 手改配置也夹到合理范围
        Cfg.RefreshIntervalMs = TaskManagerConfig.ClampRefreshInterval(Cfg.RefreshIntervalMs);
        Cfg.SortMode = TaskManagerConfig.NormalizeSortMode(Cfg.SortMode);

        RefreshIntervalOptions = OptionItem.From(
            (1000, "1 秒"),
            (2000, "2 秒"),
            (3000, "3 秒"),
            (5000, "5 秒"),
            (10000, "10 秒"));
        SortOptions = OptionItem.From(
            ("cpu", "CPU"),
            ("memory", "内存"),
            ("gpu", "GPU"),
            ("name", "名称"),
            ("pid", "PID"),
            ("ports", "端口数"));

        // 进程列表：源集合 + 实时排序视图
        ProcessView = (ListCollectionView)CollectionViewSource.GetDefaultView(Processes);
        ProcessView.IsLiveSorting = true;
        foreach (var property in new[]
                 {
                     nameof(ProcessInfo.CpuPercent), nameof(ProcessInfo.WorkingSetBytes),
                     nameof(ProcessInfo.GpuPercent), nameof(ProcessInfo.Name),
                     nameof(ProcessInfo.Pid), nameof(ProcessInfo.PortCount),
                 })
        {
            ProcessView.LiveSortingProperties.Add(property);
        }
        ApplySort();

        RefreshCommand = new RelayCommand(_ => RequestRefresh(force: true));
        TogglePauseCommand = new RelayCommand(_ => TogglePause());
        KillSelectedCommand = new AsyncRelayCommand(_ => KillSelectedAsync(), _ => HasSelectedProcess && !IsBusy);
        OpenProcessLocationCommand = new RelayCommand(_ => OpenProcessLocation(), _ => HasSelectedProcess);
        CopyPidCommand = new RelayCommand(_ => CopyToClipboard(SelectedProcess?.PidText ?? ""));
        CopyPathCommand = new RelayCommand(_ => CopyToClipboard(SelectedProcess?.Path ?? ""));
        RefreshStartupCommand = new RelayCommand(_ => RefreshStartup());
        ToggleStartupCommand = new AsyncRelayCommand(_ => ToggleStartupAsync(), _ => HasSelectedStartup && SelectedStartupItem!.CanModify);
        DeleteStartupCommand = new AsyncRelayCommand(_ => DeleteStartupAsync(), _ => HasSelectedStartup && SelectedStartupItem!.CanModify);
        OpenStartupLocationCommand = new RelayCommand(_ => OpenStartupLocation(), _ => HasSelectedStartup);
        CopyStartupCommandCommand = new RelayCommand(_ => CopyToClipboard(SelectedStartupItem?.Command ?? ""), _ => HasSelectedStartup);

        Cfg.PropertyChanged += OnConfigChanged;

        IsElevated = ProcessTerminator.IsCurrentProcessElevated();
        MonitorStateText = BuildMonitorStateText();
        RefreshStartup();

        Log($"配置文件：{Cfg.FilePath}");
        Log($"刷新间隔：{Cfg.RefreshIntervalMs} ms；排序：{TaskManagerConfig.SortModeLabel(Cfg.SortMode)}");
        Log(IsElevated
            ? "当前以管理员身份运行：可以结束管理员权限进程。"
            : "当前是普通权限：管理员权限进程可能提示「拒绝访问」，可右键以管理员身份重启本程序。");

        StartMonitoring();
    }

    // ---------------------------------------------------------------- 配置 / 选项

    public TaskManagerConfig Cfg { get; }

    public IReadOnlyList<OptionItem<int>> RefreshIntervalOptions { get; }

    public OptionItem<int> SelectedRefreshInterval
    {
        get => RefreshIntervalOptions
                   .OrderBy(o => Math.Abs(o.Value - TaskManagerConfig.ClampRefreshInterval(Cfg.RefreshIntervalMs)))
                   .FirstOrDefault() ?? RefreshIntervalOptions[1];
        set
        {
            if (value is null) return;
            Cfg.RefreshIntervalMs = TaskManagerConfig.ClampRefreshInterval(value.Value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(MonitorStateText));
        }
    }

    public IReadOnlyList<OptionItem<string>> SortOptions { get; }

    public OptionItem<string> SelectedSort
    {
        get => OptionItem<string>.Find(SortOptions, TaskManagerConfig.NormalizeSortMode(Cfg.SortMode)) ?? SortOptions[0];
        set
        {
            if (value is null) return;
            Cfg.SortMode = TaskManagerConfig.NormalizeSortMode(value.Value);
            OnPropertyChanged();
        }
    }

    /// <summary>排序方向（降序 = 从大到小）。</summary>
    public bool SortDescending
    {
        get => Cfg.SortDescending;
        set
        {
            if (Cfg.SortDescending == value) return;
            Cfg.SortDescending = value;
            OnPropertyChanged();
        }
    }

    /// <summary>搜索框（进程名 / PID / 路径 / 端口）。</summary>
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

    /// <summary>隐藏系统进程（系统目录里的 / 读不到路径的进程）。</summary>
    public bool HideSystemProcesses
    {
        get => Cfg.HideSystemProcesses;
        set
        {
            if (Cfg.HideSystemProcesses == value) return;
            Cfg.HideSystemProcesses = value;
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- 整机概览

    /// <summary>最近一次整机快照（每轮整体替换，绑定会跟着刷新）。</summary>
    public SystemSnapshot Overview
    {
        get => _overview;
        private set => Set(ref _overview, value);
    }

    public string MonitorStateText
    {
        get => _monitorStateText;
        private set => Set(ref _monitorStateText, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => Set(ref _lastUpdatedText, value);
    }

    public bool IsElevated
    {
        get => _isElevated;
        private set
        {
            if (Set(ref _isElevated, value)) OnPropertyChanged(nameof(ElevationText));
        }
    }

    public string ElevationText => IsElevated
        ? "管理员权限（可以结束管理员进程）"
        : "普通权限（管理员进程可能「拒绝访问」）";

    public bool IsPaused
    {
        get => _paused;
        private set
        {
            if (!Set(ref _paused, value)) return;
            OnPropertyChanged(nameof(PauseButtonText));
            MonitorStateText = BuildMonitorStateText();
        }
    }

    public string PauseButtonText => IsPaused ? "继续监测" : "暂停监测";

    // ---------------------------------------------------------------- 进程列表

    /// <summary>进程列表的源集合（只放筛选后的可见行）。</summary>
    public ObservableCollection<ProcessInfo> Processes { get; } = new();

    /// <summary>带实时排序的视图（XAML 里 ListBox.ItemsSource 绑它）。</summary>
    public ListCollectionView ProcessView { get; }

    public ProcessInfo? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (!Set(ref _selectedProcess, value)) return;
            _detailsPid = value?.Pid ?? 0;
            OnPropertyChanged(nameof(HasSelectedProcess));
            ClearSelectedDetails();
            if (value is not null)
            {
                SelectedPortSummaryText = "正在读取端口…";
                RequestRefresh(force: true);   // 让采样循环立刻补一次详情
            }
            else
            {
                SelectedPortSummaryText = "选择一个进程查看它占用的端口";
            }
        }
    }

    public bool HasSelectedProcess => SelectedProcess is not null;

    public string ProcessSummaryText
    {
        get => _processSummaryText;
        private set => Set(ref _processSummaryText, value);
    }

    /// <summary>选中进程占用的端口（详情面板列表）。</summary>
    public ObservableCollection<PortBinding> SelectedPorts { get; } = new();

    public string SelectedPortSummaryText
    {
        get => _selectedPortSummaryText;
        private set => Set(ref _selectedPortSummaryText, value);
    }

    // 详情面板文本（选中变化 / 采样循环补数据时更新）
    public string SelectedPathText { get => _selectedPathText; private set => Set(ref _selectedPathText, value); }
    public string SelectedUserText { get => _selectedUserText; private set => Set(ref _selectedUserText, value); }
    public string SelectedStartTimeText { get => _selectedStartTimeText; private set => Set(ref _selectedStartTimeText, value); }
    public string SelectedPriorityText { get => _selectedPriorityText; private set => Set(ref _selectedPriorityText, value); }
    public string SelectedWindowText { get => _selectedWindowText; private set => Set(ref _selectedWindowText, value); }
    public string SelectedThreadText { get => _selectedThreadText; private set => Set(ref _selectedThreadText, value); }
    public string SelectedHandleText { get => _selectedHandleText; private set => Set(ref _selectedHandleText, value); }
    public string SelectedVirtualText { get => _selectedVirtualText; private set => Set(ref _selectedVirtualText, value); }
    public string SelectedDetailsError { get => _selectedDetailsError; private set => Set(ref _selectedDetailsError, value); }

    // ---------------------------------------------------------------- 开机启动

    public ObservableCollection<StartupEntry> StartupItems { get; } = new();

    public StartupEntry? SelectedStartupItem
    {
        get => _selectedStartup;
        set
        {
            if (!Set(ref _selectedStartup, value)) return;
            OnPropertyChanged(nameof(HasSelectedStartup));
            OnPropertyChanged(nameof(StartupToggleText));
        }
    }

    public bool HasSelectedStartup => SelectedStartupItem is not null;

    /// <summary>「启用 / 禁用」按钮的文案：当前已启用就显示「禁用」。</summary>
    public string StartupToggleText
        => SelectedStartupItem is null ? "启用 / 禁用" : (SelectedStartupItem.Enabled ? "禁用" : "启用");

    public string StartupSummaryText
    {
        get => _startupSummaryText;
        private set => Set(ref _startupSummaryText, value);
    }

    // ---------------------------------------------------------------- 命令

    public RelayCommand RefreshCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public AsyncRelayCommand KillSelectedCommand { get; }
    public RelayCommand OpenProcessLocationCommand { get; }
    public RelayCommand CopyPidCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand RefreshStartupCommand { get; }
    public AsyncRelayCommand ToggleStartupCommand { get; }
    public AsyncRelayCommand DeleteStartupCommand { get; }
    public RelayCommand OpenStartupLocationCommand { get; }
    public RelayCommand CopyStartupCommandCommand { get; }

    // ---------------------------------------------------------------- 采样循环

    private void StartMonitoring()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _loop = Task.Run(async () =>
        {
            using var systemSampler = new SystemSampler();
            using var processSampler = new ProcessSampler();
            var lastDetailsPid = 0;
            ProcessDetailsData? lastDetails = null;

            // 前两次采样间隔短一点（350ms）：CPU / GPU / 磁盘都是「两次采样的差值」，
            // 第一次必然是 0；早点采第二次，界面打开后不到半秒就能看到真实数字。
            var sampleIndex = 0;
            while (!token.IsCancellationRequested)
            {
                var force = Interlocked.Exchange(ref _forceSample, 0) == 1;
                try
                {
                    if (!IsPaused || force)
                    {
                        var snapshot = systemSampler.Sample();
                        var result = processSampler.Sample(Cfg.MonitorPorts);

                        var selectedPid = _detailsPid;
                        if (selectedPid <= 0)
                        {
                            lastDetailsPid = 0;
                            lastDetails = null;
                        }
                        else if (selectedPid != lastDetailsPid || lastDetails is null)
                        {
                            lastDetails = processSampler.ReadDetails(selectedPid);
                            lastDetailsPid = selectedPid;
                        }

                        var details = lastDetails;
                        Ui(() => ApplySample(snapshot, result, details));
                    }
                }
                catch (Exception ex)
                {
                    Ui(() => StatusText = "采样失败：" + ex.Message);
                }

                var delayMs = sampleIndex < 3
                    ? 350
                    : TaskManagerConfig.ClampRefreshInterval(Cfg.RefreshIntervalMs);
                sampleIndex++;

                try
                {
                    await _wake.WaitAsync(delayMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }, token);
    }

    /// <summary>请求采样循环：立刻醒一次；<paramref name="force"/> = 即使暂停也采一次。</summary>
    private void RequestRefresh(bool force)
    {
        if (_disposed) return;
        if (force) Interlocked.Exchange(ref _forceSample, 1);
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已经有一个待处理的唤醒，忽略
        }
        catch (ObjectDisposedException)
        {
            // 工具已关闭
        }
    }

    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused) RequestRefresh(force: true);
        StatusText = IsPaused ? "已暂停监测（点「刷新」可采样一次）" : "已恢复监测";
    }

    // ---------------------------------------------------------------- 应用采样结果（UI 线程）

    private void ApplySample(SystemSnapshot snapshot, ProcessSampleResult result, ProcessDetailsData? details)
    {
        if (_disposed) return;

        Overview = snapshot;
        ApplyProcessRows(result);
        ApplySelectedPorts(result);
        if (details is not null && details.Pid == _detailsPid) ApplyDetails(details);

        LastUpdatedText = $"更新于 {snapshot.SampledAt:HH:mm:ss}";
        var suffix = result.PortsAvailable ? "" : " · 端口读取失败（可尝试以管理员身份运行）";
        var portCount = result.Ports.Count(p => p.Pid != 0);
        ProcessSummaryText =
            $"显示 {Processes.Count:N0} / 共 {result.TotalProcessCount:N0} 个进程 · 线程 {result.TotalThreadCount:N0} · 端口 {portCount:N0} 条{suffix}";
    }

    private void ApplyProcessRows(ProcessSampleResult result)
    {
        var seen = new HashSet<int>(result.Processes.Count);

        foreach (var data in result.Processes)
        {
            // PID 0（System Idle Process）不显示：系统任务管理器也默认不列它。
            // 它的「端口」其实是内核监听条目（owningPid=0 会归到它名下），显示出来是几千个端口。
            if (data.Pid == 0) continue;

            seen.Add(data.Pid);
            if (!_rows.TryGetValue(data.Pid, out var row))
            {
                row = new ProcessInfo(data.Pid);
                _rows[data.Pid] = row;
            }
            row.Update(data);
        }

        // 已退出的进程：从字典与列表里删掉（选中项跟着清空）。
        // ⚠ 不能用「数量是否变化」判断：同一轮里死一个、新出一个，数量会相等。
        foreach (var pid in _rows.Keys.Where(pid => !seen.Contains(pid)).ToList())
        {
            var dead = _rows[pid];
            _rows.Remove(pid);
            RemoveVisible(dead);
            if (ReferenceEquals(SelectedProcess, dead)) SelectedProcess = null;
        }

        SyncVisibility();
    }

    /// <summary>按「隐藏系统进程 + 搜索关键字」同步 <see cref="Processes"/> 的成员（不重建对象，选中不会丢）。</summary>
    private void SyncVisibility()
    {
        var keyword = Cfg.SearchText;
        var hideSystem = Cfg.HideSystemProcesses;

        foreach (var row in _rows.Values)
        {
            var visible = (!hideSystem || !row.IsSystemProcess) && row.Matches(keyword);
            if (visible) AddVisible(row);
            else RemoveVisible(row);
        }
    }

    private void AddVisible(ProcessInfo row)
    {
        if (_visible.Add(row)) Processes.Add(row);
    }

    private void RemoveVisible(ProcessInfo row)
    {
        if (_visible.Remove(row)) Processes.Remove(row);
    }

    private void ApplySelectedPorts(ProcessSampleResult result)
    {
        var pid = _detailsPid;
        if (pid <= 0)
        {
            if (SelectedPorts.Count > 0) SelectedPorts.Clear();
            return;
        }

        var ports = result.Ports.Where(p => p.Pid == pid)
            .OrderBy(p => p.Protocol)
            .ThenBy(p => p.LocalPort)
            .ToList();

        // 内容没变就不动（避免每 2 秒重建列表导致选中 / 滚动跳）
        var same = ports.Count == SelectedPorts.Count;
        if (same)
        {
            for (var i = 0; i < ports.Count; i++)
            {
                if (!string.Equals(ports[i].DisplayText, SelectedPorts[i].DisplayText, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            SelectedPorts.Clear();
            foreach (var port in ports) SelectedPorts.Add(port);
        }

        SelectedPortSummaryText = !result.PortsAvailable
            ? "端口读取失败（可尝试以管理员身份运行）"
            : ports.Count == 0
                ? "这个进程没有占用端口（或需要管理员权限才能读取）"
                : $"{ports.Count} 条端口记录";
    }

    private void ApplyDetails(ProcessDetailsData details)
    {
        SelectedPathText = string.IsNullOrWhiteSpace(details.Path)
            ? "（读不到路径：可能是系统保护进程）"
            : details.Path;
        SelectedUserText = string.IsNullOrWhiteSpace(details.UserName) ? "—" : details.UserName;
        SelectedStartTimeText = details.StartTime is { } start ? start.ToString("yyyy-MM-dd HH:mm:ss") : "—";
        SelectedPriorityText = string.IsNullOrWhiteSpace(details.PriorityText) ? "—" : details.PriorityText;
        SelectedWindowText = string.IsNullOrWhiteSpace(details.MainWindowTitle) ? "（没有可见窗口）" : details.MainWindowTitle;
        SelectedThreadText = details.ThreadCount > 0 ? $"{details.ThreadCount:N0}" : "—";
        SelectedHandleText = details.HandleCount > 0 ? $"{details.HandleCount:N0}" : "—";
        SelectedVirtualText = details.VirtualBytes > 0 ? DisplayFormat.Bytes(details.VirtualBytes) : "—";
        SelectedDetailsError = details.Error;
    }

    private void ClearSelectedDetails()
    {
        SelectedPathText = "";
        SelectedUserText = "";
        SelectedStartTimeText = "";
        SelectedPriorityText = "";
        SelectedWindowText = "";
        SelectedThreadText = "";
        SelectedHandleText = "";
        SelectedVirtualText = "";
        SelectedDetailsError = "";
        SelectedPorts.Clear();
    }

    // ---------------------------------------------------------------- 排序 / 配置联动

    private void ApplySort()
    {
        var property = TaskManagerConfig.NormalizeSortMode(Cfg.SortMode) switch
        {
            "memory" => nameof(ProcessInfo.WorkingSetBytes),
            "gpu" => nameof(ProcessInfo.GpuPercent),
            "name" => nameof(ProcessInfo.Name),
            "pid" => nameof(ProcessInfo.Pid),
            "ports" => nameof(ProcessInfo.PortCount),
            _ => nameof(ProcessInfo.CpuPercent),
        };

        var direction = Cfg.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        using (ProcessView.DeferRefresh())
        {
            ProcessView.SortDescriptions.Clear();
            ProcessView.SortDescriptions.Add(new SortDescription(property, direction));
            // 同值时按 PID 稳定排序，列表不会每轮乱跳
            ProcessView.SortDescriptions.Add(new SortDescription(nameof(ProcessInfo.Pid), ListSortDirection.Ascending));
        }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TaskManagerConfig.SortMode):
                ApplySort();
                OnPropertyChanged(nameof(SelectedSort));
                break;
            case nameof(TaskManagerConfig.SortDescending):
                ApplySort();
                OnPropertyChanged(nameof(SortDescending));
                break;
            case nameof(TaskManagerConfig.SearchText):
                OnPropertyChanged(nameof(SearchText));
                SyncVisibility();
                break;
            case nameof(TaskManagerConfig.HideSystemProcesses):
                OnPropertyChanged(nameof(HideSystemProcesses));
                SyncVisibility();
                break;
            case nameof(TaskManagerConfig.RefreshIntervalMs):
                Cfg.RefreshIntervalMs = TaskManagerConfig.ClampRefreshInterval(Cfg.RefreshIntervalMs);
                OnPropertyChanged(nameof(SelectedRefreshInterval));
                OnPropertyChanged(nameof(MonitorStateText));
                break;
            case nameof(TaskManagerConfig.MonitorPorts):
                if (!Cfg.MonitorPorts) SelectedPortSummaryText = "端口采集已在设置里关闭";
                break;
        }
    }

    private string BuildMonitorStateText()
    {
        if (IsPaused) return "已暂停 · 点「刷新」采样一次";
        var seconds = TaskManagerConfig.ClampRefreshInterval(Cfg.RefreshIntervalMs) / 1000.0;
        return $"监测中 · 每 {seconds:0.#} 秒刷新";
    }

    // ---------------------------------------------------------------- 结束任务

    private async Task KillSelectedAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;

        // 系统关键进程：直接拦下，不发任何结束请求（三级方案都只会拿到「拒绝访问」）
        if (ProcessProtection.IsCritical(target.Name))
        {
            StatusText = $"{target.Name} 是系统关键进程，已被 Windows 保护";
            MessageBox.Show(
                $"无法结束「{target.Name}」（PID {target.Pid}）。\n\n{ProcessProtection.CriticalWarning}",
                "受保护的进程", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Cfg.ConfirmBeforeKill)
        {
            var kind = ProcessProtection.DescribeKind(target.Name);
            var warn = ProcessProtection.IsShell(target.Name) ? "\n\n" + ProcessProtection.ShellWarning : "";
            var message = $"确定结束「{target.Name}」（PID {target.Pid}）吗？\n\n"
                          + (kind.Length > 0 ? $"注意：这是{kind}。{warn}\n\n" : "")
                          + "会先试 Process.Kill()，失败再试 taskkill /T /F，最后试 wmic；未保存的数据可能丢失。";
            var result = MessageBox.Show(message, "结束任务", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK) return;
        }

        IsBusy = true;
        StatusText = $"正在结束 {target.Name}（PID {target.Pid}）…";
        try
        {
            var outcome = await _terminator.KillAsync(target.Pid, target.Name);
            foreach (var attempt in outcome.Attempts)
            {
                Log($"结束任务 {target.Name}（PID {target.Pid}）· {attempt}");
            }

            StatusText = outcome.Summary;
            if (outcome.Success)
            {
                RemoveRow(target.Pid);
                if (ReferenceEquals(SelectedProcess, target)) SelectedProcess = null;
                RequestRefresh(force: true);
            }
            else
            {
                MessageBox.Show(
                    $"结束「{target.Name}」（PID {target.Pid}）失败。\n\n"
                    + string.Join("\n", outcome.Attempts.Select(a => "· " + a))
                    + "\n\n" + outcome.Summary,
                    "结束任务失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            StatusText = "结束任务出错：" + ex.Message;
            Log("结束任务异常：" + ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemoveRow(int pid)
    {
        if (!_rows.TryGetValue(pid, out var row)) return;
        _rows.Remove(pid);
        RemoveVisible(row);
    }

    // ---------------------------------------------------------------- 进程操作

    private void OpenProcessLocation()
    {
        var path = SelectedProcess?.Path ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "读不到这个进程的路径（可能是系统保护进程）";
            return;
        }
        RevealInExplorer(path);
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                // 程序已经不在原位了：至少把它的目录打开
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[任务管理器] 打开位置失败：{ex.Message}");
        }
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            StatusText = "已复制到剪贴板";
        }
        catch
        {
            StatusText = "复制失败（剪贴板被其它程序占用）";
        }
    }

    // ---------------------------------------------------------------- 开机启动项

    private void RefreshStartup()
    {
        try
        {
            var items = _startupManager.Scan();
            StartupItems.Clear();
            foreach (var item in items) StartupItems.Add(item);
            var disabled = items.Count(i => !i.Enabled);
            StartupSummaryText = $"共 {items.Count} 项 · 已禁用 {disabled} 项"
                                 + (IsElevated ? "" : " · 系统级项目需要管理员权限才能修改");
            if (SelectedStartupItem is not null && !StartupItems.Contains(SelectedStartupItem))
            {
                SelectedStartupItem = null;
            }
        }
        catch (Exception ex)
        {
            StartupSummaryText = "读取开机启动项失败：" + ex.Message;
            Log("扫描开机启动项失败：" + ex);
        }
    }

    private async Task ToggleStartupAsync()
    {
        var item = SelectedStartupItem;
        if (item is null) return;

        var targetEnabled = !item.Enabled;
        StatusText = $"正在{(targetEnabled ? "启用" : "禁用")}「{item.Name}」…";
        var message = "";
        var ok = await Task.Run(() => _startupManager.SetEnabled(item, targetEnabled, out message));
        StatusText = ok
            ? $"已{(targetEnabled ? "启用" : "禁用")}「{item.Name}」"
            : $"修改「{item.Name}」失败：{message}";
        if (!ok) MessageBox.Show(message, "修改开机启动项失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshStartup();
    }

    private async Task DeleteStartupAsync()
    {
        var item = SelectedStartupItem;
        if (item is null) return;

        var confirm = MessageBox.Show(
            $"确定删除开机启动项「{item.Name}」吗？\n\n来源：{item.SourceText}\n命令：{item.CommandSummary}\n\n"
            + "只会删除这一条启动记录 / 启动文件夹里的快捷方式，不会卸载程序本身。",
            "删除开机启动项", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        StatusText = $"正在删除「{item.Name}」…";
        var message = "";
        var ok = await Task.Run(() => _startupManager.Delete(item, out message));
        StatusText = ok ? $"已删除「{item.Name}」" : $"删除「{item.Name}」失败：{message}";
        if (!ok) MessageBox.Show(message, "删除开机启动项失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshStartup();
    }

    private void OpenStartupLocation()
    {
        var item = SelectedStartupItem;
        if (item is null) return;

        if (item.Kind == StartupEntryKind.StartupFolder && !string.IsNullOrWhiteSpace(item.FolderPath))
        {
            RevealInExplorer(item.FolderPath);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.FilePath) && (File.Exists(item.FilePath) || Directory.Exists(item.FilePath)))
        {
            RevealInExplorer(item.FilePath);
            return;
        }

        // 注册表项找不到程序时：把命令复制出来，告诉用户去注册表看
        CopyToClipboard(item.Command);
        StatusText = "找不到启动程序的位置，已把启动命令复制到剪贴板";
    }

    // ---------------------------------------------------------------- 生命周期

    protected override void OnClosed()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { /* 退出流程忽略 */ }
        try { _wake.Release(); } catch { /* 可能已满 / 已释放 */ }

        // 只有循环真的停下来了才释放信号量；超时就留着（进程退出时一起回收），
        // 否则后台线程可能正好在 WaitAsync 上撞到 ObjectDisposedException。
        var stopped = false;
        try { stopped = _loop?.Wait(1500) ?? true; } catch { /* 忽略 */ }
        try { _cts?.Dispose(); } catch { /* 忽略 */ }
        if (stopped)
        {
            try { _wake.Dispose(); } catch { /* 忽略 */ }
        }

        _cts = null;
        _loop = null;
        Cfg.PropertyChanged -= OnConfigChanged;
        base.OnClosed();
    }
}
