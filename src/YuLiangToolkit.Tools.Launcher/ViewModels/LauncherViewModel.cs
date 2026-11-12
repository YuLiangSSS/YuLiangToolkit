using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Launcher.Models;
using YuLiangToolkit.Tools.Launcher.Services;

namespace YuLiangToolkit.Tools.Launcher.ViewModels;

/// <summary>
/// 「软件启动器」的 ViewModel：图标墙 + 右侧详情 + 导入 / 启动 / 重命名 / 换图标 / 移除 /
/// 刷新状态 / 清空无效项。
///
/// <para><b>数据</b>：启动项列表存在 <see cref="LauncherConfig.Items"/>（<c>Config\launcher\config.json</c>），
/// 增删 / 改名 / 换图标后由配置存储自动落盘；自定义图标文件放在 <c>Data\launcher\icons\</c>。</para>
///
/// <para><b>线程</b>：文件与快捷方式探测（含 COM、Shell 图标）都在后台 <see cref="Task"/> 里跑，
/// 返回不可变快照后切回 UI 线程应用；进度 / 日志 / 状态全部走 <see cref="ToolViewModelBase"/> 的
/// <c>IsBusy / Progress / Log / StatusText</c>。</para>
///
/// <para><b>安全约定</b>：删除只移除列表项和自定义图标缓存，<b>绝不删除磁盘上的程序或快捷方式</b>。</para>
/// </summary>
public sealed class LauncherViewModel : ToolViewModelBase
{
    private const string EmptyHintText =
        "还没有程序：点右上角「导入 exe / 快捷方式…」，或者把 .exe / .lnk 文件直接拖到这里。";

    private readonly SystemLauncherProbe _probe = new();
    private readonly IconLoader _iconLoader;

    private CancellationTokenSource? _refreshCts;
    private LauncherItemViewModel? _selectedItem;
    private bool _isRenaming;
    private string _editingName = "";
    private bool _showLog;
    private bool _hasItems;
    private int _invalidCount;
    private string _statsText = "";
    private string _clearInvalidButtonText = "清空无效项 (0)";
    private DateTime _lastAutoRefreshUtc = DateTime.MinValue;

    /// <summary>创建启动器并读取配置；真正耗时的状态扫描在 <see cref="OnViewLoaded"/> 里异步开始。</summary>
    public LauncherViewModel() : base("launcher")
    {
        _iconLoader = new IconLoader(Storage);

        Cfg = LoadConfig<LauncherConfig>();
        if (Cfg.Items is null) Cfg.Items = new ObservableCollection<LauncherItemConfig>();

        ImportCommand = new AsyncRelayCommand(_ => ImportFromDialogAsync(), _ => !IsBusy);
        RefreshStatusCommand = new AsyncRelayCommand(_ => RefreshStatusAsync(), _ => Items.Count > 0 && !IsBusy);
        ClearInvalidCommand = new AsyncRelayCommand(_ => ClearInvalidAsync(), _ => HasInvalid && !IsBusy);
        StartCommand = new RelayCommand(_ => StartItem(), _ => SelectedItem is not null && !IsBusy);
        DeleteCommand = new RelayCommand(_ => Delete(SelectedItem), _ => SelectedItem is not null && !IsBusy);
        BeginRenameCommand = new RelayCommand(_ => BeginRename(), _ => SelectedItem is not null && !IsBusy);
        CommitRenameCommand = new RelayCommand(_ => CommitRename(), _ => IsRenaming);
        CancelRenameCommand = new RelayCommand(_ => CancelRename(), _ => IsRenaming);
        ChangeIconCommand = new AsyncRelayCommand(_ => ChangeIconAsync(), _ => SelectedItem is not null && !IsBusy);
        ResetIconCommand = new AsyncRelayCommand(_ => ResetIconAsync(), _ => CanResetIcon && !IsBusy);
        OpenLocationCommand = new RelayCommand(_ => OpenLocation(SelectedItem), _ => SelectedItem is not null && !IsBusy);
        ToggleLogCommand = new RelayCommand(_ => ShowLog = !ShowLog);
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        OpenDataFolderCommand = new RelayCommand(_ => Storage.OpenInExplorer());

        RebuildFromConfig();
        SelectedItem = Items.FirstOrDefault(item => string.Equals(item.Id, Cfg.LastSelectedId, StringComparison.Ordinal))
                       ?? Items.FirstOrDefault();
        UpdateSummary();

        Log("===== 软件启动器 =====");
        Log("用法：点「导入 exe / 快捷方式…」或把 .exe / .lnk 拖进图标墙；双击图标启动，右键可重命名 / 换图标 / 移除。");
        Log($"数据目录：{Storage.Directory}（自定义图标在 {LauncherRules.IconFolder}\\ 下）");
        Log($"配置文件：{ConfigPathText}");
        if (IsFirstRun) Log("首次使用：导入第一个程序后会自动记住，下次打开还在。");
        if (!string.IsNullOrWhiteSpace(LauncherTool.LastStartupSummary))
            Log("上次启动：" + LauncherTool.LastStartupSummary);
    }

