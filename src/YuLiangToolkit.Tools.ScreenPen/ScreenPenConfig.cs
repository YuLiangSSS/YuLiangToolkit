using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.ScreenPen;

/// <summary>屏幕画笔（Epic Pen 风格）的持久化配置。</summary>
public sealed class ScreenPenConfig : ToolConfig
{
    public const string ToolId = "screen-pen";

    private string _activeTool = "pen";
    private string _penColor = "#FFFF3B30";
    private double _penThickness = 4;
    private double _highlighterThickness = 18;
    private bool _showToolbar = true;
    private bool _startVisible;
    private string _backdrop = "none";
    private string _toggleOverlayHotkey = "Ctrl+Shift+F6";
    private string _toggleInputHotkey = "Ctrl+Shift+F7";
    private string _clearHotkey = "Ctrl+Shift+F8";
    private string _undoHotkey = "Ctrl+Shift+F9";
    private bool _toggleOverlayHotkeyEnabled = true;
    private bool _toggleInputHotkeyEnabled = true;
    private bool _clearHotkeyEnabled = true;
    private bool _undoHotkeyEnabled = true;
    private bool _enableGlobalHotkeys = true;
    private List<string> _paletteColors = DefaultPalette();

    /// <summary>当前工具：pen / highlighter / eraser / line / rectangle / ellipse / arrow / text。</summary>
    public string ActiveTool
    {
        get => _activeTool;
        set => SetAndSave(ref _activeTool, value ?? "pen");
    }

    /// <summary>画笔颜色（#AARRGGBB 或 #RRGGBB）。</summary>
    public string PenColor
    {
        get => _penColor;
        set => SetAndSave(ref _penColor, string.IsNullOrWhiteSpace(value) ? "#FFFF3B30" : value);
    }

    /// <summary>普通画笔线宽（DIP）。</summary>
    public double PenThickness
    {
        get => _penThickness;
        set => SetAndSave(ref _penThickness, Math.Clamp(value, 1, 80));
    }

    /// <summary>荧光笔线宽（DIP）。</summary>
    public double HighlighterThickness
    {
        get => _highlighterThickness;
        set => SetAndSave(ref _highlighterThickness, Math.Clamp(value, 4, 80));
    }

    /// <summary>是否显示屏幕上的悬浮工具栏。</summary>
    public bool ShowToolbar
    {
        get => _showToolbar;
        set => SetAndSave(ref _showToolbar, value);
    }

    /// <summary>启动 YuLiangToolkit 时是否直接显示屏幕画笔覆盖层。</summary>
    public bool StartVisible
    {
        get => _startVisible;
        set => SetAndSave(ref _startVisible, value);
    }

    /// <summary>背景模式：none / whiteboard / blackboard。</summary>
    public string Backdrop
    {
        get => _backdrop;
        set => SetAndSave(ref _backdrop, value is "whiteboard" or "blackboard" ? value : "none");
    }

    /// <summary>额外调色板：用户可编辑的预设颜色列表（每项 #RRGGBB / #AARRGGBB）。</summary>
    public List<string> PaletteColors
    {
        get => _paletteColors;
        set
        {
            _paletteColors = (value ?? new List<string>())
                .Where(ScreenPenColor.IsValid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(64)
                .ToList();
            if (_paletteColors.Count == 0) _paletteColors = DefaultPalette();
            OnPropertyChanged(nameof(PaletteColors));
        }
    }

    public string ToggleOverlayHotkey
    {
        get => _toggleOverlayHotkey;
        set => SetAndSave(ref _toggleOverlayHotkey, value ?? "");
    }

    public string ToggleInputHotkey
    {
        get => _toggleInputHotkey;
        set => SetAndSave(ref _toggleInputHotkey, value ?? "");
    }

    public string ClearHotkey
    {
        get => _clearHotkey;
        set => SetAndSave(ref _clearHotkey, value ?? "");
    }

    public string UndoHotkey
    {
        get => _undoHotkey;
        set => SetAndSave(ref _undoHotkey, value ?? "");
    }

    public bool ToggleOverlayHotkeyEnabled
    {
        get => _toggleOverlayHotkeyEnabled;
        set => SetAndSave(ref _toggleOverlayHotkeyEnabled, value);
    }

    public bool ToggleInputHotkeyEnabled
    {
        get => _toggleInputHotkeyEnabled;
        set => SetAndSave(ref _toggleInputHotkeyEnabled, value);
    }

    public bool ClearHotkeyEnabled
    {
        get => _clearHotkeyEnabled;
        set => SetAndSave(ref _clearHotkeyEnabled, value);
    }

    public bool UndoHotkeyEnabled
    {
        get => _undoHotkeyEnabled;
        set => SetAndSave(ref _undoHotkeyEnabled, value);
    }

    public bool EnableGlobalHotkeys
    {
        get => _enableGlobalHotkeys;
        set => SetAndSave(ref _enableGlobalHotkeys, value);
    }

    [JsonIgnore]
    public string BackdropName => Backdrop switch
    {
        "whiteboard" => "白板",
        "blackboard" => "黑板",
        _ => "透明",
    };

    [JsonIgnore]
    public string ActiveToolName => ActiveTool switch
    {
        "highlighter" => "荧光笔",
        "eraser" => "橡皮",
        "line" => "直线",
        "rectangle" => "矩形",
        "ellipse" => "椭圆",
        "arrow" => "箭头",
        "text" => "文字",
        _ => "画笔",
    };

    public static List<string> DefaultPalette() => new()
    {
        "#FFFF3B30", "#FFFF9500", "#FFFFCC00", "#FF34C759",
        "#FF00C7BE", "#FF32ADE6", "#FF007AFF", "#FF5856D6",
        "#FFFF2D55", "#FFFFFFFF", "#FF8E8E93", "#FF1C1C1E",
        "#FFFFB3BA", "#FFFFDFBA", "#FFFFFFB3", "#FFB5EAD7",
        "#FFC7CEEA", "#FFE0BBE4", "#FFFEC8D8", "#FFD4A5A5",
        "#FFA0C4FF", "#FFBDB2FF", "#FFCAFFBF", "#FFFFC6FF",
    };
}
