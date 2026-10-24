using System.Windows;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.App.Views;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 完整性自检：把「本进程完整性级别 + 任务栏找不找得到 + 全屏能不能藏任务栏」打到控制台
        // （同时也写 Debug 输出；设了 YLT_TASKBAR_TRACE 会追加到那个文件）。
        // 全屏藏不住任务栏时先看这几行：错误码 5 = exe 被标成 Low，改全屏代码没用（见 IntegrityCheck 类注释）。
        IntegrityCheck.WriteReport();

        // 数据/配置目录（在 exe 旁边）首次运行时建好，并各放一份“这是干什么的”说明
        AppPaths.EnsureCreated();
        RuntimeReadme.EnsureSeeded();

        // 退出前把还没落盘的配置写掉（防抖窗口内的改动不会丢），顺手把（可能被全屏藏起来的）任务栏还回去
        Exit += (_, _) =>
        {
            TaskbarHider.Restore();
            ToolConfigStore.FlushAll();
        };
        SessionEnding += (_, _) =>
        {
            TaskbarHider.Restore();
            ToolConfigStore.FlushAll();
        };

        // 自动发现所有工具（主程序集 + plugins 目录）
        var tools = ToolDiscovery.Discover();

        var vm = new MainViewModel(tools);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;

        // 截图模式（YLT_SHOT）下不激活窗口：免得正在打字/玩游戏时被弹窗抢走输入。
        // ShowActivated 必须在 Show() 之前设；RenderTargetBitmap 渲染视觉树，不依赖窗口激活。
        var shotPath = Environment.GetEnvironmentVariable("YLT_SHOT");
        if (!string.IsNullOrWhiteSpace(shotPath))
        {
            window.ShowActivated = false;
        }

        window.Show();

        // 开发调试：YLT_NAV_CATEGORY=音游工具（或 音游工具/子分类）直接进类别页（截图验收用）
        var categoryPath = Environment.GetEnvironmentVariable("YLT_NAV_CATEGORY");
        if (!string.IsNullOrWhiteSpace(categoryPath))
        {
            vm.NavigateToCategory(categoryPath.Split(
                new[] { '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // 开发调试：可选 YLT_SHOT_TOOL / YLT_NAV_TOOL 直接打开某个工具
        var toolId = Environment.GetEnvironmentVariable("YLT_SHOT_TOOL") ?? Environment.GetEnvironmentVariable("YLT_NAV_TOOL");
        if (!string.IsNullOrWhiteSpace(toolId))
        {
            var tool = tools.FirstOrDefault(t => t.Id == toolId);
            if (tool is not null) vm.OpenToolCommand.Execute(tool);
        }

        // 开发调试：YLT_OPEN_SETTINGS=1 打开「主页设置」窗口（配合 YLT_SHOT 截那个窗口本身）
        var settingsWindow = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_OPEN_SETTINGS"))
            ? null
            : vm.OpenSettings(activate: string.IsNullOrWhiteSpace(shotPath));

        if (!string.IsNullOrWhiteSpace(shotPath))
        {
            // 设置窗口开着就截它（RenderTargetBitmap 只渲染传入的视觉树，不截会被主窗口盖住）
            if (settingsWindow is not null) settingsWindow.CaptureTo(shotPath);
            else window.CaptureTo(shotPath);
        }
    }
}
