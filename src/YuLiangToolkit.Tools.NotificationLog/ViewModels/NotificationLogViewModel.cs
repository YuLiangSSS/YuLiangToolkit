using System.Collections.ObjectModel;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.NotificationLog.ViewModels;

/// <summary>
/// 「通知管理」页的 ViewModel：展示 <see cref="NotificationHistory"/>，按来源 / 类型 / 关键词筛选，
/// 逐条删除、清空，改容量，并负责把历史落盘。
///
/// <para><b>扩展性</b>：来源不是写死的列表，而是从历史里归纳出来的（<see cref="NotificationHistory.Sources"/>）。
/// 以后任何新工具只要调 <see cref="NotificationCenter.Show"/>，这里就自动多一个来源选项，
/// **不需要改本文件**。</para>
/// </summary>
public sealed class NotificationLogViewModel : ToolViewModelBase
{
    /// <summary>列表里的一行。</summary>
    public sealed class Row
    {
        /// <summary>原始记录。</summary>
        public required NotificationRecord Record { get; init; }

        /// <summary>时间文字（今天 / 昨天 / 具体日期）。</summary>
        public required string TimeText { get; init; }

        /// <summary>类型色点资源名。</summary>
        public string KindBrushKey => Record.Kind switch
        {
            NotificationKind.Success => "SuccessBrush",
            NotificationKind.Warning => "WarningBrush",
            NotificationKind.Error => "DangerBrush",
            _ => "AccentBrush",
        };

        /// <summary>来源标签。</summary>
        public string SourceText => Record.DisplaySource;

        /// <summary>正文摘要。</summary>
        public string BodyText => Record.DescribeBody();

        /// <summary>正文是否为空（空就不占一行）。</summary>
        public bool HasBody => BodyText.Length > 0;
    }

    /// <summary>来源筛选项（「全部来源」+ 历史里出现过的）。</summary>
    public sealed record SourceFilter(string Id, string Name)
    {
        /// <summary>「全部来源」的 Id。</summary>
        public const string AllId = "*";

        /// <summary>显示文字。</summary>
        public string Display => Name;
    }

    private readonly NotificationHistory _history;
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressSave;

    public NotificationLogViewModel(NotificationHistory history) : base(NotificationLogConfig.ToolId)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        Cfg = LoadConfig<NotificationLogConfig>();

        DeleteCommand = new RelayCommand(p => Delete(p as Row), p => p is Row);
        ClearAllCommand = new RelayCommand(_ => ClearAll(), _ => _history.Items.Count > 0);
        ClearFiltersCommand = new RelayCommand(_ => ResetFilters());

