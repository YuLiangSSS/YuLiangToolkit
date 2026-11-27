using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.Screenshot.Views;

/// <summary>截图工具的额外调色板：固定扩展色 + 自定义十六进制颜色。</summary>
public partial class ScreenshotPaletteWindow : Window
{
    private readonly IReadOnlyList<Color> _colors;
    private Color _selected;

    public ScreenshotPaletteWindow(IEnumerable<Color> colors, Color selected)
    {
        InitializeComponent();
        _colors = colors.ToList();
        _selected = selected;
        HexBox.Text = ScreenshotColor.ToHex(selected);
        BuildSwatches();
    }

    public Color SelectedColor => _selected;

    private void BuildSwatches()
    {
        SwatchesPanel.Children.Clear();
        foreach (var color in _colors)
        {
            var button = new Button
            {
                Width = 38,
                Height = 30,
                Margin = new Thickness(3),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(color == _selected
                    ? Color.FromRgb(0x32, 0xAD, 0xE6) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(2),
                ToolTip = ScreenshotColor.ToHex(color),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            button.Click += (_, _) =>
            {
                _selected = color;
                HexBox.Text = ScreenshotColor.ToHex(color);
                BuildSwatches();
            };
            SwatchesPanel.Children.Add(button);
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!ScreenshotColor.IsValid(HexBox.Text))
        {
            HexBox.BorderBrush = Brushes.Red;
            return;
        }
        _selected = ScreenshotColor.Parse(HexBox.Text);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
