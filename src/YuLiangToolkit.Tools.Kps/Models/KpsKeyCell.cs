using System.Windows.Media;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Kps.Services;

namespace YuLiangToolkit.Tools.Kps.Models;

/// <summary>
/// 界面上一个按键格子的运行时视图模型（<b>不序列化</b>，由配置里的 <see cref="KpsKey"/> 现场生成）。
///
/// <para>主界面预览和 Overlay 里的 <c>KeyGridView</c> 共用它：格子只负责显示与「按下 / 计数」状态，
/// 点击选中、改键这些编辑逻辑由 <c>KpsViewModel</c> 管。</para>
/// </summary>
public sealed class KpsKeyCell : ObservableObject
{
    private bool _isPressed;
    private bool _isSelected;
    private long _count;
    private ImageSource? _image;
    private string _label = "";
    private Brush _idleBrush = Brushes.Transparent;
    private Brush _pressedBrush = Brushes.Orange;
    private Brush _borderBrush = Brushes.Gray;
    private Brush _foregroundBrush = Brushes.White;
    private Brush _pressedForegroundBrush = Brushes.Black;
    private Brush _counterBrush = Brushes.LightGray;
    private bool _showCounter = true;
    private bool _hasImage;

    public KpsKeyCell(KpsKey model, KpsKeyRow row, int rowIndex, int columnIndex)
    {
        Model = model;
        Row = row;
        RowIndex = rowIndex;
        ColumnIndex = columnIndex;
    }

    /// <summary>对应的可序列化配置项。</summary>
    public KpsKey Model { get; }

    /// <summary>所在行（编辑时定位用）。</summary>
    public KpsKeyRow Row { get; }

    /// <summary>行号（从 0 开始）。</summary>
    public int RowIndex { get; }

    /// <summary>列号（从 0 开始）。</summary>
    public int ColumnIndex { get; }

    /// <summary>Windows 虚拟键码（0 = 未绑定）。</summary>
    public int VirtualKey => Model.VirtualKey;

    /// <summary>宽度倍数（1 = 标准；空格键可以设 2~3）。</summary>
    public int WidthUnits => Math.Clamp(Model.WidthUnits, 1, 8);

    // ---------- 显示状态 ----------

    /// <summary>格子上的文字（配置的 Label 优先，否则用键名）。</summary>
    public string Label
    {
        get => _label;
        private set => Set(ref _label, value);
    }

    /// <summary>物理键现在是按下状态（输入层只在「真正按下 / 抬起」时切换，长按自动重复已过滤）。</summary>
    public bool IsPressed
    {
        get => _isPressed;
        set
        {
            if (!Set(ref _isPressed, value)) return;
            OnPropertyChanged(nameof(DisplayBackground));
        }
    }

