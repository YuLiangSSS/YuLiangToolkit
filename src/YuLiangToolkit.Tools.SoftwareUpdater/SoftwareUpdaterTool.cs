using System.Windows;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.Tools.SoftwareUpdater;

/// <summary>
/// 「软件升级器」工具入口：调用系统自带的 Windows 程序包管理器（winget）读出已安装应用与可用更新，
/// 让用户用复选框挑选要升级的应用（多选 / 全选 / 反选），再按顺序逐个执行升级。
///
/// <para>本工具<b>不自带任何更新源、不替换 winget</b>：所有「有哪些应用、有没有新版」都来自
/// <c>winget list</c> / <c>winget upgrade</c> 的输出，升级也是交给 <c>winget upgrade --id … --exact</c> 执行。
/// 没装 winget（Windows 应用安装程序）时只提示，不崩溃。</para>
/// </summary>
public sealed class SoftwareUpdaterTool : ITool
{
    public string Id => "software-updater";

    public string Name => "软件升级器";

    public string Description =>
        "读出电脑上装了哪些应用、哪些有新版本（winget），挑好要升级的再一键批量更新；"
        + "支持多行多选、全选 / 全不选 / 反选、搜索过滤、逐个升级的进度与日志，升级前会先确认。";

    public string EmojiIcon => "⬆️";

    public string Category => "系统";

    public IReadOnlyList<string> Tags => new[] { "系统", "winget", "升级", "更新", "批量", "已安装应用" };

    /// <summary>主页根层卡片，排在「右键菜单管理」附近。</summary>
    public int Order => 505;

    public FrameworkElement CreateView()
        => new Views.SoftwareUpdaterView { DataContext = new ViewModels.SoftwareUpdaterViewModel() };
}
