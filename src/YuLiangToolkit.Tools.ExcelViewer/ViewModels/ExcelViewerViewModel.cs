using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Tools.ExcelViewer.Parsing;

namespace YuLiangToolkit.Tools.ExcelViewer.ViewModels;

/// <summary>
/// 「Excel 阅读器」的 ViewModel：管**打开 / 最近打开 / 当前工作簿 / 当前工作表 / 选中单元格 /
/// 查找 / 复制 / 阅读位置 / 错误提示**。
///
/// <para><b>为什么解析放在这里而不是 View</b>：解析要跑后台线程、要处理异常、要更新状态栏，
/// 这些都与界面控件无关。View 只负责「拿到当前工作表的行与列，重建 DataGrid 列」这类控件操作。</para>
///
/// <para>线程约定：解析 / 查找在 <see cref="Task.Run(Action)"/> 里做，任何改界面状态（Sheets / SelectedSheet /
/// StatusText / SearchStatusText 等）都走基类的 <see cref="ToolViewModelBase.Ui(Action)"/>（见 AGENTS.md 硬性规则 4）。</para>
/// </summary>
public sealed class ExcelViewerViewModel : ToolViewModelBase, IFileLaunchTarget
{
    /// <summary>查找输入停顿多久后自动开始（毫秒）；回车会立刻查找，不等防抖。</summary>
    private const int SearchDebounceMilliseconds = 300;

    /// <summary>解析结果（后台线程创建，UI 线程只读）。关闭工具时释放引用。</summary>
    private ExcelWorkbook? _workbook;

    /// <summary>当前工作簿的完整路径（阅读位置按它记；没打开时为 null）。</summary>
    private string? _filePath;

    private ExcelSheetItem? _selectedSheet;
    private ExcelCellSelection? _selectedCellInfo;

    /// <summary>当前选中的原始单元格（右键复制 / 纯文本组装用；<see cref="SelectedCellInfo"/> 是它的显示投影）。</summary>
    private ExcelCell? _selectedCell;

    private ExcelRecentItem? _selectedRecentItem;
    private string _errorText = "";
    private string _fileNameText = "未打开文件";
    private string _filePathText = "";
    private string _documentSummary = "";

    // ---------- 查找状态 ----------
    private string _searchText = "";
    private bool _searchCaseSensitive;
    private string _searchStatusText = "";
    private IReadOnlyList<ExcelSearchHit> _searchHits = Array.Empty<ExcelSearchHit>();
    private int _searchHitIndex = -1;
    private bool _searchIsTruncated;
    private CancellationTokenSource? _searchCts;
    private readonly DispatcherTimer _searchDebounceTimer;

    // ---------- 阅读位置状态 ----------
    /// <summary>界面最近一次报告的滚动比例（不直接落盘：等切表 / 切走 / 关工具时再写，见 AGENTS.md）。</summary>
    private double _lastScrollRatio;

    /// <summary>打开工作簿后待恢复的滚动比例（由 View 在布局完成后取走）。</summary>
    private double? _pendingRestoreRatio;

    /// <summary>恢复工作簿 / 清空文档期间不要因为「选中工作表」而把旧位置写坏。</summary>
    private bool _suppressReadingPositionSave;

    public ExcelViewerViewModel() : base(ExcelViewerConfig.ToolId)
    {
        Cfg = LoadConfig<ExcelViewerConfig>();

        // 最近列表按当前上限裁一次（手改坏配置时不会带出一长串），再把界面列表填好
        Cfg.TrimRecentToLimit();
        RefreshRecentItems();

        OpenCommand = new AsyncRelayCommand(_ => OpenFromDialogAsync());
        RefreshRecentCommand = new RelayCommand(_ => RefreshRecentItems());
        ClearRecentCommand = new RelayCommand(_ => ClearRecent());

        // 右键复制：四个命令共用一个「复制并提示」流程，只有「复制公式」多一个「必须有公式」的条件
        CopyDisplayValueCommand = new RelayCommand(
            _ => CopySelectedCell(ExcelCopyKind.DisplayValue), _ => _selectedCell is not null);
        CopyRawValueCommand = new RelayCommand(
            _ => CopySelectedCell(ExcelCopyKind.RawValue), _ => _selectedCell is not null);
        CopyFormulaCommand = new RelayCommand(
            _ => CopySelectedCell(ExcelCopyKind.Formula),
            _ => ExcelClipboardText.CanCopy(_selectedCell, ExcelCopyKind.Formula));
        CopyAddressCommand = new RelayCommand(
            _ => CopySelectedCell(ExcelCopyKind.Address), _ => _selectedCell is not null);

        // 查找：输入 300ms 防抖自动搜；回车 / 上一条 / 下一条立即搜并跳转
        FindNextCommand = new RelayCommand(_ => FindNext(), _ => HasSearchContext);
        FindPreviousCommand = new RelayCommand(_ => FindPrevious(), _ => HasSearchContext);
        ClearSearchCommand = new RelayCommand(_ => ClearSearch(), _ => _searchText.Length > 0 || _searchStatusText.Length > 0);

        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(SearchDebounceMilliseconds),
        };
        _searchDebounceTimer.Tick += OnSearchDebounceTick;

