using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.ContextMenu.ViewModels;

/// <summary>
/// 「右键菜单管理」的 ViewModel：
/// <list type="bullet">
///   <item>构造后自动扫描一次（失败只提示、不抛异常）；</item>
///   <item>搜索 / 位置 / 类型 / 只看已隐藏四个筛选条件，其中位置 / 类型 / 只看已隐藏会写进配置（下次打开还原）；</item>
///   <item>单条隐藏 / 恢复、恢复全部（仅本工具隐藏的）、重启资源管理器、复制注册表路径；</item>
///   <item>删除选中项（先自动备份 .reg → 删除 HKCU 键 → 需要时走 UAC 提权删 HKLM 键 → 重新扫描核对）；</item>
///   <item>添加「用 XX 打开」自定义菜单（表单校验 → 只写 HKCU → 刷新并尽量选中新建项）。</item>
/// </list>
/// <para>注册表扫描量很小，所有操作直接在 UI 线程同步执行，但每处都 try/catch —— 失败只写状态栏 / 日志，不让界面崩。</para>
/// </summary>
public sealed class ContextMenuManagerViewModel : ToolViewModelBase
{
    private readonly ContextMenuService _service;
    private readonly ContextMenuBackupService _backup;
    private readonly List<ContextMenuEntry> _allEntries = new();

    /// <summary>重建筛选下拉选项时先挂起，避免「改 Cfg → 触发筛选 → 又改 Cfg」的递归。</summary>
    private bool _suppressFilterRefresh;

    public ContextMenuManagerViewModel() : base("context-menu-manager")
    {
        _service = new ContextMenuService(new RegistryContextMenuStore());
        // 删除前的 .reg 备份统一放在功能数据文件夹的 backups 子目录（Data\context-menu-manager\backups）。
        _backup = new ContextMenuBackupService(Path.Combine(Storage.Directory, "backups"));
        Cfg = LoadConfig<ContextMenuManagerConfig>();

        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleCommand = new RelayCommand(
            parameter => Toggle(parameter as ContextMenuEntryItem),
            parameter => parameter is ContextMenuEntryItem { CanToggle: true });
        ToggleSelectedCommand = new RelayCommand(
            _ => Toggle(SelectedEntry),
            _ => SelectedEntry is { CanToggle: true });
        RestoreAllCommand = new RelayCommand(_ => RestoreAll());
        RestartExplorerCommand = new RelayCommand(_ => RestartExplorer());
        CopyLocationCommand = new RelayCommand(_ => CopyLocation(), _ => SelectedEntry is not null);
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected(), _ => SelectedEntry is { CanDelete: true });
        OpenBackupFolderCommand = new RelayCommand(_ => OpenBackupFolder());
        BrowseExeCommand = new RelayCommand(_ => BrowseExe());
        AddCustomMenuCommand = new RelayCommand(_ => AddCustomMenu());

        Refresh();
        // 初始默认选中第一条「可操作」的项：右边详情卡立刻有内容，不用先点一下列表
        // （第一条如果是系统关键项 open / 新菜单之类，按钮是灰的，优先挑能隐藏 / 恢复的）。
        SelectedEntry ??= Entries.FirstOrDefault(item => item.CanToggle) ?? Entries.FirstOrDefault();

