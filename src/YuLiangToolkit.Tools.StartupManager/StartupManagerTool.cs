using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.StartupManagerTool.ViewModels;
using YuLiangToolkit.Tools.StartupManagerTool.Views;

namespace YuLiangToolkit.Tools.StartupManagerTool;

/// <summary>
/// 「开机启动管理」工具入口：从「任务管理器」拆出来的独立工具。
/// 扫描注册表 Run / 启动文件夹里的开机启动项，可启用 / 禁用（写 Windows 的 StartupApproved）、
/// 删除、打开位置、复制命令；系统级项目需要管理员权限（可一键 UAC 重启）。
/// </summary>
public sealed class StartupManagerTool : ITool
{
    public string Id => "startup-manager";

    public string Name => "开机启动管理";

    public string Description =>
        "管理开机启动项：扫描注册表 Run / 启动文件夹，逐个启用 / 禁用（写 Windows 自己的 StartupApproved）、"
        + "删除、打开程序位置、复制启动命令；系统级（所有用户）项目需管理员权限，可一键 UAC 重启。";

    public string EmojiIcon => "🚀";

    public string Category => "系统";

    public IReadOnlyList<string> CategoryPath => new[] { "系统" };

    public IReadOnlyList<string> Tags => new[]
    {
        "开机启动", "启动项", "启动管理", "自启动", "启动文件夹", "Run", "注册表",
    };

    /// <summary>主页卡片排序：紧挨着任务管理器 (46)，排在应用管理器 (47) 之后。</summary>
    public int Order => 48;

    public FrameworkElement CreateView()
        => new StartupManagerView { DataContext = new StartupManagerViewModel() };
}
