using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.WordViewer.Export;
using YuLiangToolkit.Tools.WordViewer.Parsing;
using YuLiangToolkit.Tools.WordViewer.Rendering;

namespace YuLiangToolkit.Tools.WordViewer.ViewModels;

/// <summary>
/// 「最近打开」下拉里的一条记录（把 <see cref="RecentFileEntry"/> 变成界面能直接绑的显示信息）。
/// <para>文件已经不存在的记录仍然列出来，只标成「文件已不存在」——这样用户知道它曾经在哪儿，
/// 点它只给提示、不会报错（清掉记录用「最近」面板里的清空按钮）。</para>
/// </summary>
public sealed class WordRecentItem
{
    public WordRecentItem(string path)
    {
        Path = path;
        IsAvailable = File.Exists(path);
    }

    /// <summary>完整路径。</summary>
    public string Path { get; }

    /// <summary>文件现在还在不在（不在时界面标灰并禁止真正打开）。</summary>
    public bool IsAvailable { get; }

    /// <summary>文件已经不存在（界面标灰用）。</summary>
    public bool IsMissing => !IsAvailable;

    /// <summary>显示用的文件名。</summary>
    public string FileName
    {
        get
        {
            try { return System.IO.Path.GetFileName(Path); }
            catch { return Path; }
        }
    }

