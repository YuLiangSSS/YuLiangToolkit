using System.Windows.Controls;
using System.Windows.Threading;

namespace YuLiangToolkit.Tools.OsuMania.Views;

/// <summary>
/// 开发调试用：<c>YLT_OSU_SCROLL=&lt;像素&gt;</c> 时，页面加载后把外层 <see cref="ScrollViewer"/>
/// 滚到指定位置（只为无头截图能看到下半屏的「进度与日志」区；正常启动不设变量时什么都不做）。
/// 与联机聊天室的 <c>YLT_CHAT_SCROLL</c> 是同一个用途。
/// </summary>
internal static class OsuViewScroll
{
    public static void ApplyIfRequested(ScrollViewer? pageScroll)
    {
        if (pageScroll is null) return;

        string? text = Environment.GetEnvironmentVariable("YLT_OSU_SCROLL");
        if (string.IsNullOrWhiteSpace(text) || !int.TryParse(text, out int offset) || offset <= 0) return;

        // 等第一轮布局完成再滚，否则 ScrollableHeight 还是 0，滚动会被夹回顶部。
        pageScroll.Dispatcher.BeginInvoke(new Action(() => pageScroll.ScrollToVerticalOffset(offset)),
            DispatcherPriority.Background);
    }
}
