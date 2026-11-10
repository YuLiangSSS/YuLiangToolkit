using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.Games.Tetris;

namespace YuLiangToolkit.Tools.Games.Tetris.Views;

/// <summary>
/// 「俄罗斯方块 · 游玩说明」窗口：把 <see cref="TetrisHelpContent"/> 的纯数据排成
/// 「按键 → 作用」表格 + 计分规则 + 小技巧。
///
/// <para><b>为什么用代码排版</b>：说明内容是数据（以后加/改说明只要动 <see cref="TetrisHelpContent"/>），
/// 每一项的排版又完全一样，写成 ItemsControl 模板反而绕；这里直接建控件，一次建完不再改。
/// 颜色全部用 <c>SetResourceReference</c> 取主题 token，换主题立即跟着变。</para>
///
/// <para><b>焦点</b>：窗口自己处理 <c>PreviewKeyDown</c>，按 Esc 关闭；
/// 方向键 / 空格在说明窗口里不该再传给游戏页面（页面被模态窗口挡住，收不到是正常的）。</para>
/// </summary>
public partial class TetrisHelpWindow : Window
{
    /// <summary>按键列宽度（DIP）：够放「↑ 或 X」「右侧「接下来」」这种长文本。</summary>
    private const double KeyColumnWidth = 150;

    /// <summary>右侧栏里「小技巧」最多显示几条（再多就压到计分区下面，两栏高度不好平衡）。</summary>
    private const int MaxTipsInRightColumn = 5;

    public TetrisHelpWindow()
    {
        InitializeComponent();
        SummaryText.Text = TetrisHelpContent.Summary;
        BuildBody();

        try
        {
            var owner = Application.Current?.MainWindow;
            if (owner is not null && !ReferenceEquals(owner, this)) Owner = owner;
        }
        catch
        {
            // 主窗口已关闭等极端情况：没有 Owner 也能正常显示
        }

        Loaded += (_, _) => CaptureIfRequested();
    }

    /// <summary>关窗（Esc / 按钮 / 右上角 × 都走这里，非模态时也不会抛异常）。</summary>
    /// <para>只认 Esc：Enter 留给「游戏里按 Enter 开始」，别让人读说明时误按一下就关掉。</para>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseWith(true);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseWith(true);

    /// <summary>
    /// 调试 / 截图：<c>YLT_TETRIS_HELP_SHOT=&lt;png&gt;</c> 时窗口出现后自己渲染成 PNG 再退出程序
    /// （模态窗口不属于主窗口的视觉树，<c>YLT_SHOT</c> 抓不到它）。
    /// </summary>
    private void CaptureIfRequested()
    {
        var path = Environment.GetEnvironmentVariable("YLT_TETRIS_HELP_SHOT");
        if (string.IsNullOrWhiteSpace(path)) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                CaptureToPng(path);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { /* 忽略 */ }
            }

            try { Application.Current?.Shutdown(); } catch { /* 忽略 */ }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>把窗口当前画面渲染成 PNG（窗口底色自己铺，RenderTargetBitmap 不含 Window.Background）。</summary>
    private void CaptureToPng(string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            var color = (Background as SolidColorBrush)?.Color ?? Color.FromRgb(0x20, 0x24, 0x2C);
            drawing.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));
        }
        bitmap.Render(background);
        if (Content is Visual content) bitmap.Render(content);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void CloseWith(bool closed)
    {
        try
        {
            DialogResult = closed;
        }
        catch (InvalidOperationException)
        {
            // 以 Show() 打开（非模态）时不能设置 DialogResult，直接关窗
            Close();
        }
    }

    // ---------------------------------------------------------------- 排版

    private void BuildBody()
    {
        LeftColumn.Children.Clear();
        RightColumn.Children.Clear();

        // 左栏：前三个分区（移动旋转 / 快速落底 / 暂存与预览）——「怎么操作方块」
        // 右栏：其余分区（暂停重开 / 鼠标 / 联机）+ 计分规则 + 小技巧 ——「什么时候按、怎么得分、怎么联机」
        // 这样两栏高度接近，窗口按 SizeToContent 一次就能把绝大部分内容摆出来。
        var split = Math.Min(TetrisHelpContent.LeftColumnSectionCount, TetrisHelpContent.Sections.Count);
        AddSections(LeftColumn, 0, split);
        AddSections(RightColumn, split, TetrisHelpContent.Sections.Count);

        AddBlock(RightColumn, "计分与升级", TetrisHelpContent.ScoreRules, bulleted: true);
        AddBlock(RightColumn, "小技巧", TetrisHelpContent.Tips.Take(MaxTipsInRightColumn).ToList(), bulleted: true);
    }

    private void AddSections(StackPanel target, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            var section = TetrisHelpContent.Sections[i];
            if (target.Children.Count > 0) target.Children.Add(CreateGap(14));

            target.Children.Add(CreateSectionTitle(section.Title));
            if (!string.IsNullOrWhiteSpace(section.Description))
                target.Children.Add(CreateText(section.Description, 11.5, "TextSecondaryBrush", new Thickness(0, 4, 0, 0)));

            foreach (var item in section.Items)
                target.Children.Add(CreateRow(item));
        }
    }

    private void AddBlock(StackPanel target, string title, IReadOnlyList<string> lines, bool bulleted)
    {
        if (lines.Count == 0) return;
        if (target.Children.Count > 0) target.Children.Add(CreateGap(16));
        target.Children.Add(CreateSectionTitle(title));

        foreach (var line in lines)
        {
            target.Children.Add(bulleted ? CreateBullet(line) : CreateText(line, 12, "TextPrimaryBrush", new Thickness(0, 6, 0, 0)));
        }
    }

    private static FrameworkElement CreateGap(double height)
        => new Border { Height = height, Background = Brushes.Transparent };

    private TextBlock CreateSectionTitle(string title)
    {
        var text = new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }

    private static TextBlock CreateText(string text, double fontSize, string brushKey, Thickness margin)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Max(fontSize + 5, 17),
            Margin = margin,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>一条「按键 → 作用」：左列按键（等宽胶囊底），右列作用 + 补充说明。</summary>
    private FrameworkElement CreateRow(TetrisHelpItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左：按键胶囊
        var keyBorder = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        keyBorder.SetResourceReference(Border.BackgroundProperty, "InputBackgroundBrush");

        var keyText = new TextBlock
        {
            Text = item.Keys,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        keyText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        keyBorder.Child = keyText;
        Grid.SetColumn(keyBorder, 0);
        grid.Children.Add(keyBorder);

        // 右：作用 + 补充说明
        var actions = new StackPanel();
        actions.Children.Add(CreateText(item.Action, 12.5, "TextPrimaryBrush", new Thickness(0)));
        if (!string.IsNullOrWhiteSpace(item.Tip))
            actions.Children.Add(CreateText(item.Tip, 11, "TextSecondaryBrush", new Thickness(0, 3, 0, 0)));

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return grid;
    }

    /// <summary>一条带圆点的小字（计分规则 / 小技巧）。</summary>
    private FrameworkElement CreateBullet(string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new TextBlock
        {
            Text = "·",
            FontSize = 13,
            Margin = new Thickness(4, -1, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        dot.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var body = CreateText(text, 12, "TextPrimaryBrush", new Thickness(0));
        body.LineHeight = 18;
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        return grid;
    }
}
