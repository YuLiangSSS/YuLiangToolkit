using System.Diagnostics;
using System.IO;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.PictureViewer.ViewModels;

/// <summary>
/// 「图片连续阅读器」的 ViewModel：只管**设置 / 文件夹 / 页清单 / 界面状态**。
///
/// <para>图片解码、长带排版、滚轮 / 拖拽 / 中键锚点这些必须碰 WPF 视觉树的事情全在
/// <c>Views/PictureViewerView.xaml.cs</c> 里；这里保持无头可测（自检直接 new 出来跑扫描流程）。</para>
/// </summary>
public sealed class PictureViewerViewModel : ToolViewModelBase
{
    public PictureViewerViewModel() : base(PictureViewerConfig.ToolId)
    {
        Cfg = LoadConfig<PictureViewerConfig>();

        ModeOptions = OptionItem.From(
            (PictureScrollMode.Vertical, PictureScrollModes.Describe(PictureScrollMode.Vertical)),
            (PictureScrollMode.HorizontalLeftToRight, PictureScrollModes.Describe(PictureScrollMode.HorizontalLeftToRight)),
            (PictureScrollMode.HorizontalRightToLeft, PictureScrollModes.Describe(PictureScrollMode.HorizontalRightToLeft)),
            (PictureScrollMode.SpreadLeftToRight, PictureScrollModes.Describe(PictureScrollMode.SpreadLeftToRight)),
            (PictureScrollMode.SpreadRightToLeft, PictureScrollModes.Describe(PictureScrollMode.SpreadRightToLeft)),
            (PictureScrollMode.SinglePage, PictureScrollModes.Describe(PictureScrollMode.SinglePage)));

        WheelActionOptions = OptionItem.From(
            (PictureWheelAction.TurnPage, "翻页（上一张 / 下一张）"),
            (PictureWheelAction.Zoom, "缩放"));

        OpenFolderCommand = new AsyncRelayCommand(_ => OpenFolderAsync());
        OpenFolderInExplorerCommand = new RelayCommand(_ => OpenFolderInExplorer());
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsVisible = !IsSettingsVisible);
        CloseSettingsCommand = new RelayCommand(_ => IsSettingsVisible = false);
        ZoomInCommand = new RelayCommand(_ => StepZoom(1));
        ZoomOutCommand = new RelayCommand(_ => StepZoom(-1));

        ApplyModeOverrideFromEnvironment();
        ApplyZoomOverrideFromEnvironment();
        ApplyBorderOverrideFromEnvironment();

        // 边框颜色编辑器：三个颜色各一个（R/G/B/A 数字 + 调色板 + 预览都绑它）
        BorderInnerColorEditor = new RgbaColorEditor(Cfg.BorderInnerColor);
        BorderFillColorEditor = new RgbaColorEditor(Cfg.BorderFillColor);
        BorderOuterColorEditor = new RgbaColorEditor(Cfg.BorderOuterColor);
        BorderInnerColorEditor.Changed += (_, _) => SaveBorderColor(ColorPart.Inner);
        BorderFillColorEditor.Changed += (_, _) => SaveBorderColor(ColorPart.Fill);
        BorderOuterColorEditor.Changed += (_, _) => SaveBorderColor(ColorPart.Outer);

