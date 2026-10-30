using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.PdfReader.Rendering;
using YuLiangToolkit.Tools.PdfReader.Text;

namespace YuLiangToolkit.Tools.PdfReader.ViewModels;

/// <summary>目录面板里的一行（缩进已经算好，XAML 直接绑到 Button 的 Padding）。</summary>
public sealed record PdfOutlineEntryView(string Title, int PageIndex, int Level, Thickness IndentPadding);

/// <summary>
/// 「PDF 阅读器」的 ViewModel：管**设置 / 打开的 PDF / 缩放 / 双页间距 / 文字层（搜索 + 目录）/ 文件关联状态**。
///
/// <para>排版、虚拟化、鼠标拖拽 / 惯性、双页翻页动画这些必须碰 WPF 视觉树的事情全在
/// <c>Views/PdfReaderView.xaml.cs</c> 里；这里保持无头可测（自检可以直接 new 出来打开 PDF、查文字层）。</para>
/// </summary>
public sealed class PdfReaderViewModel : ToolViewModelBase
{
    public PdfReaderViewModel() : base(PdfReaderConfig.ToolId)
    {
        Cfg = LoadConfig<PdfReaderConfig>();

        ModeOptions = OptionItem.From(
            (PdfReadMode.Vertical, PdfReadModes.Describe(PdfReadMode.Vertical)),
            (PdfReadMode.Horizontal, PdfReadModes.Describe(PdfReadMode.Horizontal)),
            (PdfReadMode.Spread, PdfReadModes.Describe(PdfReadMode.Spread)));

        OpenPdfCommand = new AsyncRelayCommand(_ => OpenPdfAsync());
        ToggleSettingsCommand = new RelayCommand(_ =>
        {
            IsRecentVisible = false;
            IsSettingsVisible = !IsSettingsVisible;
        });
        CloseSettingsCommand = new RelayCommand(_ => IsSettingsVisible = false);
        ToggleOutlineCommand = new RelayCommand(_ =>
        {
            IsRecentVisible = false;
            IsOutlineVisible = !IsOutlineVisible;
        });
        CloseOutlineCommand = new RelayCommand(_ => IsOutlineVisible = false);
        ToggleRecentCommand = new RelayCommand(_ => ToggleRecent());
        ClearRecentCommand = new RelayCommand(_ => ClearRecent());
        OpenRecentCommand = new RelayCommand(p => _ = OpenRecentAsync(p as string));
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());
        ZoomInCommand = new RelayCommand(_ => StepZoom(1));
        ZoomOutCommand = new RelayCommand(_ => StepZoom(-1));
        OpenDefaultAppsCommand = new RelayCommand(_ => PdfFileAssociation.OpenDefaultAppsSettings());
        ToggleFullscreenCommand = new RelayCommand(_ => ToggleFullscreen());

        ApplyModeOverrideFromEnvironment();
        ApplyZoomOverrideFromEnvironment();
        ApplyFullscreenOverrideFromEnvironment();
        _pendingPageIndex = ParsePageOverrideFromEnvironment();

        // 最近浏览：按当前上限裁剪一次（手改坏配置时不会带出一长串），再把显示列表填好
        Cfg.TrimRecentToLimit();
        RefreshRecentItems();

        // 双击 .pdf 启动（文件关联 / 命令行）时，主程序把路径放在这里；只生效一次。
        _launchPdfPath = PdfFilePathLaunch.PendingPdfPath;
        PdfFilePathLaunch.PendingPdfPath = null;

