using System.Windows;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.ScreenPen.ViewModels;
using YuLiangToolkit.Tools.ScreenPen.Views;

namespace YuLiangToolkit.Tools.ScreenPen;

/// <summary>
/// 「屏幕画笔」：Epic Pen 风格的桌面透明批注覆盖层，支持画笔 / 荧光笔 / 橡皮 / 形状 / 文字，
/// 额外调色板、全局快捷键，并与截图工具共享屏幕覆盖状态。
/// </summary>
public sealed class ScreenPenTool : ITool, IShortcutProvider, IShortcutActionHost
{
    public const string ShortcutIdToggle = "toggle-overlay";
    public const string ShortcutIdInput = "toggle-input";
    public const string ShortcutIdClear = "clear";
    public const string ShortcutIdUndo = "undo";

    private readonly ScreenPenViewModel _viewModel;
    private readonly ShortcutProviderAdapter _adapter;

    public ScreenPenTool()
    {
        _viewModel = new ScreenPenViewModel(this);
        _adapter = new ShortcutProviderAdapter(BuildSlots, ApplyShortcuts,
            () => ShortcutsChanged?.Invoke(this, EventArgs.Empty));
        Cfg.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ScreenPenConfig.EnableGlobalHotkeys)
                or nameof(ScreenPenConfig.ToggleOverlayHotkey)
                or nameof(ScreenPenConfig.ToggleInputHotkey)
                or nameof(ScreenPenConfig.ClearHotkey)
                or nameof(ScreenPenConfig.UndoHotkey)
                or nameof(ScreenPenConfig.ToggleOverlayHotkeyEnabled)
                or nameof(ScreenPenConfig.ToggleInputHotkeyEnabled)
                or nameof(ScreenPenConfig.ClearHotkeyEnabled)
                or nameof(ScreenPenConfig.UndoHotkeyEnabled))
                ApplyShortcuts();
        };
    }

    public ScreenPenViewModel ViewModel => _viewModel;
    public ScreenPenConfig Cfg => _viewModel.Cfg;

    public string Id => ScreenPenConfig.ToolId;
    public string Name => "屏幕画笔";
    public string Description =>
        "Epic Pen 风格桌面批注：透明覆盖层上画笔、荧光笔、橡皮、直线、矩形、椭圆、箭头、文字，"
        + "支持撤销 / 重做 / 清空、白板 / 黑板、额外调色板与全局快捷键；画笔内容可被截图工具捕获，截图时自动暂停。";
    public string EmojiIcon => "✏️";
    public string Category => "桌面";
    public IReadOnlyList<string> CategoryPath => new[] { "桌面" };

    public IReadOnlyList<string> Tags => new[]
    {
        "屏幕画笔", "屏幕批注", "批注", "画板", "Epic Pen", "白板", "黑板",
        "调色板", "快捷键", "透明覆盖层",
    };

    public int Order => 173;

    public FrameworkElement CreateView() => new ScreenPenView { DataContext = _viewModel };

    public string ShortcutToolId => Id;
    public string ShortcutToolName => Name;
    public IReadOnlyList<string> ShortcutCategoryPath => CategoryPath;

    public bool ShortcutsEnabled
    {
        get => Cfg.EnableGlobalHotkeys;
        set
        {
            if (Cfg.EnableGlobalHotkeys == value) return;
            Cfg.EnableGlobalHotkeys = value;
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
        var config = Cfg;
        return new[]
        {
            new ShortcutProviderAdapter.Slot(
                ShortcutIdToggle, "显示 / 隐藏屏幕画笔",
                () => config.ToggleOverlayHotkey, value => config.ToggleOverlayHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.ToggleOverlayHotkeyEnabled,
                WriteEnabled: value => config.ToggleOverlayHotkeyEnabled = value,
                Description: "屏幕画笔：切换覆盖层显示"),
            new ShortcutProviderAdapter.Slot(
                ShortcutIdInput, "切换绘制 / 鼠标穿透",
                () => config.ToggleInputHotkey, value => config.ToggleInputHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.ToggleInputHotkeyEnabled,
                WriteEnabled: value => config.ToggleInputHotkeyEnabled = value,
                Description: "屏幕画笔：切换绘制模式和鼠标穿透"),
            new ShortcutProviderAdapter.Slot(
                ShortcutIdClear, "清空屏幕批注",
                () => config.ClearHotkey, value => config.ClearHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.ClearHotkeyEnabled,
                WriteEnabled: value => config.ClearHotkeyEnabled = value,
                Description: "屏幕画笔：清空当前所有批注"),
            new ShortcutProviderAdapter.Slot(
                ShortcutIdUndo, "撤销上一笔",
                () => config.UndoHotkey, value => config.UndoHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.UndoHotkeyEnabled,
                WriteEnabled: value => config.UndoHotkeyEnabled = value,
                Description: "屏幕画笔：撤销上一笔"),
        };
    }

    public void InvokeShortcut(string shortcutId)
    {
        if (ScreenOverlayState.IsScreenshotActive) return;
        switch (shortcutId)
        {
            case ShortcutIdToggle:
                _viewModel.ToggleOverlay();
                break;
            case ShortcutIdInput:
                _viewModel.ToggleDrawing();
                break;
            case ShortcutIdClear:
                _viewModel.Session.Clear();
                break;
            case ShortcutIdUndo:
                _viewModel.Session.Undo();
                break;
        }
    }
}
