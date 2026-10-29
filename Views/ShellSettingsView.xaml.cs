using System.Windows.Controls;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 「主页设置」页面（v0.26.0 起从独立小窗改成主窗口里的页面）：
/// 入口 = 主页右上角「⚙ 主页设置」/ 托盘右键菜单；面包屑显示「主页 › 主页设置」，
/// 后退 / 前进按钮也能回到这一页（历史记录见 <see cref="Models.ShellLocation.Settings"/>）。
///
/// <para>页面本身没有代码逻辑：所有设置都双向绑 <see cref="ViewModels.MainViewModel"/>，
/// 改一下自动落盘；特效勾选项由 <c>Core/Effects/CursorEffects.cs</c> 的清单驱动，
/// 加新特效不用改这个 XAML。</para>
/// </summary>
public partial class ShellSettingsView : UserControl
{
    public ShellSettingsView()
    {
        InitializeComponent();
    }
}
