using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.MailSender.Views;

/// <summary>
/// 取色窗：一排常用色 + 自定义色值（<c>#RRGGBB</c> 或颜色名）。
/// 正文文字色与背景高亮共用，标题由 <see cref="ForText"/> 切换。
/// </summary>
public partial class ColorPickerWindow : Window
{
    /// <summary>选中的颜色；点了「取消」时是 null。</summary>
    public Color? PickedColor { get; private set; }

    /// <summary>常用色（中性色在前，然后是品牌色与高亮色）——邮件正文够用了。</summary>
    private static readonly (string Hex, string Label)[] Swatches =
    {
        ("#1F2733", "正文黑"), ("#5B6879", "次要灰"), ("#8894A6", "浅灰"), ("#D0453E", "警示红"),
        ("#E07B39", "橙色"), ("#C9A227", "金色"), ("#2E9E5B", "成功绿"), ("#2F6FDB", "链接蓝"),
        ("#4C8DFF", "品牌蓝"), ("#8F6BFF", "紫色"), ("#C2409B", "洋红"), ("#FFFFFF", "白色"),
        ("#FFF4C2", "浅黄高亮"), ("#DDF0FF", "浅蓝高亮"), ("#E6FBEA", "浅绿高亮"), ("#F4F6FB", "浅灰底"),
    };

    public ColorPickerWindow() : this(forText: true)
    {
    }

    private ColorPickerWindow(bool forText)
    {
        InitializeComponent();
        TitleText.Text = forText ? "文字颜色" : "文字背景色（高亮）";
        SwatchList.ItemsSource = Swatches.Select(s => new { s.Hex, s.Label }).ToArray();
    }

    /// <summary>创建一个取色窗：<c>forText = true</c> 取文字色，false 取背景高亮色。</summary>
    public static ColorPickerWindow ForText(bool forText) => new(forText);

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string hex) return;
        HexBox.Text = hex;
        Confirm();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (!TryParse(HexBox.Text, out var color))
        {
            MessageBox.Show(this, "这个颜色值看不懂：填 #RRGGBB，或 red / navy 这类颜色名。", "颜色格式不对",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PickedColor = color;
        DialogResult = true;
    }

    private static bool TryParse(string? text, out Color color)
    {
        color = Colors.Black;
        var value = (text ?? "").Trim();
        if (value.Length == 0) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(value)!;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
