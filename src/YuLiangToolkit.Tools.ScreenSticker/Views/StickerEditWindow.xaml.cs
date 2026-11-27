using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.ScreenSticker.Views;

/// <summary>贴纸右键「编辑图片」：画笔 / 荧光笔 / 橡皮的轻量编辑，保存为新的 PNG 贴图。</summary>
public partial class StickerEditWindow : Window
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0xFF, 0x8A, 0x00),
        Color.FromRgb(0xFF, 0xD6, 0x00), Color.FromRgb(0x43, 0xA0, 0x47),
        Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x8E, 0x24, 0xAA),
        Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x11, 0x11, 0x11),
    };

    private readonly BitmapImage _source;
    private readonly Stack<Stroke> _undo = new();
    private readonly Stack<Stroke> _redo = new();
    private string _tool = "pen";
    private Color _color = Color.FromRgb(0xE5, 0x39, 0x35);

    public StickerEditWindow(string sourcePath)
    {
        InitializeComponent();
        _source = LoadBitmap(sourcePath);
        Ink.Width = _source.PixelWidth;
        Ink.Height = _source.PixelHeight;
        Ink.Background = new ImageBrush(_source) { Stretch = Stretch.Fill };
        Ink.DefaultDrawingAttributes = CreateAttributes();
        BuildColors();
        UpdateStatus();
    }

    public string? EditedImagePath { get; private set; }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private DrawingAttributes CreateAttributes() => new()
    {
        Color = _color,
        Width = Math.Max(1, ThicknessSlider.Value),
        Height = Math.Max(1, ThicknessSlider.Value),
        FitToCurve = true,
        IsHighlighter = _tool == "highlighter",
    };

    private void BuildColors()
    {
        foreach (var color in Palette)
        {
            var button = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(color == _color ? 2 : 0),
                ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
            };
            button.Click += (_, _) =>
            {
                _color = color;
                Ink.DefaultDrawingAttributes = CreateAttributes();
                foreach (var child in ColorPanel.Children.OfType<Button>())
                    child.BorderThickness = new Thickness(child == button ? 2 : 0);
            };
            ColorPanel.Children.Add(button);
        }
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tool }) return;
        _tool = tool;
        Ink.EditingMode = tool == "eraser" ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
        Ink.DefaultDrawingAttributes = CreateAttributes();
        StatusText.Text = tool == "highlighter" ? "荧光笔：半透明宽线" : tool == "eraser" ? "橡皮：点按 / 拖动删除笔画" : "画笔：拖动绘制";
    }

    private void OnThicknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Ink is not null) Ink.DefaultDrawingAttributes = CreateAttributes();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (Ink.Strokes.Count == 0) return;
        var stroke = Ink.Strokes[^1];
        Ink.Strokes.RemoveAt(Ink.Strokes.Count - 1);
        _redo.Push(stroke);
        UpdateStatus();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        Ink.Strokes.Add(_redo.Pop());
        UpdateStatus();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (Ink.Strokes.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        Ink.Strokes.Clear();
        UpdateStatus();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Ink.Width), Math.Max(1, (int)Ink.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Ink);

            var storage = ToolStorage.For(ScreenStickerConfig.ToolId);
            var relative = Path.Combine("edits", $"edited-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
            var path = storage.PathOf(relative);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);

            EditedImagePath = path;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UpdateStatus() => StatusText.Text = $"笔画：{Ink.Strokes.Count} 个";
}