    /// <summary>所在文件夹（下拉项第二行 / 悬停提示用）。</summary>
    public string FolderText
    {
        get
        {
            try { return System.IO.Path.GetDirectoryName(Path) ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>下拉里显示的一行文字（文件不存在时带提示）。</summary>
    public string DisplayText => IsMissing ? FileName + "（文件已不存在）" : FileName;
}

/// <summary>
/// 「Word 阅读器」的 ViewModel：管**配置 / 打开文档 / 渲染结果 / 大纲 / 缩放 / 查找 / 最近打开**。
///
/// <para>后台线程只做解析（<see cref="WordDocumentParser"/>，产出纯数据）；
/// <see cref="WordFlowRenderer"/> 建 WPF 对象这一步通过 <c>Ui(...)</c> 切回 UI 线程。
/// 界面状态（<c>IsBusy</c> / <c>StatusText</c> / <c>Log</c>）都走基类，后台线程可以直接调。</para>
///
/// <para><b>只读约定</b>：这里只有「打开」没有「保存 / 编辑」，绝不写回被浏览的文件；
/// 唯一会写入磁盘的是工具自己的 <c>Config\word-viewer\config.json</c>（由配置存储自动落盘）。</para>
/// </summary>
public sealed class WordViewerViewModel : ToolViewModelBase, IFileLaunchTarget
{
    /// <summary>开发 / 截图用的环境变量：启动后自动打开这个 docx（只影响显示，不写进配置）。</summary>
    private const string AutoOpenEnvironmentVariable = "YLT_WORD_OPEN";

    /// <summary>查找命中的临时高亮色（属于文档纸面内容，可以写死）。</summary>
    private static readonly Brush SearchHighlightBrush = CreateFrozenBrush(0xFF, 0xE5, 0x8A);

    /// <summary>导出用的 HTML 图片体积上限（20 MB）；自检会单独传更小的值验证占位逻辑。</summary>
    private const long ExportImageBytesLimit = WordHtmlExporter.DefaultMaxImageBytes;

    private WordDocument? _document;
    private IReadOnlyList<Paragraph?> _paragraphs = Array.Empty<Paragraph?>();
    private IReadOnlyList<Block?> _renderedBlocks = Array.Empty<Block?>();
    private string _currentPath = "";

    // 阅读位置：界面报告的「还没落盘」的位置 + 本次排版后要恢复的滚动比例
    private WordReadingPosition? _pendingReadingPosition;
    private double? _pendingScrollRatio;

    // 查找状态：命中列表 + 当前命中下标 + 当前高亮的段落（切换命中时要还原上一个）
    private IReadOnlyList<WordSearchHit> _searchHits = Array.Empty<WordSearchHit>();
    private int _searchIndex = -1;
    private Paragraph? _highlightedParagraph;
    private Brush? _highlightedBackground;

    // YLT_WORD_OPEN：只生效一次
    private string? _pendingAutoOpenPath;
    private bool _autoOpenRequested;

    public WordViewerViewModel() : base(WordViewerConfig.ToolId)
    {
        Cfg = LoadConfig<WordViewerConfig>();

        OpenCommand = new AsyncRelayCommand(_ => OpenWithDialogAsync());
        ZoomInCommand = new RelayCommand(_ => ZoomBySteps(1));
        ZoomOutCommand = new RelayCommand(_ => ZoomBySteps(-1));
        ZoomResetCommand = new RelayCommand(_ => SetZoomPercent(WordViewerConfig.DefaultZoomPercent));
        SearchNextCommand = new RelayCommand(_ => SearchNext());
        SearchPrevCommand = new RelayCommand(_ => SearchPrev());
        ClearSearchCommand = new RelayCommand(_ => ClearSearch());
        CopyAllTextCommand = new RelayCommand(_ => CopyAllText());
        PrintCommand = new RelayCommand(_ => PrintDocument());
        ExportHtmlCommand = new AsyncRelayCommand(_ => ExportHtmlAsync());
        ExportPlainTextCommand = new AsyncRelayCommand(_ => ExportPlainTextAsync());
        ClearRecentCommand = new RelayCommand(_ => ClearRecent());
        OpenRecentCommand = new RelayCommand(parameter =>
        {
            if (parameter is WordRecentItem item) _ = OpenRecentAsync(item);
        });

        // 最近列表按当前上限裁剪一次（手改坏配置时不会带出一长串）
        Cfg.TrimRecentToLimit();
        RefreshRecentItems();

        _pendingAutoOpenPath = Environment.GetEnvironmentVariable(AutoOpenEnvironmentVariable);
        Log($"Word 阅读器就绪 · 配置文件：{Cfg.FilePath}");
        if (!string.IsNullOrWhiteSpace(_pendingAutoOpenPath))
            Log($"开发调试：启动后自动打开 {_pendingAutoOpenPath}（不会写进配置）");
    }

    // ---------------------------------------------------------------- 配置

    /// <summary>强类型配置（<c>Config\word-viewer\config.json</c>）。</summary>
    public WordViewerConfig Cfg { get; }

    /// <summary>大纲栏是否显示（直接双向绑定配置，切换后自动落盘）。</summary>
    public bool ShowOutline
    {
        get => Cfg.ShowOutline;
        set
        {
            if (Cfg.ShowOutline == value) return;
            Cfg.ShowOutline = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 工具条的「页眉页脚」开关：切换后**从已解析模型重新排版**（不重新读文件），
    /// 并尽量把阅读位置保持在原来的滚动比例。
    /// </summary>
    public bool ShowHeaderFooter
    {
        get => Cfg.ShowHeaderFooter;
        set
        {
            if (Cfg.ShowHeaderFooter == value) return;

            // 重排会换掉 FlowDocument，先记下当前滚动比例，排完再滚回去
            var ratio = ScrollRatioProvider?.Invoke();
            Cfg.ShowHeaderFooter = value;
            OnPropertyChanged();
            RelayoutDocument(ratio);
            if (_document is not null) StatusText = value ? "已显示页眉页脚" : "已隐藏页眉页脚";
        }
    }

    /// <summary>
    /// 工具条「显示批注」：控制正文里的淡黄底与 <c>[批注 N]</c> 标记（右侧批注面板另有开关）。
    /// 切换后从已解析模型重新排版、不重新读文件，并尽量保持滚动比例。
    /// </summary>
    public bool ShowComments
    {
        get => Cfg.ShowComments;
        set
        {
            if (Cfg.ShowComments == value) return;

            var ratio = ScrollRatioProvider?.Invoke();
            Cfg.ShowComments = value;
            OnPropertyChanged();
            RelayoutDocument(ratio);
            if (_document is not null) StatusText = value ? "已显示批注高亮与标记" : "已隐藏批注高亮与标记（面板仍可开关）";
        }
    }

    /// <summary>
    /// 工具条「显示修订」：开 = 插入蓝色下划线 / 删除红色删除线同时显示；
    /// 关 = 删除内容整段不显示、插入按普通文字显示。切换后重新排版，不重新解析。
    /// </summary>
    public bool ShowRevisions
    {
        get => Cfg.ShowRevisions;
        set
        {
            if (Cfg.ShowRevisions == value) return;

            var ratio = ScrollRatioProvider?.Invoke();
            Cfg.ShowRevisions = value;
            OnPropertyChanged();
            RelayoutDocument(ratio);
            if (_document is not null) StatusText = value ? "已显示修订（插入蓝 / 删除红）" : "已隐藏修订标记（删除内容不显示）";
        }
    }

    /// <summary>
    /// 右侧批注面板开关（**不落盘**，属于临时界面状态）：有批注的文档才允许打开；
    /// 面板本身的显示还要求当前文档确实有批注（见 <see cref="IsCommentsPaneOpen"/>）。
    /// </summary>
    public bool IsCommentsPaneVisible
    {
        get => _isCommentsPaneVisible;
        set
        {
            if (_isCommentsPaneVisible == value) return;
            _isCommentsPaneVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCommentsPaneOpen));
        }
    }
    private bool _isCommentsPaneVisible = true;

    /// <summary>是否记住阅读位置（默认开；工具条没有 UI，在配置文件里改）。关掉不会清已有记录。</summary>
    public bool RememberReadingPositionEnabled
    {
        get => Cfg.RememberReadingPositionEnabled;
        set
        {
            if (Cfg.RememberReadingPositionEnabled == value) return;
            Cfg.RememberReadingPositionEnabled = value;
            OnPropertyChanged();
            StatusText = value ? "已开启阅读位置记忆" : "已关闭阅读位置记忆（已有记录保留）";
        }
    }

    /// <summary>查找是否区分大小写（切换后若已有查询，立刻重算命中）。</summary>
    public bool SearchMatchCase
    {
        get => Cfg.SearchMatchCase;
        set
        {
            if (Cfg.SearchMatchCase == value) return;
            Cfg.SearchMatchCase = value;
            OnPropertyChanged();
            RebuildSearchHits();
        }
    }

    // ---------------------------------------------------------------- 阅读位置（与视图协作）

    /// <summary>
    /// 由视图登记：读取当前竖向滚动比例（0 ~ 1）。没打开文档 / 内容不足以滚动时返回 <c>null</c>。
    /// <para>为什么用委托：滚动状态在 <c>FlowDocumentScrollViewer</c> 上，ViewModel 不该直接引用控件。</para>
    /// </summary>
    public Func<double?>? ScrollRatioProvider { get; set; }

    /// <summary>
    /// 由视图登记：把某个块尽量滚到**窗口中间**（批注面板点击跳转用）。
    /// <para>为什么也走委托：居中需要 <c>FlowDocumentScrollViewer</c> 里 <c>ScrollViewer</c> 的视口高度，
    /// ViewModel 不直接引用控件；视图返回 <c>false</c> 表示没做到居中（只做了普通的 BringIntoView）。</para>
    /// </summary>
    public Func<int, bool>? CenterBlockProvider { get; set; }

    /// <summary>界面取一次「这次排版后要滚到哪儿」的比例；没有要恢复的位置时返回 <c>null</c>。</summary>
    public double? ConsumePendingScrollRatio()
    {
        var ratio = _pendingScrollRatio;
        _pendingScrollRatio = null;
        return ratio;
    }

    /// <summary>
    /// 视图在 <c>Unloaded</c>（切走工具页）时调用：把当前滚动偏移交给 ViewModel 暂存，
    /// 随后由 <see cref="FlushReadingPosition"/> 决定是否写配置。比例会夹到 0 ~ 1。
    /// </summary>
    public void NoteReadingPosition(double verticalOffset, double scrollableHeight)
    {
        if (_document is null || _currentPath.Length == 0) return;
        if (scrollableHeight <= 0.5) return;   // 内容撑不满一屏：没有有意义的「读到哪」

        var ratio = WordReadingPosition.ClampScrollRatio(verticalOffset / scrollableHeight);
        _pendingReadingPosition = new WordReadingPosition
        {
            ScrollRatio = ratio,
            SavedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// 把暂存的阅读位置写进配置；<paramref name="saveNow"/> = true 时立刻落盘（切走 / 关工具 / 换文件时用，
    /// 防止退出时丢）。配置关了「记住阅读位置」时只丢弃，**不清已有记录**。
    /// </summary>
    public void FlushReadingPosition(bool saveNow = false)
    {
        var position = _pendingReadingPosition;
        if (position is null) return;
        _pendingReadingPosition = null;

        if (_document is null || _currentPath.Length == 0) return;
        if (!Cfg.RememberReadingPositionEnabled) return;

        try
        {
            Cfg.RememberReadingPosition(_currentPath, position);
            if (saveNow) Cfg.SaveNow();
        }
        catch
        {
            // 配置写失败不影响阅读（下次滚动 / 关工具会再试）
        }
    }

    /// <summary>
    /// 把界面当前位置抓进暂存（打开另一个文件之前调，避免切换瞬间丢掉上一份的阅读位置）。
    /// </summary>
    private void CaptureCurrentReadingPosition()
    {
        if (_document is null || _currentPath.Length == 0) return;
        if (ScrollRatioProvider?.Invoke() is not { } ratio) return;

        _pendingReadingPosition = new WordReadingPosition
        {
            ScrollRatio = WordReadingPosition.ClampScrollRatio(ratio),
            SavedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// 从已解析模型重新排版（页眉页脚开关切换时用，不重新解析文件）。
    /// <paramref name="preserveRatio"/> = 重排后希望回到的滚动比例。
    /// </summary>
    private void RelayoutDocument(double? preserveRatio)
    {
        if (_document is null) return;

        ClearSearchHighlight();

        var rendered = WordFlowRenderer.Build(_document, BuildRenderOptions());

        _paragraphs = rendered.ParagraphByBlockIndex;
        _renderedBlocks = rendered.BlockByBlockIndex;
        FlowDocument = rendered.Document;
        _pendingScrollRatio = preserveRatio;
        OnPropertyChanged(nameof(FlowDocument));
        Log($"已重新排版：页眉页脚{(Cfg.ShowHeaderFooter ? "显示" : "隐藏")}"
            + $" · 批注{(Cfg.ShowComments ? "显示" : "隐藏")}"
            + $" · 修订{(Cfg.ShowRevisions ? "显示" : "隐藏")}");
    }

    /// <summary>按当前配置拼渲染选项（打开文档 / 重新排版 / 打印都用同一套开关，保证屏幕和打印一致）。</summary>
    private WordRenderOptions BuildRenderOptions() => new()
    {
        ShowHeaderFooter = Cfg.ShowHeaderFooter,
        ShowComments = Cfg.ShowComments,
        ShowRevisions = Cfg.ShowRevisions,
    };

    // ---------------------------------------------------------------- 文档状态

    /// <summary>当前文档排版结果（绑 <c>FlowDocumentScrollViewer.Document</c>）；没打开时为 null。</summary>
    public FlowDocument? FlowDocument { get; private set; }

    /// <summary>当前有没有打开文档（决定阅读区 / 空状态的显隐）。</summary>
    public bool HasDocument { get; private set; }

    /// <summary>文档标题（<c>docProps/core.xml</c> 的 title，缺失时用文件名）。</summary>
    public string DocumentTitle { get; private set; } = "";

    /// <summary>文件名（状态栏 / 工具条）。</summary>
    public string FileName { get; private set; } = "";

    /// <summary>完整路径（鼠标悬停提示用）。</summary>
    public string FilePathText => _currentPath;

    /// <summary>文件大小文本（例如「1.2 MB」）。</summary>
    public string FileSizeText { get; private set; } = "—";

    /// <summary>状态栏摘要：「N 段落 · N 表格 · N 图片」。</summary>
    public string DocumentSummary { get; private set; } = "尚未打开文档";

    /// <summary>
    /// 文档属性的一段话（标题悬停提示用）：作者 / 最后修改者 / 创建与修改时间；
    /// 文件里没有这些信息时显示文件大小与路径（至少让用户知道看的是哪个文件）。
    /// </summary>
    public string DocumentInfoText { get; private set; } = "";

    /// <summary>大纲栏标题右侧的计数（例如「12 个标题」）。</summary>
    public string OutlineSummary { get; private set; } = "没有标题";

    /// <summary>界面上显示的错误 / 提示（空 = 不显示错误区）。</summary>
    public string ErrorText { get; private set; } = "";

    /// <summary>大纲条目（绑定到侧栏列表）。</summary>
    public ObservableCollection<WordOutlineItem> Outline { get; } = new();

    /// <summary>批注面板条目（按正文出现顺序；没有批注时为空集合）。</summary>
    public ObservableCollection<WordCommentItem> Comments { get; } = new();

    /// <summary>当前文档有没有批注（决定「批注面板」开关能不能点）。</summary>
    public bool HasComments { get; private set; }

    /// <summary>批注面板这一列要不要占位：用户打开面板 + 当前文档有批注 + 打开了文档。</summary>
    public bool IsCommentsPaneOpen => _isCommentsPaneVisible && HasComments && HasDocument;

    /// <summary>批注面板标题下的计数（「5 条批注」/「没有批注」）。</summary>
    public string CommentSummary { get; private set; } = "没有批注";

    /// <summary>
    /// 状态栏的修订摘要（「修订：N 处插入 · N 处删除」）；没有修订时为空串（界面按空串隐藏）。
    /// </summary>
    public string RevisionSummary { get; private set; } = "";

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand OpenCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomResetCommand { get; }
    public RelayCommand SearchNextCommand { get; }
    public RelayCommand SearchPrevCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand CopyAllTextCommand { get; }
    public RelayCommand PrintCommand { get; }
    public AsyncRelayCommand ExportHtmlCommand { get; }
    public AsyncRelayCommand ExportPlainTextCommand { get; }
    public RelayCommand ClearRecentCommand { get; }
    public RelayCommand OpenRecentCommand { get; }

    // ---------------------------------------------------------------- 缩放

    /// <summary>
    /// 工具条上的缩放百分比输入框文字（默认「100%」）。输入 50 ~ 300 的数字（带不带 % 都行）后
    /// 按回车 / 离开输入框生效；非法输入会把框里的字还原。
    /// </summary>
    public string ZoomText
    {
        get => $"{Cfg.ZoomPercentEffective}%";
        set
        {
            var text = (value ?? "").Trim().TrimEnd('%', '％').Trim();
            if (!int.TryParse(text, out var percent))
            {
                OnPropertyChanged();   // 输入不合法：让绑定把框里的文字还原
                return;
            }

            SetZoomPercent(percent);
        }
    }

    /// <summary>当前缩放百分比（已经夹到 50 ~ 300）。</summary>
    public int ZoomPercentEffective => Cfg.ZoomPercentEffective;

    /// <summary>缩放系数（绑 <c>FlowDocumentScrollViewer.Zoom</c>）。</summary>
    public double ZoomScale => Cfg.ZoomScale;

    /// <summary>按步数缩放（工具条 +/-、Ctrl+滚轮）。</summary>
    public void ZoomBySteps(int steps) => SetZoomPercent(WordViewerConfig.StepZoomPercent(Cfg.ZoomPercentEffective, steps));

    /// <summary>设置缩放百分比（自动夹到 50 ~ 300，写进配置并通知界面）。</summary>
    public void SetZoomPercent(int percent)
    {
        var clamped = Math.Clamp(percent, WordViewerConfig.MinZoomPercent, WordViewerConfig.MaxZoomPercent);
        if (clamped == Cfg.ZoomPercentEffective)
        {
            OnPropertyChanged(nameof(ZoomText));
            return;
        }

        Cfg.ZoomPercent = clamped;
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomPercentEffective));
        OnPropertyChanged(nameof(ZoomScale));
    }

    // ---------------------------------------------------------------- 查找

    /// <summary>查找框里的文字（输入即搜：命中数量 / 高亮立刻刷新）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            var text = value ?? "";
            if (_searchText == text) return;
            _searchText = text;
            OnPropertyChanged();
            RebuildSearchHits();
        }
    }
    private string _searchText = "";

    /// <summary>查找状态（「找到 3 处」/「2 / 3」/「没有找到」）。</summary>
    public string SearchStatus
    {
        get => _searchStatus;
        private set => Set(ref _searchStatus, value);
    }
    private string _searchStatus = "";

    /// <summary>跳到下一个命中（回车）。</summary>
    public void SearchNext() => JumpToSearchHit(1);

    /// <summary>跳到上一个命中（Shift+回车）。</summary>
    public void SearchPrev() => JumpToSearchHit(-1);

    /// <summary>清空查找框与高亮（Esc / 切换文档时）。</summary>
    public void ClearSearch()
    {
        ClearSearchHighlight();
        _searchHits = Array.Empty<WordSearchHit>();
        _searchIndex = -1;
        SearchStatus = "";
        if (_searchText.Length == 0) return;
        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
    }

    private void RebuildSearchHits()
    {
        ClearSearchHighlight();
        _searchIndex = -1;

        if (_document is null || string.IsNullOrWhiteSpace(_searchText))
        {
            _searchHits = Array.Empty<WordSearchHit>();
            SearchStatus = "";
            return;
        }

        // 大小写开关直接作用于纯逻辑查找；表格单元格里的文字也会命中（InTable = true）
        _searchHits = WordTextSearch.Find(_document, _searchText.Trim(), Cfg.SearchMatchCase);
        SearchStatus = _searchHits.Count == 0 ? "没有找到" : $"找到 {_searchHits.Count} 处";
    }

    private void JumpToSearchHit(int direction)
    {
        if (_searchHits.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(_searchText)) SearchStatus = "没有找到";
            return;
        }

        _searchIndex = _searchIndex < 0
            ? (direction > 0 ? 0 : _searchHits.Count - 1)
            : ((_searchIndex + direction) % _searchHits.Count + _searchHits.Count) % _searchHits.Count;

        var hit = _searchHits[_searchIndex];
        ClearSearchHighlight();

        // 高亮命中段落并滚过去（段落映射表由渲染器给出）
        var paragraph = GetParagraph(hit.BlockIndex);
        if (paragraph is not null)
        {
            _highlightedParagraph = paragraph;
            _highlightedBackground = paragraph.Background;
            paragraph.Background = SearchHighlightBrush;
            paragraph.BringIntoView();
        }
        else if (hit.InTable && GetRenderedBlock(hit.BlockIndex) is { } tableBlock)
        {
            // 表格命中：没有整段可高亮，滚到表格块即可（格内高亮需要逐格构造 Run，收益不大）
            tableBlock.BringIntoView();
        }

        var tableHint = hit.InTable ? "（表格内命中，无法在单元格里高亮）" : "";
        SearchStatus = $"{_searchIndex + 1} / {_searchHits.Count}{tableHint}";
        StatusText = $"查找「{_searchText.Trim()}」：第 {_searchIndex + 1} / {_searchHits.Count} 处{tableHint}";
    }

    private void ClearSearchHighlight()
    {
        if (_highlightedParagraph is null) return;
        _highlightedParagraph.Background = _highlightedBackground;
        _highlightedParagraph = null;
        _highlightedBackground = null;
    }

    // ---------------------------------------------------------------- 复制全文

    /// <summary>
    /// 工具条「复制全文」：把正文纯文本（段落换行、表格按「单元格 \t」）放进剪贴板。
    /// <para>剪贴板可能被别的程序占用，<c>Clipboard.SetText</c> 会抛
    /// <see cref="COMException"/> —— 这里必须吞掉并给状态提示，不能让阅读器崩。</para>
    /// </summary>
    public void CopyAllText()
    {
        if (_document is null)
        {
            StatusText = "还没有打开文档，没有可复制的文字";
            return;
        }

        string text;
        try
        {
            text = _document.ExtractPlainText();
        }
        catch
        {
            StatusText = "复制失败：正文文字抽取失败";
            return;
        }

        if (text.Length == 0)
        {
            StatusText = "这个文档没有可复制的正文文字";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText = $"已复制全文（{text.Length} 字）";
            Log($"已复制全文：{text.Length} 字");
        }
        catch (COMException)
        {
            StatusText = "复制失败：剪贴板被其它程序占用，请稍后重试";
            Log("复制全文失败：剪贴板被占用（COMException）");
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败：{ex.Message}";
            Log("复制全文失败：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 批注面板跳转

    /// <summary>
    /// 点批注面板里的一条：滚到被批注的段落（用渲染器的「块 → 渲染元素」映射），
    /// 并让视图尽量把这一段放到窗口中间；没有正文位置时只在状态栏说明。
    /// </summary>
    public void BringCommentIntoView(WordCommentItem? item)
    {
        if (item is null) return;

        if (!item.HasAnchor)
        {
            StatusText = $"批注 {item.Number} 没有对应的正文位置（只在批注部件里有内容）";
            Log($"批注 {item.Number}：没有 commentRangeStart / commentReference，无法跳转");
            return;
        }

        BringBlockIntoView(item.BlockIndex);
        var centered = CenterBlockProvider?.Invoke(item.BlockIndex) == true;
        StatusText = $"已跳到批注 {item.Number}（{item.Author}）" + (centered ? " · 已尽量居中" : "");
    }

    // ---------------------------------------------------------------- 打印 / 导出

    /// <summary>
    /// 工具条「打印…」：弹系统 <c>PrintDialog</c>，按可打印区域**新建**一份 FlowDocument（当前开关全带上）
    /// 交给分页器打印。
    /// <para>不复用屏幕上的文档：打印是另一份版面，打印完不会影响当前阅读位置。
    /// 取消 / 没有打印机 / 抛异常都只写状态栏，不打断阅读。</para>
    /// </summary>
    public void PrintDocument()
    {
        if (_document is null)
        {
            StatusText = "还没有打开文档，无法打印";
            return;
        }

        try
        {
            var dialog = new System.Windows.Controls.PrintDialog();
            if (dialog.ShowDialog() != true)
            {
                StatusText = "已取消打印";
                return;
            }

            var width = dialog.PrintableAreaWidth;
            var height = dialog.PrintableAreaHeight;
            var printable = WordPrintLayout.CreatePrintableDocument(
                _document, BuildRenderOptions(), new Size(width, height));

            var paginator = ((IDocumentPaginatorSource)printable).DocumentPaginator;
            // 用排版后的实际页尺寸（拿不到打印区域时 WordPrintLayout 会退回 A4，这里别再写回 0）
            paginator.PageSize = new Size(printable.PageWidth, printable.PageHeight);
            dialog.PrintDocument(paginator, $"Word 阅读器 - {FileName}");

            StatusText = $"已发送到打印机：{FileName}（{paginator.PageCount} 页）";
            Log($"打印：{FileName} · {paginator.PageCount} 页 · 纸张 {printable.PageWidth:0}×{printable.PageHeight:0}");
        }
        catch (Exception ex)
        {
            // 没有打印机 / 驱动异常 / 用户取消到一半：都只提示，绝不影响当前文档
            StatusText = "打印失败：" + ex.Message;
            Log("打印失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 工具条「导出为 HTML…」：<c>SaveFileDialog</c> 选路径 → 后台生成自包含 HTML（用户文字全部转义）。
    /// 写盘失败只写状态栏，原 docx 不会被改动。
    /// </summary>
    public async Task ExportHtmlAsync()
    {
        if (_document is null)
        {
            StatusText = "还没有打开文档，没有可导出的内容";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出为 HTML",
            Filter = "HTML 文件 (*.html)|*.html|所有文件 (*.*)|*.*",
            DefaultExt = ".html",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildExportFileName(".html"),
        };
        ApplyExportFolder(dialog);

        if (dialog.ShowDialog() != true) return;
        var path = dialog.FileName;

        try
        {
            var document = _document;
            var result = await Task.Run(() => WordHtmlExporter.WriteToFile(document, path, ExportImageBytesLimit));
            var omitted = result.OmittedImageCount > 0 ? $" · 省略 {result.OmittedImageCount} 张超大图片" : "";
            StatusText = $"已导出 HTML：{path}（{FileSize.Format(result.ByteCount)}{omitted}）";
            Log($"导出 HTML：{path} · {FileSize.Format(result.ByteCount)}{omitted}");
        }
        catch (Exception ex)
        {
            StatusText = "导出 HTML 失败：" + ex.Message;
            Log("导出 HTML 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 工具条「导出为纯文本…」：复用 <see cref="WordDocument.ExtractPlainText"/>（正文，不含页眉页脚 / 脚注，
    /// 跳过修订删除内容），写 UTF-8 with BOM 的 <c>.txt</c>。
    /// </summary>
    public async Task ExportPlainTextAsync()
    {
        if (_document is null)
        {
            StatusText = "还没有打开文档，没有可导出的内容";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出为纯文本",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildExportFileName(".txt"),
        };
        ApplyExportFolder(dialog);

        if (dialog.ShowDialog() != true) return;
        var path = dialog.FileName;

        try
        {
            var document = _document;
            var chars = await Task.Run(() => WordTextExporter.Write(document, path));
            StatusText = $"已导出纯文本：{path}（{chars} 字）";
            Log($"导出纯文本：{path} · {chars} 字");
        }
        catch (Exception ex)
        {
            StatusText = "导出纯文本失败：" + ex.Message;
            Log("导出纯文本失败：" + ex.Message);
        }
    }

    /// <summary>导出对话框的默认文件名（当前文档名 + 目标扩展名；没有文档时用一个通用名）。</summary>
    private string BuildExportFileName(string extension)
    {
        var baseName = "Word 文档";
        if (_currentPath.Length > 0)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(_currentPath);
                if (!string.IsNullOrWhiteSpace(name)) baseName = name;
            }
            catch
            {
                // 非法路径：用通用名
            }
        }

        return baseName + extension;
    }

    /// <summary>导出对话框起始目录：优先当前文档所在文件夹，其次上次打开文件所在文件夹。</summary>
    private void ApplyExportFolder(SaveFileDialog dialog)
    {
        try
        {
            if (_currentPath.Length > 0)
            {
                var folder = Path.GetDirectoryName(_currentPath);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    dialog.InitialDirectory = folder;
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(Cfg.LastFolder) && Directory.Exists(Cfg.LastFolder))
                dialog.InitialDirectory = Cfg.LastFolder;
        }
        catch
        {
            // 路径非法（被删 / 没权限）：用系统默认目录
        }
    }

    // ---------------------------------------------------------------- 打开文档

    /// <summary>文件对话框选一个文档并打开（起始目录 = 上次打开文件所在文件夹）。</summary>
    public async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开 Word 文档",
            Filter = "Word 文档 (*.docx;*.docm)|*.docx;*.docm|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(Cfg.LastFolder) && Directory.Exists(Cfg.LastFolder))
                dialog.InitialDirectory = Cfg.LastFolder;
        }
        catch
        {
            // 上次的文件夹非法（被删 / 权限）：用系统默认目录
        }

        if (dialog.ShowDialog() != true) return;
        await OpenPathAsync(dialog.FileName);
    }

    /// <summary>
    /// 打开一个文档路径：后台解析 → UI 线程排版 → 更新界面状态；失败只把中文错误显示在页面上，不弹窗。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="remember">要不要记进「最近打开」并更新上次文件夹（<c>YLT_WORD_OPEN</c> 自动打开时用 false）。</param>
    public async Task OpenPathAsync(string? path, bool remember = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var filePath = path.Trim().Trim('"');

        // 旧版 .doc 是最常见的误选：给一句明确的解决方式，而不是笼统的「格式不支持」
        if (!WordDocumentParser.IsSupportedExtension(filePath))
        {
            var extension = "";
            try { extension = Path.GetExtension(filePath).ToLowerInvariant(); } catch { /* 非法路径 */ }

            ShowOpenError(extension == ".doc"
                ? "不支持旧版 .doc，请用 Word 另存为 .docx 再打开"
                : "只支持 .docx / .docm 文件" + (extension.Length > 0 ? $"（当前是「{extension}」）" : ""));
            return;
        }

        if (!File.Exists(filePath))
        {
            ShowOpenError($"文件不存在：{filePath}");
            return;
        }

        // 换文件前先把上一份的阅读位置落盘，否则切换瞬间的位置会丢
        CaptureCurrentReadingPosition();
        FlushReadingPosition(saveNow: true);

        IsBusy = true;
        ErrorText = "";
        OnPropertyChanged(nameof(ErrorText));
        ProgressText = "正在解析…";
        StatusText = $"正在打开 {Path.GetFileName(filePath)}…";
        Log($"打开：{filePath}");

        try
        {
            // 解析在后台跑（大文档不会卡住界面）；解析器读完后已经把图片字节等拷出来，包已释放
            var parsed = await Task.Run(() =>
            {
                var document = WordDocumentParser.Parse(filePath);
                long size = 0;
                try { size = new FileInfo(filePath).Length; } catch { /* 文件刚被删 / 没权限 */ }
                return (Document: document, FileSize: size);
            });

            await UiAsync(() =>
            {
                ApplyDocument(parsed.Document, filePath, parsed.FileSize, remember);
                return Task.CompletedTask;
            });
        }
        catch (OoxmlException ex)
        {
            // 解析器抛的已经是中文消息（损坏 / 格式不对 / 不是 OOXML…），直接显示
            Ui(() => ShowOpenError(ex.Message));
        }
        catch (Exception ex)
        {
            Ui(() => ShowOpenError($"打开失败：{ex.Message}"));
        }
        finally
        {
            Ui(() =>
            {
                IsBusy = false;
                ProgressText = "";
            });
        }
    }

    /// <summary>拖入文件 / 最近列表点开时调用。</summary>
    public Task OpenDroppedFileAsync(string path) => OpenPathAsync(path);

    // ---------------------------------------------------------------- 打开方式（文件关联 / 单实例转发）

    /// <summary>
    /// 主程序通过 Windows「打开方式」交过来一个文件（见 <see cref="IFileLaunchTarget"/>）：
    /// 规范成绝对路径后复用普通打开流程（<see cref="OpenPathAsync"/>），不阻塞界面。
    ///
    /// <para>路径为空 / 非法时只写状态栏与日志；文件不存在 / 格式不支持由 <see cref="OpenPathAsync"/>
    /// 统一显示到页面的错误区，<b>绝不抛异常给主程序</b>。</para>
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

        _ = OpenPathAsync(fullPath);
    }

    /// <summary>把解析好的文档接到界面上（必须运行在 UI 线程：要建 FlowDocument）。</summary>
    private void ApplyDocument(WordDocument document, string path, long fileSize, bool remember)
    {
        // 打开新文件先清掉旧文档与查找状态，避免旧高亮 / 旧大纲还挂着
        ReleaseDocumentState();

        var rendered = WordFlowRenderer.Build(document, BuildRenderOptions());

        _document = document;
        _paragraphs = rendered.ParagraphByBlockIndex;
        _renderedBlocks = rendered.BlockByBlockIndex;
        FlowDocument = rendered.Document;
        _currentPath = path;
        _pendingReadingPosition = null;

        // 恢复上次阅读位置：比例夹到 0 ~ 1，文件变短也不会跳到非法偏移；
        // 具体滚动交给视图（它要在 FlowDocumentScrollViewer 布局完成后才能按比例滚）。
        var storedPosition = Cfg.RememberReadingPositionEnabled ? Cfg.FindReadingPosition(path) : null;
        _pendingScrollRatio = storedPosition is null
            ? null
            : WordReadingPosition.ClampScrollRatio(storedPosition.ScrollRatio);

        OnPropertyChanged(nameof(FlowDocument));

        DocumentTitle = string.IsNullOrWhiteSpace(document.Title)
            ? Path.GetFileNameWithoutExtension(path)
            : document.Title;
        FileName = Path.GetFileName(path);
        FilePathTextChanged();
        FileSizeText = fileSize > 0 ? FileSize.Format(fileSize) : "—";
        DocumentSummary = $"{document.ParagraphCount} 段落 · {document.TableCount} 表格 · {document.ImageCount} 图片";
        DocumentInfoText = BuildDocumentInfoText(document, path);
        HasDocument = true;
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(DocumentInfoText));

        Outline.Clear();
        foreach (var heading in document.Outline) Outline.Add(new WordOutlineItem(heading));
        OutlineSummary = Outline.Count == 0 ? "没有标题" : $"{Outline.Count} 个标题";
        OnPropertyChanged(nameof(OutlineSummary));

        // 批注面板 / 修订摘要：直接从已解析模型取，不参与段落计数
        Comments.Clear();
        foreach (var comment in document.Comments) Comments.Add(new WordCommentItem(comment));
        HasComments = Comments.Count > 0;
        CommentSummary = HasComments ? $"{Comments.Count} 条批注" : "没有批注";
        RevisionSummary = BuildRevisionSummary(document);
        OnPropertyChanged(nameof(HasComments));
        OnPropertyChanged(nameof(Comments));
        OnPropertyChanged(nameof(CommentSummary));
        OnPropertyChanged(nameof(RevisionSummary));
        OnPropertyChanged(nameof(IsCommentsPaneOpen));

        if (remember)
        {
            try { Cfg.LastFolder = Path.GetDirectoryName(path) ?? ""; } catch { /* 忽略非法路径 */ }
            Cfg.RememberRecent(path);
            RefreshRecentItems();
        }

        StatusText = $"已打开 {FileName}（{FileSizeText}）";
        if (document.Notes.Count > 0) StatusText += $" · 已简化 {document.Notes.Count} 处（见日志）";

        var extraSummary = new List<string>();
        if (document.HasHeader) extraSummary.Add("页眉");
        if (document.HasFooter) extraSummary.Add("页脚");
        if (document.Footnotes.Count > 0) extraSummary.Add($"脚注 {document.Footnotes.Count} 条");
        if (document.Endnotes.Count > 0) extraSummary.Add($"尾注 {document.Endnotes.Count} 条");
        if (document.HasComments) extraSummary.Add($"批注 {document.Comments.Count} 条");
        if (document.HasRevisions) extraSummary.Add(RevisionSummary);

        Log($"解析完成：{DocumentSummary} · 大纲 {Outline.Count} 条"
            + (extraSummary.Count > 0 ? " · " + string.Join(" · ", extraSummary) : ""));
        foreach (var note in document.Notes) Log(note);
    }

    /// <summary>拼状态栏 / 摘要里的修订说明：没有修订时返回空串（界面按空串隐藏）。</summary>
    private static string BuildRevisionSummary(WordDocument document)
        => document.HasRevisions
            ? $"修订：{document.InsertedRunCount} 处插入 · {document.DeletedRunCount} 处删除"
            : "";

    /// <summary>
    /// 拼标题的悬停提示：有文档属性就显示作者 / 修改者 / 时间，没有就至少显示文件大小与路径。
    /// 用换行而不是长句，鼠标悬停时更好读。
    /// </summary>
    private string BuildDocumentInfoText(WordDocument document, string path)
    {
        var lines = new List<string>();
        var properties = new List<string>();

        if (!string.IsNullOrWhiteSpace(document.Author)) properties.Add($"作者：{document.Author}");
        if (!string.IsNullOrWhiteSpace(document.LastModifiedBy)) properties.Add($"最后修改者：{document.LastModifiedBy}");
        if (document.CreatedUtc is { } created)
            properties.Add($"创建时间：{created.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (document.ModifiedUtc is { } modified)
            properties.Add($"修改时间：{modified.ToLocalTime():yyyy-MM-dd HH:mm}");

        if (properties.Count > 0) lines.Add(string.Join(" · ", properties));
        lines.Add($"大小：{FileSizeText}");
        lines.Add(path);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>打开失败：错误显示在页面错误区 + 状态栏，不弹 MessageBox。</summary>
    private void ShowOpenError(string message)
    {
        ErrorText = message;
        OnPropertyChanged(nameof(ErrorText));
        StatusText = message;
        Log("打开失败：" + message);
    }

    /// <summary>释放当前文档的 WPF 对象与查找状态（关工具 / 打开新文档时）。</summary>
    private void ReleaseDocumentState()
    {
        ClearSearchHighlight();
        _searchHits = Array.Empty<WordSearchHit>();
        _searchIndex = -1;
        SearchStatus = "";
        if (_searchText.Length > 0)
        {
            _searchText = "";
            OnPropertyChanged(nameof(SearchText));
        }

        Outline.Clear();
        OutlineSummary = "没有标题";
        Comments.Clear();
        HasComments = false;
        CommentSummary = "没有批注";
        RevisionSummary = "";
        _document = null;
        _paragraphs = Array.Empty<Paragraph?>();
        _renderedBlocks = Array.Empty<Block?>();
        _pendingReadingPosition = null;
        _pendingScrollRatio = null;
        FlowDocument = null;
        OnPropertyChanged(nameof(FlowDocument));
        HasDocument = false;
        OnPropertyChanged(nameof(HasDocument));
        _currentPath = "";
        DocumentTitle = "";
        FileName = "";
        FilePathTextChanged();
        FileSizeText = "—";
        DocumentSummary = "尚未打开文档";
        DocumentInfoText = "";
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(DocumentInfoText));
        OnPropertyChanged(nameof(OutlineSummary));
        OnPropertyChanged(nameof(HasComments));
        OnPropertyChanged(nameof(Comments));
        OnPropertyChanged(nameof(CommentSummary));
        OnPropertyChanged(nameof(RevisionSummary));
        OnPropertyChanged(nameof(IsCommentsPaneOpen));
    }

    private void FilePathTextChanged() => OnPropertyChanged(nameof(FilePathText));

    // ---------------------------------------------------------------- 大纲 / 查找跳转

    /// <summary>把某个块滚到可见区（大纲点击、查找跳转都走这里）；段落优先，表格等块也能滚。</summary>
    public void BringBlockIntoView(int blockIndex)
    {
        if (GetParagraph(blockIndex) is { } paragraph)
        {
            paragraph.BringIntoView();
            return;
        }

        GetRenderedBlock(blockIndex)?.BringIntoView();
    }

    /// <summary>块下标 → 渲染出来的段落（越界 / 非段落块返回 null）。</summary>
    public Paragraph? GetParagraph(int blockIndex)
        => blockIndex >= 0 && blockIndex < _paragraphs.Count ? _paragraphs[blockIndex] : null;

    /// <summary>块下标 → 渲染出来的块（段落 / 表格 / 图片容器；越界返回 null）。</summary>
    public Block? GetRenderedBlock(int blockIndex)
        => blockIndex >= 0 && blockIndex < _renderedBlocks.Count ? _renderedBlocks[blockIndex] : null;

    // ---------------------------------------------------------------- 最近打开

    /// <summary>「最近」下拉的数据源（文件已不存在的也会列出来，标成灰色）。</summary>
    public ObservableCollection<WordRecentItem> RecentItems { get; } = new();

    /// <summary>最近记录条数提示。</summary>
    public string RecentSummary => RecentItems.Count == 0 ? "暂无记录" : $"{RecentItems.Count} 条";

    /// <summary>
    /// 「最近」下拉的选中项：选中即打开，随后把选中项还原成 null（这样才能重复点同一个文件）。
    /// </summary>
    public WordRecentItem? SelectedRecent
    {
        get => null;
        set
        {
            if (value is null) return;
            OnPropertyChanged();   // 立刻还原 ComboBox 的显示
            _ = OpenRecentAsync(value);
        }
    }

    /// <summary>点「最近」里的一条：文件还在就打开，不在就给提示（不弹窗）。</summary>
    public async Task OpenRecentAsync(WordRecentItem item)
    {
        if (!item.IsAvailable)
        {
            StatusText = $"文件已不存在：{item.Path}";
            Log($"最近打开的文件已被移动或删除：{item.Path}");
            return;
        }

        await OpenPathAsync(item.Path);
    }

    private void ClearRecent()
    {
        Cfg.ClearRecent();
        RefreshRecentItems();
        StatusText = "已清空最近打开记录（不会删除本地文件）";
    }

    private void RefreshRecentItems()
    {
        RecentItems.Clear();
        foreach (var entry in Cfg.RecentEntries ?? new List<RecentFileEntry>())
        {
            var path = RecentFiles.NormalizePath(entry.Path);
            if (path.Length == 0) continue;
            RecentItems.Add(new WordRecentItem(path));
        }

        OnPropertyChanged(nameof(RecentSummary));
    }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>
    /// 开发 / 截图用：如果设置了 <c>YLT_WORD_OPEN</c>，视图第一次 Loaded 时自动打开它。
    /// 只生效一次，且 <b>不写进配置</b>（不记最近、不改上次文件夹）。
    /// </summary>
    public void TryOpenPendingAutoOpen()
    {
        if (_autoOpenRequested) return;
        _autoOpenRequested = true;

        var path = _pendingAutoOpenPath;
        _pendingAutoOpenPath = null;
        if (string.IsNullOrWhiteSpace(path)) return;
        _ = OpenPathAsync(path, remember: false);
    }

    /// <summary>
    /// 工具关闭（被挤出视图缓存 / 退出程序）：先把最后阅读位置落盘，再释放 FlowDocument / 模型。
    /// <para>视图缓存时切走不会调到这里，那种情况由视图 <c>Unloaded</c> 负责保存。</para>
    /// </summary>
    protected override void OnClosed()
    {
        CaptureCurrentReadingPosition();
        FlushReadingPosition(saveNow: true);
        ReleaseDocumentState();
        RecentItems.Clear();
        Log("已释放文档资源");
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
