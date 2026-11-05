using System.Windows.Media;
using YuLiangToolkit.App.Themes;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.App.ViewModels;

/// <summary>
/// 设置页里的一张「配色方案」卡片：预设配色用只读种子色，用户点「自定义」后
/// 三个色块会跟着拾色器实时更新。
///
/// <para>选中状态通过构造时传入的回调通知 <see cref="MainViewModel"/> 写配置并换肤；
/// 设置页用 ItemsControl + RadioButton 直接绑 <see cref="IsSelected"/>，加新预设只要在
/// <see cref="ThemePalette.Presets"/> 里加一条，不用改 XAML。</para>
/// </summary>
public sealed class ThemePaletteOption : ObservableObject
{
    private readonly Action<string> _select;
    private bool _isSelected;

    public ThemePaletteOption(ThemePalette palette, bool isSelected, Action<string> select)
    {
        Palette = palette;
        _isSelected = isSelected;
        _select = select;
        _accentBrush = Solid(palette.AccentDark);
        _darkBrush = Solid(palette.DarkBackground);
        _lightBrush = Solid(palette.LightBackground);
    }

    /// <summary>底层配色方案（打开 / 选中时读种子色）。</summary>
    public ThemePalette Palette { get; private set; }

    public string Id => Palette.Id;
    public string Name => Palette.Name;
    public string Description => Palette.Description;

    private SolidColorBrush _accentBrush;
    private SolidColorBrush _darkBrush;
    private SolidColorBrush _lightBrush;

    /// <summary>主色色块（深色模式的强调色）。</summary>
    public SolidColorBrush AccentBrush
    {
        get => _accentBrush;
        private set => Set(ref _accentBrush, value);
    }

    /// <summary>深色底色色块。</summary>
    public SolidColorBrush DarkBrush
    {
        get => _darkBrush;
        private set => Set(ref _darkBrush, value);
    }

    /// <summary>浅色底色色块。</summary>
    public SolidColorBrush LightBrush
    {
        get => _lightBrush;
        private set => Set(ref _lightBrush, value);
    }

    /// <summary>是否选中（RadioButton 双向绑定；只有变成 true 时才通知外壳换肤）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            if (value) _select(Id);
        }
    }

    /// <summary>自定义配色改了颜色后，原地刷新三个色块（不重建卡片，避免 RadioButton 丢选中态）。</summary>
    public void UpdatePalette(ThemePalette palette)
    {
        Palette = palette;
        AccentBrush = Solid(palette.AccentDark);
        DarkBrush = Solid(palette.DarkBackground);
        LightBrush = Solid(palette.LightBackground);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
    }

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
