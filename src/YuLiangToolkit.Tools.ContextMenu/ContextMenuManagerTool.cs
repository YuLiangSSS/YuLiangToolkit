using System.Windows;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.Tools.ContextMenu;

/// <summary>
/// 「右键菜单管理」工具入口：把 Windows 右键菜单项列出来，支持隐藏 / 恢复
/// （例如「上传到百度网盘」这类不用的菜单）。
/// <para>只改当前用户（HKCU）的覆盖项，不删原注册表键，因此不需要管理员权限。</para>
/// </summary>
public sealed class ContextMenuManagerTool : ITool
{
    public string Id => "context-menu-manager";

    public string Name => "右键菜单管理";

    public string Description =>
        "查看 Windows 右键菜单里的所有项目（文件 / 文件夹 / 桌面背景 / 驱动器），"
        + "把不需要的（例如「上传到百度网盘」）一键隐藏、随时恢复；只改当前用户注册表，不需要管理员权限。";

    public string EmojiIcon => "🖱️";

    public string Category => "系统";

    public IReadOnlyList<string> Tags => new[] { "系统", "右键菜单", "注册表", "隐藏", "清理" };

    /// <summary>主页根层卡片（不写 <see cref="CategoryPath"/> 就放根目录）。</summary>
    public int Order => 500;

    public FrameworkElement CreateView()
        => new Views.ContextMenuManagerView { DataContext = new ViewModels.ContextMenuManagerViewModel() };
}