        // 落盘：合并成 1.5 秒一次，避免每条通知都写盘
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveHistory();
        };

        _history.Changed += (_, _) => Ui(OnHistoryChanged);
        _history.SettingsChanged += (_, _) => Ui(OnHistorySettingsChanged);

        // 启动时按配置把历史灌进来（容量 / 是否记住历史都由配置决定）
        _suppressSave = true;
        _history.Capacity = Cfg.Capacity;
        _history.Enabled = true;   // 历史本身总是记录（"记住"只决定是否写盘）
        _suppressSave = false;

        if (Cfg.RememberHistory)
        {
            var saved = NotificationLogStore.Load();
            if (saved.Count > 0) _history.ReplaceAll(saved);
        }

        RebuildSourceFilters();
        ApplyFilter();

        Log($"配置文件：{Cfg.FilePath}");
        Log($"历史文件：{NotificationLogStore.PathOf()}（{(Cfg.RememberHistory ? "记住" : "只在本次运行有效")}）");
        Log($"当前历史：{_history.Items.Count} 条");
    }

    /// <summary>强类型配置。</summary>
    public NotificationLogConfig Cfg { get; }

    /// <summary>底层历史（外壳注入的那一份）。</summary>
    public NotificationHistory History => _history;

    // ---------- 命令 ----------

    public RelayCommand DeleteCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }

    // ---------- 筛选 ----------

    private string _keyword = "";

    /// <summary>关键词（标题 / 正文 / 来源）。</summary>
    public string Keyword
    {
        get => _keyword;
        set
        {
            if (!Set(ref _keyword, value ?? "")) return;
            ApplyFilter();
        }
    }

    /// <summary>来源选项。</summary>
    public ObservableCollection<SourceFilter> SourceFilters { get; } = new();

    private SourceFilter? _selectedSource;

    /// <summary>当前选中的来源。</summary>
    public SourceFilter? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Set(ref _selectedSource, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>类型选项（全部 + 四种）。</summary>
    public ObservableCollection<string> KindFilters { get; } = new()
    {
        "全部类型", "信息", "成功", "警告", "错误",
    };

    private string _selectedKind = "全部类型";

    /// <summary>当前选中的类型。</summary>
    public string SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (!Set(ref _selectedKind, value ?? "全部类型")) return;
            ApplyFilter();
        }
    }

    /// <summary>筛选后的行（界面绑这个）。</summary>
    public ObservableCollection<Row> Rows { get; } = new();

    /// <summary>一条都没有（空状态）。</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>统计文字。</summary>
    public string SummaryText
    {
        get
        {
            var total = _history.Items.Count;
            var shown = Rows.Count;
            return shown == total ? $"共 {total} 条" : $"显示 {shown} / 共 {total} 条";
        }
    }

    // ---------- 设置 ----------

    /// <summary>记住历史（写盘）。</summary>
    public bool RememberHistory
    {
        get => Cfg.RememberHistory;
        set
        {
            if (Cfg.RememberHistory == value) return;
            Cfg.RememberHistory = value;
            OnPropertyChanged();

            // 关掉时立刻把盘上的文件删掉（不留"看起来还在记住"的状态）
            SaveHistory();
            Log(value ? "已开启「记住历史」" : "已关闭「记住历史」，历史文件已删除");
        }
    }

    /// <summary>最多保留多少条。</summary>
    public int Capacity
    {
        get => Cfg.Capacity;
        set
        {
            if (Cfg.Capacity == value) return;
            Cfg.Capacity = value;
            _history.Capacity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CapacityText));

            ScheduleSave();
        }
    }

    /// <summary>容量文字。</summary>
    public string CapacityText => Cfg.CapacityText;

    // ---------- 操作 ----------

    /// <summary>删一条。</summary>
    public void Delete(Row? row)
    {
        if (row is null) return;

        if (_history.Remove(row.Record.Id))
        {
            StatusText = "已删除 1 条";
            Log($"删除通知：{row.Record.Title}");
            ScheduleSave();
        }
    }

    /// <summary>清空全部（界面已经确认过）。</summary>
    public void ClearAll()
    {
        var count = _history.Clear();
        if (count > 0)
        {
            StatusText = $"已清空 {count} 条";
            Log($"清空通知历史（{count} 条）");
            ScheduleSave();
        }
    }

    /// <summary>重置筛选。</summary>
    public void ResetFilters()
    {
        _keyword = "";
        OnPropertyChanged(nameof(Keyword));

        SelectedSource = SourceFilters.FirstOrDefault();
        SelectedKind = KindFilters[0];
        ApplyFilter();
    }

    // ---------- 内部 ----------

    private void OnHistoryChanged()
    {
        RebuildSourceFilters();
        ApplyFilter();
        ScheduleSave();
    }

    private void OnHistorySettingsChanged()
    {
        if (_suppressSave) return;
        ScheduleSave();
    }

    /// <summary>来源选项重建（历史里出现过的来源，新的自动排到最后）。</summary>
    private void RebuildSourceFilters()
    {
        var previousId = _selectedSource?.Id ?? SourceFilter.AllId;

        SourceFilters.Clear();
        SourceFilters.Add(new SourceFilter(SourceFilter.AllId, "全部来源"));

        foreach (var (id, name) in _history.Sources())
        {
            SourceFilters.Add(new SourceFilter(id, name));
        }

        _selectedSource = SourceFilters.FirstOrDefault(f => f.Id == previousId) ?? SourceFilters[0];
        OnPropertyChanged(nameof(SelectedSource));
    }

    /// <summary>按当前筛选条件重算行。</summary>
    private void ApplyFilter()
    {
        var keyword = _keyword.Trim();
        var sourceId = _selectedSource?.Id ?? SourceFilter.AllId;
        var kindFilter = KindToFilter(_selectedKind);

        var now = DateTime.Now;

        var filtered = _history.Items.Where(record =>
        {
            if (sourceId != SourceFilter.AllId
                && !string.Equals(record.SourceId, sourceId, StringComparison.Ordinal)) return false;

            if (kindFilter is { } kind && record.Kind != kind) return false;

            if (keyword.Length == 0) return true;

            return record.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                   || record.Body.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                   || record.DisplaySource.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
        });

        Rows.Clear();
        foreach (var record in filtered)
        {
            Rows.Add(new Row { Record = record, TimeText = record.DescribeTime(now) });
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>类型下拉的文字 → 枚举（「全部类型」返回 null）。</summary>
    private static NotificationKind? KindToFilter(string? text) => text switch
    {
        "信息" => NotificationKind.Info,
        "成功" => NotificationKind.Success,
        "警告" => NotificationKind.Warning,
        "错误" => NotificationKind.Error,
        _ => null,
    };

    /// <summary>安排一次落盘（合并短时间内的多次改动）。</summary>
    private void ScheduleSave()
    {
        if (_suppressSave || !Cfg.RememberHistory) return;

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立刻落盘。</summary>
    public void SaveHistory()
    {
        _saveTimer.Stop();
        NotificationLogStore.Save(_history.Items, Cfg.RememberHistory);
    }

    /// <summary>工具被切走 / 关闭：把没落盘的写掉。</summary>
    protected override void OnClosed()
    {
        _saveTimer.Stop();
        SaveHistory();
    }
}
