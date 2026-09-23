using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.BatchRename.ViewModels;

/// <summary>
/// 「批量重命名」的 ViewModel。
///
/// <para><b>分工</b>：命名规则与冲突检测全在 <see cref="RenameEngine"/>（纯逻辑，自检可断言），
/// 真正动手改名在 <see cref="RenameExecutor"/>（两阶段 + 可撤销），
/// 本类负责“把界面上的操作串起来 + 维护那张可增删排序的规则清单 + 实时预览”。</para>
///
/// <para><b>规则清单的两份表示</b>：<see cref="Rules"/> 是界面的可观察集合（<see cref="RenameRuleItem"/>，
/// 带展开状态等 UI 信息），<see cref="BatchRenameConfig.Rules"/> 是要落盘的数据。
/// 任何增删改排序之后都调一次 <see cref="SyncRulesToConfig"/> 把两者对齐 —— 顺序就是执行顺序。</para>
///
/// <para><b>实时预览</b>：每条规则参数一改就会发通知（<see cref="RenameRule"/> → <see cref="RenameRuleItem"/> 转发），
/// 经 120ms 防抖后重算整份计划，输入时不会每敲一个字就重算一遍。</para>
/// </summary>
public sealed class BatchRenameViewModel : ToolViewModelBase
{
    private readonly RenameHistoryStore _historyStore;
    private readonly DispatcherTimer _previewTimer;

    /// <summary>最近一次算出来的计划（下标与 <see cref="Files"/> 一一对应）。</summary>
    private IReadOnlyList<RenamePlanEntry> _plan = Array.Empty<RenamePlanEntry>();

    private bool _applying;          // 正在把计划灌进列表 → 忽略列表发回来的变更通知
    private bool _sorting;           // 正在排序 → 排序过程中不要重算预览
    private CancellationTokenSource? _cts;

    public BatchRenameViewModel() : base("batch-rename")
    {
        Cfg = LoadConfig<BatchRenameConfig>();
        _historyStore = new RenameHistoryStore(Storage);

        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RebuildPreview();
        };

