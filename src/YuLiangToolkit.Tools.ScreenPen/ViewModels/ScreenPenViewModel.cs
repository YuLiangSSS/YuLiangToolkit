using System.Collections.ObjectModel;
using System.Windows;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.ScreenPen.Views;

namespace YuLiangToolkit.Tools.ScreenPen.ViewModels;

/// <summary>屏幕画笔页面与快捷键共用的常驻 ViewModel。</summary>
public sealed class ScreenPenViewModel : ToolViewModelBase
{
    private readonly ScreenPenTool _tool;
    private string _selectedTool = "pen";

    public ScreenPenViewModel(ScreenPenTool tool) : base(ScreenPenConfig.ToolId)
    {
        _tool = tool;
        Cfg = LoadConfig<ScreenPenConfig>();
        Session = ScreenPenSession.Current;
        Session.Initialize(Cfg);

        ToggleOverlayCommand = new RelayCommand(_ => ToggleOverlay());
        ShowOverlayCommand = new RelayCommand(_ => Session.Show());
        HideOverlayCommand = new RelayCommand(_ => Session.Hide());
        ToggleDrawingCommand = new RelayCommand(_ => Session.ToggleDrawingMode());
        ClearCommand = new RelayCommand(_ => Session.Clear());
        UndoCommand = new RelayCommand(_ => Session.Undo());
        RedoCommand = new RelayCommand(_ => Session.Redo());
        SelectToolCommand = new RelayCommand(parameter => SelectTool(parameter as string));
        SelectColorCommand = new RelayCommand(parameter => SelectColor(parameter as string));
        SetBackdropCommand = new RelayCommand(parameter => SetBackdrop(parameter as string));
        OpenPaletteCommand = new RelayCommand(_ => OpenPalette());
        ResetPaletteCommand = new RelayCommand(_ => ResetPalette());

        Cfg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ScreenPenConfig.ActiveTool)
                or nameof(ScreenPenConfig.PenColor)
                or nameof(ScreenPenConfig.ShowToolbar)
                or nameof(ScreenPenConfig.StartVisible))
            {
                RefreshState();
            }
        };
        Session.StateChanged += (_, _) => Ui(RefreshState);
        RefreshState();
    }

    public ScreenPenConfig Cfg { get; }
    public ScreenPenSession Session { get; }

    public string SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (!Set(ref _selectedTool, value)) return;
            Cfg.ActiveTool = value;
            Session.SetTool(value);
        }
    }

    public bool IsOverlayVisible => Session.IsOverlayVisible;
    public bool IsDrawingMode => Session.IsDrawingMode;
    public string ToggleButtonText => IsOverlayVisible ? "隐藏屏幕画笔" : "显示屏幕画笔";
    public string ModeText => IsOverlayVisible
        ? (IsDrawingMode ? "正在绘制 · 鼠标被覆盖层接管" : "鼠标穿透 · 快捷键仍可用")
        : "覆盖层未显示";
    public string AnnotationText => $"当前批注：{Session.AnnotationCount} 个";

    public IReadOnlyList<ScreenPenToolOption> Tools { get; } = new[]
    {
        new ScreenPenToolOption("pen", "画笔", "✏", "P"),
        new ScreenPenToolOption("highlighter", "荧光笔", "▰", "H"),
        new ScreenPenToolOption("eraser", "橡皮", "⌫", "E"),
        new ScreenPenToolOption("line", "直线", "╱", "L"),
        new ScreenPenToolOption("rectangle", "矩形", "□", "R"),
        new ScreenPenToolOption("ellipse", "椭圆", "○", "O"),
        new ScreenPenToolOption("arrow", "箭头", "→", "A"),
        new ScreenPenToolOption("text", "文字", "T", "T"),
    };

    public RelayCommand ToggleOverlayCommand { get; }
    public RelayCommand ShowOverlayCommand { get; }
    public RelayCommand HideOverlayCommand { get; }
    public RelayCommand ToggleDrawingCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand SelectToolCommand { get; }
    public RelayCommand SelectColorCommand { get; }
    public RelayCommand SetBackdropCommand { get; }
    public RelayCommand OpenPaletteCommand { get; }
    public RelayCommand ResetPaletteCommand { get; }

    public void ToggleOverlay()
    {
        Session.ToggleVisible();
        RefreshState();
    }

    public void ToggleDrawing() => Session.ToggleDrawingMode();

    public void SelectTool(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return;
        Cfg.ActiveTool = tool;
        SelectedTool = tool;
        Session.SetTool(tool);
        RefreshState();
    }

    public void SelectColor(string? hex)
    {
        if (!ScreenPenColor.IsValid(hex)) return;
        Cfg.PenColor = hex!;
        Session.RefreshColors();
        RefreshState();
    }

    public void SetBackdrop(string? backdrop)
    {
        Cfg.Backdrop = backdrop is "whiteboard" or "blackboard" ? backdrop : "none";
        Session.SetBackdrop(Cfg.Backdrop);
        RefreshState();
    }

    public void OpenPalette()
    {
        var dialog = new ScreenPenPaletteWindow(Cfg.PaletteColors, Cfg.PenColor)
        {
            Owner = Application.Current?.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (dialog.ShowDialog() == true)
        {
            Cfg.PenColor = dialog.SelectedColor;
            if (!Cfg.PaletteColors.Contains(dialog.SelectedColor, StringComparer.OrdinalIgnoreCase))
            {
                var colors = Cfg.PaletteColors.ToList();
                colors.Add(dialog.SelectedColor);
                Cfg.PaletteColors = colors;
            }
            Session.RefreshColors();
            RefreshState();
        }
    }

    public void ResetPalette()
    {
        Cfg.PaletteColors = ScreenPenConfig.DefaultPalette();
        Session.RefreshColors();
        RefreshState();
    }

    public void OnPageLoaded()
    {
        NotifyOpened();
        RefreshState();
    }

    private void RefreshState()
    {
        _selectedTool = Cfg.ActiveTool;
        OnPropertyChanged(nameof(SelectedTool));
        OnPropertyChanged(nameof(IsOverlayVisible));
        OnPropertyChanged(nameof(IsDrawingMode));
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(AnnotationText));
        StatusText = $"{ModeText} · {Cfg.ActiveToolName} · {Cfg.PenColor}";
    }
}

/// <summary>页面工具按钮的展示数据。</summary>
public sealed record ScreenPenToolOption(string Key, string Name, string Glyph, string Hotkey);
