using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.WorldClock.ViewModels;
using YuLiangToolkit.Tools.WorldClock.Views;

namespace YuLiangToolkit.Tools.WorldClock;

/// <summary>
/// 「全球时钟」工具入口：一页看多个城市的当前时间，表盘 / 列表两种视图，
/// 可以加城市、拖动排序、锁定防手滑、删除后 6 秒内撤销。
///
/// <para>时间全部走系统时区库（<see cref="TimeZoneInfo"/>），夏令时由系统负责；
/// 界面刷新只在这一页可见时跑（见 <c>WorldClockViewModel.OnClosed</c>）。</para>
/// </summary>
public sealed class WorldClockTool : ITool
{
    public string Id => WorldClockConfig.ToolId;

    public string Name => "全球时钟";

    public string Description =>
        "一页看多个城市的当前时间：表盘 / 列表两种视图，显示日期星期、与本地时差、跨日角标、"
        + "昼夜状态与夏令时；可加城市（内置城市库 + 自定义 UTC 偏移）、拖动排序、"
        + "锁定防手滑、删除后 6 秒内撤销。";

    public string EmojiIcon => "🕐";

    public string Category => "桌面";

    public IReadOnlyList<string> CategoryPath => new[] { "桌面" };

    public IReadOnlyList<string> Tags => new[]
    {
        "时钟", "世界时间", "时区", "时差", "UTC", "世界时钟", "城市时间",
    };

    /// <summary>主页卡片排序：排在「桌面」分类里 Live2D 桌宠 (160) / 屏幕贴纸 (162) 之后。</summary>
    public int Order => 165;

    public FrameworkElement CreateView()
        => new WorldClockView { DataContext = new WorldClockViewModel() };
}
