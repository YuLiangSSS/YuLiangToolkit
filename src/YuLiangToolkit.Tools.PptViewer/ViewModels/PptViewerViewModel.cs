using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.PptViewer.Parsing;
using YuLiangToolkit.Tools.PptViewer.Rendering;
using YuLiangToolkit.Tools.PptViewer.Views;

namespace YuLiangToolkit.Tools.PptViewer.ViewModels;

/// <summary>
/// 「PPT 阅读器」的 ViewModel：管**配置 / 打开演示文稿 / 幻灯片列表 / 当前页 / 缩放 / 备注 / 查找 /
/// 最近打开 / 阅读位置 / 全屏放映**。
///
/// <para>后台线程只做解析（<see cref="PptDocumentParser"/>，产出纯数据模型）；
/// 渲染（建 WPF <see cref="DrawingImage"/>）在 UI 线程做，并把最近渲染过的页放进小 LRU 缓存，
/// 让「主视图 + 左侧缩略图」共用同一份矢量绘制结果（见 <see cref="GetSlideImage"/>）。</para>
///
/// <para><b>只读约定</b>：只有「打开」没有「保存 / 编辑」，绝不写回被浏览的文件；
/// 唯一会写入磁盘的是工具自己的 <c>Config\ppt-viewer\config.json</c>（由配置存储自动落盘）。</para>
/// </summary>
public sealed class PptViewerViewModel : ToolViewModelBase, IFileLaunchTarget
{
    /// <summary>开发 / 截图用的环境变量：启动后自动打开这个 pptx（只影响显示，不写进配置）。</summary>
    private const string AutoOpenEnvironmentVariable = "YLT_PPT_OPEN";

    /// <summary>翻页后延迟多久把阅读位置写进配置（防抖，避免快速翻页时每个键都写盘）。</summary>
    private const int ReadingPositionDebounceMilliseconds = 500;

    /// <summary>主视图 / 缩略图共用的渲染缓存最多放几页（超出淘汰最久没用的）。</summary>
    private const int SlideImageCacheLimit = 24;

    /// <summary>解析结果（后台线程创建，UI 线程只读）。关闭工具 / 打开新文件时释放引用。</summary>
    private PptPresentation? _document;

    /// <summary>当前演示文稿的完整路径（阅读位置按它记；没打开时为空串）。</summary>
    private string _currentPath = "";

    /// <summary>当前页（缩略图列表的选中项）。</summary>
    private PptSlideItem? _currentSlide;

    /// <summary>当前页包成的 DrawingImage（绑定到中间的大图）。</summary>
    private DrawingImage? _slideImage;

    /// <summary>最近渲染过的页（幻灯片对象 → DrawingImage）+ 使用顺序（LRU）。</summary>
    private readonly Dictionary<PptSlide, DrawingImage> _slideImageCache = new();
    private readonly LinkedList<PptSlide> _slideImageOrder = new();

    /// <summary>翻页防抖计时器（读到第几页 → 写配置）。</summary>
    private readonly DispatcherTimer _readingPositionTimer;

    /// <summary>恢复文档 / 清空文档期间不要因为「换当前页」而把旧位置写坏。</summary>
    private bool _suppressReadingPosition;

    /// <summary>开发 / 截图用：<c>YLT_PPT_OPEN</c> 指定的路径（只自动打开一次）。</summary>
    private string? _pendingAutoOpenPath;
    private bool _autoOpenRequested;

    // ---------- 查找状态 ----------
    private string _searchText = "";
    private string _searchSummary = "";
    private IReadOnlyList<PptSearchHit> _searchHits = Array.Empty<PptSearchHit>();
    private int _searchIndex = -1;

