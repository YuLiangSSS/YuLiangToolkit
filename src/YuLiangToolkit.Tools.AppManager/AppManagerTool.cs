using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.AppManager.ViewModels;
using YuLiangToolkit.Tools.AppManager.Views;

namespace YuLiangToolkit.Tools.AppManager;

/// <summary>
/// 「应用管理器」工具入口：仿 Windows「设置 → 应用 → 安装的应用」。
///
/// <para>列出注册表里登记过的桌面应用（名称 / 发布者 / 版本 / 占用大小 / 安装日期 / 装在哪个盘），
/// 支持按驱动器筛选、按名称 / 安装日期 / 占用大小 / 发布者排序并可切换正序逆序，
/// 每行右侧带「卸载」「修改」按钮。</para>
///
/// <para>本工具<b>只读注册表</b>：卸载 / 修改是把注册表里的 <c>UninstallString</c> / <c>ModifyPath</c>
/// 原样交给安装程序自己执行（不静默卸载、不删文件、不改注册表），用户确认后才会启动。</para>
/// </summary>
public sealed class AppManagerTool : ITool
{
    public string Id => "app-manager";

    public string Name => "应用管理器";

    public string Description =>
        "像 Windows「设置 → 应用」那样看本机装了哪些应用（名称 / 发布者 / 版本 / 占用大小 / 安装日期 / 装在哪个盘），"
        + "按驱动器筛选、按名称·安装日期·占用大小排序并切换正逆序，每个应用右侧直接「卸载」「修改」。"
        + "只读注册表，卸载交给安装程序自己的界面确认。";

    public string EmojiIcon => "🧩";

    public string Category => "系统";

    /// <summary>主页标签：点标签可快速筛出同类工具。</summary>
    public IReadOnlyList<string> Tags => new[] { "已安装应用", "卸载", "修改", "安装日期", "占用大小", "驱动器" };

    /// <summary>主页根层卡片，紧挨「任务管理器」（46）与「软件升级器」（505）。</summary>
    public int Order => 47;

    public FrameworkElement CreateView()
        => new AppManagerView { DataContext = new AppManagerViewModel() };
}
