using System.Windows;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Screenshot.ViewModels;
using YuLiangToolkit.Tools.Screenshot.Views;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>
/// 「截图工具」：Snipaste 风格的截图 → 框选 → 标注 → 复制 / 保存 / 贴图。
///
/// <para><b>快捷键</b>：四条全局快捷键在主页设置和工具页都能改。工具实例本身实现
/// <see cref="IShortcutActionHost"/>，即使截图页没打开也能响应热键。</para>
///
/// <para><b>没有托盘图标 / 托盘右键菜单</b>：所有动作都收在工具页的「操作台」与「设置」中，
/// 包括清空截屏历史、隐藏/显示贴图、切换贴图组。</para>
/// </summary>
public sealed class ScreenshotTool : ITool, IShortcutProvider, IShortcutActionHost
{
    public const string ShortcutIdCapture = "capture";
    public const string ShortcutIdCaptureCopy = "capture-copy";
    public const string ShortcutIdRepeat = "repeat-last";
    public const string ShortcutIdPaste = "paste-pin";

    private readonly ShortcutProviderAdapter _adapter;
    private readonly ScreenshotViewModel _viewModel;

    /// <summary>构造：建常驻 ViewModel（页面未打开也响应全局热键）。</summary>
    public ScreenshotTool()
    {
        _viewModel = new ScreenshotViewModel(this);
        _adapter = new ShortcutProviderAdapter(BuildSlots, ApplyShortcuts,
            () => ShortcutsChanged?.Invoke(this, EventArgs.Empty));

        Config.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ScreenshotConfig.EnableGlobalHotkeys)
                or nameof(ScreenshotConfig.CaptureHotkey) or nameof(ScreenshotConfig.CaptureCopyHotkey)
                or nameof(ScreenshotConfig.RepeatHotkey) or nameof(ScreenshotConfig.PasteHotkey)
                or nameof(ScreenshotConfig.CaptureHotkeyEnabled) or nameof(ScreenshotConfig.CaptureCopyHotkeyEnabled)
                or nameof(ScreenshotConfig.RepeatHotkeyEnabled) or nameof(ScreenshotConfig.PasteHotkeyEnabled))
                ApplyShortcuts();
        };
    }

    /// <summary>页面与快捷键共用的 ViewModel。</summary>
    public ScreenshotViewModel ViewModel => _viewModel;

    /// <summary>强类型配置。</summary>
    public ScreenshotConfig Config => _viewModel.Cfg;

    // ================================================================== ITool

    public string Id => ScreenshotConfig.ToolId;

    public string Name => "截图工具";

    public string Description =>
        "Snipaste 风格截图：全屏冻结后框选，拖动 / 缩放选区，放大镜取色；"
        + "支持矩形、椭圆、箭头、直线、画笔、荧光笔、文字、序号、马赛克、模糊、橡皮擦与撤销重做；"
        + "一键复制 / 保存 / 贴图，带截屏历史、贴图组、四个全局快捷键。";

    public string EmojiIcon => "📷";

    public string Category => "系统";

    public IReadOnlyList<string> CategoryPath => new[] { "系统" };

    public IReadOnlyList<string> Tags => new[]
    {
        "截图", "截屏", "Snipaste", "框选", "标注", "编辑", "贴图", "钉图", "历史",
        "快捷键", "取色", "放大镜", "马赛克", "模糊", "OCR",
    };

    public int Order => 172;

    public FrameworkElement CreateView()
        => new ScreenshotView { DataContext = _viewModel };

    // ================================================================== IShortcutProvider

    public string ShortcutToolId => Id;

    public string ShortcutToolName => Name;

    public IReadOnlyList<string> ShortcutCategoryPath => CategoryPath;

    public bool ShortcutsEnabled
    {
        get => Config.EnableGlobalHotkeys;
        set
        {
            if (Config.EnableGlobalHotkeys == value) return;
            Config.EnableGlobalHotkeys = value;
            ApplyShortcuts();
        }
    }

    public event EventHandler? ShortcutsChanged;

    public IReadOnlyList<ShortcutDefinition> GetShortcuts() => _adapter.Build(Id);

    public void ApplyShortcuts()
    {
        ShortcutHub.ApplyAll();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<ShortcutProviderAdapter.Slot> BuildSlots()
    {
        var config = Config;
        return new[]
        {
            new ShortcutProviderAdapter.Slot(
                ShortcutIdCapture, "截屏",
                () => config.CaptureHotkey, value => config.CaptureHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.CaptureHotkeyEnabled,
                WriteEnabled: value => config.CaptureHotkeyEnabled = value,
                Description: "截图：打开框选编辑覆盖层"),

            new ShortcutProviderAdapter.Slot(
                ShortcutIdCaptureCopy, "截屏并复制",
                () => config.CaptureCopyHotkey, value => config.CaptureCopyHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.CaptureCopyHotkeyEnabled,
                WriteEnabled: value => config.CaptureCopyHotkeyEnabled = value,
                Description: "截图：确认后直接复制到剪贴板"),

            new ShortcutProviderAdapter.Slot(
                ShortcutIdRepeat, "重复上次区域",
                () => config.RepeatHotkey, value => config.RepeatHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.RepeatHotkeyEnabled,
                WriteEnabled: value => config.RepeatHotkeyEnabled = value,
                Description: "截图：按上一次的框选区域直接打开"),

            new ShortcutProviderAdapter.Slot(
                ShortcutIdPaste, "贴图（剪贴板）",
                () => config.PasteHotkey, value => config.PasteHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.PasteHotkeyEnabled,
                WriteEnabled: value => config.PasteHotkeyEnabled = value,
                Description: "把剪贴板图片贴到桌面"),
        };
    }

    // ================================================================== IShortcutActionHost

    public void InvokeShortcut(string shortcutId)
    {
        switch (shortcutId)
        {
            case ShortcutIdCapture:
                _ = _viewModel.CaptureRegionAsync();
                break;
            case ShortcutIdCaptureCopy:
                _ = _viewModel.CaptureRegionAsync(copyAfter: true);
                break;
            case ShortcutIdRepeat:
                _ = _viewModel.CaptureRepeatAsync();
                break;
            case ShortcutIdPaste:
                _ = _viewModel.PasteClipboardAsPinFromShortcutAsync();
                break;
        }
    }
}
