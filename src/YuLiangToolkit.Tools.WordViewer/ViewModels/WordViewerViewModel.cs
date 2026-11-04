using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Core.Storage;
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

    private WordDocument? _document;
    private IReadOnlyList<Paragraph?> _paragraphs = Array.Empty<Paragraph?>();
    private string _currentPath = "";

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

    /// <summary>大纲栏标题右侧的计数（例如「12 个标题」）。</summary>
    public string OutlineSummary { get; private set; } = "没有标题";

    /// <summary>界面上显示的错误 / 提示（空 = 不显示错误区）。</summary>
    public string ErrorText { get; private set; } = "";

    /// <summary>大纲条目（绑定到侧栏列表）。</summary>
    public ObservableCollection<WordOutlineItem> Outline { get; } = new();

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand OpenCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomResetCommand { get; }
    public RelayCommand SearchNextCommand { get; }
    public RelayCommand SearchPrevCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
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

        _searchHits = WordTextSearch.Find(_document, _searchText.Trim());
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

        // 高亮命中段落并滚过去（段落映射表由渲染器给出；表格 / 图片块没有段落，就只计数不滚）
        var paragraph = GetParagraph(hit.BlockIndex);
        if (paragraph is not null)
        {
            _highlightedParagraph = paragraph;
            _highlightedBackground = paragraph.Background;
            paragraph.Background = SearchHighlightBrush;
            paragraph.BringIntoView();
        }

        SearchStatus = $"{_searchIndex + 1} / {_searchHits.Count}";
        StatusText = $"查找「{_searchText.Trim()}」：第 {_searchIndex + 1} / {_searchHits.Count} 处";
    }

    private void ClearSearchHighlight()
    {
        if (_highlightedParagraph is null) return;
        _highlightedParagraph.Background = _highlightedBackground;
        _highlightedParagraph = null;
        _highlightedBackground = null;
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

        var rendered = WordFlowRenderer.Build(document);
        _document = document;
        _paragraphs = rendered.ParagraphByBlockIndex;
        FlowDocument = rendered.Document;
        _currentPath = path;
        OnPropertyChanged(nameof(FlowDocument));

        DocumentTitle = string.IsNullOrWhiteSpace(document.Title)
            ? Path.GetFileNameWithoutExtension(path)
            : document.Title;
        FileName = Path.GetFileName(path);
        FilePathTextChanged();
        FileSizeText = fileSize > 0 ? FileSize.Format(fileSize) : "—";
        DocumentSummary = $"{document.ParagraphCount} 段落 · {document.TableCount} 表格 · {document.ImageCount} 图片";
        HasDocument = true;
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(DocumentSummary));

        Outline.Clear();
        foreach (var heading in document.Outline) Outline.Add(new WordOutlineItem(heading));
        OutlineSummary = Outline.Count == 0 ? "没有标题" : $"{Outline.Count} 个标题";
        OnPropertyChanged(nameof(OutlineSummary));

        if (remember)
        {
            try { Cfg.LastFolder = Path.GetDirectoryName(path) ?? ""; } catch { /* 忽略非法路径 */ }
            Cfg.RememberRecent(path);
            RefreshRecentItems();
        }

        StatusText = $"已打开 {FileName}（{FileSizeText}）";
        if (document.Notes.Count > 0) StatusText += $" · 已简化 {document.Notes.Count} 处（见日志）";

        Log($"解析完成：{DocumentSummary} · 大纲 {Outline.Count} 条");
        foreach (var note in document.Notes) Log(note);
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
        _document = null;
        _paragraphs = Array.Empty<Paragraph?>();
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
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(OutlineSummary));
    }

    private void FilePathTextChanged() => OnPropertyChanged(nameof(FilePathText));

    // ---------------------------------------------------------------- 大纲 / 查找跳转

    /// <summary>把某个块对应的段落滚到可见区（大纲点击、查找跳转都走这里）。</summary>
    public void BringBlockIntoView(int blockIndex) => GetParagraph(blockIndex)?.BringIntoView();

    /// <summary>块下标 → 渲染出来的段落（越界 / 非段落块返回 null）。</summary>
    public Paragraph? GetParagraph(int blockIndex)
        => blockIndex >= 0 && blockIndex < _paragraphs.Count ? _paragraphs[blockIndex] : null;

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

    /// <summary>工具关闭：释放 FlowDocument / 模型（视图缓存时切走不会调到这里）。</summary>
    protected override void OnClosed()
    {
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
