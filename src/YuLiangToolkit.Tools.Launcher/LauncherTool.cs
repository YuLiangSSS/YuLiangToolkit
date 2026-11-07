using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Launcher.ViewModels;
using YuLiangToolkit.Tools.Launcher.Views;

namespace YuLiangToolkit.Tools.Launcher;

/// <summary>
/// 「软件启动器」工具入口：实现 <see cref="ITool"/> 后会被外壳自动发现并挂到主页 / 侧边栏。
///
/// <para>功能定位：把常用的 <c>.exe</c> / <c>.lnk</c> 快捷方式收进一个图标墙（像资源管理器的
/// 小图标视图），双击图标直接启动；右侧详情区显示软件全名（文件名太短时看这里）、目标路径、
/// 版本、大小等，并提供启动 / 重命名 / 更改图标 / 打开所在位置 / 删除。<b>「清空无效项」</b>
/// 会一键扫掉已经失效的快捷方式（目标被卸载 / 删除）和丢失的程序文件。</para>
/// </summary>
public sealed class LauncherTool : ITool
{
    public string Id => "launcher";

    public string Name => "软件启动器";

    public string Description =>
        "把常用的 exe / 快捷方式收进一个图标墙，双击就能启动；右侧看完整软件名与路径，可重命名、换图标、删除，还能一键清掉失效快捷方式和已卸载的软件。";

    public string EmojiIcon => "🚀";

    public string Category => "系统";

    /// <summary>主页标签：点标签可快速筛出同类工具。</summary>
    public IReadOnlyList<string> Tags => new[] { "启动器", "快捷方式", "桌面", "图标", "exe" };

    public int Order => 44;

    public FrameworkElement CreateView()
        => new LauncherView { DataContext = new LauncherViewModel() };
}
