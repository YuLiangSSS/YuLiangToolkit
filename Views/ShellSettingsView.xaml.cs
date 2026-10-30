using System.Windows.Controls;
using System.Windows.Threading;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 「主页设置」页面（v0.26.0 起从独立小窗改成主窗口里的页面）：
/// 入口 = 主页右上角「⚙ 主页设置」/ 托盘右键菜单；面包屑显示「主页 › 主页设置」，
/// 后退 / 前进按钮也能回到这一页（历史记录见 <see cref="Models.ShellLocation.Settings"/>）。
///
/// <para>页面本身没有业务逻辑：所有设置都双向绑 <see cref="ViewModels.MainViewModel"/>，
/// 改一下自动落盘；特效勾选项由 <c>Core/Effects/CursorEffects.cs</c> 的清单驱动，
/// 加新特效不用改这个 XAML。缓存清理 / 重建配置两块面板绑 <see cref="ViewModels.MaintenanceViewModel"/>，
/// 列表由 <c>Core/Maintenance</c> 按已发现工具现算 —— 以后新增工具自动出现在这里。</para>
///
/// <para>唯一一段代码是开发调试钩子（截图验收最下面的「清理缓存 / 重建配置」用，只影响调试运行）：</para>
/// <list type="bullet">
///   <item><c>YLT_SETTINGS_TAIL=1</c>：打开时把页面滚到底部；</item>
///   <item><c>YLT_SETTINGS_SCROLL=像素</c>：滚到指定纵向位置（负数 = 从底部往上数，例如 -700）。</item>
/// </list>
/// </summary>
public partial class ShellSettingsView : UserControl
{
    public ShellSettingsView()
    {
        InitializeComponent();

        var tail = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_SETTINGS_TAIL"));
        var scrollText = Environment.GetEnvironmentVariable("YLT_SETTINGS_SCROLL");
        if (!tail && string.IsNullOrWhiteSpace(scrollText)) return;

        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (Content is not ScrollViewer scroller) return;
            scroller.UpdateLayout();

            if (tail)
            {
                scroller.ScrollToEnd();
                return;
            }

            if (double.TryParse(scrollText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var offset))
            {
                // 负数 = 从底部往上数（页面总高度未知时也能定位到“底部附近”）
                scroller.ScrollToVerticalOffset(offset < 0
                    ? Math.Max(0, scroller.ScrollableHeight + offset)
                    : offset);
            }
        }));
    }
}