        Log($"配置文件：{Cfg.FilePath}");

        // 开发 / 截图用的自动打开开关：只显示，不写配置（不改 LastFolder / 最近列表）
        var autoOpenPath = Environment.GetEnvironmentVariable("YLT_EXCEL_OPEN");
        if (!string.IsNullOrWhiteSpace(autoOpenPath))
        {
            Log($"YLT_EXCEL_OPEN={autoOpenPath}");
            _ = OpenFileAsync(autoOpenPath, rememberInConfig: false);
        }
    }

    // ---------------------------------------------------------------- 配置与命令

    /// <summary>强类型配置（<c>Config\excel-viewer\config.json</c>）。</summary>
    public ExcelViewerConfig Cfg { get; }

    /// <summary>打开文件对话框（工具条「打开…」/ Ctrl+O）。</summary>
    public AsyncRelayCommand OpenCommand { get; }

    /// <summary>刷新最近列表（文件可能在外部被删掉）。</summary>
    public RelayCommand RefreshRecentCommand { get; }

    /// <summary>清空最近列表（只删记录，不动本地文件）。</summary>
    public RelayCommand ClearRecentCommand { get; }

    /// <summary>右键菜单：复制当前单元格的显示值。</summary>
    public RelayCommand CopyDisplayValueCommand { get; }

    /// <summary>右键菜单：复制当前单元格的原始值。</summary>
    public RelayCommand CopyRawValueCommand { get; }

    /// <summary>右键菜单：复制当前单元格的公式（带等号；没有公式时禁用）。</summary>
    public RelayCommand CopyFormulaCommand { get; }

    /// <summary>右键菜单：复制当前单元格地址（A1 形式）。</summary>
    public RelayCommand CopyAddressCommand { get; }

    /// <summary>查找：下一个命中 / 没有结果时先搜索再跳第一个。</summary>
    public RelayCommand FindNextCommand { get; }

    /// <summary>查找：上一个命中 / 没有结果时先搜索再跳最后一个。</summary>
    public RelayCommand FindPreviousCommand { get; }

    /// <summary>清空查找输入与命中状态。</summary>
    public RelayCommand ClearSearchCommand { get; }

    // ---------------------------------------------------------------- 工作簿 / 工作表状态

    /// <summary>工作簿里的工作表（下拉框 / 页签数据源）。</summary>
    public ObservableCollection<ExcelSheetItem> Sheets { get; } = new();

    /// <summary>当前选中的工作表；改它会让界面重建 DataGrid 列（通过 <see cref="SheetLayoutChanged"/>）。</summary>
    public ExcelSheetItem? SelectedSheet
    {
        get => _selectedSheet;
        set
        {
            var previous = _selectedSheet;
            if (!Set(ref _selectedSheet, value)) return;

            // 切表前先把「上一张表读到哪」记进配置（此时 _filePath / previous 还是旧表的）
            if (!_suppressReadingPositionSave && previous is not null)
                SaveReadingPosition(previous.Name, _lastScrollRatio);

            _lastScrollRatio = 0;   // 新表的滚动位置等 View 报告
            _selectedCell = null;
            SelectedCellInfo = null;
            ResetSearchResults();

            OnPropertyChanged(nameof(CurrentSheet));
            OnPropertyChanged(nameof(CurrentRows));
            OnPropertyChanged(nameof(UsedRangeText));
            OnPropertyChanged(nameof(SheetInfoText));
            OnPropertyChanged(nameof(HasSheetData));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateText));
            OnPropertyChanged(nameof(FreezeText));
            OnPropertyChanged(nameof(AutoRowHeightText));

            // 通知 View：列集合 / 列宽 / 冻结布局要跟着换（列是 DataGridTemplateColumn，只能在代码后置里重建）
            SheetLayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>当前工作表（只读数据；没打开时为 null）。</summary>
    public ExcelSheet? CurrentSheet => _selectedSheet?.Sheet;

    /// <summary>当前工作表的数据行（直接绑 DataGrid.ItemsSource；行数最多 = 解析上限）。</summary>
    public IReadOnlyList<ExcelRow>? CurrentRows => CurrentSheet?.Rows;

    /// <summary>当前工作表有没有可以显示的数据（空工作表要显示空状态提示）。</summary>
    public bool HasSheetData => CurrentSheet is { Rows.Count: > 0, Columns.Count: > 0 };

    /// <summary>工作表切换 / 重新打开后触发，View 用它重建列并重新绑定行。</summary>
    public event EventHandler? SheetLayoutChanged;

    /// <summary>「冻结窗格」开关变化 / 工作表切换时触发，View 用它重建冻结行表与 FrozenColumnCount。</summary>
    public event EventHandler? FreezeLayoutChanged;

    /// <summary>「自动行高」开关变化时触发，View 用它重设 DataGrid 行高。</summary>
    public event EventHandler? RowHeightChanged;

    /// <summary>查找命中需要跳转时触发，View 用它 ScrollIntoView + 选中单元格。</summary>
    public event EventHandler<ExcelSearchNavigateEventArgs>? SearchNavigateRequested;

    // ---------------------------------------------------------------- 显示开关

    /// <summary>工具条「冻结窗格」勾选框：开 = 按文件还原，关 = 不冻结行列（不修改原文件）。</summary>
    public bool RestoreFreezePanes
    {
        get => Cfg.RestoreFreezePanes;
        set
        {
            if (Cfg.RestoreFreezePanes == value) return;
            Cfg.RestoreFreezePanes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FreezeText));
            FreezeLayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>工具条「自动行高」勾选框：开 = 按 ht / 内容自动，关 = 统一 24 DIP（旧版行为）。</summary>
    public bool AutoRowHeight
    {
        get => Cfg.AutoRowHeight;
        set
        {
            if (Cfg.AutoRowHeight == value) return;
            Cfg.AutoRowHeight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoRowHeightText));
            RowHeightChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------- 顶栏 / 状态栏文字

    /// <summary>文件名（顶栏左侧）。</summary>
    public string FileNameText
    {
        get => _fileNameText;
        private set => Set(ref _fileNameText, value);
    }

    /// <summary>完整路径（顶栏第二行 / 悬停提示）。</summary>
    public string FilePathText
    {
        get => _filePathText;
        private set => Set(ref _filePathText, value);
    }

    /// <summary>工作簿摘要，例如「3 个工作表 · 1904 日期系统」。</summary>
    public string DocumentSummary
    {
        get => _documentSummary;
        private set => Set(ref _documentSummary, value);
    }

    /// <summary>当前工作表的使用范围，例如「使用范围 A1:AZ120」。</summary>
    public string UsedRangeText
    {
        get
        {
            var sheet = CurrentSheet;
            if (sheet is null) return "";
            if (sheet.MaxRow <= 0 || sheet.MaxColumn <= 0) return "空工作表";

            return $"使用范围 {ExcelAddresses.CellReference(1, 1)}:{ExcelAddresses.CellReference(sheet.MaxRow, sheet.MaxColumn)}";
        }
    }

    /// <summary>
    /// 工作表的补充提示：截断说明 / 隐藏行 / 隐藏列 / 合并单元格数量。
    /// 这些信息用户一定要看到（否则会以为「怎么少了一半数据」），所以固定放状态栏。
    /// </summary>
    public string SheetInfoText
    {
        get
        {
            var sheet = CurrentSheet;
            if (sheet is null) return "";

            var parts = new List<string>();
            if (sheet.IsTruncated && !string.IsNullOrEmpty(sheet.TruncationNote))
                parts.Add("⚠ " + sheet.TruncationNote);
            if (sheet.HiddenRowCount > 0)
                parts.Add($"已跳过 {sheet.HiddenRowCount} 个隐藏行");
            if (sheet.HiddenColumnCount > 0)
                parts.Add($"{sheet.HiddenColumnCount} 个隐藏列不显示");
            if (sheet.MergedRanges.Count > 0)
                parts.Add($"{sheet.MergedRanges.Count} 处合并单元格");

            return string.Join("　·　", parts);
        }
    }

    /// <summary>
    /// 状态栏里的冻结提示，例如「冻结：首行 · 前 2 列」；没冻结 / 用户关了冻结还原时为空串。
    /// </summary>
    public string FreezeText
    {
        get
        {
            if (!Cfg.RestoreFreezePanes) return "";
            var freeze = CurrentSheet?.FreezePane;
            return freeze is { IsFrozen: true } ? freeze.Description : "";
        }
    }

    /// <summary>状态栏里的自动行高提示；关掉自动行高 / 没打开表时为空串。</summary>
    public string AutoRowHeightText => Cfg.AutoRowHeight && HasSheetData ? "自动行高" : "";

    // ---------------------------------------------------------------- 空状态 / 错误区

    /// <summary>错误提示（显示在页面错误区，不弹窗）；空串 = 没有错误。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (!Set(ref _errorText, value)) return;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    /// <summary>有没有错误（XAML 用它控制错误区显隐）。</summary>
    public bool HasError => _errorText.Length > 0;

    /// <summary>要不要显示「没有内容」的占位提示。</summary>
    public bool ShowEmptyState => !HasError && !IsBusy && !HasSheetData;

    /// <summary>空状态标题。</summary>
    public string EmptyStateTitle => _workbook is null ? "还没有打开工作簿" : "这个工作表没有数据";

    /// <summary>空状态说明。</summary>
    public string EmptyStateText => _workbook is null
        ? "把 .xlsx / .xlsm 文件拖进窗口，或点左上角「打开…」。只读浏览，不会改动原文件。"
        : "当前工作表没有可显示的单元格（可能只有格式、图表或图片）。";

    // ---------------------------------------------------------------- 最近打开

    /// <summary>最近打开（下拉框数据源；文件不存在时带标记）。</summary>
    public ObservableCollection<ExcelRecentItem> RecentItems { get; } = new();

    /// <summary>
    /// 下拉框的选择桥：选中一条就打开它，然后立刻清空选择（否则同一个文件第二次点没反应）。
    /// 记录已经不存在的文件时，从最近列表里删掉并在错误区提示。
    /// </summary>
    public ExcelRecentItem? SelectedRecentItem
    {
        get => _selectedRecentItem;
        set
        {
            if (!Set(ref _selectedRecentItem, value) || value is null) return;

            var chosen = value;
            _selectedRecentItem = null;
            OnPropertyChanged();

            if (chosen.IsMissing)
            {
                Cfg.RemoveRecent(chosen.Path);
                RefreshRecentItems();
                ErrorText = $"最近打开的文件已经不在了：{chosen.Path}";
                StatusText = "文件已不存在（已从最近列表移除）";
                return;
            }

            _ = OpenFileAsync(chosen.Path);
        }
    }

    // ---------------------------------------------------------------- 选中单元格 / 右键复制

    /// <summary>当前选中单元格的信息（状态栏显示地址 / 值 / 公式 / 格式）。</summary>
    public ExcelCellSelection? SelectedCellInfo
    {
        get => _selectedCellInfo;
        private set
        {
            if (!Set(ref _selectedCellInfo, value)) return;
            OnPropertyChanged(nameof(SelectedCellText));
        }
    }

    /// <summary>状态栏里选中单元格那一行（没选时给操作提示）。</summary>
    public string SelectedCellText => SelectedCellInfo?.SummaryText
        ?? "单击任意单元格查看地址 / 值 / 公式；右键可复制；拖入 .xlsx / .xlsm 文件也可以直接打开";

    /// <summary>
    /// 由 View 在 <c>SelectedCellsChanged</c> 时调用：把选中的格子交给 ViewModel 组装成状态栏文字。
    /// 传 null = 清空选择。
    /// </summary>
    public void SelectCell(ExcelCell? cell)
    {
        _selectedCell = cell;
        if (cell is null)
        {
            SelectedCellInfo = null;
            return;
        }

        SelectedCellInfo = new ExcelCellSelection(
            ExcelAddresses.CellReference(cell.Row, cell.Column),
            cell.DisplayText,
            cell.RawValue,
            cell.Formula,
            DescribeStyle(cell.Style),
            cell.IsMergedAnchor,
            cell.IsMergedCovered);
    }

    /// <summary>把样式翻译成中文说明（状态栏用；只挑用户看得懂的项）。</summary>
    private static string DescribeStyle(ExcelCellStyle style)
    {
        var parts = new List<string>();

        parts.Add(string.IsNullOrWhiteSpace(style.NumberFormatCode)
            ? "常规格式"
            : $"数字格式 {style.NumberFormatCode}");

        if (style.Bold) parts.Add("粗体");
        if (style.Italic) parts.Add("斜体");
        if (style.Underline) parts.Add("下划线");
        if (style.FontSizePt is { } size) parts.Add($"字号 {size:0.#} 磅");

        if (style.HorizontalAlignment != ExcelHorizontalAlignment.General)
        {
            parts.Add(style.HorizontalAlignment switch
            {
                ExcelHorizontalAlignment.Left => "左对齐",
                ExcelHorizontalAlignment.Center => "居中",
                ExcelHorizontalAlignment.Right => "右对齐",
                _ => "",
            });
        }

        if (style.WrapText) parts.Add("自动换行");
        if (!string.IsNullOrEmpty(style.FillColorArgb)) parts.Add("有填充色");
        if (!string.IsNullOrEmpty(style.FontColorArgb)) parts.Add("自定义字体色");

        return string.Join(" · ", parts);
    }

    /// <summary>复制当前单元格的某个内容到剪贴板；失败只改状态栏，不弹窗。</summary>
    private void CopySelectedCell(ExcelCopyKind kind)
    {
        var cell = _selectedCell;
        if (cell is null)
        {
            StatusText = "请先选中一个单元格再复制";
            return;
        }

        var text = ExcelClipboardText.ForCell(cell, kind);
        var description = ExcelClipboardText.Describe(kind);

        try
        {
            if (string.IsNullOrEmpty(text))
            {
                // Clipboard.SetText 不接受空串；空内容按「复制空」处理：清空剪贴板
                Clipboard.Clear();
                StatusText = $"{description}为空，已清空剪贴板";
            }
            else
            {
                Clipboard.SetText(text);
                StatusText = $"已复制{description}：{ShortenForStatus(text)}";
            }
        }
        catch (COMException)
        {
            // 剪贴板被别的程序占用（Windows 上很常见），提示一下即可
            StatusText = "剪贴板被占用，复制失败，请稍后重试";
        }
        catch (Exception)
        {
            StatusText = "复制失败：无法访问剪贴板";
        }
    }

    /// <summary>把要显示在状态栏的复制内容压成一行（太长就截断，完整内容已经进剪贴板了）。</summary>
    private static string ShortenForStatus(string text)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 48 ? oneLine : oneLine[..48] + "…";
    }

    // ---------------------------------------------------------------- 查找单元格

    /// <summary>查找关键字（输入停顿 300ms 自动查找；回车立即查找并跳第一处）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            if (string.IsNullOrEmpty(_searchText))
            {
                ResetSearchResults();
                return;
            }

            RestartSearchDebounce();
        }
    }

    /// <summary>是否区分大小写。</summary>
    public bool SearchCaseSensitive
    {
        get => _searchCaseSensitive;
        set
        {
            if (!Set(ref _searchCaseSensitive, value)) return;
            if (_searchText.Length > 0) RestartSearchDebounce();
        }
    }

    /// <summary>查找区的命中计数文字，例如「第 3 / 12 处」「没有找到」「正在查找…」。</summary>
    public string SearchStatusText
    {
        get => _searchStatusText;
        private set => Set(ref _searchStatusText, value);
    }

    /// <summary>有没有可用的查找上下文（输入框有字或已有命中），用来决定上一条 / 下一条是否可点。</summary>
    public bool HasSearchContext => _searchText.Length > 0 || _searchHits.Count > 0;

    /// <summary>回车 / 「下一个」：有命中就往后翻，没有就立即搜索并跳到第一处。</summary>
    public void FindNext()
    {
        if (_searchHits.Count > 0)
        {
            MoveToHit(0, forward: true);
            return;
        }

        if (_searchText.Length > 0) _ = RunSearchAsync(autoJump: true);
    }

    /// <summary>「上一个」：有命中就往前翻，没有就立即搜索并跳到最后一处。</summary>
    public void FindPrevious()
    {
        if (_searchHits.Count > 0)
        {
            MoveToHit(0, forward: false);
            return;
        }

        if (_searchText.Length > 0) _ = RunSearchAsync(autoJump: true, jumpToLast: true);
    }

    /// <summary>清空查找框与命中状态（查找区的「清除」按钮）。</summary>
    public void ClearSearch()
    {
        ResetSearchState(clearText: true);
        StatusText = "已清除查找";
    }

    /// <summary>由 View 报告「这处命中跳不过去」（隐藏列 / 行被截断），状态栏说明原因。</summary>
    public void NoteNavigationFailed(string message) => StatusText = message;

    /// <summary>输入防抖计时器到点：自动搜索，但<b>不</b>主动跳转（避免边打字边跳）。</summary>
    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        if (_searchText.Length > 0) _ = RunSearchAsync(autoJump: false);
    }

    private void RestartSearchDebounce()
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    /// <summary>
    /// 在后台线程搜索当前工作表，结果回 UI 线程应用。
    /// <para>取消策略：每次新搜索都会取消上一次（改关键字 / 切表时不会让旧结果覆盖新结果）；
    /// 搜索本身每行 / 每 64 格检查一次取消，5000 × 200 的表也能很快停下。</para>
    /// </summary>
    private async Task RunSearchAsync(bool autoJump, bool jumpToLast = false)
    {
        var sheet = CurrentSheet;
        var query = _searchText;
        if (sheet is null || query.Length == 0)
        {
            ResetSearchResults();
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        SearchStatusText = "正在查找…";

        var rows = sheet.Rows;
        var caseSensitive = _searchCaseSensitive;

        try
        {
            var result = await Task.Run(
                () => ExcelSearch.Search(rows, query, caseSensitive, ExcelSearch.MaxHits, cts.Token),
                cts.Token);

            if (cts.IsCancellationRequested) return;

            // await 之后通常已经回到 UI 线程，这里仍走 Ui(...) 明确表达「改界面状态要回 UI 线程」
            Ui(() => ApplySearchResult(sheet, result, autoJump, jumpToLast));
        }
        catch (OperationCanceledException)
        {
            // 取消了（改用新关键字 / 切表 / 关工具）：什么都不用做
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                if (!ReferenceEquals(CurrentSheet, sheet)) return;
                SearchStatusText = $"查找失败：{ex.Message}";
            });
        }
    }

    /// <summary>把后台搜索结果接到界面状态上（必须在 UI 线程调用）。</summary>
    private void ApplySearchResult(ExcelSheet sheet, ExcelSearchResult result, bool autoJump, bool jumpToLast)
    {
        // 搜索期间用户切了表 / 换了文件：结果作废
        if (!ReferenceEquals(CurrentSheet, sheet)) return;

        _searchHits = result.Hits;
        _searchIsTruncated = result.IsTruncated;
        _searchHitIndex = -1;

        if (_searchHits.Count == 0)
        {
            SearchStatusText = "没有找到";
            return;
        }

        if (autoJump) MoveToHit(jumpToLast ? _searchHits.Count - 1 : 0, forward: !jumpToLast, absolute: true);
        else SearchStatusText = BuildSearchStatus();
    }

    /// <summary>命中下标前 / 后移动（带循环），并通知 View 跳转。</summary>
    private void MoveToHit(int index, bool forward, bool absolute = false)
    {
        if (_searchHits.Count == 0) return;

        if (!absolute)
        {
            index = _searchHitIndex < 0
                ? (forward ? 0 : _searchHits.Count - 1)
                : (forward
                    ? (_searchHitIndex + 1) % _searchHits.Count
                    : (_searchHitIndex - 1 + _searchHits.Count) % _searchHits.Count);
        }

        _searchHitIndex = Math.Clamp(index, 0, _searchHits.Count - 1);
        SearchStatusText = BuildSearchStatus();
        SearchNavigateRequested?.Invoke(this,
            new ExcelSearchNavigateEventArgs(_searchHits[_searchHitIndex], _searchHitIndex + 1, _searchHits.Count));
    }

    /// <summary>查找区显示的计数文字（跳转后带「第 x / N 处」，截断时补一句说明）。</summary>
    private string BuildSearchStatus()
    {
        if (_searchHits.Count == 0) return "没有找到";

        var text = _searchHitIndex >= 0
            ? $"第 {_searchHitIndex + 1} / {_searchHits.Count} 处"
            : $"共 {_searchHits.Count} 处";

        if (_searchIsTruncated)
            text += $"（只显示前 {ExcelSearch.MaxHits} 处，请把关键字写得更具体）";

        return text;
    }

    /// <summary>取消正在进行的搜索并清空命中（保留输入框文字）。</summary>
    private void ResetSearchResults()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _searchDebounceTimer.Stop();
        _searchHits = Array.Empty<ExcelSearchHit>();
        _searchHitIndex = -1;
        _searchIsTruncated = false;
        SearchStatusText = "";
    }

    /// <summary>彻底重置查找（<paramref name="clearText"/> = true 时连输入框一起清空）。</summary>
    private void ResetSearchState(bool clearText)
    {
        ResetSearchResults();
        if (!clearText || _searchText.Length == 0) return;

        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
    }

    // ---------------------------------------------------------------- 阅读位置（记住上次看到哪）

    /// <summary>
    /// 由 View 在滚动时报告当前纵向滚动比例 0 ~ 1（只缓存、不落盘：
    /// 切表 / 切走 / 关工具时再写，避免滚动时每帧写配置）。
    /// </summary>
    public void NoteScrollPosition(double ratio)
    {
        if (_suppressReadingPositionSave || _selectedSheet is null) return;
        _lastScrollRatio = ExcelReadingPosition.ClampRatio(ratio);
    }

    /// <summary>打开工作簿后由 View 在布局完成后取一次待恢复的滚动比例；没有返回 null。</summary>
    public double? ConsumePendingRestoreRatio()
    {
        var ratio = _pendingRestoreRatio;
        _pendingRestoreRatio = null;
        return ratio;
    }

    /// <summary>
    /// 把当前工作表 + 滚动比例写进配置；<paramref name="saveNow"/> = true 时立刻落盘
    /// （关工具 / 切走 / 换文件时用，防止退出丢）。
    /// </summary>
    public void FlushReadingPosition(bool saveNow = false)
    {
        var sheet = _selectedSheet;
        if (sheet is null) return;

        SaveReadingPosition(sheet.Name, _lastScrollRatio);
        if (!saveNow) return;

        try { Cfg.SaveNow(); } catch { /* 配置写入失败不影响阅读 */ }
    }

    /// <summary>把某一本工作簿 / 某一张表的位置写进配置（记录开关关闭时直接跳过）。</summary>
    private void SaveReadingPosition(string sheetName, double scrollRatio)
    {
        if (!Cfg.RememberReadingPositions) return;
        if (_workbook is null || string.IsNullOrEmpty(_filePath) || string.IsNullOrEmpty(sheetName)) return;

        Cfg.RememberReadingPosition(_filePath, new ExcelReadingPosition
        {
            SheetName = sheetName,
            ScrollRatio = scrollRatio,
            SavedAtUtc = DateTime.UtcNow,
        });
    }

    // ---------------------------------------------------------------- 打开文件

    /// <summary>打开文件对话框（起始目录 = 上次打开的文件夹）。</summary>
    private async Task OpenFromDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开 Excel 工作簿",
            Filter = "Excel 工作簿 (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(Cfg.LastFolder) ? Cfg.LastFolder : "",
        };

        if (dialog.ShowDialog() != true) return;
        await OpenFileAsync(dialog.FileName);
    }

    /// <summary>
    /// 打开并解析一个工作簿（公开给 View 的拖放用）。
    /// <paramref name="rememberInConfig"/> = false 时不写 LastFolder / 最近列表（调试环境变量用）。
    /// </summary>
    public async Task OpenFileAsync(string? path, bool rememberInConfig = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (IsBusy)
        {
            StatusText = "正在解析上一个文件，请稍候…";
            return;
        }

        var fullPath = path.Trim().Trim('"');
        ErrorText = "";

        if (IsLegacyExcelFile(fullPath))
        {
            // .xls 是二进制 BIFF 格式，不是 ZIP+XML，解析器不认；给一句能照做的中文提示
            ErrorText = "不支持旧版 .xls 文件，请用 Excel 打开后「另存为」.xlsx（或 .xlsm）再打开。";
            StatusText = "不支持 .xls（旧版二进制格式）";
            Log($"拒绝打开：{fullPath}（.xls 旧格式）");
            return;
        }

        if (!File.Exists(fullPath))
        {
            ErrorText = $"文件不存在：{fullPath}";
            StatusText = "打开失败";
            return;
        }

        if (!XlsxParser.IsSupportedExtension(fullPath))
        {
            ErrorText = "只支持 .xlsx / .xlsm 工作簿（.xls 旧格式请先用 Excel 另存为 .xlsx）。";
            StatusText = "不支持的文件类型";
            return;
        }

        // 打开另一本之前，先把「当前这本读到哪」落盘（View.Unloaded 也会存，这里是双保险）
        FlushReadingPosition(saveNow: true);

        // 打开新文件先清旧状态，免得解析失败时界面还挂着上一份数据
        ClearDocument();

        IsBusy = true;
        ProgressText = "正在解析…";
        StatusText = $"正在打开 {Path.GetFileName(fullPath)}…";
        Log($"打开 {fullPath}");

        try
        {
            var limits = new XlsxParseLimits();
            var workbook = await Task.Run(() => XlsxParser.Parse(fullPath, limits));

            Ui(() =>
            {
                ApplyWorkbook(fullPath, workbook);

                if (rememberInConfig)
                {
                    Cfg.LastFolder = Path.GetDirectoryName(fullPath) ?? "";
                    Cfg.RememberRecent(fullPath);
                    RefreshRecentItems();
                }
            });

            Log($"已打开：{workbook.Sheets.Count} 个工作表"
                + (workbook.Uses1904DateSystem ? "（1904 日期系统）" : ""));
            foreach (var sheet in workbook.Sheets)
            {
                var flags = new List<string>();
                if (!sheet.IsVisible) flags.Add("隐藏");
                if (sheet.IsTruncated) flags.Add("已截断");
                if (sheet.FreezePane is { IsFrozen: true } freeze) flags.Add(freeze.Description);
                Log($"  工作表「{sheet.Name}」：{sheet.Rows.Count} 行 × {sheet.MaxColumn} 列"
                    + (flags.Count > 0 ? $"（{string.Join(" / ", flags)}）" : ""));
            }
        }
        catch (OoxmlException ex)
        {
            // 解析器抛的消息本来就是给用户看的中文，直接显示
            Ui(() =>
            {
                ErrorText = ex.Message;
                StatusText = "打开失败";
                FileNameText = "未打开文件";
                FilePathText = "";
                DocumentSummary = "";
                OnPropertyChanged(nameof(ShowEmptyState));
            });
            Log("打开失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                ErrorText = $"打开失败：{ex.Message}";
                StatusText = "打开失败";
                OnPropertyChanged(nameof(ShowEmptyState));
            });
            Log("打开失败：" + ex.Message);
        }
        finally
        {
            Ui(() =>
            {
                IsBusy = false;
                ProgressText = "0 / 0";
                OnPropertyChanged(nameof(ShowEmptyState));
            });
        }
    }

    // ---------------------------------------------------------------- 打开方式（文件关联 / 单实例转发）

    /// <summary>
    /// 主程序通过 Windows「打开方式」交过来一个工作簿（见 <see cref="IFileLaunchTarget"/>）：
    /// 规范成绝对路径后复用普通打开流程（<see cref="OpenFileAsync"/>），不弹窗、不阻塞界面。
    ///
    /// <para>路径为空 / 非法时只写状态栏与日志；文件不存在 / 不是 .xlsx 由 <see cref="OpenFileAsync"/>
    /// 统一写进错误区，<b>绝不抛异常给主程序</b>。</para>
    /// </summary>
    public void OpenLaunchedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "打开方式：没有拿到文件路径";
            Log("[打开方式] 没有拿到文件路径，忽略");
            return;
        }

        string fullPath;
        try
        {
            // 主程序一般给绝对路径，但仍容忍带引号 / 相对路径的写法
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            StatusText = $"打开方式：路径不合法（{path}）";
            Log($"[打开方式] 路径不合法：{path} · {ex.Message}");
            return;
        }

        _ = OpenFileAsync(fullPath);
    }

    /// <summary>把解析结果挂到界面上（必须在 UI 线程调用）。</summary>
    private void ApplyWorkbook(string fullPath, ExcelWorkbook workbook)
    {
        _workbook = workbook;
        _filePath = fullPath;

        FileNameText = Path.GetFileName(fullPath);
        FilePathText = fullPath;
        DocumentSummary = $"{workbook.Sheets.Count} 个工作表"
            + (workbook.Uses1904DateSystem ? " · 1904 日期系统" : "");

        Sheets.Clear();
        for (var index = 0; index < workbook.Sheets.Count; index++)
        {
            Sheets.Add(new ExcelSheetItem(workbook.Sheets[index], index));
        }

        // 阅读位置：优先选记录里的工作表；记录不存在 / 表被改名时回退到第一个可见表
        ExcelReadingPosition? stored = Cfg.RememberReadingPositions
            ? Cfg.FindReadingPosition(fullPath)
            : null;

        ExcelSheetItem? target = null;
        if (stored is { HasSheetName: true })
        {
            target = Sheets.FirstOrDefault(item =>
                string.Equals(item.Name, stored.SheetName, StringComparison.OrdinalIgnoreCase));
        }

        target ??= Sheets.FirstOrDefault(item => item.IsVisible) ?? Sheets.FirstOrDefault();

        _suppressReadingPositionSave = true;
        try
        {
            // 待恢复比例要在触发 SheetLayoutChanged 之前放好：View 重建完列会立刻来取
            _pendingRestoreRatio = target is not null && stored is { HasSheetName: true }
                && string.Equals(target.Name, stored.SheetName, StringComparison.OrdinalIgnoreCase)
                    ? stored.ScrollRatio
                    : null;

            SelectedSheet = target;
        }
        finally
        {
            _suppressReadingPositionSave = false;
        }

        _lastScrollRatio = 0;
        StatusText = stored is not null && _pendingRestoreRatio is not null
            ? $"已打开 {FileNameText}，已回到上次阅读位置"
            : $"已打开 {FileNameText}（{workbook.Sheets.Count} 个工作表）";

        OnPropertyChanged(nameof(HasSheetData));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(FreezeText));
        OnPropertyChanged(nameof(AutoRowHeightText));
    }

    /// <summary>打开失败 / 关闭工具时把界面上的旧文档清掉。</summary>
    private void ClearDocument()
    {
        _workbook = null;
        _filePath = null;
        _pendingRestoreRatio = null;
        _lastScrollRatio = 0;
        _selectedCell = null;
        Sheets.Clear();
        SelectedSheet = null;

        FileNameText = "未打开文件";
        FilePathText = "";
        DocumentSummary = "";
        SelectedCellInfo = null;
        ResetSearchState(clearText: true);

        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(UsedRangeText));
        OnPropertyChanged(nameof(SheetInfoText));
        OnPropertyChanged(nameof(HasSheetData));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(FreezeText));
        OnPropertyChanged(nameof(AutoRowHeightText));
    }

    // ---------------------------------------------------------------- 最近列表

    /// <summary>按配置重建最近列表（新 → 旧；文件不存在标记 IsMissing）。</summary>
    private void RefreshRecentItems()
    {
        RecentItems.Clear();
        foreach (var entry in Cfg.RecentEntries
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                     .Take(Cfg.RecentLimitEffective))
        {
            RecentItems.Add(new ExcelRecentItem(entry.Path, entry.LastOpenedUtc));
        }
        OnPropertyChanged(nameof(HasRecentItems));
    }

    /// <summary>最近列表里有没有记录（XAML 用来显示「暂无记录」提示）。</summary>
    public bool HasRecentItems => RecentItems.Count > 0;

    private void ClearRecent()
    {
        Cfg.ClearRecent();
        SelectedRecentItem = null;
        RefreshRecentItems();
        StatusText = "已清空最近打开记录（不会删除本地文件）";
    }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>
    /// 判断是不是旧版 .xls（含 .xlsb / .xlt 这类非 OOXML 工作簿也顺带给出提示）。
    /// <b>.xlsm 是 OOXML，支持</b>，别把它也拦了。
    /// </summary>
    private static bool IsLegacyExcelFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>关闭工具 / 被挤出缓存：先落盘阅读位置，再释放解析结果（视图被缓存、切走时不调用这里）。</summary>
    protected override void OnClosed()
    {
        try
        {
            _searchDebounceTimer.Stop();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            FlushReadingPosition(saveNow: true);
        }
        catch
        {
            // 清理失败不影响关闭流程
        }

        _workbook = null;
        ClearDocument();
        Log("已释放解析结果");
    }
}

/// <summary>查找跳转事件参数：告诉界面要定位到哪个 Excel 坐标（1 基）。</summary>
public sealed class ExcelSearchNavigateEventArgs : EventArgs
{
    public ExcelSearchNavigateEventArgs(ExcelSearchHit hit, int index, int total)
    {
        Hit = hit;
        Index = index;
        Total = total;
    }

    /// <summary>命中的单元格 / 字段。</summary>
    public ExcelSearchHit Hit { get; }

    /// <summary>当前是第几处（1 基）。</summary>
    public int Index { get; }

    /// <summary>一共多少处命中。</summary>
    public int Total { get; }
}
