using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.TaskManager.ViewModels;
using YuLiangToolkit.Tools.TaskManager.Views;

namespace YuLiangToolkit.Tools.TaskManager;

/// <summary>
/// 「任务管理器」工具入口：系统监测（CPU / 内存 / GPU / 硬盘读写了多少）+
/// 进程列表（PID / CPU / 内存 / GPU / 占用端口）+ 详情 + 结束任务 + 开机启动项管理。
///
/// <para>结束任务按「三级后备」执行：<c>Process.Kill()</c> → <c>taskkill /PID … /T /F</c> →
/// <c>wmic process where name='…' delete</c>；系统关键进程会提前拦下并说明「拒绝访问」，
/// 不会让界面崩掉。</para>
/// </summary>
public sealed class TaskManagerTool : ITool
{
    public string Id => "task-manager";

    public string Name => "任务管理器";

    public string Description =>
        "看系统 CPU / 内存 / GPU / 硬盘读写的实时占用，列出每个进程的 PID、内存、CPU、GPU 与占用端口；"
        + "可结束任务（三级后备）、查看进程详情，并管理开机启动项（启用 / 禁用 / 删除）。";

    public string EmojiIcon => "🖥️";

    public string Category => "系统";

    /// <summary>主页标签：点标签可快速筛出同类工具。</summary>
    public IReadOnlyList<string> Tags => new[] { "任务管理器", "进程", "端口", "开机启动", "性能监测", "PID", "GPU" };

    public int Order => 46;

    public FrameworkElement CreateView()
        => new TaskManagerView { DataContext = new TaskManagerViewModel() };
}
