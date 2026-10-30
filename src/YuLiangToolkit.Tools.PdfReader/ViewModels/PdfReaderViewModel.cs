using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.PdfReader.Rendering;

namespace YuLiangToolkit.Tools.PdfReader.ViewModels;

/// <summary>
/// 「PDF 阅读器」的 ViewModel：只管**设置 / 打开哪个 PDF / 页数 / 状态文案**。
///
/// <para>排版、虚拟化、鼠标拖拽 / 惯性、双页翻页动画这些必须碰 WPF 视觉树的事情全在
/// <c>Views/PdfReaderView.xaml.cs</c> 里；这里保持无头可测（自检可以直接 new 出来打开 PDF）。</para>
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

        ApplyModeOverrideFromEnvironment();
        _pendingPageIndex = ParsePageOverrideFromEnvironment();
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

    /// <summary>下拉框的桥接属性：一改就写进配置，并通知界面重建视图（尽量保持当前页）。</summary>
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

    // ---------------------------------------------------------------- 事件 / 界面注入

    /// <summary>打开 / 关闭 PDF 之后（界面据此重建视图）。</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>阅读方式变了（界面重建视图并尽量停在原来的页）。</summary>
    public event EventHandler? ModeChanged;

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

    /// <summary>有 PDF 时工具条的提示文案（沿用基类 StatusText，打开成功后写观看方式提示）。</summary>
    public string LoadedHintText => _session is null
        ? ""
        : $"共 {PageCount} 页 · {PdfReadModes.Hint(CurrentReadMode)}";

    // ---------------------------------------------------------------- 命令

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

        IsBusy = true;
        StatusText = "正在打开 PDF…";
        try
        {
            var session = await PdfDocumentSession.OpenAsync(path);
            var old = _session;
            _session = session;
            _filePath = session.FilePath;
            if (!string.IsNullOrWhiteSpace(Path.GetDirectoryName(session.FilePath)))
                Cfg.LastFolder = Path.GetDirectoryName(session.FilePath)!;

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
        OnPropertyChanged(nameof(LoadedHintText));
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

    protected override void OnClosed()
    {
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