        AddFilesCommand = new RelayCommand(_ => AddFilesViaDialog());
        PickFolderCommand = new RelayCommand(_ => PickFolderAndScan());
        RescanCommand = new RelayCommand(_ => ScanFolder(clearFirst: true), _ => Cfg.LastFolder.Length > 0 && !IsBusy);
        ClearAllCommand = new RelayCommand(_ => ClearAll(), _ => Files.Count > 0 && !IsBusy);
        RemoveSelectedCommand = new RelayCommand(_ => RemoveItems(_selected), _ => _selected.Count > 0 && !IsBusy);
        MoveUpCommand = new RelayCommand(_ => MoveSelected(-1), _ => CanMoveFiles());
        MoveDownCommand = new RelayCommand(_ => MoveSelected(+1), _ => CanMoveFiles());
        CheckAllCommand = new RelayCommand(_ => SetAllChecked(true), _ => Files.Count > 0);
        UncheckAllCommand = new RelayCommand(_ => SetAllChecked(false), _ => Files.Count > 0);
        RevealSelectedCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedFile is not null);
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());
        OpenHistoryFolderCommand = new RelayCommand(_ => Storage.OpenInExplorer());
        ClearLogCommand = new RelayCommand(_ => ClearLog());

        // ---- 规则清单 ----
        AddRuleCommand = new RelayCommand(_ => AddRule(), _ => !IsBusy);
        ClearRulesCommand = new RelayCommand(_ => ClearRules(), _ => Rules.Count > 0 && !IsBusy);
        RemoveRuleCommand = new RelayCommand(p => RemoveRule(p as RenameRuleItem), p => p is RenameRuleItem && !IsBusy);
        MoveRuleUpCommand = new RelayCommand(p => MoveRule(p as RenameRuleItem, -1), p => CanMoveRule(p, -1));
        MoveRuleDownCommand = new RelayCommand(p => MoveRule(p as RenameRuleItem, +1), p => CanMoveRule(p, +1));
        ToggleExpandCommand = new RelayCommand(p => ToggleExpand(p as RenameRuleItem));

        ExecuteCommand = new AsyncRelayCommand(_ => ExecuteAsync(), _ => RenameCount > 0);
        UndoSessionCommand = new AsyncRelayCommand(p => UndoAsync(p as RenameSession), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);

        LoadRulesFromConfig();
        LoadHistory();
        RestoreLastFolderIfAny();
        UpdateSummary();

        Log($"配置文件：{Cfg.FilePath}");
        Log($"改名流水账：{_historyStore.FilePath}");
        Log("提示：把文件或文件夹直接拖进窗口也能添加；改名永远不换目录，只改文件名。");
        if (IsFirstRun) Log("首次运行：规则清单是空的——在「② 命名规则」里选类型、点「添加规则」开始搭。");
    }

    // =====================================================================
    //  配置与状态
    // =====================================================================

    /// <summary>规则清单（落盘的那一份）+ 使用偏好。改一下就自动存。</summary>
    public BatchRenameConfig Cfg { get; }

    /// <summary>待改名的文件列表（顺序 = 序号顺序）。</summary>
    public ObservableCollection<RenameItem> Files { get; } = new();

    private RenameItem? _selectedFile;
    /// <summary>列表里当前选中的行。</summary>
    public RenameItem? SelectedFile
    {
        get => _selectedFile;
        set => Set(ref _selectedFile, value);
    }

    private readonly List<RenameItem> _selected = new();

    private string _summaryText = "";
    /// <summary>底部的一句话统计。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    /// <summary>当前会真正改名的文件数（「执行」按钮的可用条件）。</summary>
    public int RenameCount => _plan.Count(e => e.Status == RenameStatus.Ok);

    /// <summary>
    /// 危险操作前的二次确认出口（默认弹 <see cref="MessageBox"/>）。
    /// 做成可替换的委托，是为了让自检程序能<b>无头</b>地跑完「执行 → 撤销」整条流程。
    /// </summary>
    public Func<string, string, bool> Confirm { get; set; } = static (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    /// <summary>有没有拦路的问题需要提示（冲突 / 错误）。</summary>
    public bool HasBlockingIssues =>
        _plan.Any(e => e.Status is RenameStatus.Conflict or RenameStatus.Error);

    // =====================================================================
    //  规则清单（本功能的核心：增 / 删 / 上下移动 / 拖动排序）
    // =====================================================================

    /// <summary>规则清单，<b>列表顺序 = 执行顺序</b>。同一种规则可以放多条。</summary>
    public ObservableCollection<RenameRuleItem> Rules { get; } = new();

    /// <summary>「新建规则」下拉框的选项。</summary>
    public IReadOnlyList<OptionItem<RenameRuleKind>> RuleKindOptions { get; } = OptionItem.From(
        (RenameRuleKind.Replace, "查找替换"),
        (RenameRuleKind.Cleanup, "清理（空格 / 指定字符）"),
        (RenameRuleKind.DeleteRange, "截取 / 删除"),
        (RenameRuleKind.Case, "大小写"),
        (RenameRuleKind.Affix, "加前缀 / 后缀"),
        (RenameRuleKind.Sequence, "自动编号"),
        (RenameRuleKind.Extension, "扩展名"));

    private OptionItem<RenameRuleKind>? _newRuleKind;
    /// <summary>「新建规则」下拉框选中的类型。</summary>
    public OptionItem<RenameRuleKind> NewRuleKind
    {
        get => _newRuleKind ??= RuleKindOptions[0];
        set
        {
            if (value is null || ReferenceEquals(_newRuleKind, value)) return;
            _newRuleKind = value;
            OnPropertyChanged();
        }
    }

    /// <summary>规则清单空的时候给界面显示个引导。</summary>
    public bool HasNoRules => Rules.Count == 0;

    /// <summary>
    /// 新加了一条规则。界面据此把新卡片滚进视野。
    ///
    /// <para>特意用独立事件而不是监听 <c>Rules.CollectionChanged</c>：拖动排序也走集合变更，
    /// 界面没法分辨“新加的”和“挪过来的”；而且 <c>CollectionChanged</c> 处理函数里不能强行布局（见 View 的说明）。
    /// 这个事件是在 <c>Rules.Add</c> 完全返回之后才发的，界面可以放心操作。</para>
    /// </summary>
    public event EventHandler<RenameRuleItem>? RuleAdded;

    /// <summary>把当前界面上的规则清单写回配置对象（顺序也一起同步），下次自动落盘。</summary>
    private void SyncRulesToConfig()
    {
        Cfg.Rules.Clear();
        foreach (var item in Rules) Cfg.Rules.Add(item.Rule);
        OnPropertyChanged(nameof(HasNoRules));
    }

    private void LoadRulesFromConfig()
    {
        foreach (var rule in Cfg.Rules)
        {
            if (rule is null) continue;              // 手改 JSON 时可能写出 null，忽略掉
            var item = new RenameRuleItem(rule);
            item.PropertyChanged += OnRuleItemChanged;
            Rules.Add(item);
        }
        SyncRulesToConfig();                          // 顺手把 null 项清掉
    }

    /// <summary>把「新建规则」下拉框选中的类型追加到清单末尾，并展开它让你填参数。</summary>
    public void AddRule()
    {
        var item = new RenameRuleItem(RenameRule.Create(NewRuleKind.Value));
        item.PropertyChanged += OnRuleItemChanged;

        foreach (var other in Rules) other.IsExpanded = false;   // 同时只展开一条，列表才不会长得看不见
        item.IsExpanded = true;
        Rules.Add(item);

        SyncRulesToConfig();
        RebuildPreview();

        StatusText = $"已添加「{item.Rule.KindName}」规则：在下面填参数，或按住左侧 ≡ 拖到别的位置（顺序就是执行顺序）";
        Log($"添加规则 #{Rules.Count}：{item.Rule.KindName}");

        RuleAdded?.Invoke(this, item);               // 集合变更都处理完了，现在通知界面去滚动才安全
    }

    /// <summary>删掉一条规则。</summary>
    public void RemoveRule(RenameRuleItem? item)
    {
        if (item is null) return;
        var index = Rules.IndexOf(item);
        if (index < 0) return;

        item.PropertyChanged -= OnRuleItemChanged;
        item.Detach();
        Rules.RemoveAt(index);

        SyncRulesToConfig();
        RebuildPreview();
        StatusText = $"已删除第 {index + 1} 条规则（{item.Rule.KindName}）";
        Log($"删除规则 #{index + 1}：{item.Rule.KindName}");
    }

    private void ClearRules()
    {
        if (Rules.Count == 0) return;
        if (!Confirm($"确定清空全部 {Rules.Count} 条规则吗？（只清规则，不影响已选的文件）", "清空规则")) return;

        foreach (var item in Rules)
        {
            item.PropertyChanged -= OnRuleItemChanged;
            item.Detach();
        }
        Rules.Clear();
        SyncRulesToConfig();
        RebuildPreview();
        StatusText = "规则已清空";
        Log("---- 规则已清空 ----");
    }

    /// <summary>上移 / 下移一条规则（<paramref name="delta"/> = -1 / +1）。</summary>
    public void MoveRule(RenameRuleItem? item, int delta)
    {
        if (item is null) return;
        var from = Rules.IndexOf(item);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Rules.Count) return;

        Rules.Move(from, to);
        SyncRulesToConfig();
        RebuildPreview();
        StatusText = $"「{item.Rule.KindName}」现在是第 {to + 1} 条";
    }

    /// <summary>
    /// 拖动排序：把 <paramref name="dragged"/> 插到 <paramref name="target"/> 的前面 / 后面
    /// （<paramref name="target"/> 传 null = 放到最末尾）。由 <c>BatchRenameView.xaml.cs</c> 的拖放处理调用。
    ///
    /// <para>用一次 <see cref="System.Collections.ObjectModel.ObservableCollection{T}.Move"/> 完成，
    /// 而不是“先删再插”：后者会发两个集合变更事件，ListBox 要为所有卡片重新生成 / 复用容器，
    /// 拖动时肉眼能看出闪一下，也更容易踩到“集合变更处理途中的状态不一致”。</para>
    /// </summary>
    public void MoveRuleTo(RenameRuleItem? dragged, RenameRuleItem? target, bool before)
    {
        if (dragged is null) return;

        var from = Rules.IndexOf(dragged);
        if (from < 0) return;

        int to;
        if (target is null)
        {
            to = Rules.Count - 1;                     // 拖到清单空白处 = 放到末尾
        }
        else
        {
            var targetIndex = Rules.IndexOf(target);
            if (targetIndex < 0) return;              // 目标已经不在了（理论上不会），什么都不做
            to = before ? targetIndex : targetIndex + 1;
            if (from < to) to--;                      // 自己先被挪走，它后面那些项的下标会前移一位
        }

        to = Math.Clamp(to, 0, Rules.Count - 1);
        if (to == from) return;                       // 位置没变（拖回原处）→ 不动

        Rules.Move(from, to);
        SyncRulesToConfig();
        RebuildPreview();
        StatusText = $"「{dragged.Rule.KindName}」已移到第 {to + 1} 条";
    }

    /// <summary>拖动经过某项时高亮插入位置（<paramref name="target"/> 为 null = 清掉所有指示线）。</summary>
    public void SetDropTarget(RenameRuleItem? target, bool before)
    {
        foreach (var item in Rules)
        {
            item.ShowDropTop = ReferenceEquals(item, target) && before;
            item.ShowDropBottom = ReferenceEquals(item, target) && !before;
        }
    }

    /// <summary>拖动结束（无论放没放下）都清掉指示线。</summary>
    public void ClearDropTarget() => SetDropTarget(null, false);

    /// <summary>点标题行展开 / 收起参数编辑区（同时只展开一条）。</summary>
    public void ToggleExpand(RenameRuleItem? item)
    {
        if (item is null) return;
        var expand = !item.IsExpanded;
        foreach (var other in Rules) other.IsExpanded = false;
        item.IsExpanded = expand;
    }

    private bool CanMoveRule(object? parameter, int delta)
    {
        if (parameter is not RenameRuleItem item || IsBusy) return false;
        var from = Rules.IndexOf(item);
        var to = from + delta;
        return from >= 0 && to >= 0 && to < Rules.Count;
    }

    // =====================================================================
    //  列表排序选项
    // =====================================================================

    public IReadOnlyList<OptionItem<FileSortMode>> SortOptions { get; } = OptionItem.From(
        (FileSortMode.None, "不排序"),
        (FileSortMode.Name, "文件名"),
        (FileSortMode.Path, "完整路径"),
        (FileSortMode.Time, "修改时间"),
        (FileSortMode.Size, "文件大小"));

    public OptionItem<FileSortMode> SelectedSort
    {
        get => OptionItem<FileSortMode>.Find(SortOptions, Cfg.SortField) ?? SortOptions[2];
        set
        {
            if (value is null) return;
            Cfg.SortField = value.Value;
            OnPropertyChanged();
            ApplySort();
        }
    }

    /// <summary>是否降序（勾上/取消都会立刻重排列表）。</summary>
    public bool SortDescending
    {
        get => Cfg.SortDescending;
        set
        {
            if (Cfg.SortDescending == value) return;
            Cfg.SortDescending = value;
            OnPropertyChanged();
            ApplySort();
        }
    }

    // =====================================================================
    //  历史记录
    // =====================================================================

    /// <summary>最近几次改名（最新的在最上面），每行都能单独撤销。</summary>
    public ObservableCollection<RenameSession> History { get; } = new();

    /// <summary>是否存在可撤销的记录（撤销按钮的可用条件）。</summary>
    public bool CanUndoLast => History.Any(s => !s.Undone && s.Entries.Count > 0);

    // =====================================================================
    //  命令
    // =====================================================================

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand PickFolderCommand { get; }
    public RelayCommand RescanCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand CheckAllCommand { get; }
    public RelayCommand UncheckAllCommand { get; }
    public RelayCommand RevealSelectedCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenHistoryFolderCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    /// <summary>把「新建规则」下拉框里的类型加进清单。</summary>
    public RelayCommand AddRuleCommand { get; }

    /// <summary>清空规则清单（会二次确认）。</summary>
    public RelayCommand ClearRulesCommand { get; }

    /// <summary>删除某条规则（参数 = <see cref="RenameRuleItem"/>）。</summary>
    public RelayCommand RemoveRuleCommand { get; }

    /// <summary>规则上移 / 下移（参数 = <see cref="RenameRuleItem"/>）。</summary>
    public RelayCommand MoveRuleUpCommand { get; }
    public RelayCommand MoveRuleDownCommand { get; }

    /// <summary>展开 / 收起某条规则的参数（参数 = <see cref="RenameRuleItem"/>）。</summary>
    public RelayCommand ToggleExpandCommand { get; }

    public AsyncRelayCommand ExecuteCommand { get; }
    public AsyncRelayCommand UndoSessionCommand { get; }
    public RelayCommand CancelCommand { get; }

    // =====================================================================
    //  添加文件 / 扫描
    // =====================================================================

    /// <summary>从资源管理器拖进来 / 代码里直接调：把一批路径加进列表（文件夹会按当前设置展开）。</summary>
    public void AddPaths(IEnumerable<string> paths, bool clearFirst = false)
    {
        if (clearFirst) ClearAll();

        var files = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) files.Add(path);
                else if (Directory.Exists(path)) files.AddRange(EnumerateFolder(path));
            }
            catch { /* 单个路径读不了就跳过 */ }
        }

        AddFilePaths(files);
    }

    /// <summary>把一批文件路径加进列表（自动去重、自动按当前排序规则重排）。</summary>
    public void AddFilePaths(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
        {
            StatusText = "没有找到符合条件的文件";
            return;
        }

        var existing = new HashSet<string>(Files.Select(f => f.SourcePath), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;

        foreach (var path in paths)
        {
            if (!existing.Add(path)) { skipped++; continue; }
            try
            {
                var info = new FileInfo(path);
                var item = new RenameItem(info.FullName, info.Length, info.LastWriteTime);
                item.PropertyChanged += OnItemChanged;
                Files.Add(item);
                added++;
            }
            catch { skipped++; }
        }

        ApplySort();
        RebuildPreview();

        StatusText = skipped > 0
            ? $"已添加 {added} 个文件（{skipped} 个重复或读不到，已跳过）"
            : $"已添加 {added} 个文件";
        Log($"{StatusText}（列表共 {Files.Count} 个）");
    }

    private void AddFilesViaDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要批量改名的文件",
            Filter = "所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        AddPaths(dialog.FileNames);
    }

    private void PickFolderAndScan()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要批量改名的文件夹",
        };
        if (Directory.Exists(Cfg.LastFolder)) dialog.InitialDirectory = Cfg.LastFolder;
        if (dialog.ShowDialog() != true) return;

        Cfg.LastFolder = dialog.FolderName;
        ScanFolder(clearFirst: true);
    }

    private void ScanFolder(bool clearFirst)
    {
        var folder = Cfg.LastFolder;
        if (!Directory.Exists(folder))
        {
            StatusText = "文件夹不存在了，请重新选择";
            Log($"扫描失败：找不到文件夹 {folder}");
            return;
        }

        var found = EnumerateFolder(folder).ToList();

        // 扫描文件夹 / 重新扫描 = 列表完全反映这个文件夹（一个都没找到也照样清空，免得留着上一次的旧内容）
        if (clearFirst) ClearAll();

        if (found.Count == 0)
        {
            StatusText = $"「{Path.GetFileName(folder)}」里没有找到符合条件的文件";
            Log(StatusText + (Cfg.ExtensionFilter.Trim().Length > 0 ? $"（扩展名过滤：{Cfg.ExtensionFilter}）" : ""));
            RebuildPreview();
            return;
        }

        AddFilePaths(found);
    }

    /// <summary>按「包含子文件夹 + 扩展名过滤」列出一个文件夹里的文件。</summary>
    private IEnumerable<string> EnumerateFolder(string folder)
    {
        var option = Cfg.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var patterns = RenameEngine.ParseExtensionFilter(Cfg.ExtensionFilter);

        try
        {
            if (patterns.Count == 0) return Directory.EnumerateFiles(folder, "*", option).ToList();

            var result = new List<string>();
            foreach (var pattern in patterns)
                result.AddRange(Directory.EnumerateFiles(folder, pattern, option));
            return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log($"扫描 {folder} 出错：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    private void ClearAll()
    {
        foreach (var item in Files) item.PropertyChanged -= OnItemChanged;
        Files.Clear();
        _selected.Clear();
        SelectedFile = null;
        StatusText = "列表已清空";
        RebuildPreview();
    }

    /// <summary>把列表里被移除的项清出来（<see cref="_selected"/> 由界面同步）。</summary>
    private void RemoveItems(IReadOnlyCollection<RenameItem> items)
    {
        if (items.Count == 0) return;
        foreach (var item in items.ToList())
        {
            item.PropertyChanged -= OnItemChanged;
            Files.Remove(item);
        }
        _selected.Clear();
        if (SelectedFile is not null && !Files.Contains(SelectedFile)) SelectedFile = null;

        StatusText = $"已移除 {items.Count} 个文件";
        RebuildPreview();
    }

    /// <summary>界面把 ListBox 的选中项同步过来（支持多选删除 / 上移下移）。</summary>
    public void UpdateSelection(IList? selectedItems)
    {
        _selected.Clear();
        if (selectedItems is null) return;
        foreach (var entry in selectedItems)
            if (entry is RenameItem item) _selected.Add(item);
    }

    private bool CanMoveFiles() => _selected.Count > 0 && !IsBusy;

    private void MoveSelected(int delta)
    {
        if (_selected.Count == 0) return;

        // 多选时只移动选中的那些：上移时从上往下处理，下移时反过来，选中的相对顺序不会乱
        var indices = _selected.Select(item => Files.IndexOf(item)).Where(i => i >= 0).ToList();
        if (indices.Count == 0) return;

        var ordered = delta < 0 ? indices.OrderBy(i => i).ToList() : indices.OrderByDescending(i => i).ToList();
        var moved = 0;
        foreach (var index in ordered)
        {
            var target = index + delta;
            if (target < 0 || target >= Files.Count) continue;
            if (_selected.Contains(Files[target])) continue;   // 目标位置也是选中的，不用换
            Files.Move(index, target);
            moved++;
        }

        if (moved == 0) return;

        _sorting = true;
        Cfg.SortField = FileSortMode.None;   // 手动调过顺序后就别自动重排了
        _sorting = false;
        OnPropertyChanged(nameof(SelectedSort));

        StatusText = "已调整顺序（序号按列表顺序生成）";
        RebuildPreview();
    }

    private void RevealSelected()
    {
        if (SelectedFile is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{SelectedFile.SourcePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = "打开资源管理器失败：" + ex.Message;
        }
    }

    private void SetAllChecked(bool value)
    {
        foreach (var item in Files) item.IsChecked = value;
    }

    // =====================================================================
    //  预览
    // =====================================================================

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        if (e.PropertyName == nameof(RenameItem.IsChecked)) SchedulePreview();
    }

    private void OnRuleItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 纯界面状态不触发重算
        if (e.PropertyName is nameof(RenameRuleItem.IsExpanded)
            or nameof(RenameRuleItem.ShowDropTop)
            or nameof(RenameRuleItem.ShowDropBottom)) return;

        SchedulePreview();
    }

    /// <summary>120ms 防抖：连续输入时只算最后一次。</summary>
    private void SchedulePreview()
    {
        if (_sorting) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>立刻重算整份计划并刷新列表（排序、勾选、改规则、执行前后都会直接调它）。</summary>
    public void RebuildPreview()
    {
        _previewTimer.Stop();

        var sources = Files.Select(f => f.SourcePath).ToList();
        var include = Files.Select(f => f.IsChecked).ToList();
        _plan = RenameEngine.BuildPlan(sources, Cfg, include);

        _applying = true;
        try
        {
            for (var i = 0; i < Files.Count && i < _plan.Count; i++) Files[i].Apply(_plan[i]);
        }
        finally
        {
            _applying = false;
        }

        UpdateSummary();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateSummary()
    {
        if (Files.Count == 0)
        {
            SummaryText = "还没有文件：点「添加文件…」或直接把文件拖进窗口";
            return;
        }

        if (Rules.Count == 0)
        {
            SummaryText = $"共 {Files.Count} 个文件 · 还没有规则：在下面选个类型点「添加规则」";
            return;
        }

        var count = _plan.Count(e => e.Status == RenameStatus.Ok);
        var unchanged = _plan.Count(e => e.Status == RenameStatus.Unchanged);
        var skipped = _plan.Count(e => e.Status == RenameStatus.Skipped);
        var conflicts = _plan.Count(e => e.Status == RenameStatus.Conflict);
        var errors = _plan.Count(e => e.Status == RenameStatus.Error);

        var parts = new List<string> { $"共 {Files.Count} 个", $"将改名 {count} 个" };
        if (unchanged > 0) parts.Add($"不变 {unchanged}");
        if (skipped > 0) parts.Add($"跳过 {skipped}");
        if (conflicts > 0) parts.Add($"冲突 {conflicts}");
        if (errors > 0) parts.Add($"错误 {errors}");

        SummaryText = string.Join(" · ", parts);
    }

    // =====================================================================
    //  排序
    // =====================================================================

    private void ApplySort()
    {
        if (Cfg.SortField == FileSortMode.None || Files.Count < 2) return;

        _sorting = true;
        try
        {
            var comparer = StringComparer.CurrentCulture;   // 中文系统下就是按拼音
            IEnumerable<RenameItem> sorted = Cfg.SortField switch
            {
                FileSortMode.Name => Files.OrderBy(f => f.OriginalName, comparer),
                FileSortMode.Path => Files.OrderBy(f => f.SourcePath, comparer),
                FileSortMode.Time => Files.OrderBy(f => f.LastWriteTime),
                FileSortMode.Size => Files.OrderBy(f => f.SizeBytes),
                _ => Files,
            };
            if (Cfg.SortDescending) sorted = sorted.Reverse();

            var reordered = sorted.ToList();
            for (var i = 0; i < reordered.Count; i++)
            {
                var current = Files.IndexOf(reordered[i]);
                if (current != i) Files.Move(current, i);
            }
        }
        finally
        {
            _sorting = false;
        }

        RebuildPreview();
    }

    // =====================================================================
    //  执行改名
    // =====================================================================

    private async Task ExecuteAsync()
    {
        RebuildPreview();   // 保证用的是最新规则，别拿旧计划去改

        var targets = Enumerable.Range(0, Files.Count)
            .Where(i => i < _plan.Count && _plan[i].Status == RenameStatus.Ok)
            .ToList();
        if (targets.Count == 0)
        {
            StatusText = Rules.Count == 0 ? "还没有规则，先加一条" : "没有需要改名的文件";
            return;
        }

        var message = $"将重命名 {targets.Count} 个文件（套用 {Rules.Count} 条规则）。";
        if (targets.Count < Files.Count)
            message += $"\n\n其余 {Files.Count - targets.Count} 个会被跳过（不变 / 冲突 / 错误 / 未勾选）。";
        if (HasBlockingIssues)
            message += "\n\n注意：列表里还有冲突或错误的文件（会跳过，不会改名）。";
        message += "\n\n改完可以在下方「历史记录」里一键撤销。确定继续吗？";

        if (!Confirm(message, "确认批量重命名")) return;

        var entries = targets.Select(i => _plan[i]).ToList();
        var indexOf = new Dictionary<RenamePlanEntry, int>();
        for (var i = 0; i < targets.Count; i++) indexOf[entries[i]] = targets[i];

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Progress = 0;
        ProgressText = $"0 / {entries.Count}";
        Log($"===== 开始重命名 {entries.Count} 个文件（{Rules.Count} 条规则）=====");

        try
        {
            var outcomes = await Task.Run(
                () => RenameExecutor.Execute(entries, token, (done, total) => ReportProgress(done, total)),
                token);

            var succeeded = new List<RenameHistoryEntry>();
            var failed = 0;
            foreach (var outcome in outcomes)
            {
                var item = Files[indexOf[outcome.Entry]];
                if (outcome.Success)
                {
                    item.MarkRenamed(outcome.Entry.TargetPath);
                    succeeded.Add(new RenameHistoryEntry
                    {
                        From = outcome.Entry.SourcePath,
                        To = outcome.Entry.TargetPath,
                    });
                }
                else
                {
                    item.Status = RenameStatus.Failed;
                    item.Message = outcome.Error ?? "改名失败";
                    failed++;
                    Log($"✗ {outcome.Entry.OriginalName} → {outcome.Entry.NewName}：{outcome.Error}");
                }
            }

            if (succeeded.Count > 0) PushHistory(succeeded);

            StatusText = failed == 0
                ? $"已重命名 {succeeded.Count} 个文件"
                : $"重命名完成：成功 {succeeded.Count} 个，失败 {failed} 个";
            Log($"===== {StatusText} =====");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消（正在改的文件已改回原名）";
            Log("===== 已取消 =====");
        }
        catch (Exception ex)
        {
            StatusText = "重命名出错：" + ex.Message;
            Log("✗ " + ex);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            Progress = 0;
            ProgressText = "0 / 0";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>后台线程回报进度：切回 UI 线程 + 抽样刷新（几千个文件也不会把界面刷爆）。</summary>
    private void ReportProgress(int done, int total)
    {
        if (done != total && done % 20 != 0) return;
        Ui(() =>
        {
            Progress = total == 0 ? 0 : (double)done / total;
            ProgressText = $"{done} / {total}";
        });
    }

    // =====================================================================
    //  撤销
    // =====================================================================

    private async Task UndoAsync(RenameSession? session)
    {
        session ??= History.FirstOrDefault(s => !s.Undone);
        if (session is null)
        {
            StatusText = "没有可撤销的记录";
            return;
        }
        if (session.Undone)
        {
            StatusText = "这一批已经撤销过了";
            return;
        }

        var message = $"要把这 {session.Entries.Count} 个文件改回原来的名字吗？\n\n"
                      + "如果这些文件之后被手动改过名或删掉了，对应的那几条会被跳过。";
        if (!Confirm(message, "确认撤销")) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Progress = 0;
        Log($"===== 开始撤销 {session.TimeText} 的 {session.Entries.Count} 个改名 =====");

        try
        {
            var results = await Task.Run(
                () => RenameExecutor.Undo(session, token, (done, total) => ReportProgress(done, total)),
                token);

            var restored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failed = 0;
            foreach (var (entry, success, error) in results)
            {
                if (success)
                {
                    restored[entry.To] = entry.From;
                }
                else
                {
                    failed++;
                    Log($"✗ 撤销失败 {Path.GetFileName(entry.To)}：{error}");
                }
            }

            session.Undone = restored.Count > 0;
            SaveHistory();
            RefreshHistoryList();

            // 列表里还在的文件同步回原名
            foreach (var item in Files)
                if (restored.TryGetValue(item.SourcePath, out var original)) item.MarkUndone(original);

            RebuildPreview();

            StatusText = failed == 0
                ? $"已撤销 {restored.Count} 个改名"
                : $"撤销完成：成功 {restored.Count} 个，跳过 {failed} 个";
            Log($"===== {StatusText} =====");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消撤销";
            Log("===== 撤销已取消 =====");
        }
        catch (Exception ex)
        {
            StatusText = "撤销出错：" + ex.Message;
            Log("✗ " + ex);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            Progress = 0;
            ProgressText = "0 / 0";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // =====================================================================
    //  历史记录的加载 / 保存
    // =====================================================================

    private void LoadHistory()
    {
        var history = _historyStore.Load();
        History.Clear();
        foreach (var session in history.Sessions) History.Add(session);
        OnPropertyChanged(nameof(CanUndoLast));
    }

    private void PushHistory(List<RenameHistoryEntry> entries)
    {
        var history = _historyStore.Load();
        history.Push(new RenameSession { Entries = entries });
        if (_historyStore.Save(history)) Log($"已记录流水账（可撤销）：{_historyStore.FilePath}");
        else Log("⚠ 流水账写入失败，本次改名可能无法撤销");
        LoadHistory();
    }

    private void SaveHistory()
    {
        var history = new RenameHistory();
        history.Sessions.AddRange(History);
        _historyStore.Save(history);
    }

    /// <summary>撤销后刷新历史列表（<see cref="RenameSession"/> 不是可观察对象，整体重建最简单）。</summary>
    private void RefreshHistoryList()
    {
        var snapshot = History.ToList();
        History.Clear();
        foreach (var session in snapshot) History.Add(session);
        OnPropertyChanged(nameof(CanUndoLast));
    }

    /// <summary>上次用过的文件夹还在的话，提示一下（不自动载入，免得一打开就冒出一堆文件）。</summary>
    private void RestoreLastFolderIfAny()
    {
        if (Cfg.LastFolder.Length == 0 || !Directory.Exists(Cfg.LastFolder)) return;
        Log($"上次的文件夹还在，可点「重新扫描」重新载入：{Cfg.LastFolder}");
    }

    /// <summary>
    /// 工具被切走 / 关闭：停掉预览防抖计时器、摘掉挂在缓存对象上的事件。
    ///
    /// <para>摘事件这一步不能省：配置对象和规则对象都由框架按类型/实例缓存着（关掉再打开拿到的是同一批），
    /// 不摘的话每打开一次就多挂一个旧 VM，之后改规则会把已经不显示的旧 VM 也一起唤醒去算预览。</para>
    /// </summary>
    protected override void OnClosed()
    {
        _cts?.Cancel();
        _previewTimer.Stop();
        foreach (var item in Files) item.PropertyChanged -= OnItemChanged;
        foreach (var item in Rules)
        {
            item.PropertyChanged -= OnRuleItemChanged;
            item.Detach();
        }
    }
}
