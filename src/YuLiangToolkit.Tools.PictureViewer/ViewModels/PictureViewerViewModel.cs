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
            (PictureScrollMode.Vertical, PictureScrollModes.DescribeCompact(PictureScrollMode.Vertical)),
            (PictureScrollMode.HorizontalLeftToRight, PictureScrollModes.DescribeCompact(PictureScrollMode.HorizontalLeftToRight)),
            (PictureScrollMode.HorizontalRightToLeft, PictureScrollModes.DescribeCompact(PictureScrollMode.HorizontalRightToLeft)));

        OpenFolderCommand = new AsyncRelayCommand(_ => OpenFolderAsync());
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsVisible = !IsSettingsVisible);
        CloseSettingsCommand = new RelayCommand(_ => IsSettingsVisible = false);
        ZoomInCommand = new RelayCommand(_ => StepZoom(1));
        ZoomOutCommand = new RelayCommand(_ => StepZoom(-1));

        ApplyModeOverrideFromEnvironment();
        ApplyZoomOverrideFromEnvironment();
    }

    // ---------------------------------------------------------------- 配置与选项

    /// <summary>强类型配置（<c>Config\picture-viewer\config.json</c>）。</summary>
    public PictureViewerConfig Cfg { get; }

    /// <summary>三种滚动方式的下拉选项。</summary>
    public IReadOnlyList<OptionItem<PictureScrollMode>> ModeOptions { get; }

    /// <summary>下拉框的桥接属性：一改就写进配置，并通知界面重建长带（保持阅读位置）。</summary>
    public OptionItem<PictureScrollMode> SelectedMode
    {
        get => OptionItem<PictureScrollMode>.Find(ModeOptions, Cfg.ScrollModeEffective) ?? ModeOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.ScrollModeEffective) return;
            Cfg.ScrollMode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeHint));
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>当前滚动方式的一句说明。</summary>
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
            OnPropertyChanged(nameof(ImageCountText));
        }
    }

    /// <summary>文件夹名（标题 / 状态文案用）。</summary>
    public string FolderName => string.IsNullOrWhiteSpace(_folderPath)
        ? ""
        : Path.GetFileName(_folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>工具条第二行的路径文本。</summary>
    public string FolderPathText => string.IsNullOrWhiteSpace(_folderPath) ? "还没有打开文件夹" : _folderPath;

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

    /// <summary>展开 / 收起设置面板。</summary>
    public RelayCommand ToggleSettingsCommand { get; }

    /// <summary>收起设置面板。</summary>
    public RelayCommand CloseSettingsCommand { get; }

    /// <summary>放大 10%（工具条右侧放大镜 +）。</summary>
    public RelayCommand ZoomInCommand { get; }

    /// <summary>缩小 10%（工具条右侧放大镜 −）。</summary>
    public RelayCommand ZoomOutCommand { get; }

    // ---------------------------------------------------------------- 加载

    /// <summary>弹文件夹对话框 → 加载。取消对话框时什么都不做。</summary>
    public async Task OpenFolderAsync()
    {
        var folder = FolderPicker?.Invoke();
        if (!string.IsNullOrWhiteSpace(folder)) await LoadFolderAsync(folder);
    }

    /// <summary>
    /// 加载一个文件夹：后台读图片尺寸（只读文件头，不进内存），完成后换掉页清单并通知界面重建。
    /// 传进来的目录不存在 / 里面没图片时不会抛异常，只把状态文字改成提示。
    /// </summary>
    public async Task LoadFolderAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;

        var full = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(name)) name = full;

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

            StatusText = pages.Count == 0
                ? $"「{name}」里没有找到图片（支持 jpg / jpeg / png / bmp / gif / webp / tif / tiff）"
                : $"已加载「{name}」共 {pages.Count} 张 · {PictureScrollModes.Describe(Cfg.ScrollModeEffective)}";
        }
        catch (Exception ex)
        {
            Pages = Array.Empty<PicturePage>();
            FolderPath = "";
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

    // ---------------------------------------------------------------- 环境变量（开发用）

    /// <summary>开发 / 截图用：<c>YLT_PICTURE_MODE=vertical|ltr|rtl</c> 直接指定滚动方式。</summary>
    private void ApplyModeOverrideFromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable("YLT_PICTURE_MODE");
        if (string.IsNullOrWhiteSpace(text)) return;

        PictureScrollMode? mode = text.Trim().ToLowerInvariant() switch
        {
            "vertical" or "v" or "上下" or "1" => PictureScrollMode.Vertical,
            "ltr" or "left-to-right" or "lefttoright" or "从左到右" or "2" => PictureScrollMode.HorizontalLeftToRight,
            "rtl" or "right-to-left" or "righttoleft" or "从右到左" or "3" => PictureScrollMode.HorizontalRightToLeft,
            _ => null,
        };
        if (mode is null) return;

        Cfg.ScrollMode = mode.Value;
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(ModeHint));
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