        _fileAssociationStatus = PdfFileAssociation.DescribeState();
    }

    // ---------------------------------------------------------------- 配置与选项

    /// <summary>强类型配置（<c>Config\pdf-reader\config.json</c>）。</summary>
    public PdfReaderConfig Cfg { get; }

    /// <summary>三种阅读方式的选项（工具条下拉框直接用）。</summary>
    public IReadOnlyList<OptionItem<PdfReadMode>> ModeOptions { get; }

    /// <summary>开发 / 截图用的临时阅读方式覆盖（<c>YLT_PDF_MODE</c>；只影响显示，**不写配置**）。</summary>
    private PdfReadMode? _modeOverride;

    /// <summary>当前生效的阅读方式（配置值，截图时可能被环境变量临时覆盖）。</summary>
    public PdfReadMode CurrentReadMode => _modeOverride ?? Cfg.ReadModeEffective;

    /// <summary>下拉框的桥接属性：一改就写进配置，并通知界面重建视图（尽量保持当前位置）。</summary>
    public OptionItem<PdfReadMode> SelectedMode
    {
        get => OptionItem<PdfReadMode>.Find(ModeOptions, CurrentReadMode) ?? ModeOptions[0];
        set
        {
            if (value is null || value.Value == CurrentReadMode) return;
            _modeOverride = null;          // 用户亲自选了方式：回到写配置的正常路径
            Cfg.ReadMode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentReadMode));
            OnPropertyChanged(nameof(CurrentModeText));
            OnPropertyChanged(nameof(ModeHint));
            OnPropertyChanged(nameof(IsSpreadMode));
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>当前阅读方式的完整名字。</summary>
    public string CurrentModeText => PdfReadModes.Describe(CurrentReadMode);

    /// <summary>当前方式的一句说明（下拉框 tooltip）。</summary>
    public string ModeHint => PdfReadModes.Hint(CurrentReadMode);

    /// <summary>当前是不是双页翻页（界面用它决定显示翻页按钮还是滚动提示）。</summary>
    public bool IsSpreadMode => PdfReadModes.IsSpread(CurrentReadMode);

    // ---------------------------------------------------------------- 缩放

    /// <summary>
    /// 工具条上那个百分比输入框的文字（默认「100%」）。
    /// 输入 25 ~ 800 的数字（带不带 % 都行）按回车 / 离开输入框生效；非法输入会还原成当前值。
    /// </summary>
    public string ZoomText
    {
        get => $"{Cfg.ZoomPercentEffective}%";
        set
        {
            var text = (value ?? "").Trim().TrimEnd('%', '％').Trim();
            if (!int.TryParse(text, out var percent))
            {
                OnPropertyChanged();   // 输入不合法：把框里的字还原
                return;
            }

            SetZoomPercent(percent);
        }
    }

    /// <summary>当前缩放百分比（已经夹到 25 ~ 800）。</summary>
    public int ZoomPercentEffective => Cfg.ZoomPercentEffective;

    private void SetZoomPercent(int percent)
    {
        var effective = Math.Clamp(percent, PdfReaderConfig.MinZoomPercent, PdfReaderConfig.MaxZoomPercent);
        if (effective == Cfg.ZoomPercentEffective)
        {
            OnPropertyChanged(nameof(ZoomText));
            return;
        }

        Cfg.ZoomPercent = effective;
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomPercentEffective));
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StepZoom(int steps)
        => SetZoomPercent(PdfReaderConfig.StepZoomPercent(Cfg.ZoomPercentEffective, steps));

    /// <summary>缩放变了（界面重排 / 重新渲染可见页，并尽量保持画面中心不动）。</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>放大 10%（工具条放大镜 / Ctrl+滚轮）。</summary>
    public RelayCommand ZoomInCommand { get; }

    /// <summary>缩小 10%。</summary>
    public RelayCommand ZoomOutCommand { get; }

    // ---------------------------------------------------------------- 双页页面间距

    /// <summary>
    /// 双页翻页时左右两页之间的间距（像素，0 ~ 64，默认 2 = 贴近）。
    /// 设置面板的滑块直接绑它；改一下立刻重排当前双页视图并写配置。
    /// </summary>
    public int SpreadGapPixels
    {
        get => Cfg.SpreadGapEffective;
        set
        {
            var clamped = Math.Clamp(value, PdfReaderConfig.MinSpreadGapPixels, PdfReaderConfig.MaxSpreadGapPixels);
            if (clamped == Cfg.SpreadGapEffective) return;

            Cfg.SpreadGapPixels = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpreadGapText));
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>间距的显示文本（设置面板滑块右侧的「2 px」）。</summary>
    public string SpreadGapText => $"{SpreadGapPixels} px";

    /// <summary>影响排版但不影响缩放的设置变了（目前是双页间距），界面重排当前视图。</summary>
    public event EventHandler? LayoutChanged;

    // ---------------------------------------------------------------- 界面状态

    private bool _isSettingsVisible;
    private bool _isOutlineVisible;

    /// <summary>设置面板是否展开（双击间距 / 文件关联 / 恢复默认都放这里）。</summary>
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set => Set(ref _isSettingsVisible, value);
    }

    /// <summary>目录 / 书签面板是否展开。</summary>
    public bool IsOutlineVisible
    {
        get => _isOutlineVisible;
        set => Set(ref _isOutlineVisible, value);
    }

    /// <summary>展开 / 收起设置面板。</summary>
    public RelayCommand ToggleSettingsCommand { get; }

    /// <summary>收起设置面板。</summary>
    public RelayCommand CloseSettingsCommand { get; }

    /// <summary>展开 / 收起目录面板。</summary>
    public RelayCommand ToggleOutlineCommand { get; }

    /// <summary>收起目录面板。</summary>
    public RelayCommand CloseOutlineCommand { get; }

    /// <summary>恢复默认设置（阅读方式 / 惯性 / 滚轮 / 缩放 / 双页间距）。</summary>
    public RelayCommand ResetSettingsCommand { get; }

    // ---------------------------------------------------------------- 事件 / 界面注入

    /// <summary>打开 / 关闭 PDF 之后（界面据此重建视图）。</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>阅读方式变了（界面重建视图并尽量停在当前页）。</summary>
    public event EventHandler? ModeChanged;

    /// <summary>文字层（每页文字 + 目录）加载完成 / 被替换（搜索与目录面板据此刷新）。</summary>
    public event EventHandler? TextLayerChanged;

    /// <summary>
    /// 选 PDF 的对话框。由界面在 Loaded 时注入（真实实现是 <c>OpenFileDialog</c>）；
    /// 无头自检可以换成「直接返回一个临时文件」，不弹窗。
    /// </summary>
    public Func<string?>? PdfPicker { get; set; }

    // ---------------------------------------------------------------- 当前 PDF

    private PdfDocumentSession? _session;
    private string _filePath = "";
    private int? _pendingPageIndex;

    /// <summary>当前打开的 PDF（没打开时为 null）。</summary>
    public PdfDocumentSession? Session => _session;

    /// <summary>当前有没有打开 PDF（决定显示空状态还是长带 / 双页）。</summary>
    public bool HasDocument => _session is not null;

    /// <summary>当前 PDF 的页数（没打开是 0）。</summary>
    public int PageCount => _session?.PageCount ?? 0;

    /// <summary>工具条第一行：「文件名 · 共 N 页」；没打开时是工具名。</summary>
    public string FileTitleText => _session is null
        ? "PDF 阅读器"
        : $"{FileName} · 共 {PageCount} 页";

    /// <summary>文件名（没有就空串）。</summary>
    public string FileName => string.IsNullOrWhiteSpace(_filePath) ? "" : Path.GetFileName(_filePath);

    /// <summary>工具条第二行的完整路径；没打开时是一句引导。</summary>
    public string FilePathText => string.IsNullOrWhiteSpace(_filePath)
        ? "打开一个 PDF，用上下 / 左右滚动或双页翻着看"
        : _filePath;

    /// <summary>打开 PDF 后的一行提示（页数 + 当前阅读方式怎么操作）；没打开时为空串。</summary>
    public string LoadedHintText => _session is null
        ? ""
        : $"共 {PageCount} 页 · {PdfReadModes.Hint(CurrentReadMode)}";

    // ---------------------------------------------------------------- 文字层（搜索 + 目录）

    private PdfTextLayer? _textLayer;
    private CancellationTokenSource? _textLayerCts;
    private IReadOnlyList<PdfOutlineEntryView> _outlineEntries = Array.Empty<PdfOutlineEntryView>();
    private bool _isTextLayerLoading;
    private string _textLayerStatusText = "";

    /// <summary>当前 PDF 的文字层（提取中 / 失败时为 null）。</summary>
    public PdfTextLayer? TextLayer => _textLayer;

    /// <summary>文字层是否还在后台提取（界面显示「正在提取文字…」）。</summary>
    public bool IsTextLayerLoading
    {
        get => _isTextLayerLoading;
        private set
        {
            if (Set(ref _isTextLayerLoading, value)) OnPropertyChanged(nameof(TextLayerStatusText));
        }
    }

    /// <summary>目录面板的行（层级缩进已算好；没有目录时为空）。</summary>
    public IReadOnlyList<PdfOutlineEntryView> OutlineEntries
    {
        get => _outlineEntries;
        private set
        {
            _outlineEntries = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasOutline));
        }
    }

    /// <summary>这个 PDF 有没有书签目录。</summary>
    public bool HasOutline => _outlineEntries.Count > 0;

    /// <summary>搜索栏 / 目录面板里的一句话状态（提取中 / 没有目录 / 扫描版没有文字）。</summary>
    public string TextLayerStatusText
    {
        get => _textLayerStatusText;
        private set => Set(ref _textLayerStatusText, value);
    }

    /// <summary>
    /// 在整个 PDF 里搜索文字（大小写不敏感、支持中文）。没有文字层 / 空查询 → 空结果。
    /// 界面拿到结果后自己维护「第几个命中」并跳页。
    /// </summary>
    public IReadOnlyList<PdfSearchHit> Search(string? query) => PdfTextSearch.Find(_textLayer, query);

    private void StartTextLayer(PdfDocumentSession session)
    {
        CancelTextLayer();

        var cts = new CancellationTokenSource();
        _textLayerCts = cts;

        _textLayer = null;
        OutlineEntries = Array.Empty<PdfOutlineEntryView>();
        IsTextLayerLoading = true;
        TextLayerStatusText = "正在提取文字与目录…";
        TextLayerChanged?.Invoke(this, EventArgs.Empty);

        var path = session.FilePath;
        _ = Task.Run(async () =>
        {
            var layer = await PdfTextLayer.LoadAsync(path, cts.Token).ConfigureAwait(false);

            Ui(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_textLayerCts, cts)) return;

                _textLayer = layer;
                IsTextLayerLoading = false;
                OutlineEntries = layer.Outline
                    .Select(item => new PdfOutlineEntryView(
                        item.Title,
                        item.PageIndex,
                        item.Level,
                        new Thickness(Math.Min(6, Math.Max(0, item.Level - 1)) * 14, 0, 0, 0)))
                    .ToList();

                TextLayerStatusText = layer.HasText
                    ? (layer.HasOutline ? "" : "这个 PDF 没有书签目录。")
                    : (string.IsNullOrWhiteSpace(layer.Note)
                        ? "这个 PDF 提取不到文字（可能是扫描版，只能翻页看）。"
                        : layer.Note);

                TextLayerChanged?.Invoke(this, EventArgs.Empty);
                OnPropertyChanged(nameof(TextLayer));
                OnPropertyChanged(nameof(IsTextLayerLoading));
            });
        });
    }

    private void CancelTextLayer()
    {
        try
        {
            _textLayerCts?.Cancel();
            _textLayerCts?.Dispose();
        }
        catch { /* 退出流程忽略 */ }
        _textLayerCts = null;
    }

    // ---------------------------------------------------------------- 文件关联（双击 .pdf 打开）

    private bool? _fileAssociationEnabled;
    private string _fileAssociationStatus;

    /// <summary>是否已把 .pdf 登记到本工具（读注册表；带缓存）。</summary>
    public bool IsFileAssociationEnabled
    {
        get
        {
            _fileAssociationEnabled ??= PdfFileAssociation.IsRegistered();
            return _fileAssociationEnabled.Value;
        }
        set
        {
            var current = IsFileAssociationEnabled;
            if (value == current) return;

            var result = value ? PdfFileAssociation.Register() : PdfFileAssociation.Unregister();
            _fileAssociationStatus = result.Message;
            OnPropertyChanged(nameof(FileAssociationStatus));

            if (!result.Success)
            {
                OnPropertyChanged();   // 失败时勾选状态保持原样
                return;
            }

            _fileAssociationEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>文件关联区的状态说明（已登记 / 需要去系统「默认应用」确认一次）。</summary>
    public string FileAssociationStatus => _fileAssociationStatus;

    /// <summary>打开 Windows「默认应用」设置（UserChoice 只能由用户自己确认）。</summary>
    public RelayCommand OpenDefaultAppsCommand { get; }

    // ---------------------------------------------------------------- 最近浏览

    private bool _isRecentVisible;

    /// <summary>「最近浏览」面板是否展开。</summary>
    public bool IsRecentVisible
    {
        get => _isRecentVisible;
        set => Set(ref _isRecentVisible, value);
    }

    /// <summary>最近浏览面板里的行（新 → 旧；显示名 + 路径）。</summary>
    public ObservableCollection<PdfRecentDisplayItem> RecentItems { get; } = new();

    /// <summary>有没有最近浏览记录（空状态文字用）。</summary>
    public bool HasRecentItems => RecentItems.Count > 0;

    /// <summary>最近浏览面板的空状态文字（上限 0 时说明为什么不记录）。</summary>
    public string RecentEmptyText => Cfg.RecentLimitEffective == 0
        ? "最近浏览上限是 0（不记录）。到 ⚙ 设置 → 最近浏览 里调大即可。"
        : "还没有最近浏览记录。打开一个 PDF 后会自动记在这里。";

    /// <summary>设置面板里「最近浏览」的说明文字。</summary>
    public string RecentSectionHint =>
        "工具条「🕘 最近」最多记多少本打开过的 PDF：0 ~ 100，0 = 不记录并清空列表；默认 50。";

    /// <summary>上限的数字显示（设置面板 / 滑块右边）。</summary>
    public string RecentLimitText => Cfg.RecentLimitEffective == 0 ? "不记录" : $"{Cfg.RecentLimitEffective} 条";

    /// <summary>
    /// 「最近浏览」上限（0 ~ 100，默认 50）。改小会立刻把多余的老记录裁掉；改成 0 = 不记录并清空列表。
    /// </summary>
    public int RecentLimit
    {
        get => Cfg.RecentLimitEffective;
        set
        {
            var clamped = Math.Clamp(value, 0, PdfReaderConfig.MaxRecentLimit);
            Cfg.RecentLimit = clamped;
            Cfg.TrimRecentToLimit();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecentLimitText));
            RefreshRecentItems();
        }
    }

    public RelayCommand ToggleRecentCommand { get; }
    public RelayCommand ClearRecentCommand { get; }
    public RelayCommand OpenRecentCommand { get; }

    /// <summary>展开 / 收起「最近浏览」面板；展开前重新读一次配置（可能在别处被改过）。</summary>
    public void ToggleRecent()
    {
        if (!IsRecentVisible)
        {
            Cfg.TrimRecentToLimit();
            RefreshRecentItems();
            IsSettingsVisible = false;   // 两个右侧浮层别叠在一起
            IsOutlineVisible = false;
        }
        IsRecentVisible = !IsRecentVisible;
    }

    /// <summary>清空最近浏览记录（不会删本地文件）。</summary>
    public void ClearRecent()
    {
        Cfg.ClearRecent();
        RefreshRecentItems();
        StatusText = "已清空最近浏览";
    }

    /// <summary>点最近浏览里的一条：文件还在就打开它（会顺带恢复上次阅读位置）；已经不在了就移除并提示。</summary>
    public async Task OpenRecentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!File.Exists(path))
        {
            Cfg.RemoveRecent(path);
            RefreshRecentItems();
            StatusText = $"最近打开过的 PDF 已经不在了：{path}";
            return;
        }

        await LoadPdfAsync(path);
    }

    /// <summary>按配置里的记录重建显示列表（新 → 旧）。</summary>
    public void RefreshRecentItems()
    {
        RecentItems.Clear();

        foreach (var entry in Cfg.RecentEntries ?? new List<PdfRecentEntry>())
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) continue;

            var path = entry.Path;
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;
            var folder = Path.GetDirectoryName(path) ?? "";
            RecentItems.Add(new PdfRecentDisplayItem(path, name, folder));
        }

        OnPropertyChanged(nameof(HasRecentItems));
        OnPropertyChanged(nameof(RecentEmptyText));
    }

    private void RememberPdfInRecent(string path)
    {
        Cfg.RememberRecent(path);
        RefreshRecentItems();
    }

    // ---------------------------------------------------------------- 记住上次阅读位置

    /// <summary>界面最近一次报告的阅读位置（还没落盘）；由 <see cref="NoteReadingPosition"/> 更新。</summary>
    private PdfReadingPosition? _pendingReadingPosition;

    /// <summary>上次把阅读位置写进配置的时间（限制写盘频率：滚动时每帧都报，不能每帧都改配置）。</summary>
    private long _lastReadingPositionFlushMs;

    /// <summary>滚动停下 1.2 秒后落一次盘（滚动过程中反复重启；只靠关工具落盘的话进程被结束 / 断电会丢）。</summary>
    private DispatcherTimer? _readingPositionTimer;

    /// <summary>滚动 / 翻页时界面报告「当前看到第几页 + 整体阅读比例」，关工具 / 换文件时强制落盘。</summary>
    public void NoteReadingPosition(int pageIndex, double readingRatio)
    {
        var session = _session;
        if (session is null || session.PageCount <= 0) return;

        var page = Math.Clamp(pageIndex, 0, session.PageCount - 1);
        var ratio = double.IsNaN(readingRatio) || double.IsInfinity(readingRatio)
            ? 0
            : Math.Clamp(readingRatio, 0, 1);

        _pendingReadingPosition = new PdfReadingPosition
        {
            PageIndex = page,
            ReadingRatio = ratio,
            Mode = CurrentReadMode,
            SavedAtUtc = DateTime.UtcNow,
        };

        // 先兜一个「滚动停下 1.2s 就落盘」的计时器；再按 2 秒节流直接落一次（连续滚很久也不至于太久没写盘）
        ScheduleReadingPositionFlush();

        var now = Environment.TickCount64;
        if (now - _lastReadingPositionFlushMs >= 2000) FlushReadingPosition();
    }

    private void ScheduleReadingPositionFlush()
    {
        _readingPositionTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.2),
        };
        if (_readingPositionTimer.Tag is null)
        {
            _readingPositionTimer.Tick += (_, _) =>
            {
                _readingPositionTimer?.Stop();
                FlushReadingPosition();
            };
            _readingPositionTimer.Tag = true;
        }

        _readingPositionTimer.Stop();
        _readingPositionTimer.Start();
    }

    /// <summary>把缓存的阅读位置写进配置；<paramref name="saveNow"/> = true 时立刻落盘（关工具 / 换文件时用，防止退出丢）。</summary>
    public void FlushReadingPosition(bool saveNow = false)
    {
        var session = _session;
        var position = _pendingReadingPosition;
        if (session is null || position is null) return;

        _pendingReadingPosition = null;
        _lastReadingPositionFlushMs = Environment.TickCount64;
        try
        {
            Cfg.RememberReadingPosition(session.FilePath, position);
            if (saveNow) Cfg.SaveNow();
        }
        catch { /* 配置写入失败不影响阅读 */ }
    }

    /// <summary>
    /// 打开 PDF 后由界面取一次「上次读到哪」：没有记录返回 null；
    /// 存坏的页号 / 比例在这里夹回合法范围（手改配置文件也不会跳飞）。
    /// </summary>
    public PdfReadingPosition? ConsumeRestorePosition()
    {
        var session = _session;
        if (session is null || session.PageCount <= 0) return null;

        var stored = Cfg.FindReadingPosition(session.FilePath);
        if (stored is null) return null;

        stored.PageIndex = Math.Clamp(stored.PageIndex, 0, session.PageCount - 1);
        stored.ReadingRatio = double.IsNaN(stored.ReadingRatio) || double.IsInfinity(stored.ReadingRatio)
            ? 0
            : Math.Clamp(stored.ReadingRatio, 0, 1);
        return stored;
    }

    /// <summary>
    /// 把「跳页」输入框里的页码（1 起）解析成 0 起页号：非数字 / 小于 1 / 超过总页数都算非法。
    /// 抽在 VM 里是为了自检能直接断言（界面只负责把输入框文字递进来）。
    /// </summary>
    public bool TryParsePageNumber(string? text, out int pageIndex)
    {
        pageIndex = -1;
        if (!int.TryParse(text?.Trim(), out var page)) return false;
        if (page < 1 || page > PageCount) return false;
        pageIndex = page - 1;
        return true;
    }

    // ---------------------------------------------------------------- 命令 / 加载

    /// <summary>打开 PDF（Ctrl+O 也走它）。</summary>
    public AsyncRelayCommand OpenPdfCommand { get; }

    /// <summary>弹文件对话框 → 打开。取消对话框时什么都不做。</summary>
    public async Task OpenPdfAsync()
    {
        var picker = PdfPicker;
        if (picker is null) return;

        var path = picker();
        if (!string.IsNullOrWhiteSpace(path)) await LoadPdfAsync(path);
    }

    /// <summary>
    /// 打开一个 PDF：读页数 / 页尺寸，成功后换掉当前文档并通知界面重建。
    /// 打不开时只把状态文字改成提示，不会抛异常。
    /// </summary>
    public async Task LoadPdfAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (IsBusy) return;

        // 换书之前先把「上一本读到哪」落盘，否则切走瞬间的位置会丢
        FlushReadingPosition(saveNow: true);

        IsBusy = true;
        StatusText = "正在打开 PDF…";
        try
        {
            var session = await PdfDocumentSession.OpenAsync(path);
            var old = _session;
            _session = session;
            _filePath = session.FilePath;
            _pendingReadingPosition = null;   // 新书的位置等界面报告，别把上一本的缓存写到新书上
            _lastReadingPositionFlushMs = 0;

            // 最近浏览：换书成功后记一笔（上限 0 = 不记录并清空列表）
            RememberPdfInRecent(session.FilePath);
            IsRecentVisible = false;          // 打开成功后收起最近浏览面板

            var folder = Path.GetDirectoryName(session.FilePath);
            if (!string.IsNullOrWhiteSpace(folder)) Cfg.LastFolder = folder;

            StartTextLayer(session);

            StatusText = $"已打开「{FileName}」共 {PageCount} 页 · {CurrentModeText}";
            NotifyDocumentState();
            try { old?.Dispose(); } catch { /* 旧文档关闭失败不影响新文档 */ }
        }
        catch (Exception ex)
        {
            StatusText = PdfDocumentSession.DescribeOpenError(ex);
        }
        finally
        {
            IsBusy = false;
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NotifyDocumentState()
    {
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(FileTitleText));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePathText));
    }

    /// <summary>这次创建 ViewModel 时是不是带着「要打开的 PDF」（主程序从命令行 / 文件关联拿到）。</summary>
    public bool HasLaunchPdf => !string.IsNullOrWhiteSpace(_launchPdfPath);

    private string? _launchPdfPath;

    /// <summary>打开命令行 / 双击传进来的 PDF（取一次就清空）。</summary>
    public async Task LoadLaunchPdfAsync()
    {
        if (string.IsNullOrWhiteSpace(_launchPdfPath)) return;

        var path = _launchPdfPath;
        _launchPdfPath = null;
        await LoadPdfAsync(path);
    }

    /// <summary>
    /// 开发 / 截图用：<c>YLT_PDF_DEMO=1</c> 时生成一份演示 PDF（<c>Data\pdf-reader\demo.pdf</c>）并打开。
    /// 只有界面第一次加载、且当前还没打开任何 PDF 时才会执行。
    /// </summary>
    public async Task TryStartDemoAsync()
    {
        if (!IsEnvironmentOn("YLT_PDF_DEMO") || HasDocument) return;

        try
        {
            Directory.CreateDirectory(Storage.Directory);
            var path = Path.Combine(Storage.Directory, "demo.pdf");
            await File.WriteAllBytesAsync(path, PdfDemoPdf.Create(PdfDemoPdf.DefaultPageCount));
            await LoadPdfAsync(path);
        }
        catch (Exception ex)
        {
            StatusText = $"演示 PDF 生成失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 开发 / 截图用：<c>YLT_PDF_OPEN=路径</c> 直接打开指定 PDF（不弹对话框；也用来手工验证真实文件）。
    /// 正常运行时不会设这个环境变量。
    /// </summary>
    public async Task TryOpenFromEnvironmentAsync()
    {
        if (HasDocument) return;

        var path = Environment.GetEnvironmentVariable("YLT_PDF_OPEN");
        if (string.IsNullOrWhiteSpace(path)) return;

        try { await LoadPdfAsync(path.Trim().Trim('"')); }
        catch (Exception ex) { StatusText = PdfDocumentSession.DescribeOpenError(ex); }
    }

    /// <summary>
    /// 界面取走「打开后要定位到第几页（0 起）」：来源是环境变量 <c>YLT_PDF_PAGE</c>（截图 / 自检用）。
    /// 取一次就清空，避免每次重建都跳回去。
    /// </summary>
    public int? ConsumePendingPageIndex()
    {
        var index = _pendingPageIndex;
        _pendingPageIndex = null;
        return index;
    }

    /// <summary>把所有显示设置改回默认（阅读方式纵向、缩放 100%、双页间距 2、惯性开、滚轮 120）。</summary>
    private void ResetSettings()
    {
        Cfg.ResetToDefaults();

        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(CurrentReadMode));
        OnPropertyChanged(nameof(CurrentModeText));
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(IsSpreadMode));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomPercentEffective));
        OnPropertyChanged(nameof(SpreadGapPixels));
        OnPropertyChanged(nameof(SpreadGapText));

        StatusText = "已恢复默认设置（纵向 上→下 · 缩放 100% · 双页间距 2 px · 惯性开）";
        ModeChanged?.Invoke(this, EventArgs.Empty);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- 全屏（沉浸阅读）

    private bool _isFullscreen;
    private bool _controlsVisible = true;

    /// <summary>
    /// 是不是全屏。ViewModel 只记状态，真正的窗口操作由界面（外壳的 <c>IFullscreenHost</c>）做，
    /// 自检里没有窗口也能断言状态机；全屏只是藏外壳 / 工具条，**当前阅读方式不变**。
    /// </summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        private set
        {
            if (!Set(ref _isFullscreen, value)) return;
            ControlsVisible = true;
            OnPropertyChanged(nameof(FullscreenGlyph));
            OnPropertyChanged(nameof(FullscreenTooltip));
            OnPropertyChanged(nameof(IsToolbarVisible));
            OnPropertyChanged(nameof(ToolbarRowHeight));
            FullscreenChanged?.Invoke(this, value);
        }
    }

    /// <summary>全屏时工具条要不要露出来：鼠标一动露，停 2.5 秒缩回（普通模式永远露着）。</summary>
    public bool ControlsVisible
    {
        get => _controlsVisible;
        private set
        {
            if (!Set(ref _controlsVisible, value)) return;
            OnPropertyChanged(nameof(IsToolbarVisible));   // 工具条显隐绑的是 IsToolbarVisible
        }
    }

    /// <summary>鼠标动了 / 刚进全屏 → 把工具条亮出来。</summary>
    public void ShowControls()
    {
        if (!ControlsVisible) ControlsVisible = true;
    }

    /// <summary>鼠标闲置够久 → 把工具条缩回去（普通模式 / 正开着设置面板或搜索时不动）。</summary>
    public void HideControls()
    {
        if (IsFullscreen && !IsSettingsVisible && !IsOutlineVisible) ControlsVisible = false;
    }

    /// <summary>全屏按钮的图标：Segoe MDL2 的「全屏」E740 / 「退出全屏」E73F。</summary>
    public string FullscreenGlyph => IsFullscreen ? "\uE73F" : "\uE740";

    /// <summary>全屏按钮的悬停提示。</summary>
    public string FullscreenTooltip => IsFullscreen
        ? "退出全屏（F11 / Esc）"
        : "全屏沉浸阅读（F11；鼠标移到顶部调设置）";

    /// <summary>工具条现在可不可见（全屏时跟着鼠标显隐）。</summary>
    public bool IsToolbarVisible => !IsFullscreen || ControlsVisible;

    /// <summary>工具条那两行的高度：全屏时归零，让阅读区铺满（工具条改为浮层，见 View 的 DataTrigger）。</summary>
    public GridLength ToolbarRowHeight => IsFullscreen ? new GridLength(0) : GridLength.Auto;

    /// <summary>全屏状态变了（界面据此去操作真实窗口：藏标题栏 / 侧边栏、铺满屏幕）。参数是目标状态。</summary>
    public event EventHandler<bool>? FullscreenChanged;

    /// <summary>切换全屏（按钮 / F11 / Esc 都走它）。</summary>
    public void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    /// <summary>强制退出全屏（切走工具、界面卸载时调用，避免留下一个“藏了壳”的窗口）。</summary>
    public void ExitFullscreen()
    {
        if (IsFullscreen) IsFullscreen = false;
    }

    /// <summary>全屏按钮（工具条第一行最右边）。</summary>
    public RelayCommand ToggleFullscreenCommand { get; }

    protected override void OnClosed()
    {
        // 切走工具时一定退出全屏，否则外壳会一直停在被藏起来的状态
        ExitFullscreen();

        // 关工具 / 切走前把最后阅读位置立刻落盘（外壳会在界面卸载前调 NotifyClosed）
        FlushReadingPosition(saveNow: true);
        _readingPositionTimer?.Stop();
        _readingPositionTimer = null;
        IsRecentVisible = false;

        CancelTextLayer();

        var session = _session;
        _session = null;
        try { session?.Dispose(); } catch { /* 退出流程忽略 */ }
    }

    // ---------------------------------------------------------------- 环境变量（开发用）

    /// <summary>开发 / 截图用：<c>YLT_PDF_MODE=vertical|horizontal|spread</c> 直接指定阅读方式。</summary>
    private void ApplyModeOverrideFromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable("YLT_PDF_MODE");
        if (string.IsNullOrWhiteSpace(text)) return;

        PdfReadMode? mode = text.Trim().ToLowerInvariant() switch
        {
            "vertical" or "v" or "上下" or "1" => PdfReadMode.Vertical,
            "horizontal" or "h" or "ltr" or "左右" or "2" => PdfReadMode.Horizontal,
            "spread" or "s" or "double" or "双页" or "3" => PdfReadMode.Spread,
            _ => null,
        };
        if (mode is null) return;

        // ⚠ 只改显示覆盖，不写 Cfg：截图 / 调试跑完不会把用户配置改成截图用的模式
        _modeOverride = mode.Value;
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(CurrentReadMode));
        OnPropertyChanged(nameof(CurrentModeText));
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(IsSpreadMode));
    }

    /// <summary>开发 / 截图用：<c>YLT_PDF_ZOOM=200</c> 直接指定缩放百分比（截图核对放大后的排版）。</summary>
    private void ApplyZoomOverrideFromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable("YLT_PDF_ZOOM");
        if (string.IsNullOrWhiteSpace(text)) return;

        var cleaned = text.Trim().TrimEnd('%', '％').Trim();
        if (!int.TryParse(cleaned, out var percent)) return;

        Cfg.ZoomPercent = Math.Clamp(percent, PdfReaderConfig.MinZoomPercent, PdfReaderConfig.MaxZoomPercent);
    }

    /// <summary>开发 / 截图用：<c>YLT_PDF_FULLSCREEN=1</c> 直接以全屏启动（截图核对沉浸式版式）。</summary>
    private void ApplyFullscreenOverrideFromEnvironment()
    {
        if (IsEnvironmentOn("YLT_PDF_FULLSCREEN"))
            _isFullscreen = true;   // 界面 Loaded 时会把它同步给外壳（见 PdfReaderView）
    }

    /// <summary>开发 / 截图用：<c>YLT_PDF_PAGE=3</c> 打开后直接定位到第 3 页（从 1 数）。</summary>
    private static int? ParsePageOverrideFromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable("YLT_PDF_PAGE");
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), out var page) && page > 0 ? page - 1 : null;
    }

    /// <summary>环境变量开关：不设 / 空 / 0 / false 都算关。</summary>
    internal static bool IsEnvironmentOn(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }
}

/// <summary>「最近浏览」面板里的一行：完整路径 + 显示名 + 副标题（所在文件夹）。</summary>
public sealed record PdfRecentDisplayItem(string Path, string Name, string Detail);
