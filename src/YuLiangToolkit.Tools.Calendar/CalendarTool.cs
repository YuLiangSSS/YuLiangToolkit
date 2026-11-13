using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Calendar.ViewModels;
using YuLiangToolkit.Tools.Calendar.Views;

namespace YuLiangToolkit.Tools.Calendar;

/// <summary>
/// 「日历」工具入口：月视图日历，可上/下月、回今天，键盘可翻。
/// <para>标题栏那个小日历是外壳自带的（不依赖本工具被打开过）。</para>
/// </summary>
public sealed class CalendarTool : ITool
{
    public string Id => CalendarConfig.ToolId;

    public string Name => "日历";

    public string Description =>
        "月视图日历：上/下月、回今天、点选某天；周末高亮、可设为每周从周日开始、"
        + "可只显示本月。标题栏右上角也能显示日期时间并点开一个小日历。";

    public string EmojiIcon => "📅";

    public string Category => "系统";

    public IReadOnlyList<string> CategoryPath => new[] { "系统" };

    public IReadOnlyList<string> Tags => new[]
    {
        "日历", "月历", "日期", "calendar", "今天", "星期",
    };

    /// <summary>排在系统分类里秒表 (170) 之前。</summary>
    public int Order => 168;

    public FrameworkElement CreateView()
        => new CalendarView { DataContext = new CalendarViewModel() };
}
