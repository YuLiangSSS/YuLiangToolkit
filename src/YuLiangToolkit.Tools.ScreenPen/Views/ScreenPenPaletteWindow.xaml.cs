using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.ScreenPen.Views;

/// <summary>完整调色板：预设色块 + 自定义十六进制颜色。</summary>
public partial class ScreenPenPaletteWindow : Window
{
    private readonly List<string> _colors;
    private string _selected;

    public ScreenPenPaletteWindow(IEnumerable<string> colors, string selected)
    {
        InitializeComponent();
        _colors = colors.Where(ScreenPenColor.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _selected = ScreenPenColor.IsValid(selected) ? selected : _colors.FirstOrDefault() ?? "#FFFF3B30";
        HexBox.Text = _selected;
        BuildSwatches();
    }

    public string SelectedColor => _selected;

    private void BuildSwatches()
    {
        SwatchesPanel.Children.Clear();
        foreach (var hex in _colors)
        {
            var color = ScreenPenColor.Parse(hex);
            var button = new Button
            {
                Width = 38,
                Height = 30,
                Margin = new Thickness(3),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(string.Equals(hex, _selected, StringComparison.OrdinalIgnoreCase)
                    ? Color.FromRgb(0x32, 0xAD, 0xE6) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(2),
                ToolTip = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            button.Click += (_, _) =>
            {
                _selected = hex;
                HexBox.Text = hex;
                BuildSwatches();
            };
            SwatchesPanel.Children.Add(button);
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!ScreenPenColor.IsValid(HexBox.Text))
        {
            HexBox.BorderBrush = Brushes.Red;
            return;
        }
        _selected = HexBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