    public PptViewerViewModel() : base(PptViewerConfig.ToolId)
    {
        Cfg = LoadConfig<PptViewerConfig>();

        // 最近列表按当前上限裁一次（手改坏配置时不会带出一长串），再把界面列表填好
        Cfg.TrimRecentToLimit();
        RefreshRecentItems();

        OpenCommand = new AsyncRelayCommand(_ => OpenWithDialogAsync(), _ => !IsBusy);
        OpenRecentCommand = new RelayCommand(parameter =>
        {
            if (parameter is PptRecentItem item) _ = OpenRecentAsync(item);
        });
        ClearRecentCommand = new RelayCommand(_ => ClearRecent());
        NextSlideCommand = new RelayCommand(_ => StepSlide(1), _ => HasDocument);
        PrevSlideCommand = new RelayCommand(_ => StepSlide(-1), _ => HasDocument);
        FirstSlideCommand = new RelayCommand(_ => SetCurrentSlideByIndex(1), _ => HasDocument);
        LastSlideCommand = new RelayCommand(_ => SetCurrentSlideByIndex(SlideCount), _ => HasDocument);
        ZoomInCommand = new RelayCommand(_ => ZoomBySteps(1));
        ZoomOutCommand = new RelayCommand(_ => ZoomBySteps(-1));
        ZoomResetCommand = new RelayCommand(_ => SetZoomPercent(PptViewerConfig.DefaultZoomPercent));
        SearchNextCommand = new RelayCommand(_ => SearchNext(), _ => HasDocument);
        SearchPrevCommand = new RelayCommand(_ => SearchPrev(), _ => HasDocument);
        ClearSearchCommand = new RelayCommand(_ => ClearSearch());
        CopySlideTextCommand = new RelayCommand(_ => CopySlideText(), _ => HasDocument);
        StartPresentationCommand = new RelayCommand(_ => StartPresentation(), _ => HasDocument);

        _readingPositionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ReadingPositionDebounceMilliseconds),
        };
        _readingPositionTimer.Tick += OnReadingPositionTimerTick;

        _pendingAutoOpenPath = Environment.GetEnvironmentVariable(AutoOpenEnvironmentVariable);
        Log($"PPT 阅读器就绪 · 配置文件：{Cfg.FilePath}");
        if (!string.IsNullOrWhiteSpace(_pendingAutoOpenPath))
            Log($"开发调试：启动后自动打开 {_pendingAutoOpenPath}（不会写进配置）");
    }

    // ---------------------------------------------------------------- 配置与命令

    /// <summary>强类型配置（<c>Config\ppt-viewer\config.json</c>）。</summary>
    public PptViewerConfig Cfg { get; }

    /// <summary>打开文件对话框（工具条「打开演示文稿…」/ Ctrl+O）。</summary>
    public AsyncRelayCommand OpenCommand { get; }

    /// <summary>点「最近」下拉里的一条（参数是 <see cref="PptRecentItem"/>）。</summary>
    public RelayCommand OpenRecentCommand { get; }

    /// <summary>清空最近打开记录（只删记录，不删本地文件）。</summary>
    public RelayCommand ClearRecentCommand { get; }

    /// <summary>下一张幻灯片（PageDown / ↓ / → / 空格）。</summary>
    public RelayCommand NextSlideCommand { get; }

    /// <summary>上一张幻灯片（PageUp / ↑ / ←）。</summary>
    public RelayCommand PrevSlideCommand { get; }

    /// <summary>第一张（Home）。</summary>
    public RelayCommand FirstSlideCommand { get; }

    /// <summary>最后一张（End）。</summary>
    public RelayCommand LastSlideCommand { get; }

    /// <summary>放大 10%（Ctrl+滚轮 / Ctrl++）。</summary>
    public RelayCommand ZoomInCommand { get; }

    /// <summary>缩小 10%（Ctrl+滚轮 / Ctrl+-）。</summary>
    public RelayCommand ZoomOutCommand { get; }

    /// <summary>恢复 100% 缩放。</summary>
    public RelayCommand ZoomResetCommand { get; }

    /// <summary>跳到下一个查找命中（回车）。</summary>
    public RelayCommand SearchNextCommand { get; }

    /// <summary>跳到上一个查找命中（Shift+回车）。</summary>
    public RelayCommand SearchPrevCommand { get; }

    /// <summary>清空查找（Esc）。</summary>
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>复制本页文字。</summary>
    public RelayCommand CopySlideTextCommand { get; }

    /// <summary>从当前页开始全屏放映（F5）。</summary>
    public RelayCommand StartPresentationCommand { get; }

    // ---------------------------------------------------------------- 视图开关（双向绑配置）

    /// <summary>是否「适应窗口」：开 = 按窗口大小铺满一张幻灯片；关 = 用 <see cref="ZoomScale"/> 手工缩放。</summary>
    public bool FitToWindow
    {
        get => Cfg.FitToWindow;
        set
        {
            if (Cfg.FitToWindow == value) return;
            Cfg.FitToWindow = value;
            OnPropertyChanged();
            NotifySlideDisplayChanged();
            StatusText = value ? "已切换为「适应窗口」" : $"已切换到手工缩放（{Cfg.ZoomPercentEffective}%）";
        }
    }

    /// <summary>左侧缩略图栏是否显示（写配置，下次打开保持）。</summary>
    public bool ShowThumbnails
    {
        get => Cfg.ShowThumbnails;
        set
        {
            if (Cfg.ShowThumbnails == value) return;
            Cfg.ShowThumbnails = value;
            OnPropertyChanged();
            StatusText = value ? "已显示缩略图" : "已隐藏缩略图";
        }
    }

    /// <summary>右侧备注栏开关（写配置；当前页没有备注时按钮禁用，见 <see cref="CanShowNotes"/>）。</summary>
    public bool ShowNotes
    {
        get => Cfg.ShowNotes;
        set
        {
            if (Cfg.ShowNotes == value) return;
            Cfg.ShowNotes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotesPaneOpen));
            OnPropertyChanged(nameof(CanShowNotes));
            StatusText = value ? "已显示备注栏" : "已隐藏备注栏";
        }
    }

    /// <summary>备注栏这一列要不要占位：打开了开关 + 已经打开演示文稿。</summary>
    public bool IsNotesPaneOpen => HasDocument && Cfg.ShowNotes;

    /// <summary>当前页有没有演讲者备注。</summary>
    public bool HasCurrentNotes => CurrentSlide?.Slide.HasNotes == true;

    /// <summary>
    /// 「备注」开关能不能点：没有打开演示文稿时不能；打开时只要当前页有备注、
    /// 或者备注栏已经开着（方便关掉）都允许。
    /// </summary>
    public bool CanShowNotes => HasDocument && (HasCurrentNotes || Cfg.ShowNotes);

    /// <summary>是否记住「每份演示文稿看到第几页」（默认开；关掉不会清已有记录）。</summary>
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

    // ---------------------------------------------------------------- 文档状态

    /// <summary>当前演示文稿的解析结果（没有打开时为 null）。</summary>
    public PptPresentation? Presentation => _document;

    /// <summary>当前有没有打开演示文稿（决定幻灯片区 / 空状态的显隐）。</summary>
    public bool HasDocument => _document is not null;

    /// <summary>文档标题（docProps 的 title，缺失时用文件名）。</summary>
    public string DocumentTitle { get; private set; } = "";

    /// <summary>文件名（状态栏 / 工具条）。</summary>
    public string FileName { get; private set; } = "";

    /// <summary>完整路径（悬停提示用）。</summary>
    public string FilePathText => _currentPath;

    /// <summary>文件大小文本（例如「12.3 MB」）。</summary>
    public string FileSizeText { get; private set; } = "—";

    /// <summary>状态栏摘要：「12 张幻灯片 · 隐藏 2 页 · 含备注 5 页」。</summary>
    public string DocumentSummary { get; private set; } = "尚未打开演示文稿";

    /// <summary>文档属性的悬停提示（作者 / 修改时间 / 尺寸 / 大小 / 路径）。</summary>
    public string DocumentInfo { get; private set; } = "";

    /// <summary>界面上显示的错误 / 提示（空 = 不显示错误区）。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }
    private string _errorMessage = "";

    /// <summary>幻灯片总数。</summary>
    public int SlideCount => _document?.SlideCount ?? 0;

    /// <summary>这份演示文稿里有没有幻灯片（空文件 / 解析器还没覆盖的部件时显示提示）。</summary>
    public bool HasSlides => SlideCount > 0;

    /// <summary>当前页页码（1 开始；没有打开时为 0）。</summary>
    public int CurrentIndex => CurrentSlide?.Index ?? 0;

    /// <summary>工具条 / 状态栏的页码文本「3 / 12」。</summary>
    public string SlideIndicator => HasDocument ? $"{CurrentIndex} / {SlideCount}" : "0 / 0";

    /// <summary>左侧缩略图列表的数据源。</summary>
    public ObservableCollection<PptSlideItem> Slides { get; } = new();

    /// <summary>当前幻灯片；设置它就会换主视图、备注栏和缩略图高亮。</summary>
    public PptSlideItem? CurrentSlide
    {
        get => _currentSlide;
        set
        {
            var previous = _currentSlide;
            if (!Set(ref _currentSlide, value)) return;

            if (previous is not null) previous.IsCurrent = false;
            if (value is not null) value.IsCurrent = true;

            SlideImage = value is null ? null : GetSlideImage(value.Slide);

            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(SlideIndicator));
            OnPropertyChanged(nameof(NotesText));
            OnPropertyChanged(nameof(HasCurrentNotes));
            OnPropertyChanged(nameof(CanShowNotes));
            OnPropertyChanged(nameof(IsNotesPaneOpen));

            if (!_suppressReadingPosition && HasDocument) ScheduleReadingPositionSave();

            if (HasDocument && value is not null)
            {
                StatusText = $"第 {value.Index} 页 / 共 {SlideCount} 页"
                    + (value.IsHidden ? "（隐藏页）" : "")
                    + (value.HasNotes ? " · 有备注" : "");
            }
        }
    }

    /// <summary>当前页的绘图对象（绑定到中间的大图）。</summary>
    public DrawingImage? SlideImage
    {
        get => _slideImage;
        private set
        {
            if (ReferenceEquals(_slideImage, value)) return;
            _slideImage = value;
            OnPropertyChanged();
        }
    }

    /// <summary>中间大图的拉伸方式：适应窗口 = Uniform；手工缩放 = Fill（宽高已按比例算好）。</summary>
    public Stretch SlideStretch => FitToWindow ? Stretch.Uniform : Stretch.Fill;

    /// <summary>
    /// 手工缩放时的显示宽度（DIP）；适应窗口时是 <see cref="double.NaN"/>（= 由布局决定）。
    /// </summary>
    public double SlideDisplayWidth
    {
        get
        {
            if (FitToWindow || _document is null) return double.NaN;
            return Math.Max(1, _document.WidthDip * Cfg.ZoomScale);
        }
    }

    /// <summary>手工缩放时的显示高度（DIP）；适应窗口时是 <see cref="double.NaN"/>。</summary>
    public double SlideDisplayHeight
    {
        get
        {
            if (FitToWindow || _document is null) return double.NaN;
            return Math.Max(1, _document.HeightDip * Cfg.ZoomScale);
        }
    }

    /// <summary>当前页的演讲者备注（纯文本；没有备注时为空串）。</summary>
    public string NotesText => CurrentSlide?.Slide.Notes ?? "";

    // ---------------------------------------------------------------- 缩放

    /// <summary>工具条缩放输入框里的文字（「120%」）；手动输入数字后按回车生效。</summary>
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

    /// <summary>当前生效的缩放百分比（已经夹到 25 ~ 400；赋值等同 <see cref="SetZoomPercent"/>）。</summary>
    public int ZoomPercent
    {
        get => Cfg.ZoomPercentEffective;
        set => SetZoomPercent(value);
    }

    /// <summary>缩放系数（100% → 1.0，200% → 2.0）。</summary>
    public double ZoomScale => Cfg.ZoomScale;

    /// <summary>按步数缩放（工具条 +/-、Ctrl+滚轮）。</summary>
    public void ZoomBySteps(int steps)
        => SetZoomPercent(PptViewerConfig.StepZoomPercent(Cfg.ZoomPercentEffective, steps));

    /// <summary>
    /// 设置缩放百分比（夹到 25 ~ 400）；手动缩放会自动关掉「适应窗口」。
    /// </summary>
    public void SetZoomPercent(int percent)
    {
        var clamped = Math.Clamp(percent, PptViewerConfig.MinZoomPercent, PptViewerConfig.MaxZoomPercent);
        var changed = clamped != Cfg.ZoomPercentEffective || Cfg.FitToWindow;

        Cfg.ZoomPercent = clamped;
        if (Cfg.FitToWindow)
        {
            Cfg.FitToWindow = false;
            OnPropertyChanged(nameof(FitToWindow));
        }

        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomPercent));
        OnPropertyChanged(nameof(ZoomScale));
        NotifySlideDisplayChanged();

        if (changed) StatusText = $"缩放 {clamped}%";
    }

    /// <summary>缩放 / 适应窗口变化后，把所有跟显示尺寸有关的属性通知一遍。</summary>
    private void NotifySlideDisplayChanged()
    {
        OnPropertyChanged(nameof(SlideStretch));
        OnPropertyChanged(nameof(SlideDisplayWidth));
        OnPropertyChanged(nameof(SlideDisplayHeight));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomPercent));
        OnPropertyChanged(nameof(ZoomScale));
    }

    // ---------------------------------------------------------------- 幻灯片导航

    /// <summary>按 1 基页号选当前页（越界自动夹到第一 / 最后一页）。</summary>
    public void SetCurrentSlideByIndex(int slideIndex)
    {
        if (Slides.Count == 0) return;

        var clamped = Math.Clamp(slideIndex, 1, Slides.Count);
        CurrentSlide = Slides[clamped - 1];
    }

    /// <summary>
    /// 按模型里的「页号」选当前页（查找命中 / 放映回来时用；页号和列表位置不一致时也能找对）。
    /// 找不到对应页号时退回按位置夹取。
    /// </summary>
    public void SetCurrentSlideByPageIndex(int pageIndex)
    {
        if (Slides.Count == 0) return;

        var item = Slides.FirstOrDefault(slide => slide.Index == pageIndex);
        if (item is not null)
        {
            CurrentSlide = item;
            return;
        }

        SetCurrentSlideByIndex(pageIndex);
    }

    /// <summary>上一页 / 下一页（<paramref name="direction"/> = 1 或 -1）。</summary>
    private void StepSlide(int direction)
    {
        if (Slides.Count == 0) return;

        var position = CurrentSlide is null ? 0 : Slides.IndexOf(CurrentSlide);
        if (position < 0) position = 0;

        var target = Math.Clamp(position + direction, 0, Slides.Count - 1);
        CurrentSlide = Slides[target];
    }

    // ---------------------------------------------------------------- 渲染缓存（主视图 + 缩略图共用）

    /// <summary>
    /// 取某一页的矢量绘制结果：命中 LRU 缓存就直接复用，否则现渲染一页并放进缓存。
    /// 缩略图列表也走这里，所以「缩略图里看过的页」切到主视图时不用重画。
    /// </summary>
    private DrawingImage? GetSlideImage(PptSlide slide)
    {
        if (_document is null || slide is null) return null;

        if (_slideImageCache.TryGetValue(slide, out var cached))
        {
            TouchSlideImageCache(slide);
            return cached;
        }

        try
        {
            var image = PptSlideRenderer.CreateImage(_document, slide);
            _slideImageCache[slide] = image;
            _slideImageOrder.AddLast(slide);
            TrimSlideImageCache();
            return image;
        }
        catch (Exception ex)
        {
            // 单页渲染失败时只影响这一页，不要让整个文件打不开
            Log($"渲染第 {slide.Index} 页失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>把某页标记成「最近使用过」（LRU 挪到队尾）。</summary>
    private void TouchSlideImageCache(PptSlide slide)
    {
        var node = _slideImageOrder.Find(slide);
        if (node is null) return;

        _slideImageOrder.Remove(node);
        _slideImageOrder.AddLast(node);
    }

    /// <summary>缓存超过上限时淘汰最久没用的页。</summary>
    private void TrimSlideImageCache()
    {
        while (_slideImageOrder.Count > SlideImageCacheLimit && _slideImageOrder.First is { } oldest)
        {
            _slideImageOrder.RemoveFirst();
            _slideImageCache.Remove(oldest.Value);
        }
    }

    // ---------------------------------------------------------------- 查找

    /// <summary>查找框里的文字（输入即搜：命中数 / 摘要立刻刷新）。</summary>
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

    /// <summary>查找状态摘要（「找到 3 处 / 2 页」/「没找到…」/ 空）。</summary>
    public string SearchSummary
    {
        get => _searchSummary;
        private set => Set(ref _searchSummary, value);
    }

    /// <summary>查找命中列表（页号 + 来源 + 片段；留给以后做结果列表，界面现在只显示摘要）。</summary>
    public ObservableCollection<PptSearchHitItem> SearchHits { get; } = new();

    /// <summary>原始命中记录（纯逻辑层数据结构，自检 / 调试用）。</summary>
    public IReadOnlyList<PptSearchHit> SearchHitRecords => _searchHits;

    /// <summary>当前命中在 <see cref="SearchHitRecords"/> 里的下标（-1 = 还没开始跳）。</summary>
    public int CurrentSearchHitIndex => _searchIndex;

    /// <summary>有没有查找命中（界面可以据此显示小三角 / 结果入口）。</summary>
    public bool HasSearchHits => SearchHits.Count > 0;

    /// <summary>
    /// 查找结果浮层是否展开（界面 Popup 双向绑定）：有命中且输入了内容时自动展开，
    /// 点一条命中 / 清空查找 / 点浮层外面时收起。
    /// </summary>
    public bool IsSearchResultOpen
    {
        get => _isSearchResultOpen;
        set => Set(ref _isSearchResultOpen, value);
    }
    private bool _isSearchResultOpen;

    /// <summary>
    /// 查找结果浮层里选中的一条：选中即跳到对应页并收起浮层，然后把选中项还原成 null
    /// （这样同一条结果可以重复点）。
    /// </summary>
    public PptSearchHitItem? SelectedSearchHit
    {
        get => _selectedSearchHit;
        set
        {
            if (!Set(ref _selectedSearchHit, value)) return;
            if (value is null) return;

            var index = SearchHits.IndexOf(value);
            if (index >= 0) ApplySearchHit(index);

            IsSearchResultOpen = false;
            _selectedSearchHit = null;
            OnPropertyChanged(nameof(SelectedSearchHit));
        }
    }
    private PptSearchHitItem? _selectedSearchHit;

    /// <summary>跳到下一个命中（回车）。</summary>
    public void SearchNext() => JumpToSearchHit(1);

    /// <summary>跳到上一个命中（Shift+回车）。</summary>
    public void SearchPrev() => JumpToSearchHit(-1);

    /// <summary>清空查找框、命中列表与摘要（Esc / 切换文档时）。</summary>
    public void ClearSearch()
    {
        _searchHits = Array.Empty<PptSearchHit>();
        _searchIndex = -1;
        SearchHits.Clear();
        SearchSummary = "";
        IsSearchResultOpen = false;
        _selectedSearchHit = null;
        OnPropertyChanged(nameof(SelectedSearchHit));
        OnPropertyChanged(nameof(HasSearchHits));

        if (_searchText.Length == 0) return;
        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
    }

    /// <summary>按当前查询重新算命中（输入 / 大小写开关 / 打开文档后调用）。</summary>
    private void RebuildSearchHits()
    {
        _searchIndex = -1;
        _selectedSearchHit = null;
        OnPropertyChanged(nameof(SelectedSearchHit));
        SearchHits.Clear();
        OnPropertyChanged(nameof(HasSearchHits));

        if (_document is null || string.IsNullOrWhiteSpace(_searchText))
        {
            _searchHits = Array.Empty<PptSearchHit>();
            SearchSummary = "";
            IsSearchResultOpen = false;
            return;
        }

        var query = _searchText.Trim();
        _searchHits = PptTextSearch.Search(_document, query, Cfg.SearchMatchCase);
        foreach (var hit in _searchHits) SearchHits.Add(new PptSearchHitItem(hit));

        SearchSummary = PptTextSearch.DescribeHits(_searchHits, query);
        OnPropertyChanged(nameof(HasSearchHits));

        // 有命中就把结果浮层展开（界面上「输入即搜」）；没有命中时收起
        IsSearchResultOpen = _searchHits.Count > 0;

        // 命中在备注里时顺手打开备注栏，让用户能看到上下文
        if (_searchHits.Any(hit => hit.Source == PptSearchSource.Notes) && !Cfg.ShowNotes && HasCurrentNotes)
        {
            Cfg.ShowNotes = true;
            OnPropertyChanged(nameof(ShowNotes));
            OnPropertyChanged(nameof(IsNotesPaneOpen));
            OnPropertyChanged(nameof(CanShowNotes));
        }
    }

    /// <summary>循环跳到下一个 / 上一个命中，并切到命中页。</summary>
    private void JumpToSearchHit(int direction)
    {
        if (_document is null) return;

        if (_searchHits.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(_searchText)) SearchSummary = $"没找到「{_searchText.Trim()}」";
            return;
        }

        var index = _searchIndex < 0
            ? (direction > 0 ? 0 : _searchHits.Count - 1)
            : ((_searchIndex + direction) % _searchHits.Count + _searchHits.Count) % _searchHits.Count;

        ApplySearchHit(index);
    }

    /// <summary>
    /// 切到第 <paramref name="index"/> 条命中（页号 + 备注栏 + 状态文案）。
    /// 「下一个 / 上一个」和「点结果浮层里的某一条」共用这一份逻辑。
    /// </summary>
    private void ApplySearchHit(int index)
    {
        if (_document is null || index < 0 || index >= _searchHits.Count) return;

        _searchIndex = index;
        var hit = _searchHits[index];
        SetCurrentSlideByPageIndex(hit.SlideIndex);

        // 命中在备注里：如果这一页有备注就展开备注栏，否则只提示
        if (hit.Source == PptSearchSource.Notes && HasCurrentNotes && !Cfg.ShowNotes)
        {
            Cfg.ShowNotes = true;
            OnPropertyChanged(nameof(ShowNotes));
            OnPropertyChanged(nameof(IsNotesPaneOpen));
            OnPropertyChanged(nameof(CanShowNotes));
        }

        var source = hit.Source == PptSearchSource.Notes ? "备注" : "正文";
        SearchSummary = $"{_searchIndex + 1} / {_searchHits.Count} · {hit.SlideTitle}（{source}）";
        StatusText = $"查找「{_searchText.Trim()}」：第 {_searchIndex + 1} / {_searchHits.Count} 处"
            + $" · 第 {hit.SlideIndex} 页（{source}）";
    }

    // ---------------------------------------------------------------- 复制本页文字

    /// <summary>
    /// 复制本页的纯文字（标题 + 正文 + 表格单元格拼好的 <see cref="PptSlide.PlainText"/>）。
    /// 剪贴板可能被别的程序占用，<c>Clipboard.SetText</c> 会抛 <see cref="COMException"/>——
    /// 这里必须吞掉并提示，不能让阅读器崩。
    /// </summary>
    public void CopySlideText()
    {
        var slide = CurrentSlide?.Slide;
        if (slide is null)
        {
            StatusText = "还没有打开演示文稿，没有可复制的文字";
            return;
        }

        var text = slide.PlainText;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText = $"第 {slide.Index} 页没有可复制的文字";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            StatusText = $"已复制本页文字（{text.Length} 字）";
            Log($"已复制第 {slide.Index} 页文字：{text.Length} 字");
        }
        catch (COMException)
        {
            StatusText = "复制失败：剪贴板被其它程序占用，请稍后重试";
            Log("复制失败：剪贴板被占用（COMException）");
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ex.Message;
            Log("复制失败：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 全屏放映

    /// <summary>
    /// 从当前页开始打开全屏放映窗口（黑底无边框、Esc 退出）；放映在独立窗口里进行，
    /// 关闭后主界面停到放映时看到的最后一页。没有打开文件时只提示，不弹窗。
    /// </summary>
    public void StartPresentation()
    {
        if (_document is null)
        {
            StatusText = "还没有打开演示文稿，无法放映";
            return;
        }

        var startIndex = CurrentIndex <= 0 ? 1 : CurrentIndex;

        try
        {
            var window = new PptPresentationWindow(
                _document,
                startIndex,
                Cfg.SkipHiddenInShow,
                GetSlideImage);

            // 主窗口已经显示时挂成 Owner（模态 + 居中更自然）；不可用就干脆不设，避免 Set Owner 抛异常
            var owner = Application.Current?.MainWindow;
            if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, window))
            {
                try { window.Owner = owner; } catch { /* Owner 不可用不影响放映 */ }
            }

            window.ShowDialog();

            if (window.LastSlideIndex > 0) SetCurrentSlideByPageIndex(window.LastSlideIndex);
            StatusText = window.LastSlideIndex > 0 ? $"已退出放映（停在第 {window.LastSlideIndex} 页）" : "已退出放映";
            Log($"放映结束：最后停在第 {window.LastSlideIndex} 页");
        }
        catch (Exception ex)
        {
            StatusText = "放映失败：" + ex.Message;
            Log("放映失败：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 最近打开

    /// <summary>「最近」下拉的数据源（文件已不存在的也会列出来，标成灰色）。</summary>
    public ObservableCollection<PptRecentItem> RecentItems { get; } = new();

    /// <summary>最近记录条数提示。</summary>
    public string RecentSummary => RecentItems.Count == 0 ? "暂无记录" : $"{RecentItems.Count} 条";

    /// <summary>有没有最近记录（空列表时界面显示「暂无记录」）。</summary>
    public bool HasRecentItems => RecentItems.Count > 0;

    /// <summary>
    /// 「最近」下拉的选中项：选中即打开，随后把选中项还原成 null（这样才能重复点同一个文件）。
    /// </summary>
    public PptRecentItem? SelectedRecent
    {
        get => null;
        set
        {
            if (value is null) return;
            OnPropertyChanged();   // 立刻还原 ComboBox 的显示
            _ = OpenRecentAsync(value);
        }
    }

    /// <summary>点「最近」里的一条：文件还在就打开，不在就给提示并顺手清掉这条记录（不弹窗）。</summary>
    public async Task OpenRecentAsync(PptRecentItem item)
    {
        if (item is null) return;

        if (!item.IsAvailable)
        {
            StatusText = $"文件已不存在：{item.Path}";
            Log($"最近打开的演示文稿已被移动或删除：{item.Path}");
            Cfg.RemoveRecent(item.Path);
            RefreshRecentItems();
            return;
        }

        await OpenPathAsync(item.Path);
    }

    /// <summary>清空最近打开记录（只删记录，不删本地文件）。</summary>
    private void ClearRecent()
    {
        Cfg.ClearRecent();
        RefreshRecentItems();
        StatusText = "已清空最近打开记录（不会删除本地文件）";
    }

    /// <summary>把配置里的最近列表投影成界面数据源。</summary>
    private void RefreshRecentItems()
    {
        RecentItems.Clear();
        foreach (var entry in Cfg.RecentEntries ?? new List<RecentFileEntry>())
        {
            var path = RecentFiles.NormalizePath(entry.Path);
            if (path.Length == 0) continue;
            RecentItems.Add(new PptRecentItem(path));
        }

        OnPropertyChanged(nameof(RecentSummary));
        OnPropertyChanged(nameof(HasRecentItems));
    }

    // ---------------------------------------------------------------- 打开演示文稿

    /// <summary>文件对话框选一个演示文稿并打开（起始目录 = 上次打开文件所在文件夹）。</summary>
    public async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开演示文稿",
            Filter = PptViewerConfig.FileDialogFilter,
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
            // 上次的文件夹非法（被删 / 没权限）：用系统默认目录
        }

        if (dialog.ShowDialog() != true) return;
        await OpenPathAsync(dialog.FileName);
    }

    /// <summary>
    /// 打开一个演示文稿路径：后台解析 → UI 线程渲染 → 更新界面状态；
    /// 失败只把中文错误显示在页面上，不弹窗。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="remember">要不要记进「最近打开」并更新上次文件夹（<c>YLT_PPT_OPEN</c> 自动打开时用 false）。</param>
    public async Task OpenPathAsync(string? path, bool remember = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (IsBusy)
        {
            StatusText = "正在解析上一个文件，请稍候…";
            return;
        }

        var filePath = path.Trim().Trim('"');

        // 旧版 .ppt 是最常见的误选：给一句照做就行的中文提示
        if (!IsSupportedExtension(filePath))
        {
            var extension = "";
            try { extension = Path.GetExtension(filePath).ToLowerInvariant(); } catch { /* 非法路径 */ }

            ShowOpenError(extension == ".ppt"
                ? "不支持旧版 .ppt，请用 PowerPoint / WPS 另存为 .pptx 再打开"
                : "只支持 .pptx / .pptm / .ppsx 文件" + (extension.Length > 0 ? $"（当前是「{extension}」）" : ""));
            return;
        }

        if (!File.Exists(filePath))
        {
            ShowOpenError($"文件不存在：{filePath}");
            return;
        }

        // 换文件前先把上一份的阅读位置落盘，否则切换瞬间的位置会丢
        FlushReadingPosition(saveNow: true);

        ErrorMessage = "";
        IsBusy = true;
        CommandManager.InvalidateRequerySuggested();   // 让「打开」按钮立刻变灰
        ProgressText = "正在解析…";
        StatusText = $"正在打开 {Path.GetFileName(filePath)}…";
        Log($"打开：{filePath}");

        try
        {
            // 解析在后台跑（大演示文稿不会卡住界面）；解析器读完已经把图片字节等拷出来，包已释放
            var parsed = await Task.Run(() =>
            {
                var presentation = PptDocumentParser.Parse(filePath);
                long size = 0;
                try { size = new FileInfo(filePath).Length; } catch { /* 文件刚被删 / 没权限 */ }
                return (Presentation: presentation, FileSize: size);
            });

            await UiAsync(() =>
            {
                ApplyDocument(parsed.Presentation, filePath, parsed.FileSize, remember);
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
                CommandManager.InvalidateRequerySuggested();
            });
        }
    }

    /// <summary>拖入文件 / 最近列表点开时调用。</summary>
    public Task OpenDroppedFileAsync(string path) => OpenPathAsync(path);

    /// <summary>路径是不是本工具支持的演示文稿扩展名（拖放 / 打开方式共用）。</summary>
    public static bool IsSupportedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var extension = Path.GetExtension(path.Trim().Trim('"'));
            return PptViewerConfig.SupportedExtensions.Any(item =>
                string.Equals(item, extension, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 打开方式（文件关联 / 单实例转发）

    /// <summary>
    /// 主程序通过 Windows「打开方式」交过来一个演示文稿（见 <see cref="IFileLaunchTarget"/>）：
    /// 规范成绝对路径后复用普通打开流程（<see cref="OpenPathAsync"/>），不阻塞界面。
    ///
    /// <para>路径为空 / 非法时只写状态栏与日志；文件不存在 / 格式不支持由 <see cref="OpenPathAsync"/>
    /// 统一显示到页面错误区，<b>绝不抛异常给主程序</b>。</para>
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

    /// <summary>把解析好的演示文稿接到界面上（必须运行在 UI 线程：要建 DrawingImage）。</summary>
    private void ApplyDocument(PptPresentation presentation, string path, long fileSize, bool remember)
    {
        ResetDocumentState();

        _suppressReadingPosition = true;
        try
        {
            _document = presentation;
            _currentPath = path;

            DocumentTitle = string.IsNullOrWhiteSpace(presentation.Title)
                ? Path.GetFileNameWithoutExtension(path)
                : presentation.Title;
            FileName = Path.GetFileName(path);
            FileSizeText = fileSize > 0 ? FileSize.Format(fileSize) : "—";
            DocumentSummary = BuildDocumentSummary(presentation);
            DocumentInfo = BuildDocumentInfo(presentation, path);

            Slides.Clear();
            for (var index = 0; index < presentation.Slides.Count; index++)
            {
                Slides.Add(new PptSlideItem(presentation.Slides[index], GetSlideImage, index + 1));
            }

            // 恢复上次阅读位置（页号夹到 1 ~ 总页数；记录失效时从第一页开始）
            var stored = Cfg.RememberReadingPositionEnabled ? Cfg.FindReadingPosition(path) : null;
            var startIndex = stored?.SlideIndex ?? 1;
            if (startIndex < 1 || startIndex > Slides.Count) startIndex = 1;
            if (Slides.Count > 0)
            {
                var target = Slides.FirstOrDefault(item => item.Index == startIndex) ?? Slides[startIndex - 1];
                CurrentSlide = target;
            }

            ErrorMessage = "";
            if (presentation.SlideCount == 0)
                ErrorMessage = "这份演示文稿里没有幻灯片（可能是空文件或格式不完整）";
            NotifyDocumentStateChanged();

            // 换文档后按原来的查询重新算一遍命中（避免摘要里还挂着上一份文件的结果）
            RebuildSearchHits();

            if (remember)
            {
                try { Cfg.LastFolder = Path.GetDirectoryName(path) ?? ""; } catch { /* 忽略非法路径 */ }
                Cfg.RememberRecent(path);
                RefreshRecentItems();
            }

            StatusText = stored is not null && Slides.Count > 0
                ? $"已打开 {FileName}，已回到上次阅读位置（第 {startIndex} 页）"
                : $"已打开 {FileName}（{presentation.SlideCount} 张幻灯片）";

            Log($"解析完成：{DocumentSummary} · {presentation.WidthDip:0}×{presentation.HeightDip:0} DIP"
                + (presentation.IsWideScreen ? "（16:9）" : ""));
        }
        finally
        {
            _suppressReadingPosition = false;
        }
    }

    /// <summary>状态栏摘要：张数 + 隐藏页 + 有备注的页数。</summary>
    private static string BuildDocumentSummary(PptPresentation presentation)
    {
        var parts = new List<string> { $"{presentation.SlideCount} 张幻灯片" };

        var hiddenCount = presentation.Slides.Count(slide => slide.IsHidden);
        if (hiddenCount > 0) parts.Add($"隐藏 {hiddenCount} 页");

        var notesCount = presentation.Slides.Count(slide => slide.HasNotes);
        if (notesCount > 0) parts.Add($"含备注 {notesCount} 页");

        return string.Join(" · ", parts);
    }

    /// <summary>拼文档属性的悬停提示（作者 / 时间 / 尺寸 / 大小 / 路径）。</summary>
    private string BuildDocumentInfo(PptPresentation presentation, string path)
    {
        var lines = new List<string>();
        var properties = new List<string>();
        var info = presentation.Properties;

        if (!string.IsNullOrWhiteSpace(info.Author)) properties.Add($"作者：{info.Author}");
        if (!string.IsNullOrWhiteSpace(info.LastModifiedBy)) properties.Add($"最后修改者：{info.LastModifiedBy}");
        if (info.CreatedUtc is { } created) properties.Add($"创建时间：{created.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (info.ModifiedUtc is { } modified) properties.Add($"修改时间：{modified.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (properties.Count > 0) lines.Add(string.Join(" · ", properties));

        lines.Add($"幻灯片尺寸：{presentation.WidthDip:0} × {presentation.HeightDip:0} DIP"
            + (presentation.IsWideScreen ? "（16:9）" : "（4:3）"));
        lines.Add($"大小：{FileSizeText}");
        lines.Add(path);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>打开失败：错误显示在页面错误区 + 状态栏，不弹 MessageBox。</summary>
    private void ShowOpenError(string message)
    {
        ErrorMessage = message;
        StatusText = message;
        Log("打开失败：" + message);
    }

    /// <summary>清空文档相关状态（打开新文件 / 关工具时），关掉旧的图片引用。</summary>
    private void ResetDocumentState()
    {
        _readingPositionTimer.Stop();

        _suppressReadingPosition = true;
        try
        {
            foreach (var item in Slides) item.IsCurrent = false;
            Slides.Clear();
            _slideImageCache.Clear();
            _slideImageOrder.Clear();
            // 静态图片解码缓存跨文档保留最近 200 张，换文档 / 关工具时主动清掉
            PptSlideRenderer.ClearImageCache();
            _currentSlide = null;
            _slideImage = null;
        }
        finally
        {
            _suppressReadingPosition = false;
        }

        _document = null;
        _currentPath = "";
        DocumentTitle = "";
        FileName = "";
        FileSizeText = "—";
        DocumentSummary = "尚未打开演示文稿";
        DocumentInfo = "";

        NotifyDocumentStateChanged();
        OnPropertyChanged(nameof(Presentation));
    }

    /// <summary>把所有「依赖当前文档」的属性通知一遍（打开 / 关闭 / 恢复文档后统一调）。</summary>
    private void NotifyDocumentStateChanged()
    {
        OnPropertyChanged(nameof(Presentation));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePathText));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(DocumentInfo));
        OnPropertyChanged(nameof(SlideCount));
        OnPropertyChanged(nameof(HasSlides));
        OnPropertyChanged(nameof(CurrentSlide));
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(SlideIndicator));
        OnPropertyChanged(nameof(SlideImage));
        OnPropertyChanged(nameof(SlideStretch));
        OnPropertyChanged(nameof(SlideDisplayWidth));
        OnPropertyChanged(nameof(SlideDisplayHeight));
        OnPropertyChanged(nameof(NotesText));
        OnPropertyChanged(nameof(HasCurrentNotes));
        OnPropertyChanged(nameof(CanShowNotes));
        OnPropertyChanged(nameof(IsNotesPaneOpen));
    }

    /// <summary>
    /// 释放当前文档的 WPF 对象与查找状态（关工具 / Ctrl+关闭时调用）。
    /// </summary>
    public void ReleaseDocumentState()
    {
        ResetDocumentState();
        ClearSearch();
        ErrorMessage = "";
    }

    // ---------------------------------------------------------------- 阅读位置（记住上次看到第几页）

    /// <summary>翻页后启动防抖计时器：500ms 内不再翻页就写一次配置。</summary>
    private void ScheduleReadingPositionSave()
    {
        if (_document is null || _currentPath.Length == 0 || _suppressReadingPosition) return;

        _readingPositionTimer.Stop();
        _readingPositionTimer.Start();
    }

    private void OnReadingPositionTimerTick(object? sender, EventArgs e)
    {
        _readingPositionTimer.Stop();
        SaveReadingPositionNow();
    }

    /// <summary>把当前页号写进配置（记录开关关掉时只跳过，不清已有记录）。</summary>
    private void SaveReadingPositionNow()
    {
        if (!Cfg.RememberReadingPositionEnabled) return;
        if (_document is null || _currentPath.Length == 0) return;

        var index = CurrentIndex;
        if (index <= 0) return;

        try { Cfg.RememberReadingPosition(_currentPath, index); }
        catch { /* 配置写失败不影响阅读（下次翻页 / 关工具会再试） */ }
    }

    /// <summary>
    /// 立刻把当前页号写进配置；<paramref name="saveNow"/> = true 时同步落盘
    /// （视图切走 / 关工具 / 换文件时用，防止退出时丢）。
    /// </summary>
    public void FlushReadingPosition(bool saveNow = false)
    {
        _readingPositionTimer.Stop();
        SaveReadingPositionNow();

        if (!saveNow) return;
        try { Cfg.SaveNow(); } catch { /* 配置写入失败不影响阅读 */ }
    }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>
    /// 开发 / 截图用：如果设置了 <c>YLT_PPT_OPEN</c>，视图第一次 Loaded 时自动打开它。
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
    /// 工具关闭（被挤出视图缓存 / 退出程序）：先把最后阅读位置落盘，再释放模型与渲染缓存。
    /// <para>视图缓存时切走不会调到这里，那种情况由视图 <c>Unloaded</c> 调
    /// <see cref="FlushReadingPosition"/>。</para>
    /// </summary>
    protected override void OnClosed()
    {
        _readingPositionTimer.Stop();
        _readingPositionTimer.Tick -= OnReadingPositionTimerTick;   // 摘掉计时器事件，别让 DispatcherTimer 留着 ViewModel
        FlushReadingPosition(saveNow: true);
        ReleaseDocumentState();
        RecentItems.Clear();
        OnPropertyChanged(nameof(RecentSummary));
        OnPropertyChanged(nameof(HasRecentItems));
        Log("已释放演示文稿资源");
    }
}
