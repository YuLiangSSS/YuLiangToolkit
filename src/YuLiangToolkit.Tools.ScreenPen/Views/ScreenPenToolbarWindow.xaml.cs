using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenPenConfigModel = YuLiangToolkit.Tools.ScreenPen.ScreenPenConfig;

namespace YuLiangToolkit.Tools.ScreenPen.Views;

/// <summary>屏幕上的悬浮画笔工具栏（独立小窗，不抢任务栏、不抢焦点）。</summary>
public partial class ScreenPenToolbarWindow : Window
{
    private readonly ScreenPenConfigModel _config;
    private readonly Action<string> _selectTool;
    private readonly Action _undo;
    private readonly Action _redo;
    private readonly Action _clear;
    private readonly Action _hideOverlay;
    private readonly Action _hideToolbar;

    public ScreenPenToolbarWindow(
        ScreenPenConfigModel config,
        Action<string> selectTool,
        Action undo,
        Action redo,
        Action clear,
        Action hideOverlay,
        Action hideToolbar)
    {
        _config = config;
        _selectTool = selectTool;
        _undo = undo;
        _redo = redo;
        _clear = clear;
        _hideOverlay = hideOverlay;
        _hideToolbar = hideToolbar;
        InitializeComponent();
        RebuildColors();
        Loaded += (_, _) => SyncToolState();
    }

    public void RefreshColors()
    {
        RebuildColors();
        SyncToolState();
    }

    public void SyncToolState()
    {
        foreach (var button in new[] { PenButton, HighlighterButton, EraserButton, LineButton, RectangleButton, EllipseButton, ArrowButton, TextButton })
        {
            var isActive = string.Equals(button.Tag as string, _config.ActiveTool, StringComparison.OrdinalIgnoreCase);
            button.Background = isActive ? new SolidColorBrush(Color.FromArgb(0x88, 0x32, 0xAD, 0xE6)) : Brushes.Transparent;
        }
    }

    private void RebuildColors()
    {
        ColorPanel.Children.Clear();
        foreach (var hex in _config.PaletteColors.Take(6))
        {
            var color = ScreenPenColor.Parse(hex);
            var button = new Button
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(1),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                ToolTip = hex,
                Cursor = Cursors.Hand,
            };
            button.Click += (_, _) =>
            {
                _config.PenColor = ScreenPenColor.ToHex(color);
            };
            ColorPanel.Children.Add(button);
        }
    }

    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 拖拽失败忽略 */ }
        }
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tool }) _selectTool(tool);
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => _undo();
    private void OnRedoClick(object sender, RoutedEventArgs e) => _redo();
    private void OnClearClick(object sender, RoutedEventArgs e) => _clear();
    private void OnHideClick(object sender, RoutedEventArgs e) => _hideOverlay();
    private void OnHideToolbarClick(object sender, RoutedEventArgs e) => _hideToolbar();

    private void OnPaletteClick(object sender, RoutedEventArgs e)
    {
        var picker = new ScreenPenPaletteWindow(_config.PaletteColors, _config.PenColor)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (picker.ShowDialog() == true)
        {
            _config.PenColor = picker.SelectedColor;
            RefreshColors();
        }
    }
}
