using System.IO;
using System.Windows;
using YuLiangToolkit.App.Themes;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.App.Views;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PdfReader;
using YuLiangToolkit.Tools.PictureViewer;

namespace YuLiangToolkit.App;

public partial class App : Application
{
    /// <summary>单实例协调器（第一个实例开管道服务端；第二实例只负责发消息然后退出）。</summary>
    private static SingleInstance? _singleInstance;

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

        // 单实例：已经有窗口在跑时，把命令行（例如双击图片的路径）发给它，自己直接退出。
        // 截图 / 调试进程不参与（见 SingleInstance.IsEnabled），否则测试会被正在使用的窗口吸走。
        if (SingleInstance.IsEnabled)
        {
            _singleInstance = new SingleInstance();
            if (!_singleInstance.IsFirstInstance)
            {
                var outgoing = NormalizeLaunchArguments(e.Args);
                SingleInstance.AllowForegroundActivation();
                if (_singleInstance.TrySendToExisting(outgoing, TimeSpan.FromSeconds(3)))
                {
                    _singleInstance.Dispose();
                    _singleInstance = null;
                    Shutdown(0);
                    return;
                }

                // 连不上旧实例（它还在启动 / 已经卡死）：别让用户双击没反应，退回正常开窗口
            }
        }

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
            _singleInstance?.Dispose();
            _singleInstance = null;
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

        // 建主窗口之前先应用主题（默认 / 液态玻璃）：窗口 XAML 一解析就要用主题里的颜色，
        // 反过来的话第一帧会闪一下默认主题（见 Views/Themes/ThemeManager.cs）。
        ThemeManager.Apply(vm.UiTheme);

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

        // 双击 PDF 文件启动（文件关联 / 命令行参数）：直接进「PDF 阅读器」并打开这个文件。
        var launchPdf = e.Args.FirstOrDefault(PdfFileAssociation.IsSupportedPdfFile);
        if (!string.IsNullOrWhiteSpace(launchPdf) && string.IsNullOrWhiteSpace(launchImage))
        {
            try { PdfFilePathLaunch.PendingPdfPath = Path.GetFullPath(launchPdf.Trim().Trim('"')); }
            catch { PdfFilePathLaunch.PendingPdfPath = launchPdf; }

            var pdfTool = tools.FirstOrDefault(t => t.Id == PdfReaderConfig.ToolId);
            if (pdfTool is not null) vm.OpenToolCommand.Execute(pdfTool);
        }

        // 开发调试：YLT_OPEN_SETTINGS=1 进入「主页设置」页面（v0.26.0 起设置是页面而不是独立窗口；
        // 配 YLT_SHOT 时截的就是主窗口这一页，命令见 docs/shell.md）
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_OPEN_SETTINGS")))
        {
            vm.OpenSettings();
        }

        if (!string.IsNullOrWhiteSpace(shotPath))
        {
            window.CaptureTo(shotPath);
        }
        else
        {
            // 正常启动：开管道服务端，之后每次双击图片 / 再启动一次都交给这个窗口处理
            _singleInstance?.StartServer(args => HandleSecondInstance(args, window, vm, tools));
        }
    }

    /// <summary>
    /// 第二实例发来的命令行：先把窗口从托盘 / 最小化恢复并置前；如果带了一张受支持的图片 / 一个 PDF，
    /// 就打开对应工具（图片阅读器 / PDF 阅读器）——新 ViewModel 会取走 Pending 路径打开它。
    /// </summary>
    private static void HandleSecondInstance(
        string[] args, MainWindow window, MainViewModel viewModel, IReadOnlyList<ITool> tools)
    {
        SingleInstance.ActivateWindow(window);

        var image = args.FirstOrDefault(PictureFileAssociation.IsSupportedImageFile);
        if (!string.IsNullOrWhiteSpace(image))
        {
            try { PictureFilePathLaunch.PendingImagePath = Path.GetFullPath(image.Trim().Trim('"')); }
            catch { PictureFilePathLaunch.PendingImagePath = image; }

            var pictureTool = tools.FirstOrDefault(t => t.Id == PictureViewerConfig.ToolId);
            if (pictureTool is not null) viewModel.OpenToolCommand.Execute(pictureTool);
            return;
        }

        var pdf = args.FirstOrDefault(PdfFileAssociation.IsSupportedPdfFile);
        if (string.IsNullOrWhiteSpace(pdf)) return;

        try { PdfFilePathLaunch.PendingPdfPath = Path.GetFullPath(pdf.Trim().Trim('"')); }
        catch { PdfFilePathLaunch.PendingPdfPath = pdf; }

        var pdfTool = tools.FirstOrDefault(t => t.Id == PdfReaderConfig.ToolId);
        if (pdfTool is not null) viewModel.OpenToolCommand.Execute(pdfTool);
    }

    /// <summary>
    /// 第二实例发消息前先把「受支持的图片 / PDF 路径」转成绝对路径：两个进程工作目录可能不同，
    /// 直接发相对路径的话第一个实例会按自己的目录解析错。
    /// </summary>
    private static string[] NormalizeLaunchArguments(string[] args)
    {
        var result = new string[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var isLaunchFile = PictureFileAssociation.IsSupportedImageFile(arg)
                               || PdfFileAssociation.IsSupportedPdfFile(arg);
            if (!isLaunchFile)
            {
                result[i] = arg;
                continue;
            }

            try { result[i] = Path.GetFullPath(arg.Trim().Trim('"')); }
            catch { result[i] = arg; }
        }

        return result;
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
