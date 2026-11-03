using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.ExcelViewer.Parsing;

namespace YuLiangToolkit.Tools.ExcelViewer.ViewModels;

/// <summary>
/// 「Excel 阅读器」的 ViewModel：管**打开 / 最近打开 / 当前工作簿 / 当前工作表 / 选中单元格 / 错误提示**。
///
/// <para><b>为什么解析放在这里而不是 View</b>：解析要跑后台线程、要处理异常、要更新状态栏，
/// 这些都与界面控件无关。View 只负责「拿到当前工作表的行与列，重建 DataGrid 列」这件事。</para>
///
/// <para>线程约定：解析在 <see cref="Task.Run(Action)"/> 里做，任何改界面状态（Sheets / SelectedSheet /
/// StatusText / ErrorText）都走基类的 <see cref="ToolViewModelBase.Ui(Action)"/>（见 AGENTS.md 硬性规则 4）。</para>
/// </summary>
public sealed class ExcelViewerViewModel : ToolViewModelBase
{
    /// <summary>解析结果（后台线程创建，UI 线程只读）。关闭工具时释放引用。</summary>
    private ExcelWorkbook? _workbook;

    private ExcelSheetItem? _selectedSheet;
    private ExcelCellSelection? _selectedCellInfo;
    private ExcelRecentItem? _selectedRecentItem;
    private string _errorText = "";
    private string _fileNameText = "未打开文件";
    private string _filePathText = "";
    private string _documentSummary = "";

    public ExcelViewerViewModel() : base(ExcelViewerConfig.ToolId)
    {
        Cfg = LoadConfig<ExcelViewerConfig>();

        // 最近列表按当前上限裁一次（手改坏配置时不会带出一长串），再把界面列表填好
        Cfg.TrimRecentToLimit();
        RefreshRecentItems();

        OpenCommand = new AsyncRelayCommand(_ => OpenFromDialogAsync());
        RefreshRecentCommand = new RelayCommand(_ => RefreshRecentItems());
        ClearRecentCommand = new RelayCommand(_ => ClearRecent());

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

    // ---------------------------------------------------------------- 工作簿 / 工作表状态

    /// <summary>工作簿里的工作表（下拉框 / 页签数据源）。</summary>
    public ObservableCollection<ExcelSheetItem> Sheets { get; } = new();

    /// <summary>当前选中的工作表；改它会让界面重建 DataGrid 列（通过 <see cref="SheetLayoutChanged"/>）。</summary>
    public ExcelSheetItem? SelectedSheet
    {
        get => _selectedSheet;
        set
        {
            if (!Set(ref _selectedSheet, value)) return;

            SelectedCellInfo = null;
            OnPropertyChanged(nameof(CurrentSheet));
            OnPropertyChanged(nameof(CurrentRows));
            OnPropertyChanged(nameof(UsedRangeText));
            OnPropertyChanged(nameof(SheetInfoText));
            OnPropertyChanged(nameof(HasSheetData));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateText));

            // 通知 View：列集合 / 列宽要跟着换（列是 DataGridTemplateColumn，只能在代码后置里重建）
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

    // ---------------------------------------------------------------- 选中单元格

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
        ?? "单击任意单元格查看地址 / 值 / 公式；拖入 .xlsx / .xlsm 文件也可以直接打开";

    /// <summary>
    /// 由 View 在 <c>SelectedCellsChanged</c> 时调用：把选中的格子交给 ViewModel 组装成状态栏文字。
    /// 传 null = 清空选择。
    /// </summary>
    public void SelectCell(ExcelCell? cell)
    {
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

    /// <summary>把解析结果挂到界面上（必须在 UI 线程调用）。</summary>
    private void ApplyWorkbook(string fullPath, ExcelWorkbook workbook)
    {
        _workbook = workbook;

        FileNameText = Path.GetFileName(fullPath);
        FilePathText = fullPath;
        DocumentSummary = $"{workbook.Sheets.Count} 个工作表"
            + (workbook.Uses1904DateSystem ? " · 1904 日期系统" : "");

        Sheets.Clear();
        for (var index = 0; index < workbook.Sheets.Count; index++)
        {
            Sheets.Add(new ExcelSheetItem(workbook.Sheets[index], index));
        }

        // 优先显示第一个可见工作表（隐藏表也能手动切过去看）
        SelectedSheet = Sheets.FirstOrDefault(sheet => sheet.IsVisible) ?? Sheets.FirstOrDefault();

        StatusText = $"已打开 {FileNameText}（{workbook.Sheets.Count} 个工作表）";
        OnPropertyChanged(nameof(HasSheetData));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    /// <summary>打开失败 / 关闭工具时把界面上的旧文档清掉。</summary>
    private void ClearDocument()
    {
        _workbook = null;
        Sheets.Clear();
        SelectedSheet = null;

        FileNameText = "未打开文件";
        FilePathText = "";
        DocumentSummary = "";
        SelectedCellInfo = null;

        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(UsedRangeText));
        OnPropertyChanged(nameof(SheetInfoText));
        OnPropertyChanged(nameof(HasSheetData));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
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

    /// <summary>关闭工具 / 被挤出缓存：释放解析结果（视图被缓存、切走时不调用这里）。</summary>
    protected override void OnClosed()
    {
        _workbook = null;
        ClearDocument();
        Log("已释放解析结果");
    }
}