        PickInnerBorderColorCommand = new RelayCommand(p => PickBorderColor(ColorPart.Inner, p));
        PickFillColorCommand = new RelayCommand(p => PickBorderColor(ColorPart.Fill, p));
        PickOuterBorderColorCommand = new RelayCommand(p => PickBorderColor(ColorPart.Outer, p));
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());

        // 双击图片启动（文件关联 / 命令行）时，主程序把图片路径放在这里；只生效一次。
        _launchImagePath = PictureFilePathLaunch.PendingImagePath;
        PictureFilePathLaunch.PendingImagePath = null;
        _fileAssociationStatus = PictureFileAssociation.DescribeState();
        OpenDefaultAppsCommand = new RelayCommand(_ => PictureFileAssociation.OpenDefaultAppsSettings());
    }

    // ---------------------------------------------------------------- 配置与选项

    /// <summary>强类型配置（<c>Config\picture-viewer\config.json</c>）。</summary>
    public PictureViewerConfig Cfg { get; }

    /// <summary>六种阅读方式的选项（工具条只显示当前方式，切换在设置面板里）。</summary>
    public IReadOnlyList<OptionItem<PictureScrollMode>> ModeOptions { get; }

    /// <summary>翻页模式下滚轮做什么的选项。</summary>
    public IReadOnlyList<OptionItem<PictureWheelAction>> WheelActionOptions { get; }

    /// <summary>下拉框的桥接属性：一改就写进配置，并通知界面重建长带 / 翻页视图（保持位置）。</summary>
    public OptionItem<PictureScrollMode> SelectedMode
    {
        get => OptionItem<PictureScrollMode>.Find(ModeOptions, Cfg.ScrollModeEffective) ?? ModeOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.ScrollModeEffective) return;
            Cfg.ScrollMode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeHint));
            OnPropertyChanged(nameof(CurrentModeText));
            OnPropertyChanged(nameof(IsPagedMode));
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>翻页模式滚轮动作（存配置，改一下立即生效）。</summary>
    public OptionItem<PictureWheelAction> SelectedWheelAction
    {
        get => OptionItem<PictureWheelAction>.Find(WheelActionOptions, Cfg.PagedWheelEffective) ?? WheelActionOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.PagedWheelEffective) return;
            Cfg.PagedWheel = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>工具条上「阅读方式」按钮显示的文字（当前方式）。</summary>
    public string CurrentModeText => PictureScrollModes.Describe(Cfg.ScrollModeEffective);

    /// <summary>当前方式是翻页 / 单张类（设置面板据此显示「翻页设置」那一块）。</summary>
    public bool IsPagedMode => PictureScrollModes.IsPaged(Cfg.ScrollModeEffective);

    /// <summary>当前阅读方式的一句说明。</summary>
    public string ModeHint => PictureScrollModes.Hint(Cfg.ScrollModeEffective);

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

    private void SetZoomPercent(int percent)
    {
        var effective = PictureScrollMath.EffectiveZoomPercent(percent);
        if (effective == Cfg.ZoomPercentEffective)
        {
            OnPropertyChanged(nameof(ZoomText));
            return;
        }

        Cfg.ZoomPercent = effective;
        OnPropertyChanged(nameof(ZoomText));
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StepZoom(int steps) => SetZoomPercent(PictureScrollMath.StepZoomPercent(Cfg.ZoomPercentEffective, steps));

    // ---------------------------------------------------------------- 页清单

    private IReadOnlyList<PicturePage> _pages = Array.Empty<PicturePage>();

    /// <summary>当前文件夹按页序排好的图片（界面据此搭长带）。</summary>
    public IReadOnlyList<PicturePage> Pages
    {
        get => _pages;
        private set
        {
            _pages = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPages));
            OnPropertyChanged(nameof(ImageCountText));
        }
    }

    /// <summary>有没有图片（决定显示空状态还是长带）。</summary>
    public bool HasPages => _pages.Count > 0;

    private string _folderPath = "";

    /// <summary>当前打开的文件夹（空串 = 还没打开）。只用于界面展示与「上次文件夹」。</summary>
    public string FolderPath
    {
        get => _folderPath;
        private set
        {
            _folderPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FolderName));
            OnPropertyChanged(nameof(FolderPathText));
            OnPropertyChanged(nameof(HasFolder));
            OnPropertyChanged(nameof(ImageCountText));
        }
    }

    /// <summary>文件夹名（标题 / 状态文案用）。</summary>
    public string FolderName => string.IsNullOrWhiteSpace(_folderPath)
        ? ""
        : Path.GetFileName(_folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>工具条第二行的路径文本。</summary>
    public string FolderPathText => string.IsNullOrWhiteSpace(_folderPath) ? "还没有打开文件夹" : _folderPath;

    /// <summary>当前有没有打开文件夹（路径那一行能不能点）。</summary>
    public bool HasFolder => !string.IsNullOrWhiteSpace(_folderPath);

    /// <summary>工具条第一行：张数 + 文件夹名。</summary>
    public string ImageCountText => _pages.Count == 0
        ? "图片连续阅读器"
        : $"{_pages.Count} 张图片 · {FolderName}";

    // ---------------------------------------------------------------- 界面状态

    private bool _isSettingsVisible;

    /// <summary>设置面板是否展开。</summary>
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set => Set(ref _isSettingsVisible, value);
    }

    /// <summary>页清单变了（界面重建长带）。</summary>
    public event EventHandler? PagesChanged;

    /// <summary>滚动方式变了（界面重建长带并尽量停在原来的位置）。</summary>
    public event EventHandler? ModeChanged;

    /// <summary>缩放变了（界面重新排版，并尽量保持画面中心不动）。</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>
    /// 选文件夹的对话框。由界面在 Loaded 时注入（真实实现是 <c>OpenFolderDialog</c>）；
    /// 无头自检可以换成「直接返回一个临时目录」，不弹窗。
    /// </summary>
    public Func<string?>? FolderPicker { get; set; }

    // ---------------------------------------------------------------- 命令

    /// <summary>打开文件夹（Ctrl+O 也走它）。</summary>
    public AsyncRelayCommand OpenFolderCommand { get; }

    /// <summary>点工具条上的文件夹路径 → 在资源管理器里打开这个文件夹。</summary>
    public RelayCommand OpenFolderInExplorerCommand { get; }

    /// <summary>展开 / 收起设置面板。</summary>
    public RelayCommand ToggleSettingsCommand { get; }

    /// <summary>收起设置面板。</summary>
    public RelayCommand CloseSettingsCommand { get; }

    /// <summary>放大 10%（工具条右侧放大镜 +）。</summary>
    public RelayCommand ZoomInCommand { get; }

    /// <summary>缩小 10%（工具条右侧放大镜 −）。</summary>
    public RelayCommand ZoomOutCommand { get; }

    /// <summary>打开 Windows「默认应用」设置（文件关联的最后一步只能由用户确认）。</summary>
    public RelayCommand OpenDefaultAppsCommand { get; }

    // ---------------------------------------------------------------- 加载

    /// <summary>弹文件夹对话框 → 加载。取消对话框时什么都不做。</summary>
    public async Task OpenFolderAsync()
    {
        var folder = FolderPicker?.Invoke();
        if (!string.IsNullOrWhiteSpace(folder)) await LoadFolderAsync(folder);
    }

    /// <summary>
    /// 在资源管理器里打开当前文件夹。自检可以把 <see cref="FolderOpener"/> 换掉，
    /// 不真的弹资源管理器，只断言传进来的目录对不对。
    /// </summary>
    public Func<string, bool>? FolderOpener { get; set; }

    /// <summary>点工具条上的路径：打开当前文件夹；目录已经不在时把状态文字改成提示。</summary>
    private void OpenFolderInExplorer()
    {
        if (string.IsNullOrWhiteSpace(_folderPath)) return;

        var folder = _folderPath;
        try
        {
            var opened = FolderOpener?.Invoke(folder) ?? LaunchExplorer(folder);
            if (!opened) StatusText = $"打开文件夹失败：目录可能已经不在了 —— {folder}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开文件夹失败：{ex.Message}";
        }
    }

    private static bool LaunchExplorer(string folder)
    {
        if (!Directory.Exists(folder)) return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
        return true;
    }

    /// <summary>
    /// 加载一个文件夹：后台读图片尺寸（只读文件头，不进内存），完成后换掉页清单并通知界面重建。
    /// 传进来的目录不存在 / 里面没图片时不会抛异常，只把状态文字改成提示。
    /// </summary>
    /// <param name="preferredImagePath">
    /// 可选：双击 / 命令行指定要打开的那张图（不区分大小写）。加载完界面会**自动跳到它所在位置**；
    /// 找不到（例如文件刚被删）就停在文件夹开头。
    /// </param>
    public async Task LoadFolderAsync(string folder, string? preferredImagePath = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;

        var full = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(name)) name = full;

        _launchTargetFound = false;

        IsBusy = true;
        Progress = 0;
        ProgressText = "正在读取图片信息…";
        StatusText = $"正在扫描「{name}」…";

        try
        {
            var pages = await Task.Run(() => PictureScanner.ScanFolder(full, (done, total) =>
            {
                Ui(() =>
                {
                    Progress = total > 0 ? (double)done / total : 0;
                    ProgressText = $"正在读取图片信息 {done} / {total}";
                });
            }));

            Pages = pages;
            FolderPath = pages.Count > 0 ? full : "";
            if (pages.Count > 0 && Cfg.LastFolder != full) Cfg.LastFolder = full;

            _pendingPageIndex = FindPageIndex(pages, preferredImagePath);
            _launchTargetFound = _pendingPageIndex is not null;
            StatusText = pages.Count == 0
                ? $"「{name}」里没有找到图片（支持 jpg / jpeg / png / bmp / gif / webp / tif / tiff）"
                : $"已加载「{name}」共 {pages.Count} 张 · {PictureScrollModes.Describe(Cfg.ScrollModeEffective)}";
            if (_pendingPageIndex is { } target && pages.Count > 0)
                StatusText += $" · 已定位到第 {target + 1} 张";
        }
        catch (Exception ex)
        {
            Pages = Array.Empty<PicturePage>();
            FolderPath = "";
            _pendingPageIndex = null;
            _launchTargetFound = false;
            StatusText = $"读取「{name}」失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = 1;
            ProgressText = "";
            PagesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>页清单里找指定图片（完整路径、不区分大小写）；找不到返回 null。</summary>
    private static int? FindPageIndex(IReadOnlyList<PicturePage> pages, string? preferredImagePath)
    {
        if (string.IsNullOrWhiteSpace(preferredImagePath) || pages.Count == 0) return null;

        try
        {
            var target = Path.GetFullPath(preferredImagePath.Trim().Trim('"'));
            for (var i = 0; i < pages.Count; i++)
            {
                if (string.Equals(pages[i].FilePath, target, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }
        catch { /* 路径不合法就当没指定 */ }

        return null;
    }

    /// <summary>
    /// 双击图片启动时：取走主程序放下的图片路径，加载它所在文件夹并记住要定位的那一张。
    /// 没传图片就什么都不做。
    /// </summary>
    public async Task LoadLaunchImageAsync()
    {
        if (string.IsNullOrWhiteSpace(_launchImagePath)) return;

        var path = _launchImagePath;
        _launchImagePath = null;

        string? folder;
        try { folder = Path.GetDirectoryName(Path.GetFullPath(path.Trim().Trim('"'))); }
        catch { folder = null; }

        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusText = $"无法识别要打开的图片路径：{path}";
            return;
        }

        await LoadFolderAsync(folder, path);
        // _pendingPageIndex 可能已经被界面在 PagesChanged 里取走（取一次就清空），所以用 _launchTargetFound 判断
        if (!_launchTargetFound)
            StatusText = $"已加载「{Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}」，但没找到刚才那张图，已停在开头。";
    }

    /// <summary>界面取走「加载后要定位到第几张」（取一次就清空，避免每次重建都跳回去）。</summary>
    public int? ConsumePendingPageIndex()
    {
        var index = _pendingPageIndex;
        _pendingPageIndex = null;
        return index;
    }

    /// <summary>
    /// 开发 / 截图用：<c>YLT_PICTURE_DEMO=1</c> 时在 <c>Data\picture-viewer\demo\</c> 生成演示图片并加载。
    /// 必须在界面线程调用（生成图片要用 WPF 渲染管线）。
    /// </summary>
    public async Task TryStartDemoAsync()
    {
        if (!IsEnvironmentOn("YLT_PICTURE_DEMO")) return;

        try
        {
            var demoDirectory = Path.Combine(Storage.Directory, "demo");
            Directory.CreateDirectory(demoDirectory);
            PictureDemoData.Ensure(demoDirectory);
            await LoadFolderAsync(demoDirectory);
        }
        catch (Exception ex)
        {
            StatusText = $"演示图片生成失败：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 双击图片打开 / 文件关联

    private string? _launchImagePath;
    private bool _launchTargetFound;
    private int? _pendingPageIndex;
    private bool? _fileAssociationEnabled;
    private string _fileAssociationStatus = "";

    /// <summary>这次创建 ViewModel 时是不是带着「要打开的图片」（主程序从命令行取到的）。</summary>
    public bool HasLaunchImage => !string.IsNullOrWhiteSpace(_launchImagePath);

    /// <summary>是否已把图片后缀登记到本工具（读注册表；带缓存）。</summary>
    public bool IsFileAssociationEnabled
    {
        get
        {
            _fileAssociationEnabled ??= PictureFileAssociation.IsRegistered();
            return _fileAssociationEnabled.Value;
        }
        set
        {
            var current = IsFileAssociationEnabled;
            if (value == current) return;

            var result = value ? PictureFileAssociation.Register() : PictureFileAssociation.Unregister();
            _fileAssociationStatus = result.Message;
            OnPropertyChanged(nameof(FileAssociationStatus));

            if (!result.Success)
            {
                // 失败时勾选状态保持原样（比如注册表被策略锁住）
                OnPropertyChanged();
                return;
            }

            _fileAssociationEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>文件关联区的状态说明（已关联多少个后缀 / 需不需要去系统里确认默认程序）。</summary>
    public string FileAssociationStatus => _fileAssociationStatus;

    // ---------------------------------------------------------------- 图片边框

    private enum ColorPart
    {
        Inner,
        Fill,
        Outer,
    }

    /// <summary>边框设置变了（颜色 / 粗细 / 开关）→ 界面重排长带或重画翻页视图。</summary>
    public event EventHandler? BorderChanged;

    /// <summary>开发 / 截图用的临时边框覆盖（<c>YLT_PICTURE_BORDER=demo|off</c>；只影响显示，不写配置）。</summary>
    public PictureBorderStyle? BorderStyleOverride { get; private set; }

    /// <summary>当前生效的边框样式（配置值；截图钩子可能临时覆盖）。</summary>
    public PictureBorderStyle BorderStyle => BorderStyleOverride ?? Cfg.BorderStyle;

    /// <summary>内边框颜色编辑器（R/G/B/A 四个数字 + 调色板 + 预览）。</summary>
    public RgbaColorEditor BorderInnerColorEditor { get; }

    /// <summary>填充颜色编辑器。</summary>
    public RgbaColorEditor BorderFillColorEditor { get; }

    /// <summary>外边框颜色编辑器。</summary>
    public RgbaColorEditor BorderOuterColorEditor { get; }

    /// <summary>调色板预设颜色（ARGB）。点一下会填进对应颜色，A 也可以被预设改动（透明色）。</summary>
    public IReadOnlyList<string> BorderPalette { get; } = new[]
    {
        "#FFFFFFFF", "#FF000000", "#FF6B7280", "#FFEF4444", "#FFF97316", "#FFFACC15",
        "#FF22C55E", "#FF06B6D4", "#FF3B82F6", "#FFA855F7", "#FFEC4899", "#00000000",
    };

    /// <summary>单实例模式（启动级：关掉后下次启动就是多开；当前窗口不受影响）。</summary>
    public bool SingleInstanceEnabled
    {
        get => Cfg.SingleInstanceEnabled;
        set
        {
            if (value == Cfg.SingleInstanceEnabled) return;
            Cfg.SingleInstanceEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否显示图片边框。</summary>
    public bool BorderEnabled
    {
        get => Cfg.BorderEnabled;
        set
        {
            if (value == Cfg.BorderEnabled) return;
            Cfg.BorderEnabled = value;
            OnPropertyChanged();
            BorderChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>内边框粗细（0 ~ 32）。</summary>
    public int BorderInnerSize
    {
        get => PictureBorderStyle.ClampBorder(Cfg.BorderInnerSize);
        set
        {
            var clamped = PictureBorderStyle.ClampBorder(value);
            if (clamped == Cfg.BorderInnerSize) return;
            Cfg.BorderInnerSize = clamped;
            OnPropertyChanged();
            BorderChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>填充宽度（0 ~ 128）。</summary>
    public int BorderFillSize
    {
        get => PictureBorderStyle.ClampFill(Cfg.BorderFillSize);
        set
        {
            var clamped = PictureBorderStyle.ClampFill(value);
            if (clamped == Cfg.BorderFillSize) return;
            Cfg.BorderFillSize = clamped;
            OnPropertyChanged();
            BorderChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>外边框粗细（0 ~ 32）。</summary>
    public int BorderOuterSize
    {
        get => PictureBorderStyle.ClampBorder(Cfg.BorderOuterSize);
        set
        {
            var clamped = PictureBorderStyle.ClampBorder(value);
            if (clamped == Cfg.BorderOuterSize) return;
            Cfg.BorderOuterSize = clamped;
            OnPropertyChanged();
            BorderChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>内边框调色板。</summary>
    public RelayCommand PickInnerBorderColorCommand { get; }

    /// <summary>填充调色板。</summary>
    public RelayCommand PickFillColorCommand { get; }

    /// <summary>外边框调色板。</summary>
    public RelayCommand PickOuterBorderColorCommand { get; }

    /// <summary>恢复默认设置（阅读方式回到纵向；边框等全部回默认）。</summary>
    public RelayCommand ResetSettingsCommand { get; }

    private RgbaColorEditor EditorFor(ColorPart part) => part switch
    {
        ColorPart.Fill => BorderFillColorEditor,
        ColorPart.Outer => BorderOuterColorEditor,
        _ => BorderInnerColorEditor,
    };

    private void SaveBorderColor(ColorPart part)
    {
        var editor = EditorFor(part);
        switch (part)
        {
            case ColorPart.Inner: Cfg.BorderInnerColor = editor.Hex; break;
            case ColorPart.Fill: Cfg.BorderFillColor = editor.Hex; break;
            case ColorPart.Outer: Cfg.BorderOuterColor = editor.Hex; break;
        }
        BorderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PickBorderColor(ColorPart part, object? parameter)
    {
        if (parameter is string hex) EditorFor(part).Load(hex);
    }

    /// <summary>把所有设置改回默认：配置、颜色编辑器、界面通知一起复位。</summary>
    private void ResetSettings()
    {
        Cfg.ResetToDefaults();
        BorderStyleOverride = null;

        // 编辑器重新加载（会触发 Changed → 写回配置并通知界面，重复一次没关系）
        BorderInnerColorEditor.Load(Cfg.BorderInnerColor);
        BorderFillColorEditor.Load(Cfg.BorderFillColor);
        BorderOuterColorEditor.Load(Cfg.BorderOuterColor);

        OnPropertyChanged(nameof(BorderEnabled));
        OnPropertyChanged(nameof(SingleInstanceEnabled));
        OnPropertyChanged(nameof(BorderInnerSize));
        OnPropertyChanged(nameof(BorderFillSize));
        OnPropertyChanged(nameof(BorderOuterSize));
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(SelectedWheelAction));
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(CurrentModeText));
        OnPropertyChanged(nameof(IsPagedMode));
        OnPropertyChanged(nameof(ZoomText));

        StatusText = "已恢复默认设置（阅读方式：纵向 上 → 下）";
        ModeChanged?.Invoke(this, EventArgs.Empty);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        BorderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>开发 / 截图用：<c>YLT_PICTURE_BORDER=off|demo</c> 临时覆盖边框（demo = 红/黄/蓝的夸张样式）。
    /// 只改显示、**不写配置**，免得截图跑完把用户设置污染成红黄蓝。</summary>
    private void ApplyBorderOverrideFromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable("YLT_PICTURE_BORDER");
        if (string.IsNullOrWhiteSpace(text)) return;

        switch (text.Trim().ToLowerInvariant())
        {
            case "0" or "off" or "false" or "no":
                BorderStyleOverride = PictureBorderStyle.Default with { Enabled = false };
                break;
            case "demo":
                BorderStyleOverride = new PictureBorderStyle(
                    true, 2, 10, 2, "#FFFF4040", "#FFFFD54A", "#FF4A90FF");
                break;
        }
    }

    // ---------------------------------------------------------------- 环境变量（开发用）

    /// <summary>开发 / 截图用：<c>YLT_PICTURE_MODE=vertical|ltr|rtl|spread-ltr|spread-rtl|single</c> 直接指定阅读方式。</summary>
    private void ApplyModeOverrideFromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable("YLT_PICTURE_MODE");
        if (string.IsNullOrWhiteSpace(text)) return;

        PictureScrollMode? mode = text.Trim().ToLowerInvariant() switch
        {
            "vertical" or "v" or "上下" or "1" => PictureScrollMode.Vertical,
            "ltr" or "left-to-right" or "lefttoright" or "从左到右" or "2" => PictureScrollMode.HorizontalLeftToRight,
            "rtl" or "right-to-left" or "righttoleft" or "从右到左" or "3" => PictureScrollMode.HorizontalRightToLeft,
            "spread-ltr" or "spreadltr" or "双页左到右" or "4" => PictureScrollMode.SpreadLeftToRight,
            "spread-rtl" or "spreadrtl" or "双页右到左" or "5" => PictureScrollMode.SpreadRightToLeft,
            "single" or "single-page" or "singlepage" or "单页" or "6" => PictureScrollMode.SinglePage,
            _ => null,
        };
        if (mode is null) return;

        Cfg.ScrollMode = mode.Value;
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(CurrentModeText));
        OnPropertyChanged(nameof(IsPagedMode));
    }

    /// <summary>开发 / 截图用：<c>YLT_PICTURE_ZOOM=200</c> 直接指定缩放百分比（截图核对放大后的排版）。</summary>
    private void ApplyZoomOverrideFromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable("YLT_PICTURE_ZOOM");
        if (string.IsNullOrWhiteSpace(text)) return;

        var cleaned = text.Trim().TrimEnd('%', '％').Trim();
        if (!int.TryParse(cleaned, out var percent)) return;

        Cfg.ZoomPercent = PictureScrollMath.EffectiveZoomPercent(percent);
    }

    /// <summary>环境变量开关：不设 / 空 / 0 / false 都算关。</summary>
    internal static bool IsEnvironmentOn(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }
}