    /// <summary>是不是当前在编辑面板里选中的格子（主界面预览画一圈高亮）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>累计按下次数（统计会话 + 存下来的历史值；由 ViewModel 定时刷新）。</summary>
    public long Count
    {
        get => _count;
        set
        {
            if (!Set(ref _count, value)) return;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(HasCount));
        }
    }

    /// <summary>计数器文字（0 次时显示空字符串，避免每个格子都挂个 0）。</summary>
    public string CountText => Count <= 0 ? "" : KpsCountFormat.Format(Count);

    /// <summary>有没有计数（>= 1）。</summary>
    public bool HasCount => Count > 0;

    /// <summary>是否显示计数器（全局开关 && 这个格子有计数）。</summary>
    public bool ShowCounter
    {
        get => _showCounter;
        set => Set(ref _showCounter, value);
    }

    /// <summary>按键图片（来自 <see cref="KpsKey.ImagePath"/>；null = 不用图片）。</summary>
    public ImageSource? Image
    {
        get => _image;
        set
        {
            if (!Set(ref _image, value)) return;
            HasImage = value is not null;
        }
    }

    /// <summary>有没有图片（XAML 用它切换显示）。</summary>
    public bool HasImage
    {
        get => _hasImage;
        private set => Set(ref _hasImage, value);
    }

    // ---------- 外观（由 ApplyStyle 从配置刷新） ----------

    /// <summary>未按下时的背景色。</summary>
    public Brush IdleBrush
    {
        get => _idleBrush;
        private set => Set(ref _idleBrush, value);
    }

    /// <summary>按下时的背景色（每个键可以在配置里覆盖）。</summary>
    public Brush PressedBrush
    {
        get => _pressedBrush;
        private set => Set(ref _pressedBrush, value);
    }

    /// <summary>边框色。</summary>
    public Brush BorderBrush
    {
        get => _borderBrush;
        private set => Set(ref _borderBrush, value);
    }

    /// <summary>按键文字颜色。</summary>
    public Brush ForegroundBrush
    {
        get => _foregroundBrush;
        private set => Set(ref _foregroundBrush, value);
    }

    /// <summary>按下时的文字颜色（深色按键变白 / 白色按键变深，保证对比度）。</summary>
    public Brush PressedForegroundBrush
    {
        get => _pressedForegroundBrush;
        private set => Set(ref _pressedForegroundBrush, value);
    }

    /// <summary>计数器文字颜色。</summary>
    public Brush CounterBrush
    {
        get => _counterBrush;
        private set => Set(ref _counterBrush, value);
    }

    /// <summary>当前该显示哪个背景（按下 / 未按下）。控制模板也可以自己用 DataTrigger 切换。</summary>
    public Brush DisplayBackground => IsPressed ? PressedBrush : IdleBrush;

    /// <summary>当前该显示的文字颜色（按下 / 未按下）。</summary>
    public Brush DisplayForeground => IsPressed ? PressedForegroundBrush : ForegroundBrush;

    /// <summary>从全局配置刷新颜色、文字、计数器显隐（改设置 / 切预设时调用）。</summary>
    public void ApplyStyle(KpsConfig config)
    {
        Label = string.IsNullOrWhiteSpace(Model.Label) ? KeyNames.Display(Model.VirtualKey) : Model.Label.Trim();
        IdleBrush = KpsColor.ParseBrush(config.KeyIdleColor, Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        var pressed = string.IsNullOrWhiteSpace(Model.ColorHex) ? config.KeyPressedColor : Model.ColorHex;
        PressedBrush = KpsColor.ParseBrush(pressed, Color.FromArgb(0xFF, 0xFF, 0xC8, 0x57));
        BorderBrush = KpsColor.ParseBrush(config.KeyBorderColor, Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        ForegroundBrush = KpsColor.ParseBrush(config.KeyTextColor, Colors.White);
        PressedForegroundBrush = KpsColor.ParseBrush(config.KeyPressedTextColor, Colors.Black);
        CounterBrush = KpsColor.ParseBrush(config.KeyCounterColor, Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        ShowCounter = config.ShowKeyCounters && Count > 0;
        OnPropertyChanged(nameof(DisplayBackground));
        OnPropertyChanged(nameof(DisplayForeground));
    }

    /// <summary>导出回一个新的 <see cref="KpsKey"/>（另存预设用）。</summary>
    public KpsKey Snapshot() => Model.Clone();
}

/// <summary>界面上一行按键的运行时视图模型（主界面预览与 Overlay 共用）。</summary>
public sealed class KpsRowView
{
    public KpsRowView(IReadOnlyList<KpsKeyCell> cells)
    {
        Cells = cells;
    }

    public IReadOnlyList<KpsKeyCell> Cells { get; }
}

/// <summary>计数器的大数字显示：1234 → 1,234；10000 → 1.0万（Overlay 上不至于撑破格子）。</summary>
public static class KpsCountFormat
{
    public static string Format(long count)
    {
        if (count < 0) return "0";
        if (count >= 100_000_000) return (count / 100_000_000d).ToString("0.#") + "亿";
        if (count >= 10_000) return (count / 10_000d).ToString("0.#") + "万";
        return count.ToString("N0");
    }
}
