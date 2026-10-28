using System.IO;
using System.Windows;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.App.Views;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PictureViewer;

namespace YuLiangToolkit.App;

public partial class App : Application
{
    /// <summary>任务栏右侧的托盘图标（整个进程一个；关窗口时是否「最小化到托盘」以它是否真的在为准）。</summary>
    public static TrayIcon? Tray { get; private set; }

    /// <summary>托盘图标现在是不是真的显示着（没有它就不能把窗口藏起来，否则用户找不回窗口）。</summary>
    public static bool IsTrayVisible => Tray?.IsVisible == true;

    /// <summary>是不是正在真正退出程序。为 true 时「关闭=最小化到托盘」不再拦 Close()。</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>真正退出程序（托盘「退出」/ 截图模式 / 系统关机）。</summary>
    public static void RequestExit()
    {
        IsExiting = true;
        Current?.Shutdown();
    }

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
            Tray?.Dispose();
            Tray = null;
        };
        SessionEnding += (_, _) =>
        {
            IsExiting = true;   // 系统关机 / 注销：别再被「关闭=最小化到托盘」拦住
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

        // 任务栏右侧的托盘图标：单击 / 双击恢复窗口，右键菜单（显示主窗口 / 主页设置 / 退出）。
        SetupTrayIcon(window, vm);

        // 开发调试：YLT_WINDOW_SIZE=1024x700 强制窗口尺寸（截图核对最小窗口 / 窄屏版式用）
        var windowSize = Environment.GetEnvironmentVariable("YLT_WINDOW_SIZE");
        if (!string.IsNullOrWhiteSpace(windowSize))
        {
            var parts = windowSize.Split('x', 'X', '×');
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), out var sizeW) && sizeW > 0
                && int.TryParse(parts[1].Trim(), out var sizeH) && sizeH > 0)
            {
                window.Width = Math.Max(window.MinWidth, sizeW);
                window.Height = Math.Max(window.MinHeight, sizeH);
            }
        }

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

        // 双击图片文件启动（文件关联 / 命令行参数）：直接进「图片连续阅读器」，
        // 加载这张图所在文件夹，并定位到它（连续滚动滚过去、翻页模式直接翻到那一页）。
        var launchImage = e.Args.FirstOrDefault(PictureFileAssociation.IsSupportedImageFile);
        if (!string.IsNullOrWhiteSpace(launchImage))
        {
            try { PictureFilePathLaunch.PendingImagePath = Path.GetFullPath(launchImage.Trim().Trim('"')); }
            catch { PictureFilePathLaunch.PendingImagePath = launchImage; }

            var pictureTool = tools.FirstOrDefault(t => t.Id == PictureViewerConfig.ToolId);
            if (pictureTool is not null) vm.OpenToolCommand.Execute(pictureTool);
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

    /// <summary>
    /// 建任务栏托盘图标（不引 WinForms，手写 <see cref="TrayIcon"/>）：
    /// 左键 / 双击恢复主窗口；右键菜单 = 显示主窗口 / 主页设置 / 退出。
    ///
    /// <para>托盘建不出来（极端环境）时只写 Debug 输出、**不影响程序启动**；
    /// 此时 <see cref="IsTrayVisible"/> 为 false，「关闭=最小化到托盘」会退化成正常关闭，
    /// 免得窗口藏起来之后用户找不回来。</para>
    /// </summary>
    private static void SetupTrayIcon(MainWindow window, MainViewModel viewModel)
    {
        try
        {
            var tray = new TrayIcon("YuLiang 工具集", () => new[]
            {
                new TrayMenuItem("显示主窗口", window.ShowFromTray, isDefault: true),
                new TrayMenuItem("主页设置…", () =>
                {
                    window.ShowFromTray();
                    viewModel.OpenSettings();
                }),
                TrayMenuItem.Separator,
                new TrayMenuItem("退出 YuLiang 工具集", RequestExit),
            });
            tray.Activated += (_, _) => window.ShowFromTray();
            Tray = tray;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayIcon] 创建托盘图标失败：{ex}");
        }
    }
}