    // ---------------------------------------------------------------- 配置 / 列表

    /// <summary>强类型配置（启动项列表 + 上次选中 + 自动刷新开关）。</summary>
    public LauncherConfig Cfg { get; }

    /// <summary>图标墙上的启动项（顺序 = 导入顺序）。</summary>
    public ObservableCollection<LauncherItemViewModel> Items { get; } = new();

    /// <summary>当前选中的启动项（右侧详情 / 右键菜单命令都作用于它）。</summary>
    public LauncherItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;

            Cfg.LastSelectedId = value?.Id ?? "";
            IsRenaming = false;
            EditingName = "";
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanResetIcon));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>有没有选中项（右侧详情据此显示 / 隐藏）。</summary>
    public bool HasSelection => SelectedItem is not null;

    /// <summary>选中项有没有自定义图标（「恢复默认图标」按钮 / 菜单项是否可用）。</summary>
    public bool CanResetIcon => SelectedItem?.HasCustomIcon == true;

    /// <summary>图标墙是否非空（空时显示居中提示）。</summary>
    public bool HasItems
    {
        get => _hasItems;
        private set => Set(ref _hasItems, value);
    }

    /// <summary>失效项数量。</summary>
    public int InvalidCount
    {
        get => _invalidCount;
        private set
        {
            if (!Set(ref _invalidCount, value)) return;
            OnPropertyChanged(nameof(HasInvalid));
        }
    }

    /// <summary>有没有失效项（决定「清空无效项」是否可用）。</summary>
    public bool HasInvalid => _invalidCount > 0;

    /// <summary>顶部统计文案。</summary>
    public string StatsText
    {
        get => _statsText;
        private set => Set(ref _statsText, value);
    }

    /// <summary>「清空无效项」按钮上的文案（带数量）。</summary>
    public string ClearInvalidButtonText
    {
        get => _clearInvalidButtonText;
        private set => Set(ref _clearInvalidButtonText, value);
    }

    /// <summary>空列表提示。</summary>
    public string EmptyHint => EmptyHintText;

    // ---------------------------------------------------------------- 联动启动

    /// <summary>
    /// 联动启动总开关：启动凉雨工具集时，把勾了「联动启动」的程序一起拉起来。
    /// <para>改一下立刻存进配置，下一次程序启动生效（这里不会去启动任何东西）。</para>
    /// </summary>
    public bool StartLinkedOnToolkitStartup
    {
        get => Cfg.StartLinkedOnToolkitStartup;
        set
        {
            if (Cfg.StartLinkedOnToolkitStartup == value) return;
            Cfg.StartLinkedOnToolkitStartup = value;
            OnPropertyChanged();
            UpdateSummary();
            Log(value ? "联动启动：已开启总开关" : "联动启动：已关闭总开关（勾选的程序都不会自动启动）");
        }
    }

    /// <summary>联动启动状态的一句话说明（总开关关着时明确写出来，免得勾了一堆却不知道为什么不启动）。</summary>
    public string LinkedStartupText
        => LauncherRules.DescribeLinkedStartup(Cfg.StartLinkedOnToolkitStartup, Cfg.LinkedItemCount);

    // ---------------------------------------------------------------- 内联重命名 / 日志面板

    /// <summary>右侧详情是否处于内联重命名状态。</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    /// <summary>内联重命名时输入框里的草稿名字（点「保存」才写回配置）。</summary>
    public string EditingName
    {
        get => _editingName;
        set => Set(ref _editingName, value);
    }

    /// <summary>底部「运行日志」面板是否展开。</summary>
    public bool ShowLog
    {
        get => _showLog;
        set
        {
            if (!Set(ref _showLog, value)) return;
            OnPropertyChanged(nameof(LogButtonText));
        }
    }

    /// <summary>日志按钮文案。</summary>
    public string LogButtonText => ShowLog ? "收起日志" : "运行日志";

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand RefreshStatusCommand { get; }
    public AsyncRelayCommand ClearInvalidCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand BeginRenameCommand { get; }
    public RelayCommand CommitRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public AsyncRelayCommand ChangeIconCommand { get; }
    public AsyncRelayCommand ResetIconCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand ToggleLogCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    // ---------------------------------------------------------------- 视图生命周期

    /// <summary>
    /// 工具页每次显示时调用（视图 Loaded 事件）：后台读一遍图标 / 版本信息；
    /// 配置里「打开页面时自动检查失效项」开着时顺带检查失效状态。10 秒内不重复扫。
    /// </summary>
    public void OnViewLoaded()
    {
        if ((DateTime.UtcNow - _lastAutoRefreshUtc).TotalSeconds < 10) return;
        _lastAutoRefreshUtc = DateTime.UtcNow;
        _ = RefreshCoreAsync(
            includeHealth: Cfg.AutoRefreshStatus,
            reason: Cfg.AutoRefreshStatus ? "打开页面：读取启动项状态" : "打开页面：读取启动项信息");
    }

    /// <summary>切走 / 关闭页面：取消还在跑的扫描。</summary>
    protected override void OnClosed()
    {
        try { _refreshCts?.Cancel(); } catch (ObjectDisposedException) { /* 扫描刚好结束 */ }
    }

    // ---------------------------------------------------------------- 导入

    /// <summary>弹多选文件对话框导入（只允许 <c>.exe</c> / <c>.lnk</c>）。</summary>
    private async Task ImportFromDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入程序或快捷方式（可多选）",
            Filter = "程序或快捷方式|*.exe;*.lnk|可执行文件|*.exe|快捷方式|*.lnk|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        await ImportPathsAsync(dialog.FileNames);
    }

    /// <summary>
    /// 导入一批路径（对话框 / 拖入共用）：校验类型与存在性、按路径去重、补默认名、
    /// 逐项探测并选中新导入项；全程显示进度与日志，不卡界面。
    /// </summary>
    public async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        if (IsBusy) return;

        var candidates = (paths ?? Array.Empty<string>())
            .Select(LauncherRules.NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            StatusText = "没有可导入的文件";
            return;
        }

        IsBusy = true;
        Progress = 0;
        ProgressText = $"0 / {candidates.Count}";
        var added = 0;
        var duplicate = 0;
        var skipped = 0;
        Log($"===== 导入开始：{candidates.Count} 个文件 =====");

        try
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                var path = candidates[index];
                try
                {
                    if (!LauncherRules.IsSupportedPath(path))
                    {
                        skipped++;
                        Log($"跳过（只支持 exe / lnk）：{path}");
                    }
                    else if (!_probe.Exists(path))
                    {
                        skipped++;
                        Log($"跳过（文件不存在）：{path}");
                    }
                    else
                    {
                        // 去重：列表里已有同一个文件 → 只选中它，不重复添加。
                        var existingItem = Items.FirstOrDefault(item => LauncherRules.SamePath(item.Path, path));
                        if (existingItem is not null || LauncherRules.ContainsPath(Cfg.Items, path))
                        {
                            duplicate++;
                            if (existingItem is not null) SelectedItem = existingItem;
                            Log($"已在列表中，已选中：「{existingItem?.Name ?? path}」");
                        }
                        else
                        {
                            var description = LauncherRules.IsShortcutPath(path)
                                ? ""
                                : await Task.Run(() => ReadFileDescription(path));
                            var config = new LauncherItemConfig
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                Path = path,
                                Name = LauncherRules.DefaultDisplayName(path, description),
                                AddedAt = DateTime.Now,
                            };

                            Cfg.Items.Add(config);
                            var item = CreateItem(config);
                            Items.Add(item);

                            var snapshot = await Task.Run(() => item.Probe(_probe, _iconLoader, includeHealth: true));
                            item.Apply(snapshot);
                            SelectedItem = item;
                            added++;
                            Log($"导入：{item.Name}（{path}）");
                        }
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    Log($"导入失败（{path}）：{ex.Message}");
                }

                Progress = (double)(index + 1) / candidates.Count;
                ProgressText = $"{index + 1} / {candidates.Count}";
            }

            StatusText = $"导入完成：新增 {added} 个"
                         + (duplicate > 0 ? $"，重复 {duplicate} 个" : "")
                         + (skipped > 0 ? $"，跳过 {skipped} 个" : "");
            Log($"===== {StatusText} =====");
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            UpdateSummary();
        }
    }

    /// <summary>读 exe 的版本信息「文件说明」当默认显示名（微信之类）；失败返回空串。</summary>
    private static string ReadFileDescription(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            return FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    // ---------------------------------------------------------------- 刷新 / 清空无效项

    /// <summary>手动刷新：清缓存后重新探测所有项（文件是否存在、快捷方式目标、图标、版本信息）。</summary>
    private async Task RefreshStatusAsync()
    {
        _probe.ClearCache();
        _iconLoader.ClearCache();
        await RefreshCoreAsync(includeHealth: true, reason: "刷新状态");
    }

    /// <summary>后台逐项探测并应用结果；错误按项隔离，最后显示失效数量。</summary>
    private async Task RefreshCoreAsync(bool includeHealth, string reason)
    {
        if (IsBusy) return;
        if (Items.Count == 0)
        {
            UpdateSummary();
            StatusText = EmptyHintText;
            return;
        }

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;
        var targets = Items.ToList();

        IsBusy = true;
        Progress = 0;
        ProgressText = $"0 / {targets.Count}";
        Log($"===== {reason}：{targets.Count} 项 =====");

        var invalid = 0;
        var failed = 0;

        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var item = targets[index];

                LauncherItemSnapshot snapshot;
                try
                {
                    // 后台线程探测：内部的 COM / Shell 图标提取会自己切到 STA 线程。
                    snapshot = await Task.Run(() => item.Probe(_probe, _iconLoader, includeHealth), token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log($"读取「{item.Name}」失败：{ex.Message}");
                    snapshot = item.CurrentSnapshot();
                }

                token.ThrowIfCancellationRequested();
                var captured = snapshot;
                var target = item;
                Ui(() =>
                {
                    // 扫描期间用户可能已经移除了这一项：不要再去改已离开列表的对象。
                    if (Items.Contains(target)) target.Apply(captured);
                });

                if (snapshot.Health != LauncherHealth.Ok) invalid++;

                Progress = (double)(index + 1) / targets.Count;
                ProgressText = $"{index + 1} / {targets.Count}";
            }

            StatusText = includeHealth
                ? $"刷新完成：共 {targets.Count} 项，其中 {invalid} 项失效"
                : $"已读取 {targets.Count} 项信息";
            if (failed > 0) StatusText += $"（{failed} 项读取失败）";
            Log($"===== {StatusText} =====");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消刷新";
            Log("===== 已取消刷新 =====");
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            UpdateSummary();
        }
    }

    /// <summary>清空全部失效项：先列出前几个名字让用户确认，只删列表项与自定义图标。</summary>
    private async Task ClearInvalidAsync()
    {
        if (IsBusy || Items.Count == 0) return;

        IsBusy = true;
        ProgressText = "检查中";
        try
        {
            var invalid = await Task.Run(() => LauncherRules.FindInvalid(Cfg.Items, _probe));
            if (invalid.Count == 0)
            {
                StatusText = "没有失效项，列表很干净";
                Log("检查完成：没有失效项");
                return;
            }

            var preview = string.Join(
                Environment.NewLine,
                invalid.Take(8).Select(item => "· " + (string.IsNullOrWhiteSpace(item.Name) ? item.Path : item.Name)));
            if (invalid.Count > 8) preview += Environment.NewLine + $"… 其余 {invalid.Count - 8} 项";

            var answer = MessageBox.Show(
                $"即将从列表中清空下面 {invalid.Count} 个失效项：{Environment.NewLine}{Environment.NewLine}"
                + preview + Environment.NewLine + Environment.NewLine
                + "只会移除列表项和自定义图标文件，不会删除磁盘上的任何程序或快捷方式。继续？",
                "清空无效项",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                StatusText = "已取消清空无效项";
                Log("用户取消了「清空无效项」");
                return;
            }

            var removed = 0;
            foreach (var config in invalid)
            {
                var item = Items.FirstOrDefault(candidate => ReferenceEquals(candidate.Config, config));
                if (item is null) continue;
                RemoveItemCore(item);
                removed++;
            }

            if (SelectedItem is not null && !Items.Contains(SelectedItem)) SelectedItem = Items.FirstOrDefault();
            UpdateSummary();
            StatusText = $"已清空 {removed} 个失效项";
            Log($"===== 已清空 {removed} 个失效项 =====");
        }
        catch (Exception ex)
        {
            StatusText = "清空无效项失败：" + ex.Message;
            Log("✗ 清空无效项失败：" + ex);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            UpdateSummary();
        }
    }

    // ---------------------------------------------------------------- 单项操作

    /// <summary>从启动器移除一项（不删磁盘文件）；确认框在 <see cref="Delete"/> 里，这里只处理数据。</summary>
    private void RemoveItemCore(LauncherItemViewModel item)
    {
        if (!Items.Contains(item)) return;

        var config = item.Config;
        Cfg.Items.Remove(config);
        Items.Remove(item);

        // 自定义图标文件是本工具自己生成的缓存，跟着列表项一起删；原程序 / 快捷方式不碰。
        if (config.HasCustomIcon) _iconLoader.DeleteCustomIcon(config.CustomIconPath);

        if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
    }

    /// <summary>移除选中项（带确认框）。</summary>
    private void Delete(LauncherItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;

        var answer = MessageBox.Show(
            $"从启动器移除「{item.Name}」？{Environment.NewLine}{Environment.NewLine}"
            + "只会移除列表项和它的自定义图标，不会删除磁盘上的程序或快捷方式。",
            "移除启动项",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        var index = Items.IndexOf(item);
        var name = item.Name;
        RemoveItemCore(item);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Clamp(index, 0, Items.Count - 1)];
        UpdateSummary();
        StatusText = $"已移除「{name}」";
        Log($"移除启动项：{name}（{item.Path}）");
    }

    /// <summary>开始内联重命名：把当前名字填进草稿框。</summary>
    private void BeginRename()
    {
        if (SelectedItem is null) return;
        EditingName = SelectedItem.Name;
        IsRenaming = true;
    }

    /// <summary>保存内联重命名（清理非法字符 / 空白 / 超长，不会改磁盘文件名）。</summary>
    private void CommitRename()
    {
        if (SelectedItem is not { } item)
        {
            IsRenaming = false;
            return;
        }

        var clean = LauncherRules.SanitizeDisplayName(EditingName, item.FileName);
        item.Name = clean;
        IsRenaming = false;
        EditingName = "";
        StatusText = $"已重命名为「{clean}」";
        Log($"重命名：{item.Path} → {clean}");
        UpdateSummary();
    }

    /// <summary>取消内联重命名。</summary>
    private void CancelRename()
    {
        IsRenaming = false;
        EditingName = "";
    }

    /// <summary>启动选中项 / 指定项（快捷方式交给 Windows Shell 解析，exe 设置自己的目录为起始位置）。</summary>
    public void StartItem(LauncherItemViewModel? item = null)
    {
        item ??= SelectedItem;
        if (item is null) return;

        if (!item.IsHealthy)
        {
            StatusText = $"「{item.Name}」现在无法启动：{item.StatusText}";
            Log($"✗ 无法启动「{item.Name}」：{item.StatusText}（{item.Path}）");
            return;
        }

        try
        {
            var request = LauncherStartService.BuildRequest(item.Path);
            if (request is null)
            {
                StatusText = $"「{item.Name}」的路径无效，无法启动";
                Log($"✗ 无法启动「{item.Name}」：路径为空（{item.Path}）");
                return;
            }

            SystemLauncherProcessStarter.Shared.Start(request.Value);

            StatusText = $"已启动「{item.Name}」";
            Log($"启动：{item.Name}（{request.Value.FileName}）");
        }
        catch (Exception ex)
        {
            StatusText = "启动失败：" + ex.Message;
            Log($"✗ 启动「{item.Name}」失败：{ex}");
        }
    }

    /// <summary>联动启动相关的勾选变了：刷新统计文案和标题栏那句话（不重扫状态）。</summary>
    private void OnStartupFlagsChanged()
    {
        OnPropertyChanged(nameof(LinkedStartupText));
        OnPropertyChanged(nameof(StartLinkedOnToolkitStartup));
        UpdateSummaryText();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>在资源管理器中显示选中项（优先选中快捷方式指向的程序，程序不在了就显示快捷方式本身）。</summary>
    private void OpenLocation(LauncherItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;

        try
        {
            var imported = LauncherRules.NormalizePath(item.Path);
            var target = LauncherRules.NormalizePath(item.TargetPath);
            var targetExists = target.Length > 0 && (File.Exists(target) || Directory.Exists(target));
            var revealPath = targetExists ? target : imported;

            if (File.Exists(revealPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{revealPath}\"",
                    UseShellExecute = true,
                });
            }
            else if (Directory.Exists(revealPath))
            {
                Process.Start(new ProcessStartInfo { FileName = revealPath, UseShellExecute = true });
            }
            else
            {
                StatusText = "文件已不存在，无法打开所在位置";
                Log($"✗ 打开所在位置失败：{item.Path} 已不存在");
                return;
            }

            StatusText = "已在资源管理器中显示";
        }
        catch (Exception ex)
        {
            StatusText = "打开所在位置失败：" + ex.Message;
            Log("✗ 打开所在位置失败：" + ex);
        }
    }

    /// <summary>选一个图片 / 程序文件当自定义图标：转成 PNG 存进数据文件夹后写回配置。</summary>
    private async Task ChangeIconAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择新的图标（支持 .ico / .exe / .dll / .png / .jpg / .bmp）",
            Filter = "图标与图片|*.ico;*.exe;*.dll;*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            var relative = await Task.Run(() => _iconLoader.SaveCustomIcon(dialog.FileName, item.Id));
            if (string.IsNullOrWhiteSpace(relative))
            {
                StatusText = "图标读取失败：这个文件里没有可用的图标";
                Log($"✗ 读取图标失败：{dialog.FileName}");
                return;
            }

            item.Config.CustomIconPath = relative;
            var icon = await Task.Run(() => _iconLoader.LoadCustomIcon(relative, forceReload: true));
            item.RefreshCustomIconState(icon);
            OnPropertyChanged(nameof(CanResetIcon));
            StatusText = $"已更换「{item.Name}」的图标（已复制进数据文件夹）";
            Log($"更换图标：{item.Name} ← {dialog.FileName}（{relative}）");
        }
        catch (Exception ex)
        {
            StatusText = "更换图标失败：" + ex.Message;
            Log("✗ 更换图标失败：" + ex);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>删掉自定义图标文件、清空配置，重新取程序自己的图标。</summary>
    private async Task ResetIconAsync()
    {
        var item = SelectedItem;
        if (item is null || !item.HasCustomIcon) return;

        var relative = item.Config.CustomIconPath;
        _iconLoader.DeleteCustomIcon(relative);
        item.Config.CustomIconPath = "";
        item.RefreshCustomIconState(null);
        OnPropertyChanged(nameof(CanResetIcon));

        IsBusy = true;
        try
        {
            var icon = await Task.Run(() => _iconLoader.LoadShellIcon(item.Path, forceReload: true));
            item.SetIcon(icon);
            StatusText = "已恢复程序默认图标";
            Log($"恢复默认图标：{item.Name}");
        }
        catch (Exception ex)
        {
            StatusText = "恢复默认图标失败：" + ex.Message;
            Log("✗ 恢复默认图标失败：" + ex);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---------------------------------------------------------------- 内部工具

    /// <summary>把配置里的启动项包装成界面对象（跳过 JSON 里可能出现的 null）。</summary>
    private void RebuildFromConfig()
    {
        Items.Clear();
        foreach (var config in Cfg.Items)
        {
            if (config is null) continue;
            if (string.IsNullOrWhiteSpace(config.Id)) config.Id = Guid.NewGuid().ToString("N");
            Items.Add(CreateItem(config));
        }
    }

    /// <summary>
    /// 建一个启动项界面对象并盯住它的「联动启动」开关：勾选框 / 右键菜单改完要立刻刷新统计文案。
    /// <para>事件订阅不摘也没事：这些对象都由本 VM 直接持有，随 VM 一起被回收（不像缓存的外部对象）。</para>
    /// </summary>
    private LauncherItemViewModel CreateItem(LauncherItemConfig config)
    {
        var item = new LauncherItemViewModel(config);
        item.PropertyChanged += OnItemPropertyChanged;
        return item;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherItemViewModel.StartWithToolkit)) OnStartupFlagsChanged();
    }

    /// <summary>重算统计条 / 空状态 / 失效数量 / 按钮文字，并让命令重新查询可用性。</summary>
    private void UpdateSummary()
    {
        HasItems = Items.Count > 0;
        InvalidCount = Items.Count(item => !item.IsHealthy);
        UpdateSummaryText();
        OnPropertyChanged(nameof(LinkedStartupText));
        ClearInvalidButtonText = $"清空无效项 ({InvalidCount})";
        OnPropertyChanged(nameof(CanResetIcon));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>只刷新顶部统计文案（联动启动勾选变化时不需要重算失效数量）。</summary>
    private void UpdateSummaryText()
    {
        StatsText = Items.Count == 0
            ? "还没有导入任何程序"
            : $"共 {Items.Count} 项 · 失效 {InvalidCount} 项 · 联动 {Cfg.LinkedItemCount} 项 · 双击图标启动";
    }
}
