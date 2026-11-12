using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Tools.WorldClock.Models;

namespace YuLiangToolkit.Tools.WorldClock;

/// <summary>
/// 「全球时钟」的强类型配置 → <c>Config\world-clock\config.json</c>。
///
/// <para>城市列表本身就是配置（<see cref="Clocks"/>）：加一只、删一只、锁定、拖动排序
/// 都会自动落盘，所以没有「保存」按钮，也不需要有 —— 下次打开还是上次那面钟墙。</para>
///
/// <para>⚠ 列表元素 <see cref="WorldClockItem"/> 必须继承 <c>ObservableObject</c>：
/// 拖排序按钮 / 锁定开关改了属性要立刻回写 JSON（自动保存靠内容比对，但界面联动要靠通知）。</para>
/// </summary>
public sealed class WorldClockConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名）。</summary>
    [JsonIgnore]
    public static string ToolId => "world-clock";

    /// <summary>表盘视图（默认：用户要的那面钟墙）。</summary>
    public const string ViewAnalog = "表盘";

    /// <summary>列表视图。</summary>
    public const string ViewList = "列表";

    // ===================== 城市列表 =====================

    private ObservableCollection<WorldClockItem> _clocks = new();

    /// <summary>
    /// 城市列表（顺序 = 界面顺序）。首次运行由 ViewModel 填入几个常用城市，
    /// 之后完全由用户操作维护；清空后就是空状态（不强行塞默认值，尊重用户的删除）。
    /// </summary>
    public ObservableCollection<WorldClockItem> Clocks
    {
        get => _clocks;
        set => Set(ref _clocks, value ?? new ObservableCollection<WorldClockItem>());
    }

    // ===================== 视图 =====================

    private string _viewMode = ViewAnalog;

    /// <summary>视图：表盘 / 列表（下拉框绑界面标签，存干净的值）。</summary>
    public string ViewMode
    {
        get => _viewMode;
        set
        {
            if (Set(ref _viewMode, value ?? ViewAnalog)) OnPropertyChanged(nameof(IsAnalogView));
        }
    }

    // ===================== 表盘外观 =====================

    private double _dialSize = 160;

    /// <summary>表盘直径（130 ~ 320，DIP）。默认 160：常见窗口宽度下一排能放三只钟。</summary>
    public double DialSize
    {
        get => _dialSize;
        set => Set(ref _dialSize, Math.Clamp(value, 130, 320));
    }

    private bool _showSeconds = true;

    /// <summary>显示秒针 / 秒数。</summary>
    public bool ShowSeconds
    {
        get => _showSeconds;
        set
        {
            if (Set(ref _showSeconds, value)) OnPropertyChanged(nameof(IsAnalogView));
        }
    }

    private bool _hour24 = true;

    /// <summary>24 小时制（关掉 = 12 小时制带 AM / PM）。</summary>
    public bool Hour24
    {
        get => _hour24;
        set => Set(ref _hour24, value);
    }

    private bool _showDate = true;

    /// <summary>显示日期与星期。</summary>
    public bool ShowDate
    {
        get => _showDate;
        set => Set(ref _showDate, value);
    }

    private bool _showOffset = true;

    /// <summary>显示与本地时差（快 8 小时 / 慢 5 小时 30 分 / 同本地时间）。</summary>
    public bool ShowOffset
    {
        get => _showOffset;
        set => Set(ref _showOffset, value);
    }

    private bool _colorByDayNight = true;

    /// <summary>底色按昼夜变化（白天亮 / 傍晚暖 / 夜晚暗）——半夜的城市一眼能看出来。</summary>
    public bool ColorByDayNight
    {
        get => _colorByDayNight;
        set => Set(ref _colorByDayNight, value);
    }

    private bool _showDialTicks = true;

    /// <summary>表盘上画刻度（12 / 3 / 6 / 9 加长）。关掉就是一只素盘。</summary>
    public bool ShowDialTicks
    {
        get => _showDialTicks;
        set => Set(ref _showDialTicks, value);
    }

    // ===================== 计算属性（不写进 JSON） =====================

    /// <summary>当前是不是表盘视图（界面用它切模板 / 停秒针动画）。</summary>
    [JsonIgnore]
    public bool IsAnalogView => string.Equals(ViewMode, ViewAnalog, StringComparison.Ordinal);

    /// <summary>夹到合法值的视图（手改配置写错值时回退表盘）。</summary>
    [JsonIgnore]
    public string SafeViewMode => IsAnalogView || string.Equals(ViewMode, ViewList, StringComparison.Ordinal)
        ? ViewMode
        : ViewAnalog;
}
