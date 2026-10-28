using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.App.Models;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 颜色拾取按钮（小巧版，不弹系统对话框）：按钮上显示「色块 + #RRGGBB」，
/// 点开是**预设色板 + 手动填 <c>#RRGGBB</c>** 的 Popup。
///
/// <para>对外只有一个双向绑定属性 <see cref="SelectedColor"/>；主页设置里两个粒子颜色都用它。
/// 颜色只处理不透明 RGB（粒子透明效果由引擎按寿命淡出，不靠颜色 alpha）。</para>
/// </summary>
public partial class ColorPickerButton : UserControl
{
    /// <summary>选中的颜色（双向绑定；默认是粒子颜色 1 的淡紫白）。</summary>
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(ColorPickerButton),
        new FrameworkPropertyMetadata(ParticleColors.Default1,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    private static readonly DependencyPropertyKey HexTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HexText), typeof(string), typeof(ColorPickerButton), new PropertyMetadata("#F2EEFF"));

    /// <summary>按钮上显示的 <c>#RRGGBB</c> 文本（只读，跟着 <see cref="SelectedColor"/> 变）。</summary>
    public static readonly DependencyProperty HexTextProperty = HexTextPropertyKey.DependencyProperty;

    /// <summary>预设色板（静态内容，构造时建一次；绑定给弹层里的色块列表）。</summary>
    public IReadOnlyList<PaletteColor> Palette { get; }

    /// <summary>创建颜色拾取按钮。</summary>
    public ColorPickerButton()
    {
        Palette = BuildPalette();      // 必须在 InitializeComponent 之前，ItemsControl 才能一次性拿到
        InitializeComponent();
        UpdateHexText();
    }

    /// <summary>选中颜色，见 <see cref="SelectedColorProperty"/>。</summary>
    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>按钮上的十六进制文本，见 <see cref="HexTextProperty"/>。</summary>
    public string HexText => (string)GetValue(HexTextProperty);

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ColorPickerButton)d).UpdateHexText();

    private void UpdateHexText() => SetValue(HexTextPropertyKey, ParticleColors.ToHex(SelectedColor));

    // ================================================================== 弹层交互

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        HexBox.Text = HexText;
        PickerPopup.IsOpen = true;
        HexBox.Focus();
        HexBox.SelectAll();
    }

    private void OnPaletteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Color color })
        {
            SelectedColor = color;
            PickerPopup.IsOpen = false;
        }
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryApplyHex();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PickerPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => TryApplyHex();

    /// <summary>解析手填的十六进制：合法就应用（并规范化大小写），不合法就地提示、不改颜色。</summary>
    private void TryApplyHex()
    {
        if (ParticleColors.TryParse(HexBox.Text, out var color))
        {
            SelectedColor = color;
            ErrorText.Visibility = Visibility.Collapsed;
            HexBox.Text = HexText;
        }
        else
        {
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    // ================================================================== 预设色板

    private static IReadOnlyList<PaletteColor> BuildPalette()
    {
        // 深色主题上看着舒服的一组：白/灰、蓝/青、绿/黄、红/粉/紫
        string[] hexes =
        {
            "#FFFFFF", "#F2EEFF", "#D8D8E8", "#98A1B3",
            "#4C8DFF", "#8FB4FF", "#5AC8FA", "#2EE6D0",
            "#4CD964", "#A3E635", "#FFD43D", "#FFA93D",
            "#FF5A5A", "#FF7AB8", "#D96BFF", "#9F8FFF",
        };

        var list = new List<PaletteColor>(hexes.Length);
        foreach (var hex in hexes)
        {
            if (!ParticleColors.TryParse(hex, out var color)) continue;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            list.Add(new PaletteColor(color, brush, ParticleColors.ToHex(color)));
        }
        return list;
    }
}

/// <summary>拾色器色板里的一格（颜色 + 冻结画刷 + #RRGGBB 文本，供 DataTemplate 绑定）。</summary>
public sealed class PaletteColor
{
    /// <summary>一格预设颜色。</summary>
    public PaletteColor(Color color, SolidColorBrush brush, string hex)
    {
        Color = color;
        Brush = brush;
        Hex = hex;
    }

    /// <summary>颜色。</summary>
    public Color Color { get; }

    /// <summary>画刷（冻结，直接给 Border.Background 绑定）。</summary>
    public SolidColorBrush Brush { get; }

    /// <summary><c>#RRGGBB</c> 文本（tooltip 用）。</summary>
    public string Hex { get; }
}
