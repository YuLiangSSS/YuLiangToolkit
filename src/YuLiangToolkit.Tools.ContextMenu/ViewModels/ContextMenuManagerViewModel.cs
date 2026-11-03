using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.ContextMenu.ViewModels;

/// <summary>
/// 「右键菜单管理」的 ViewModel：
/// <list type="bullet">
///   <item>构造后自动扫描一次（失败只提示、不抛异常）；</item>
///   <item>搜索 / 位置 / 类型 / 只看已隐藏四个筛选条件，其中位置 / 类型 / 只看已隐藏会写进配置（下次打开还原）；</item>
///   <item>单条隐藏 / 恢复、恢复全部（仅本工具隐藏的）、重启资源管理器、复制注册表路径。</item>
/// </list>
/// <para>注册表扫描量很小，所有操作直接在 UI 线程同步执行，但每处都 try/catch —— 失败只写状态栏 / 日志，不让界面崩。</para>
/// </summary>
public sealed class ContextMenuManagerViewModel : ToolViewModelBase
{
    private readonly ContextMenuService _service;
    private readonly List<ContextMenuEntry> _allEntries = new();

    /// <summary>重建筛选下拉选项时先挂起，避免「改 Cfg → 触发筛选 → 又改 Cfg」的递归。</summary>
    private bool _suppressFilterRefresh;

    public ContextMenuManagerViewModel() : base("context-menu-manager")
    {
        _service = new ContextMenuService(new RegistryContextMenuStore());
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

        Refresh();
        // 初始默认选中第一条「可操作」的项：右边详情卡立刻有内容，不用先点一下列表
        // （第一条如果是系统关键项 open / 新菜单之类，按钮是灰的，优先挑能隐藏 / 恢复的）。
        SelectedEntry ??= Entries.FirstOrDefault(item => item.CanToggle) ?? Entries.FirstOrDefault();

        Log($"已扫描 {_allEntries.Count} 个右键菜单条目（显示中 {_allEntries.Count(entry => !entry.IsHidden)} / "
            + $"已隐藏 {_allEntries.Count(entry => entry.IsHidden)}）");
        if (IsFirstRun)
        {
            Log("首次运行：筛选条件会保存在 Config\\context-menu-manager\\config.json");
        }
    }

    // ---------- 配置 ----------

    /// <summary>强类型配置（筛选条件自动保存）。</summary>
    public ContextMenuManagerConfig Cfg { get; }

    // ---------- 列表 / 筛选 ----------

    /// <summary>筛选后要显示的行（每次刷新都重建，行项本身不可变）。</summary>
    public ObservableCollection<ContextMenuEntryItem> Entries { get; } = new();

    /// <summary>位置下拉选项：<c>"全部"</c> + 当前扫描结果里出现过的位置名。</summary>
    public ObservableCollection<string> ScopeOptions { get; } = new();

    /// <summary>类型下拉选项（固定三项）。</summary>
    public IReadOnlyList<string> KindOptions { get; } = new[] { "全部", "菜单命令", "扩展模块" };

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

    /// <summary>类型筛选（与配置联动：全部 / 菜单命令 / 扩展模块）。</summary>
    public string SelectedKind
    {
        get
        {
            var value = Cfg.KindFilter;
            return value is "菜单命令" or "扩展模块" ? value : "全部";
        }
        set
        {
            if (_suppressFilterRefresh) return;
            var wanted = value is "菜单命令" or "扩展模块" ? value : "全部";
            if (string.Equals(Cfg.KindFilter, wanted, StringComparison.Ordinal)) return;
            Cfg.KindFilter = wanted;
            OnPropertyChanged();
            RefreshFiltered();
        }
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

    // ---------- 扫描 / 筛选 ----------

    /// <summary>重新枚举注册表并套用当前筛选。异常只写状态栏 / 日志。</summary>
    private void Refresh()
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
            IsBusy = false;
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
}