        Log($"已扫描 {_allEntries.Count} 个右键菜单条目（显示中 {_allEntries.Count(entry => !entry.IsHidden)} / "
            + $"已隐藏 {_allEntries.Count(entry => entry.IsHidden)}）");
        Log($"隐藏 / 恢复只写 HKCU；「删除」会先备份到 {_backup.BackupDirectory}（.reg 双击可还原）。");
        if (IsFirstRun)
        {
            Log("首次运行：筛选条件会保存在 Config\\context-menu-manager\\config.json");
        }
    }

    // ---------- 配置 ----------

    /// <summary>强类型配置（筛选条件自动保存）。</summary>
    public ContextMenuManagerConfig Cfg { get; }

    /// <summary>删除前自动备份 .reg 的文件夹（详情卡 / 确认框 / 日志里展示）。</summary>
    public string BackupDirectory => _backup.BackupDirectory;

    // ---------- 列表 / 筛选 ----------

    /// <summary>筛选后要显示的行（每次刷新都重建，行项本身不可变）。</summary>
    public ObservableCollection<ContextMenuEntryItem> Entries { get; } = new();

    /// <summary>位置下拉选项：<c>"全部"</c> + 当前扫描结果里出现过的位置名。</summary>
    public ObservableCollection<string> ScopeOptions { get; } = new();

    /// <summary>类型下拉选项（固定四项：全部 / 菜单命令 / 扩展模块 / 自定义菜单）。</summary>
    public IReadOnlyList<string> KindOptions { get; } = new[] { "全部", "菜单命令", "扩展模块", "自定义菜单" };

    private ContextMenuEntryItem? _selectedEntry;

    /// <summary>当前选中的行（详情卡的数据源）。</summary>
    public ContextMenuEntryItem? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!Set(ref _selectedEntry, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>是否选中了条目（详情卡 / 占位提示的切换开关）。</summary>
    public bool HasSelection => _selectedEntry is not null;

    /// <summary>列表里是否有行（空状态提示的切换开关）。</summary>
    public bool HasEntries => Entries.Count > 0;

    /// <summary>空列表提示文字（区分「没扫描到」和「筛选后为空」）。</summary>
    public string EmptyHint => _allEntries.Count == 0
        ? "没有扫描到右键菜单项。\n点击上方「刷新」重新扫描。"
        : "没有符合当前筛选条件的条目。\n试试放宽搜索 / 位置 / 类型，或取消「只看已隐藏」。";

    private string _searchText = "";

    /// <summary>搜索关键词（不持久化：属于临时筛选）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? "")) RefreshFiltered();
        }
    }

    /// <summary>只看已隐藏（与配置联动）。</summary>
    public bool ShowOnlyHidden
    {
        get => Cfg.ShowOnlyHidden;
        set
        {
            if (Cfg.ShowOnlyHidden == value) return;
            Cfg.ShowOnlyHidden = value;
            OnPropertyChanged();
            RefreshFiltered();
        }
    }

    /// <summary>位置筛选（与配置联动，值 = <see cref="ContextMenuEntry.ScopeLabel"/> 或「全部」）。</summary>
    public string SelectedScope
    {
        get => string.IsNullOrWhiteSpace(Cfg.ScopeFilter) ? "全部" : Cfg.ScopeFilter;
        set
        {
            // 重建下拉选项时 ComboBox 会先把 SelectedItem 置空（中间态），别把它当成用户改筛选。
            if (_suppressFilterRefresh) return;
            var wanted = string.IsNullOrWhiteSpace(value) ? "全部" : value;
            if (string.Equals(Cfg.ScopeFilter, wanted, StringComparison.Ordinal)) return;
            Cfg.ScopeFilter = wanted;
            OnPropertyChanged();
            RefreshFiltered();
        }
    }

    /// <summary>类型筛选（与配置联动：全部 / 菜单命令 / 扩展模块 / 自定义菜单）。</summary>
    public string SelectedKind
    {
        get
        {
            var value = Cfg.KindFilter;
            return value is "菜单命令" or "扩展模块" or "自定义菜单" ? value : "全部";
        }
        set
        {
            if (_suppressFilterRefresh) return;
            var wanted = value is "菜单命令" or "扩展模块" or "自定义菜单" ? value : "全部";
            if (string.Equals(Cfg.KindFilter, wanted, StringComparison.Ordinal)) return;
            Cfg.KindFilter = wanted;
            OnPropertyChanged();
            RefreshFiltered();
        }
    }

    // ---------- 「添加打开菜单」表单 ----------
    // 说明：表单内容是临时输入，不写进配置（改一下不落盘）；只把合法的菜单真正写进注册表。

    private string _newMenuName = "";

    /// <summary>新菜单名称（例如「用 Photoshop 打开」；浏览 exe 后若为空会自动生成）。</summary>
    public string NewMenuName
    {
        get => _newMenuName;
        set => Set(ref _newMenuName, value ?? "");
    }

    private string _newExePath = "";

    /// <summary>要用来打开文件 / 文件夹的程序完整路径（.exe）。</summary>
    public string NewExePath
    {
        get => _newExePath;
        set => Set(ref _newExePath, value ?? "");
    }

    private string _newExtensions = "";

    /// <summary>文件后缀输入（例如 ".psd .ai"；可留空，交给 <see cref="CustomContextMenuRules.NormalizeExtensions"/> 规范化）。</summary>
    public string NewExtensions
    {
        get => _newExtensions;
        set => Set(ref _newExtensions, value ?? "");
    }

    private bool _newApplyFolders;

    /// <summary>是否对文件夹生效（右键文件夹时出现）。</summary>
    public bool NewApplyFolders
    {
        get => _newApplyFolders;
        set => Set(ref _newApplyFolders, value);
    }

    private bool _newApplyFolderBackground;

    /// <summary>是否对文件夹空白处生效（在文件夹里右键空白处时出现，命令用 %V）。</summary>
    public bool NewApplyFolderBackground
    {
        get => _newApplyFolderBackground;
        set => Set(ref _newApplyFolderBackground, value);
    }

    private bool _newApplyAllFiles;

    /// <summary>是否对所有文件生效（不需要填后缀）。</summary>
    public bool NewApplyAllFiles
    {
        get => _newApplyAllFiles;
        set => Set(ref _newApplyAllFiles, value);
    }

    private string _statsText = "";

    /// <summary>页头统计文字（总数 / 当前列表 / 显示中 / 已隐藏 / 本工具隐藏）。</summary>
    public string StatsText
    {
        get => _statsText;
        private set => Set(ref _statsText, value);
    }

    // ---------- 命令 ----------

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand ToggleSelectedCommand { get; }
    public RelayCommand RestoreAllCommand { get; }
    public RelayCommand RestartExplorerCommand { get; }
    public RelayCommand CopyLocationCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }
    public RelayCommand BrowseExeCommand { get; }
    public RelayCommand AddCustomMenuCommand { get; }

    // ---------- 扫描 / 筛选 ----------

    /// <summary>重新枚举注册表并套用当前筛选。异常只写状态栏 / 日志。</summary>
    /// <param name="keepBusy">
    /// 调用方自己还包着 <c>IsBusy</c>（例如删除 / 添加流程中间要刷新）时传 true，
    /// 避免这里提前把忙碌状态清掉、让按钮在流程没结束时又能点。
    /// </param>
    private void Refresh(bool keepBusy = false)
    {
        IsBusy = true;
        try
        {
            var entries = _service.Enumerate();
            _allEntries.Clear();
            if (entries is not null)
            {
                foreach (var entry in entries)
                {
                    if (entry is not null) _allEntries.Add(entry);
                }
            }

            RebuildScopeOptions();
            RefreshFiltered();

            StatusText = _allEntries.Count == 0
                ? "没有扫描到右键菜单项"
                : $"已扫描 {_allEntries.Count} 个条目，当前显示 {Entries.Count} 个";
        }
        catch (Exception ex)
        {
            _allEntries.Clear();
            RefreshFiltered();
            StatusText = $"扫描右键菜单失败：{ex.Message}";
            Log($"扫描右键菜单失败：{ex.Message}");
        }
        finally
        {
            if (!keepBusy) IsBusy = false;
        }
    }

    /// <summary>用缓存的全量条目 + 当前筛选条件重建 <see cref="Entries"/>（不重新读注册表）。</summary>
    private void RefreshFiltered()
    {
        if (_suppressFilterRefresh) return;

        // 刷新前记住选中项 Id，尽量让它刷新后仍然选中（被筛掉自然就没有了）。
        var selectedId = SelectedEntry?.Id;
        var kind = SelectedKind switch
        {
            "菜单命令" => ContextMenuEntryKind.Verb,
            "扩展模块" => ContextMenuEntryKind.ShellExtension,
            "自定义菜单" => ContextMenuEntryKind.Custom,
            _ => (ContextMenuEntryKind?)null,
        };

        var filtered = ContextMenuFilter.Apply(_allEntries, SearchText, SelectedScope, kind, ShowOnlyHidden);
        Entries.Clear();
        foreach (var entry in filtered) Entries.Add(new ContextMenuEntryItem(entry));

        SelectedEntry = selectedId is null
            ? null
            : Entries.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));

        UpdateStats();
    }

    /// <summary>重建位置下拉选项（"全部" + 扫描结果里出现过的位置，保持扫描顺序）。</summary>
    private void RebuildScopeOptions()
    {
        var current = SelectedScope;
        var desired = new List<string> { "全部" };
        desired.AddRange(_allEntries
            .Select(entry => entry.ScopeLabel)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        // 选项没变化就不动集合：避免每次刷新都让 ComboBox 清空 / 重选一遍、把筛选条件弄丢。
        if (!ScopeOptions.SequenceEqual(desired, StringComparer.Ordinal))
        {
            _suppressFilterRefresh = true;
            try
            {
                ScopeOptions.Clear();
                foreach (var option in desired) ScopeOptions.Add(option);
            }
            finally
            {
                _suppressFilterRefresh = false;
            }
            OnPropertyChanged(nameof(SelectedScope));
        }

        // 配置里记的位置这次没扫描到（例如对应注册表项被删了）：退回「全部」。
        if (!desired.Any(option => string.Equals(option, current, StringComparison.OrdinalIgnoreCase)))
        {
            Cfg.ScopeFilter = "全部";
            OnPropertyChanged(nameof(SelectedScope));
        }
    }

    private void UpdateStats()
    {
        var total = _allEntries.Count;
        var hidden = _allEntries.Count(entry => entry.IsHidden);
        var hiddenByThisTool = _allEntries.Count(entry => entry.IsHiddenByThisTool);

        StatsText = $"共 {total} 项 · 当前列表 {Entries.Count} 项 · 显示中 {total - hidden} · 已隐藏 {hidden}"
            + $"（本工具隐藏 {hiddenByThisTool}）";

        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(EmptyHint));
    }

    // ---------- 隐藏 / 恢复 ----------

    /// <summary>隐藏 / 恢复一条。失败时显示 Service 返回的中文 Message。</summary>
    private void Toggle(ContextMenuEntryItem? item)
    {
        item ??= SelectedEntry;
        if (item is null)
        {
            StatusText = "请先在列表里选中一项";
            return;
        }

        if (!item.CanToggle)
        {
            var note = string.IsNullOrWhiteSpace(item.StatusNote) ? "这一项不允许从本工具修改。" : item.StatusNote;
            StatusText = note;
            Log($"跳过「{item.Name}」：{note}");
            return;
        }

        var hide = !item.IsHidden;
        ContextMenuOperationResult result;
        try
        {
            result = _service.SetHidden(item.Entry, hide);
        }
        catch (Exception ex)
        {
            StatusText = $"操作失败：{ex.Message}";
            Log($"{(hide ? "隐藏" : "恢复")}「{item.Name}」失败：{ex.Message}");
            return;
        }

        var message = string.IsNullOrWhiteSpace(result.Message)
            ? (result.Success ? "操作完成" : "操作失败")
            : result.Message;

        if (result.Success)
        {
            Log($"{(hide ? "已隐藏" : "已恢复")}「{item.Name}」：{message}");
            Refresh();                 // 重新枚举，保持当前筛选
            StatusText = message;      // Refresh 会写默认状态，这里以操作结果为准
        }
        else
        {
            StatusText = message;
            Log($"{(hide ? "隐藏" : "恢复")}「{item.Name}」失败：{message}");
        }
    }

    /// <summary>恢复全部「本工具隐藏」的条目（先弹确认框；其他程序隐藏的不动）。</summary>
    private void RestoreAll()
    {
        var hiddenByThisTool = _allEntries.Count(entry => entry.IsHiddenByThisTool);
        if (hiddenByThisTool == 0)
        {
            MessageBox.Show(
                "当前没有由本工具隐藏的右键菜单项。\n\n其他程序或系统隐藏的项不会被本工具修改。",
                "恢复全部", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText = "没有需要恢复的条目";
            return;
        }

        var confirm = MessageBox.Show(
            $"确定恢复全部 {hiddenByThisTool} 个由本工具隐藏的右键菜单项？\n\n"
            + "只会恢复「本工具隐藏」的项；其他程序或系统隐藏的不会被修改。",
            "恢复全部", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        ContextMenuRestoreResult result;
        try
        {
            result = _service.RestoreAllHidden();
        }
        catch (Exception ex)
        {
            StatusText = $"恢复全部失败：{ex.Message}";
            Log($"恢复全部失败：{ex.Message}");
            return;
        }

        var failures = result.Failures ?? Array.Empty<string>();
        foreach (var failure in failures)
        {
            Log($"恢复失败：{failure}");
        }

        var message = result.Success
            ? $"已恢复 {result.RestoredCount} 项右键菜单"
            : $"已恢复 {result.RestoredCount} 项，{failures.Count} 项失败（详见运行日志）";

        Refresh();
        StatusText = message;
        Log(message);
    }

    /// <summary>重启资源管理器（explorer.exe），让部分右键菜单改动立即生效。</summary>
    private void RestartExplorer()
    {
        var confirm = MessageBox.Show(
            "重启资源管理器会让任务栏和桌面图标短暂消失，然后自动恢复。\n\n"
            + "正在进行的文件复制 / 移动建议先完成；其他程序不受影响。\n\n确定现在重启吗？",
            "重启资源管理器", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            Log("正在结束 explorer.exe…");
            using (var kill = Process.Start(new ProcessStartInfo
                   {
                       FileName = "taskkill",
                       Arguments = "/f /im explorer.exe",
                       UseShellExecute = false,
                       CreateNoWindow = true,
                   }))
            {
                kill?.WaitForExit(5000);
            }

            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true });
            StatusText = "已重启资源管理器";
            Log("已重启资源管理器（部分右键菜单改动会立即生效）");
        }
        catch (Exception ex)
        {
            StatusText = $"重启资源管理器失败：{ex.Message}";
            Log($"重启资源管理器失败：{ex.Message}");
        }
    }

    /// <summary>复制选中项的注册表路径（剪贴板被占用等异常只提示，不抛）。</summary>
    private void CopyLocation()
    {
        var item = SelectedEntry;
        if (item is null)
        {
            StatusText = "请先在列表里选中一项";
            return;
        }

        try
        {
            Clipboard.SetText(item.RegistryLocation);
            StatusText = "已复制注册表路径";
            Log($"已复制：{item.RegistryLocation}");
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败：{ex.Message}";
        }
    }

    // ---------- 删除 ----------

    /// <summary>
    /// 删除选中项：确认 → 自动备份 .reg → 删除 HKCU 键 → 需要时 UAC 提权删 HKLM 键 → 重新扫描核对是否删干净。
    /// <para>任何一步失败都只写状态栏 / 日志并中止，绝不在没有备份的情况下删注册表。</para>
    /// </summary>
    private void DeleteSelected()
    {
        var item = SelectedEntry;
        if (item is null)
        {
            StatusText = "请先在列表里选中一项";
            return;
        }

        var entry = item.Entry;
        if (!entry.CanDelete)
        {
            var reason = string.IsNullOrWhiteSpace(entry.DeleteBlockReason)
                ? "这一项不允许从本工具删除。"
                : entry.DeleteBlockReason;
            StatusText = reason;
            Log($"跳过删除「{entry.Name}」：{reason}");
            return;
        }

        var keys = entry.SourceKeys ?? Array.Empty<ContextMenuKeyRef>();
        if (keys.Count == 0)
        {
            StatusText = "这一项没有可删除的注册表键，已跳过";
            Log($"跳过删除「{entry.Name}」：没有可删除的注册表键");
            return;
        }

        // 二次确认：条目名 + 要处理的注册表键（前几条）+ 自动备份位置 + HKLM 会弹 UAC + 不可撤回。
        var keyLines = string.Join(Environment.NewLine, keys.Take(6).Select(key => "· " + key));
        if (keys.Count > 6) keyLines += Environment.NewLine + $"· …共 {keys.Count} 个注册表键";
        var hasLocalMachine = keys.Any(key => key.Root == RegistryRoot.LocalMachine);
        var confirmText =
            $"确定删除右键菜单项「{entry.Name}」吗？" + Environment.NewLine + Environment.NewLine
            + "要处理的注册表键：" + Environment.NewLine + keyLines + Environment.NewLine + Environment.NewLine
            + $"删除前会自动备份到：{BackupDirectory}" + Environment.NewLine
            + "（备份是 .reg 文件，双击即可还原）" + Environment.NewLine + Environment.NewLine
            + (hasLocalMachine
                ? "其中包含电脑级（HKLM）键：删除时可能需要管理员权限，会再弹一次 UAC 授权窗口。"
                  + Environment.NewLine + Environment.NewLine
                : "")
            + "⚠ 删除后本工具不能撤回；如果只是不想看到它，用「隐藏」更安全。";

        if (MessageBox.Show(confirmText, "删除右键菜单项", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
        {
            StatusText = "已取消删除";
            return;
        }

        IsBusy = true;
        try
        {
            // 1) 删除前自动备份：备份失败直接中止（绝不无备份删除）。
            Log($"正在备份「{entry.Name}」的 {keys.Count} 个注册表键到 {BackupDirectory}…");
            ContextMenuBackupResult backup;
            try
            {
                backup = _backup.Backup(keys);
            }
            catch (Exception ex)
            {
                StatusText = $"删除已中止：备份失败（{ex.Message}）";
                Log($"备份「{entry.Name}」失败：{ex.Message}");
                return;
            }

            if (backup is null || !backup.Success)
            {
                var reason = BuildFailureText(backup?.Failures);
                StatusText = $"删除已中止：备份失败（{reason}）";
                Log($"备份「{entry.Name}」失败，已中止删除：{reason}");
                return;
            }

            foreach (var file in backup.Files) Log($"已备份：{file}");

            // 2) 删除（HKCU 直接删；HKLM 没权限时引擎会放进 PermissionDeniedKeys）。
            ContextMenuDeleteResult result;
            try
            {
                result = _service.Delete(entry);
            }
            catch (Exception ex)
            {
                StatusText = $"删除失败：{ex.Message}";
                Log($"删除「{entry.Name}」失败：{ex.Message}");
                return;
            }

            if (result is null)
            {
                StatusText = "删除失败：引擎没有返回结果";
                Log($"删除「{entry.Name}」失败：引擎没有返回结果");
                return;
            }

            var deletedCount = result.DeletedCount;
            var messages = new List<string>();
            if (result.FailureMessages is { Count: > 0 })
            {
                messages.AddRange(result.FailureMessages);
                foreach (var failure in result.FailureMessages) Log($"删除失败：{failure}");
            }

            // 3) 有 HKLM 键权限不足：再确认一次，走 UAC 提权删除（只弹一次授权）。
            var elevatedAttempted = false;
            var elevatedDeleted = 0;
            if (result.NeedsElevation && result.PermissionDeniedKeys is { Count: > 0 })
            {
                var count = result.PermissionDeniedKeys.Count;
                var elevateConfirm = MessageBox.Show(
                    $"有 {count} 个键需要管理员权限才能删除（电脑级 HKLM 注册表）。" + Environment.NewLine + Environment.NewLine
                    + "继续会弹出 Windows UAC 授权窗口；取消授权的话这些键会保留（当前用户那部分已经删掉了）。"
                    + Environment.NewLine + Environment.NewLine + "是否继续提权删除？",
                    "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (elevateConfirm == MessageBoxResult.Yes)
                {
                    elevatedAttempted = true;
                    Log($"正在提权删除 {count} 个 HKLM 键（会弹 UAC）…");
                    try
                    {
                        var elevated = ElevatedContextMenuDelete.Delete(result.PermissionDeniedKeys);
                        elevatedDeleted = elevated?.DeletedKeys.Count ?? 0;
                        if (elevated is not null && elevated.Messages is { Count: > 0 })
                        {
                            messages.AddRange(elevated.Messages);
                            foreach (var message in elevated.Messages) Log($"提权删除：{message}");
                        }
                        if (elevated is not null && elevated.Success)
                        {
                            Log($"已提权删除 {elevatedDeleted} 个 HKLM 键");
                        }
                    }
                    catch (Exception ex)
                    {
                        messages.Add("提权删除失败：" + ex.Message);
                        Log("提权删除失败：" + ex.Message);
                    }
                }
                else
                {
                    messages.Add($"保留了 {count} 个需要管理员权限的 HKLM 键（已取消 UAC 提权）");
                    Log($"已取消提权：{count} 个 HKLM 键会保留");
                }
            }

            // 4) 重新扫描，按 Id 找原条目：找不到 = 删干净；还在 = 部分失败（HKCU 已删 / HKLM 未删等）。
            //    Refresh 内部会清 IsBusy，这里传 true 保持忙碌状态直到流程结束。
            Refresh(keepBusy: true);
            var remaining = _allEntries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, entry.Id, StringComparison.Ordinal));

            var totalDeleted = deletedCount + elevatedDeleted;
            string summary;
            if (remaining is null)
            {
                summary = $"已删除「{entry.Name}」（共 {totalDeleted} 个注册表键；备份在 {BackupDirectory}）";
                Log(summary);
            }
            else
            {
                var remainingRoots = remaining.SourceKeys
                    .Select(key => key.Root)
                    .Distinct()
                    .Select(root => root == RegistryRoot.LocalMachine ? "HKLM（电脑级）" : "HKCU（当前用户）")
                    .ToList();
                var stillText = remainingRoots.Count > 0 ? string.Join(" / ", remainingRoots) : "未知位置";
                summary = $"「{entry.Name}」没删干净：已删除 {totalDeleted} 个键，还剩 {stillText} 的键"
                    + (elevatedAttempted ? "（提权删除未全部成功）" : "")
                    + (messages.Count > 0 ? $"；{messages[0]}" : "");
                Log(summary);
            }

            StatusText = summary;
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败：{ex.Message}";
            Log($"删除「{entry.Name}」失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>打开删除前备份文件夹（不存在先建）；失败只提示。</summary>
    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            Process.Start(new ProcessStartInfo { FileName = BackupDirectory, UseShellExecute = true });
            StatusText = "已打开备份文件夹";
            Log("已打开备份文件夹：" + BackupDirectory);
        }
        catch (Exception ex)
        {
            StatusText = $"打开备份文件夹失败：{ex.Message}";
            Log($"打开备份文件夹失败：{ex.Message}");
        }
    }

    // ---------- 添加打开菜单 ----------

    /// <summary>「浏览…」：选一个 exe；名称还空着时自动生成「用 XX 打开」。</summary>
    private void BrowseExe()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择要用来打开文件 / 文件夹的程序",
                Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dialog.ShowDialog() != true) return;

            NewExePath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(NewMenuName))
                NewMenuName = CustomContextMenuRules.BuildMenuName(dialog.FileName);
            StatusText = "已选择程序：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusText = $"选择程序失败：{ex.Message}";
            Log($"选择程序失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 添加「用 XX 打开」自定义菜单：构造表单 → 校验（错误写状态栏 / 日志）→ 只写 HKCU →
    /// 刷新并尽量选中新建的「自定义菜单」条目（输入内容保留，方便继续微调）。
    /// </summary>
    private void AddCustomMenu()
    {
        var definition = new CustomContextMenuDefinition(
            (NewMenuName ?? "").Trim(),
            (NewExePath ?? "").Trim(),
            CustomContextMenuRules.NormalizeExtensions(NewExtensions),
            NewApplyFolders,
            NewApplyFolderBackground,
            NewApplyAllFiles);

        var validation = CustomContextMenuRules.Validate(definition);
        if (!validation.IsValid)
        {
            var first = validation.Errors.Count > 0 ? validation.Errors[0] : "表单内容不完整。";
            StatusText = "添加失败：" + first;
            Log("添加打开菜单失败：" + validation.ErrorSummary);
            return;
        }

        IsBusy = true;
        try
        {
            ContextMenuAddResult result;
            try
            {
                result = _service.AddCustomMenu(definition);
            }
            catch (Exception ex)
            {
                StatusText = $"添加失败：{ex.Message}";
                Log($"添加「{definition.Name}」失败：{ex.Message}");
                return;
            }

            if (result is null || !result.Success)
            {
                var message = string.IsNullOrWhiteSpace(result?.Message) ? "引擎没有返回具体原因" : result!.Message;
                StatusText = "添加失败：" + message;
                Log($"添加「{definition.Name}」失败：{message}");
                return;
            }

            var created = result.CreatedKeys?.Count ?? 0;
            var okMessage = string.IsNullOrWhiteSpace(result.Message)
                ? $"已添加「{definition.Name}」（写入 {created} 个 HKCU 注册表键）"
                : result.Message;
            StatusText = okMessage;
            Log(okMessage);

            // 重新扫描，并尽量选中刚加的那一条（按名称 + 命令里的 exe 路径匹配）。
            Refresh(keepBusy: true);
            var added = FindAddedEntry(definition);
            if (added is not null)
            {
                SelectedEntry = added;
            }
            else if (SelectedKind != "全部" && SelectedKind != "自定义菜单")
            {
                Log("刚添加的「自定义菜单」被当前类型筛选挡住了：把上面的「类型」切到「全部」或「自定义菜单」就能看到。");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"添加失败：{ex.Message}";
            Log($"添加「{definition.Name}」失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>在刷新后的列表里找刚添加的自定义条目（名称 + 命令里的 exe 路径；找不到就逐级放宽）。</summary>
    private ContextMenuEntryItem? FindAddedEntry(CustomContextMenuDefinition definition)
    {
        var name = (definition.Name ?? "").Trim();
        var exe = (definition.ExePath ?? "").Trim().Trim('"');
        bool ContainsExe(ContextMenuEntryItem item)
            => exe.Length > 0 && (item.Detail ?? "").Contains(exe, StringComparison.OrdinalIgnoreCase);
        bool SameName(ContextMenuEntryItem item)
            => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase);

        return Entries.FirstOrDefault(item => item.Entry.IsCustom && SameName(item) && ContainsExe(item))
            ?? Entries.FirstOrDefault(item => item.Entry.IsCustom && SameName(item))
            ?? Entries.FirstOrDefault(item => item.Entry.IsCustom && ContainsExe(item));
    }

    /// <summary>把失败说明合成为一句短文本（最多列 3 条，避免弹窗 / 状态栏被刷爆）。</summary>
    private static string BuildFailureText(IReadOnlyList<string>? failures)
    {
        if (failures is null || failures.Count == 0) return "未知原因";
        var text = string.Join("；", failures.Take(3));
        return failures.Count > 3 ? $"{text}…（共 {failures.Count} 项）" : text;
    }
}

/// <summary>
/// 列表行项：包装一条 <see cref="ContextMenuEntry"/>，把界面要绑定的文本 / 状态一次算好。
/// <para>每次刷新都会全部重建，所以它本身不需要发属性变更通知；
/// 继承 <see cref="ObservableObject"/> 是跟着项目 MVVM 约定走，以后要就地更新时可以直接用。</para>
/// </summary>
public sealed class ContextMenuEntryItem : ObservableObject
{
    public ContextMenuEntryItem(ContextMenuEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
    }

    /// <summary>原始条目（切隐藏 / 恢复时交给 <see cref="ContextMenuService"/>）。</summary>
    public ContextMenuEntry Entry { get; }

    public string Id => Entry.Id;

    public string Name => Entry.Name;

    public string KindLabel => Entry.KindLabel;

    public string ScopeLabel => Entry.ScopeLabel;

    public string StatusLabel => Entry.StatusLabel;

    /// <summary>隐藏来源文本（本工具隐藏 / 其他程序隐藏；没隐藏为空串）。</summary>
    public string SourceLabel => Entry.SourceLabel;

    public string RegistryLocation => Entry.RegistryLocation;

    public string Detail => Entry.Detail;

    /// <summary>Service 给出的状态说明（关键项不可隐藏、需要重启资源管理器等）。</summary>
    public string StatusNote => Entry.StatusNote;

    public string ToggleActionLabel => Entry.ToggleActionLabel;

    public bool IsHidden => Entry.IsHidden;

    public bool IsHiddenByThisTool => Entry.IsHiddenByThisTool;

    /// <summary>是否允许隐藏 / 恢复（系统关键项、其他程序隐藏的项为 false）。</summary>
    public bool CanToggle => Entry.CanToggle;

    /// <summary>第二行摘要：类型 · 位置 · 状态（隐藏时带来源）。</summary>
    public string Subtitle => $"{KindLabel} · {ScopeLabel} · {StatusBadge}";

    /// <summary>状态徽标文字。</summary>
    public string StatusBadge => IsHidden
        ? (string.IsNullOrWhiteSpace(SourceLabel) ? "已隐藏" : $"已隐藏（{SourceLabel}）")
        : "显示中";

    /// <summary>鼠标悬停提示：注册表路径 + 状态说明。</summary>
    public string ToolTipText => string.IsNullOrWhiteSpace(StatusNote)
        ? RegistryLocation
        : $"{RegistryLocation}{Environment.NewLine}{StatusNote}";

    /// <summary>是否允许删除（系统关键项、没有真实注册表键的条目为 false）。</summary>
    public bool CanDelete => Entry.CanDelete;

    /// <summary>不允许删除时的中文原因（空串 = 可以删）。</summary>
    public string DeleteBlockReason => Entry.DeleteBlockReason;

    /// <summary>删除按钮的悬停提示：不可删时显示原因；可删时说明「会先自动备份」。</summary>
    public string DeleteToolTipText => string.IsNullOrWhiteSpace(DeleteBlockReason)
        ? "删除前会自动备份 .reg 到功能数据文件夹的 backups 子目录（双击备份可还原）；删除后本工具不能撤回。"
        : DeleteBlockReason;

    /// <summary>是不是本工具「添加打开菜单」创建的自定义项。</summary>
    public bool IsCustom => Entry.IsCustom;

    /// <summary>这个条目实际存在的注册表键（每行一条；没有时为空串，界面据此隐藏该段）。</summary>
    public string SourceKeysText => Entry.SourceKeys.Count == 0
        ? ""
        : string.Join(Environment.NewLine, Entry.SourceKeys.Select(key => key.ToString()));
}
