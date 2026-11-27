using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ScreenSticker;
using YuLiangToolkit.Tools.ScreenSticker.Views;
using YuLiangToolkit.Tools.Screenshot.Views;

namespace YuLiangToolkit.Tools.Screenshot.ViewModels;

/// <summary>
/// 「截图工具」的常驻 ViewModel。截图覆盖层不依赖这个页面是否打开：
/// 全局快捷键由 <see cref="ScreenshotTool"/> 注册，直接调这里的 Capture 方法。
/// </summary>
public sealed class ScreenshotViewModel : ToolViewModelBase
{
    private readonly ScreenshotTool _tool;
    private bool _isCapturing;
    private bool _forceCopyNext;
    private ScreenshotHistoryItem? _selectedHistory;
    private BitmapSource? _lastPreview;
    private string _historySummary = "还没有截图历史";
    private string _pinGroupText = "";

    /// <summary>构造：加载配置 / 绑定命令 / 刷新历史。</summary>
    public ScreenshotViewModel(ScreenshotTool tool) : base(ScreenshotConfig.ToolId)
    {
        _tool = tool;
        Cfg = LoadConfig<ScreenshotConfig>();
        Cfg.PinGroupNames ??= new List<string> { "默认", "参考图", "对照图" };
        Cfg.PinRecords ??= new List<ScreenshotPinRecord>();
        if (Cfg.CompressionEnabled) Cfg.ImageFormat = "jpg";

        CaptureCommand = new AsyncRelayCommand(_ => CaptureRegionAsync(), _ => !IsCapturing);
        CaptureCopyCommand = new AsyncRelayCommand(_ => CaptureRegionAsync(copyAfter: true), _ => !IsCapturing);
        CaptureRepeatCommand = new AsyncRelayCommand(_ => CaptureRepeatAsync(), _ => !IsCapturing && !Cfg.LastRegion.IsEmpty);
        CaptureFullCommand = new AsyncRelayCommand(_ => CaptureFullScreenAsync(), _ => !IsCapturing);
        CaptureActiveWindowCommand = new AsyncRelayCommand(_ => CaptureActiveWindowAsync(), _ => !IsCapturing);
        CustomCaptureCommand = new AsyncRelayCommand(_ => CustomCaptureAsync(), _ => !IsCapturing);
        PastePinCommand = new AsyncRelayCommand(_ => PasteClipboardAsPinAsync(), _ => !IsCapturing);
        TogglePinsCommand = new RelayCommand(_ => TogglePins());
        SwitchPinGroupCommand = new RelayCommand(_ => SwitchPinGroup());
        ClearHistoryCommand = new RelayCommand(_ => ClearHistory());
        RefreshHistoryCommand = new RelayCommand(_ => RefreshHistory());
        CopyHistoryCommand = new RelayCommand(_ => CopySelectedHistory(), _ => SelectedHistory is not null);
        PinHistoryCommand = new RelayCommand(_ => PinSelectedHistory(), _ => SelectedHistory is not null);
        OpenHistoryCommand = new RelayCommand(_ => OpenSelectedHistory(), _ => SelectedHistory is not null);
        RevealHistoryCommand = new RelayCommand(_ => RevealSelectedHistory(), _ => SelectedHistory is not null);
        DeleteHistoryCommand = new RelayCommand(_ => DeleteSelectedHistory(), _ => SelectedHistory is not null);
        BrowseSaveFolderCommand = new RelayCommand(_ => BrowseSaveFolder());
        OpenSaveFolderCommand = new RelayCommand(_ => OpenSaveFolder());
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());
        ClearShortcutCommand = new RelayCommand(p => ClearShortcut(p as string), p => p is string);

