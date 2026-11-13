using System.Windows;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Shell;
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

    /// <summary>主页卡片排序：排在 Live2D 桌宠 (160) / 屏幕贴纸 (162) 之后。</summary>
    public int Order => 165;

    public FrameworkElement CreateView()
        => new WorldClockView { DataContext = new WorldClockViewModel() };

    /// <summary>
    /// 构造时就把「标题栏跟随哪个时区」注册好（不等页面被打开）。
    /// <para>标题栏的日期时间要用这个时区，而用户可能从没点开过时钟页 ——
    /// 所以这里直接读一次配置（<see cref="ToolConfigStore"/> 缓存同一实例，页面打开后拿到的是同一份）。</para>
    /// </summary>
    public WorldClockTool()
    {
        try
        {
            var config = ToolConfigStore.For(WorldClockConfig.ToolId).Load<WorldClockConfig>();

            ClockBarSource.Provider = () =>
            {
                var clock = config.PrimaryClock;
                if (clock is null) return null;

                return (clock.CityName, clock.TimeZoneId, clock.CustomOffsetMinutes);
            };

            // 页面里改「跟随哪只钟」/ 加减城市后，标题栏要立刻跟着变
            config.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(WorldClockConfig.PrimaryClockId)) ClockBarSource.NotifyChanged();
            };
            config.Clocks.CollectionChanged += (_, _) => ClockBarSource.NotifyChanged();
        }
        catch (Exception ex)
        {
            // 读配置失败不能让工具加载不了：标题栏退回本机时区
            System.Diagnostics.Debug.WriteLine($"[WorldClock] 注册标题栏时区失败：{ex.Message}");
        }
    }
}