        Cfg.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ScreenshotConfig.CurrentPinGroup)
                or nameof(ScreenshotConfig.PinGroupNames) or nameof(ScreenshotConfig.PinRecords))
                RefreshPinGroupText();
            if (args.PropertyName is nameof(ScreenshotConfig.ImageFormat))
            {
                OnPropertyChanged(nameof(IsJpg));
                OnPropertyChanged(nameof(SelectedFormat));
                OnPropertyChanged(nameof(EffectiveImageFormat));
            }
            if (args.PropertyName is nameof(ScreenshotConfig.CompressionMode))
            {
                OnPropertyChanged(nameof(IsJpg));
                OnPropertyChanged(nameof(SelectedCompression));
                OnPropertyChanged(nameof(EffectiveImageFormat));
            }
            if (args.PropertyName is nameof(ScreenshotConfig.DefaultTool))
                OnPropertyChanged(nameof(SelectedDefaultTool));
            if (args.PropertyName is nameof(ScreenshotConfig.DefaultThickness))
                OnPropertyChanged(nameof(SelectedDefaultThickness));
            if (args.PropertyName is nameof(ScreenshotConfig.CustomCaptureTarget))
                OnPropertyChanged(nameof(SelectedCustomTarget));
            if (args.PropertyName is nameof(ScreenshotConfig.LastRegionWidth)
                or nameof(ScreenshotConfig.LastRegionHeight))
                CommandManager.InvalidateRequerySuggested();
            if (args.PropertyName is nameof(ScreenshotConfig.HistoryLimit))
            {
                if (Cfg.EffectiveHistoryLimit == 0) ScreenshotHistoryService.Clear();
                else ScreenshotHistoryService.Prune(Cfg.EffectiveHistoryLimit);
                RefreshHistory();
            }
        };

        _tool.ShortcutsChanged += (_, _) => OnPropertyChanged(nameof(ShortcutSummary));

        RefreshHistory();
        RefreshPinGroupText();
        Log($"配置文件：{Cfg.FilePath}");
        Log("截图工具已就绪；快捷键可在右侧或「主页设置 → 快捷键」中修改。");
    }

    /// <summary>强类型配置。</summary>
    public ScreenshotConfig Cfg { get; }

    // ---------- 命令 ----------

    public AsyncRelayCommand CaptureCommand { get; }
    public AsyncRelayCommand CaptureCopyCommand { get; }
    public AsyncRelayCommand CaptureRepeatCommand { get; }
    public AsyncRelayCommand CaptureFullCommand { get; }
    public AsyncRelayCommand CaptureActiveWindowCommand { get; }
    public AsyncRelayCommand CustomCaptureCommand { get; }
    public AsyncRelayCommand PastePinCommand { get; }
    public RelayCommand TogglePinsCommand { get; }
    public RelayCommand SwitchPinGroupCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand RefreshHistoryCommand { get; }
    public RelayCommand CopyHistoryCommand { get; }
    public RelayCommand PinHistoryCommand { get; }
    public RelayCommand OpenHistoryCommand { get; }
    public RelayCommand RevealHistoryCommand { get; }
    public RelayCommand DeleteHistoryCommand { get; }
    public RelayCommand BrowseSaveFolderCommand { get; }
    public RelayCommand OpenSaveFolderCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand ClearShortcutCommand { get; }

    // ---------- 状态 ----------

    /// <summary>正在截图（按钮禁用，防重复进入）。</summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (!Set(ref _isCapturing, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>当前页面的最后一张成品缩略图（左侧预览）。</summary>
    public BitmapSource? LastPreviewImage
    {
        get => _lastPreview;
        private set
        {
            if (!Set(ref _lastPreview, value)) return;
            OnPropertyChanged(nameof(HasLastPreview));
        }
    }

    /// <summary>有没有左侧预览。</summary>
    public bool HasLastPreview => LastPreviewImage is not null;

    /// <summary>历史列表。</summary>
    public ObservableCollection<ScreenshotHistoryItem> HistoryItems { get; } = new();

    /// <summary>当前选中的历史。</summary>
    public ScreenshotHistoryItem? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (!Set(ref _selectedHistory, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>历史摘要（供页面显示）。</summary>
    public string HistorySummary
    {
        get => _historySummary;
        private set => Set(ref _historySummary, value);
    }

    /// <summary>有没有历史（空状态提示用）。</summary>
    public bool HasHistory => HistoryItems.Count > 0;

    /// <summary>贴图组摘要。</summary>
    public string PinGroupText
    {
        get => _pinGroupText;
        private set => Set(ref _pinGroupText, value);
    }

    /// <summary>快捷键摘要。</summary>
    public string ShortcutSummary
    {
        get
        {
            var parts = _tool.GetShortcuts().Where(s => s.Enabled && !s.Primary.IsEmpty)
                .Select(s => $"{s.DisplayText} {s.Name}");
            return !Cfg.EnableGlobalHotkeys
                ? "快捷键已关闭（可在主页设置 → 快捷键中重新启用）"
                : parts.Any() ? string.Join(" · ", parts) : "还没设置快捷键";
        }
    }

    /// <summary>当前输出格式是不是 JPG（启用压缩时由压缩档位接管质量，不再显示自定义 JPG 质量）。</summary>
    public bool IsJpg => !Cfg.CompressionEnabled && string.Equals(Cfg.ImageFormat, "jpg", StringComparison.OrdinalIgnoreCase);

    /// <summary>实际输出格式：开启压缩后统一走 JPG，避免 PNG 把体积拉回去。</summary>
    public string EffectiveImageFormat => Cfg.CompressionEnabled ? "jpg" : Cfg.ImageFormat;

    /// <summary>格式下拉选项。</summary>
    public IReadOnlyList<OptionItem<string>> FormatOptions { get; } = OptionItem.From(
        ("png", "PNG（无损）"), ("jpg", "JPG（较小）"));

    /// <summary>格式下拉桥接。</summary>
    public OptionItem<string> SelectedFormat
    {
        get => OptionItem<string>.Find(FormatOptions, Cfg.ImageFormat) ?? FormatOptions[0];
        set
        {
            if (value is null) return;
            Cfg.ImageFormat = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsJpg));
        }
    }

    /// <summary>成品压缩档位选项。</summary>
    public IReadOnlyList<OptionItem<string>> CompressionOptions { get; } = OptionItem.From(
        ("none", "不压缩（原图 / PNG）"),
        ("light", "轻度压缩（JPG 92）"),
        ("standard", "标准压缩（JPG 82）"),
        ("high", "高压缩（JPG 70）"));

    /// <summary>压缩档位桥接；选择非「不压缩」时输出自动切到 JPG。</summary>
    public OptionItem<string> SelectedCompression
    {
        get => OptionItem<string>.Find(CompressionOptions, Cfg.CompressionMode) ?? CompressionOptions[0];
        set
        {
            if (value is null) return;
            Cfg.CompressionMode = value.Value;
            if (Cfg.CompressionEnabled) Cfg.ImageFormat = "jpg";
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedFormat));
            OnPropertyChanged(nameof(IsJpg));
        }
    }

    /// <summary>自定义截屏目标选项。</summary>
    public IReadOnlyList<OptionItem<string>> CustomTargetOptions { get; } = OptionItem.From(
        ("region", "框选区域"), ("fullscreen", "整块屏幕"), ("active-window", "当前活动窗口"));

    /// <summary>自定义截屏目标桥接。</summary>
    public OptionItem<string> SelectedCustomTarget
    {
        get => OptionItem<string>.Find(CustomTargetOptions, Cfg.CustomCaptureTarget) ?? CustomTargetOptions[0];
        set
        {
            if (value is null) return;
            Cfg.CustomCaptureTarget = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>默认标注工具选项。</summary>
    public IReadOnlyList<OptionItem<string>> ToolOptions { get; } = OptionItem.From(
        ("select", "选择 / 调整选区"),
        ("rectangle", "矩形"),
        ("ellipse", "椭圆"),
        ("arrow", "箭头"),
        ("line", "直线"),
        ("pen", "画笔"),
        ("highlighter", "荧光笔"),
        ("text", "文字"),
        ("number", "序号"),
        ("mosaic", "马赛克"),
        ("blur", "模糊"));

    /// <summary>默认标注工具桥接。</summary>
    public OptionItem<string> SelectedDefaultTool
    {
        get => OptionItem<string>.Find(ToolOptions, Cfg.DefaultTool) ?? ToolOptions[0];
        set
        {
            if (value is null) return;
            Cfg.DefaultTool = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>默认线宽选项。</summary>
    public IReadOnlyList<OptionItem<double>> ThicknessOptions { get; } = OptionItem.From(
        (1.0, "1 px"), (2.0, "2 px"), (3.0, "3 px"), (4.0, "4 px"), (6.0, "6 px"), (8.0, "8 px"), (12.0, "12 px"));

    /// <summary>默认线宽桥接。</summary>
    public OptionItem<double> SelectedDefaultThickness
    {
        get => OptionItem<double>.Find(ThicknessOptions, Cfg.DefaultThickness) ?? ThicknessOptions[2];
        set
        {
            if (value is null) return;
            Cfg.DefaultThickness = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>页面显示出来时刷新历史（切换回来也能看到新截图）。</summary>
    public void OnPageLoaded()
    {
        if (Cfg.EffectiveHistoryLimit == 0) ScreenshotHistoryService.Clear();
        else ScreenshotHistoryService.Prune(Cfg.EffectiveHistoryLimit);
        RefreshHistory();
        RefreshPinGroupText();
        OnPropertyChanged(nameof(ShortcutSummary));
    }

    /// <summary>给快捷键宿主用：从剪贴板贴图（不依赖页面是否打开）。</summary>
    public Task PasteClipboardAsPinFromShortcutAsync() => PasteClipboardAsPinAsync();

    // ---------- 截图流程 ----------

    /// <summary>框选截图；<paramref name="copyAfter"/> 只影响提示文案，实际复制在覆盖层确认时决定。</summary>
    public async Task CaptureRegionAsync(bool copyAfter = false)
    {
        if (IsCapturing) return;
        _forceCopyNext = copyAfter;
        try
        {
            await CaptureCoreAsync(null, copyAfter ? "截图并复制" : "截图", fullScreen: false);
        }
        finally
        {
            _forceCopyNext = false;
        }
    }

    /// <summary>重复上一次成功截图的区域。</summary>
    public async Task CaptureRepeatAsync()
    {
        if (IsCapturing) return;
        var last = Cfg.LastRegion;
        if (last.IsEmpty)
        {
            StatusText = "还没有上一次截图区域，请先截一张图。";
            return;
        }
        await CaptureCoreAsync(last, "重复上次区域", fullScreen: false);
    }

    /// <summary>全屏截图（覆盖层仍可编辑 / 框选）。</summary>
    public async Task CaptureFullScreenAsync()
    {
        if (IsCapturing) return;
        await CaptureCoreAsync(null, "全屏截图", fullScreen: true);
    }

    /// <summary>截取当前活动窗口；如果前台是工具集自己，先隐藏主窗口让用户切到目标窗口。</summary>
    public async Task CaptureActiveWindowAsync()
    {
        if (IsCapturing) return;
        var main = Application.Current?.MainWindow;
        var wasVisible = main is { IsVisible: true } && main.WindowState != WindowState.Minimized;
        var hiddenByUs = false;
        try
        {
            var bounds = ScreenCaptureService.GetForegroundWindowBounds();
            if (bounds is null || IsOwnWindowForeground())
            {
                if (wasVisible)
                {
                    main!.Hide();
                    hiddenByUs = true;
                    await Task.Delay(700);   // 让 Windows 把前台交还给上一个应用
                }
                bounds = ScreenCaptureService.GetForegroundWindowBounds();
            }

            if (bounds is null || IsOwnWindowForeground())
            {
                StatusText = "没有可截的活动窗口，请切到目标窗口后重试。";
                return;
            }

            await CaptureCoreAsync(bounds, "活动窗口截图", fullScreen: false);
        }
        finally
        {
            if (hiddenByUs)
            {
                try
                {
                    main!.Show();
                    if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                    main.Activate();
                }
                catch { /* 忽略 */ }
            }
        }
    }

    /// <summary>按设置里的目标 / 延迟执行自定义截屏。</summary>
    public async Task CustomCaptureAsync()
    {
        if (IsCapturing) return;
        var delay = Math.Clamp(Cfg.CustomCaptureDelaySeconds, 0, 30);
        if (delay > 0)
        {
            StatusText = $"将在 {delay} 秒后截图…";
            await Task.Delay(TimeSpan.FromSeconds(delay));
        }

        switch ((Cfg.CustomCaptureTarget ?? "region").ToLowerInvariant())
        {
            case "fullscreen":
                await CaptureFullScreenAsync();
                break;
            case "active-window":
                await CaptureActiveWindowAsync();
                break;
            default:
                await CaptureRegionAsync();
                break;
        }
    }

    private async Task CaptureCoreAsync(ScreenshotPixelRect? initialPhysical, string label, bool fullScreen)
    {
        if (IsCapturing) return;
        IsCapturing = true;
        var main = Application.Current?.MainWindow;
        var wasVisible = main is { IsVisible: true } && main.WindowState != WindowState.Minimized;
        try
        {
            StatusText = $"正在准备{label}…";
            if (wasVisible)
            {
                main!.Hide();
                await Task.Delay(130);
            }

            ScreenOverlayState.SetScreenshotActive(true);
            var desktop = ScreenCaptureService.CaptureVirtualScreen();
            Rect? initialDip = null;
            if (fullScreen)
            {
                initialDip = new Rect(0, 0,
                    Math.Max(1, SystemParameters.VirtualScreenWidth),
                    Math.Max(1, SystemParameters.VirtualScreenHeight));
            }
            else if (initialPhysical is { IsEmpty: false } physical)
            {
                initialDip = ScreenshotGeometry.MapSelectionBack(physical,
                    Math.Max(1, SystemParameters.VirtualScreenWidth),
                    Math.Max(1, SystemParameters.VirtualScreenHeight), desktop.Bounds);
            }

            var overlay = new ScreenshotOverlayWindow(desktop, Cfg, initialDip);
            var accepted = overlay.ShowDialog() == true;
            var result = accepted ? overlay.Result : null;
            if (result is null)
            {
                StatusText = "已取消截图。";
                return;
            }

            result = result with { Image = ScreenshotCompression.Apply(result.Image, Cfg.CompressionMode) };
            Cfg.RememberRegion(result.PhysicalRegion);
            Cfg.SaveNow();

            if (Cfg.KeepHistory && Cfg.EffectiveHistoryLimit > 0)
            {
                var historyPath = ScreenshotHistoryService.Save(result.Image);
                if (historyPath is not null) ScreenshotHistoryService.Prune(Cfg.EffectiveHistoryLimit);
            }

            LastPreviewImage = MakeThumbnail(result.Image, 360);
            StatusText = await ApplyResultAsync(result, label);
            RefreshHistory();
            Log($"✓ {label}：{result.PhysicalRegion.SizeText}，动作 {result.Action}");
        }
        catch (Exception ex)
        {
            StatusText = $"截图失败：{ex.Message}";
            Log("✗ 截图失败：" + ex);
        }
        finally
        {
            ScreenOverlayState.SetScreenshotActive(false);
            if (wasVisible)
            {
                try
                {
                    main!.Show();
                    if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                    main.Activate();
                }
                catch { /* 忽略 */ }
            }
            IsCapturing = false;
        }
    }

    private bool IsOwnWindowForeground()
    {
        try
        {
            var main = Application.Current?.MainWindow;
            if (main is null) return false;
            var hwnd = new WindowInteropHelper(main).Handle;
            return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
        }
        catch { return false; }
    }

    private async Task<string> ApplyResultAsync(ScreenshotOverlayResult result, string label)
    {
        var action = result.Action;
        var shouldCopy = action == ScreenshotAction.Copy || _forceCopyNext || Cfg.AutoCopyOnConfirm;
        var shouldAutoSave = Cfg.AutoSaveOnCapture && action is not ScreenshotAction.Save;
        var parts = new List<string> { $"已完成{label}" };

        if (shouldCopy)
        {
            if (ScreenshotClipboard.TrySetImage(result.Image, out var clipboardError))
                parts.Add("已复制到剪贴板");
            else
            {
                parts.Add("复制失败：" + clipboardError);
                Log("复制到剪贴板失败：" + clipboardError);
            }
        }

        if (action == ScreenshotAction.Save)
        {
            try
            {
                var folder = Cfg.EffectiveSaveFolder;
                Directory.CreateDirectory(folder);
                var ext = ScreenshotGeometry.NormalizeExtension(EffectiveImageFormat);
                var dialog = new SaveFileDialog
                {
                    Title = "保存截图",
                    InitialDirectory = folder,
                    FileName = Path.GetFileNameWithoutExtension(ScreenshotGeometry.BuildOutputPath(folder, Cfg.FilePrefix, ext, DateTime.Now)),
                    DefaultExt = ext,
                    Filter = ext == ".jpg" ? "JPEG 图片|*.jpg|PNG 图片|*.png" : "PNG 图片|*.png|JPEG 图片|*.jpg",
                    AddExtension = true,
                    OverwritePrompt = true,
                };
                if (dialog.ShowDialog() == true)
                {
                    var chosenExt = ScreenshotGeometry.NormalizeExtension(Path.GetExtension(dialog.FileName));
                    ScreenCaptureService.Save(result.Image, dialog.FileName, chosenExt, Cfg.JpgQuality);
                    parts.Add("已保存：" + dialog.FileName);
                    if (Cfg.OpenFolderAfterSave) OpenFolder(Path.GetDirectoryName(dialog.FileName));
                }
                else
                {
                    parts.Add("已取消保存");
                }
            }
            catch (Exception ex)
            {
                parts.Add("保存失败：" + ex.Message);
                Log("✗ 保存失败：" + ex);
            }
        }

        if (shouldAutoSave)
        {
            try
            {
                var ext = ScreenshotGeometry.NormalizeExtension(EffectiveImageFormat);
                var folder = Cfg.EffectiveSaveFolder;
                Directory.CreateDirectory(folder);
                var path = ScreenshotGeometry.BuildOutputPath(folder, Cfg.FilePrefix, ext, DateTime.Now);
                ScreenCaptureService.Save(result.Image, path, ext, Cfg.JpgQuality);
                parts.Add("已自动保存");
                Log("已自动保存：" + path);
                if (Cfg.OpenFolderAfterSave) OpenFolder(folder);
            }
            catch (Exception ex)
            {
                parts.Add("自动保存失败：" + ex.Message);
                Log("自动保存失败：" + ex.Message);
            }
        }

        if (action == ScreenshotAction.Pin)
        {
            var pinResult = await PinBitmapAsync(result.Image, "截图");
            if (!string.IsNullOrWhiteSpace(pinResult)) parts.Add(pinResult);
        }

        return string.Join(" · ", parts);
    }

    // ---------- 贴图 ----------

    private async Task<string?> PinBitmapAsync(BitmapSource image, string sourceLabel)
    {
        try
        {
            var storage = ToolStorage.For(ScreenshotConfig.ToolId);
            storage.EnsureCreated();
            var pinsFolder = storage.PathOf("pins");
            Directory.CreateDirectory(pinsFolder);
            var path = ScreenshotGeometry.BuildOutputPath(pinsFolder, "pin", ".png", DateTime.Now);
            ScreenCaptureService.Save(image, path, "png");
            var window = StickerSession.Current.AddSticker(path, topmost: Cfg.PinTopmost);
            if (window is null)
                return "贴图失败：" + (StickerSession.Current.LastError ?? "未知错误");

            Cfg.RememberPin(path, Cfg.EffectivePinGroup);
            Cfg.SaveNow();
            RefreshPinGroupText();
            Log($"贴图：{path}");
            return $"已贴到桌面（{Cfg.CurrentPinGroupName}组）";
        }
        catch (Exception ex)
        {
            Log("✗ 贴图失败：" + ex);
            return "贴图失败：" + ex.Message;
        }
        finally
        {
            await Task.CompletedTask;
        }
    }

    private async Task PasteClipboardAsPinAsync()
    {
        if (IsCapturing) return;
        IsCapturing = true;
        try
        {
            var existing = new HashSet<string>(StickerSession.Current.Windows.Select(w => w.ImagePath),
                StringComparer.OrdinalIgnoreCase);
            var result = await StickerSession.Current.PasteClipboardAsync();
            if (result.Error is not null)
            {
                StatusText = result.Error;
                return;
            }
            if (result.Added == 0)
            {
                StatusText = "剪贴板里没有可贴的图片。";
                return;
            }

            foreach (var window in StickerSession.Current.Windows.Where(w => !existing.Contains(w.ImagePath)))
                Cfg.RememberPin(window.ImagePath, Cfg.EffectivePinGroup);
            Cfg.SaveNow();
            RefreshPinGroupText();
            StatusText = $"已从剪贴板贴 {result.Added} 张图到「{Cfg.CurrentPinGroupName}」组";
            Log(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = "贴图失败：" + ex.Message;
            Log("✗ 贴图失败：" + ex);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    private IReadOnlyList<StickerWindow> ScreenshotPinWindows()
        => StickerSession.Current.Windows.Where(w => Cfg.IsScreenshotPin(w.ImagePath)).ToList();

    private void TogglePins()
    {
        var windows = ScreenshotPinWindows();
        if (windows.Count == 0)
        {
            StatusText = "截图工具当前没有贴图。";
            return;
        }

        if (windows.Any(w => w.IsVisible))
        {
            foreach (var window in windows) try { window.Hide(); } catch { }
            StatusText = "已隐藏截图工具的贴图";
        }
        else
        {
            ShowCurrentPinGroup();
            StatusText = $"已显示截图工具「{Cfg.CurrentPinGroupName}」组贴图";
        }
    }

    private void SwitchPinGroup()
    {
        var count = Cfg.PinGroupNames.Count;
        if (count <= 1)
        {
            StatusText = "当前只有一个贴图组，可在配置里添加组名。";
            return;
        }
        Cfg.CurrentPinGroup = (Cfg.EffectivePinGroup + 1) % count;
        Cfg.SaveNow();
        RefreshPinGroupText();
        ShowCurrentPinGroup();
        StatusText = $"已切换到截图工具「{Cfg.CurrentPinGroupName}」组";
    }

    private void ShowCurrentPinGroup()
    {
        foreach (var window in ScreenshotPinWindows())
        {
            try
            {
                if (Cfg.GetPinGroup(window.ImagePath) == Cfg.EffectivePinGroup) window.Show();
                else window.Hide();
            }
            catch { /* 单张失败不影响其它 */ }
        }
    }

    private void RefreshPinGroupText()
    {
        PinGroupText = $"当前贴图组：{Cfg.CurrentPinGroupName}（{Cfg.EffectivePinGroup + 1}/{Math.Max(1, Cfg.PinGroupNames.Count)}）";
    }

    // ---------- 历史 ----------

    /// <summary>重新扫描历史（页面/清空后调用）。</summary>
    public void RefreshHistory()
    {
        var selectedPath = SelectedHistory?.FullPath;
        HistoryItems.Clear();
        foreach (var entry in ScreenshotHistoryService.List())
        {
            HistoryItems.Add(new ScreenshotHistoryItem
            {
                Entry = entry,
                Thumbnail = LoadThumbnail(entry.FullPath),
            });
        }

        if (selectedPath is not null)
            SelectedHistory = HistoryItems.FirstOrDefault(x =>
                string.Equals(x.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        else
            SelectedHistory = HistoryItems.FirstOrDefault();

        HistorySummary = HistoryItems.Count == 0
            ? "还没有截图历史"
            : $"{HistoryItems.Count} 张 · 共 {FileSize.Format(HistoryItems.Sum(x => x.Entry.SizeBytes))}";
        if (LastPreviewImage is null && HistoryItems.Count > 0)
            LastPreviewImage = HistoryItems[0].Thumbnail;
        OnPropertyChanged(nameof(HasHistory));
    }

    private void CopySelectedHistory()
    {
        var item = SelectedHistory;
        if (item is null) return;
        var image = LoadFullImage(item.FullPath);
        if (image is null) { StatusText = "历史图片读取失败（文件可能被删除）"; return; }
        if (!ScreenshotClipboard.TrySetImage(image, out var error)) { StatusText = error; return; }
        StatusText = "已复制历史截图到剪贴板。";
    }

    private void PinSelectedHistory()
    {
        var item = SelectedHistory;
        if (item is null) return;
        var image = LoadFullImage(item.FullPath);
        if (image is null) { StatusText = "历史图片读取失败（文件可能被删除）"; return; }
        _ = PinBitmapAsync(image, "历史截图");
    }

    private void OpenSelectedHistory()
    {
        if (SelectedHistory is null) return;
        OpenFile(SelectedHistory.FullPath);
    }

    private void RevealSelectedHistory()
    {
        if (SelectedHistory is null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{SelectedHistory.FullPath}\"",
                UseShellExecute = true,
            });
        }
        catch { /* 忽略 */ }
    }

    private void DeleteSelectedHistory()
    {
        var item = SelectedHistory;
        if (item is null) return;
        if (MessageBox.Show($"删除这张截图历史？\n{item.FileName}", "删除确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        if (ScreenshotHistoryService.Delete(item.FullPath))
        {
            LastPreviewImage = null;
            StatusText = "已删除这张历史截图。";
            RefreshHistory();
        }
        else StatusText = "删除失败（文件可能正在被使用）。";
    }

    private void ClearHistory()
    {
        var count = HistoryItems.Count;
        if (count == 0)
        {
            StatusText = "历史已经是空的。";
            return;
        }
        if (MessageBox.Show($"清空全部 {count} 张截屏历史？\n只会删除 Cache 里的历史缩略图，不会删除已保存到文件夹的原图或桌面贴图。",
                "清空截屏历史", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var cleared = ScreenshotHistoryService.Clear();
        SelectedHistory = null;
        LastPreviewImage = null;
        RefreshHistory();
        StatusText = cleared && HistoryItems.Count == 0
            ? "截屏历史已清空。"
            : "部分历史文件未能删除，请稍后重试。";
    }

    // ---------- 设置辅助 ----------

    private void BrowseSaveFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择截图输出文件夹",
            InitialDirectory = Directory.Exists(Cfg.EffectiveSaveFolder) ? Cfg.EffectiveSaveFolder : "",
        };
        if (dialog.ShowDialog() == true) Cfg.SaveFolder = dialog.FolderName;
    }

    private void OpenSaveFolder()
    {
        try
        {
            Directory.CreateDirectory(Cfg.EffectiveSaveFolder);
            OpenFolder(Cfg.EffectiveSaveFolder);
        }
        catch (Exception ex) { StatusText = "打开文件夹失败：" + ex.Message; }
    }

    private void ResetSettings()
    {
        if (MessageBox.Show("恢复截图工具的全部设置？不会删除历史截图和已贴到桌面的图。", "恢复默认设置",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var defaults = new ScreenshotConfig();
        Cfg.EnableGlobalHotkeys = defaults.EnableGlobalHotkeys;
        Cfg.CaptureHotkey = defaults.CaptureHotkey;
        Cfg.CaptureCopyHotkey = defaults.CaptureCopyHotkey;
        Cfg.RepeatHotkey = defaults.RepeatHotkey;
        Cfg.PasteHotkey = defaults.PasteHotkey;
        Cfg.CaptureHotkeyEnabled = true;
        Cfg.CaptureCopyHotkeyEnabled = true;
        Cfg.RepeatHotkeyEnabled = true;
        Cfg.PasteHotkeyEnabled = true;
        Cfg.AutoCopyOnConfirm = defaults.AutoCopyOnConfirm;
        Cfg.AutoSaveOnCapture = defaults.AutoSaveOnCapture;
        Cfg.SaveFolder = "";
        Cfg.FilePrefix = defaults.FilePrefix;
        Cfg.ImageFormat = defaults.ImageFormat;
        Cfg.JpgQuality = defaults.JpgQuality;
        Cfg.CompressionMode = defaults.CompressionMode;
        Cfg.OpenFolderAfterSave = defaults.OpenFolderAfterSave;
        Cfg.CustomCaptureTarget = defaults.CustomCaptureTarget;
        Cfg.CustomCaptureDelaySeconds = defaults.CustomCaptureDelaySeconds;
        Cfg.MagnifierEnabled = defaults.MagnifierEnabled;
        Cfg.ShowSizeBadge = defaults.ShowSizeBadge;
        Cfg.DefaultColor = defaults.DefaultColor;
        Cfg.DefaultThickness = defaults.DefaultThickness;
        Cfg.DefaultFill = defaults.DefaultFill;
        Cfg.DefaultTool = defaults.DefaultTool;
        Cfg.KeepHistory = defaults.KeepHistory;
        Cfg.HistoryLimit = defaults.HistoryLimit;
        Cfg.PinTopmost = defaults.PinTopmost;
        Cfg.PinGroupNames = new List<string> { "默认", "参考图", "对照图" };
        Cfg.CurrentPinGroup = 0;
        Cfg.SaveNow();
        RefreshHistory();
        RefreshPinGroupText();
        OnPropertyChanged(nameof(ShortcutSummary));
        StatusText = "已恢复默认设置。";
    }

    private void ClearShortcut(string? which)
    {
        switch (which)
        {
            case "capture": Cfg.CaptureHotkey = ""; break;
            case "capture-copy": Cfg.CaptureCopyHotkey = ""; break;
            case "repeat": Cfg.RepeatHotkey = ""; break;
            case "paste": Cfg.PasteHotkey = ""; break;
        }
        _tool.ApplyShortcuts();
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = File.OpenRead(path);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 220;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static BitmapSource? LoadFullImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = File.OpenRead(path);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static BitmapSource MakeThumbnail(BitmapSource source, int maxWidth)
    {
        var scale = Math.Min(1.0, maxWidth / (double)Math.Max(1, source.PixelWidth));
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static void OpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
        catch { /* 忽略 */ }
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch { /* 忽略 */ }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
