using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.Rendering;
using YuLiangToolkit.App.Themes;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.App.Views;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Notifications;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.NotificationLog;
using YuLiangToolkit.Tools.TaskManager;

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

    /// <summary>
    /// 主 ViewModel 的进程内引用（启动时存下）。
    /// <para>⚠ 程序退出时**不能**再从 <see cref="Application.MainWindow"/> 取 DataContext：主窗口一关，
    /// WPF 就把 <c>Application.MainWindow</c> 置成 null，<see cref="MainViewModel.ShutdownTools"/> 会被静默跳过，
    /// 工具（播放器）的 <c>OnClosed</c> / 落盘就全丢了 —— 用户报「关掉应用后播放器没记住上次那首 / 进度」
    /// 的根因就是这条（v0.65.1 修）。</para>
    /// </summary>
    private static MainViewModel? _mainViewModel;

    /// <summary>全局快捷键引擎（整个进程一个；退出时释放，注销所有热键并卸下键盘钩子）。</summary>
    private static GlobalShortcutEngine? _shortcutEngine;

    /// <summary>Windows 通知通道（托盘图标是后面才建的，所以这里用「现取」的委托）。</summary>
    private static readonly TrayNotificationSink _trayNotificationSink = new(() => Tray);

    /// <summary>通知历史（所有工具的通知都记一份；由「通知管理」工具展示）。</summary>
    private static NotificationHistory? _notificationHistory;

    /// <summary>历史有变化、等退出时统一写盘。</summary>
    private static bool _notificationHistoryDirty;

    /// <summary>
    /// 退出时把通知历史写盘（只在「记住历史」开着且有变化时写）。
    /// <para>由「通知管理」工具的配置决定记不记；这里读它的配置，避免把「关掉记住」的用户又写一份。</para>
    /// </summary>
    private static void SaveNotificationHistory()
    {
        var history = _notificationHistory;
        if (history is null || !_notificationHistoryDirty) return;

        try
        {
            var config = ToolConfigStore.For(NotificationLogConfig.ToolId, "config.json")
                .Load<NotificationLogConfig>();

            NotificationLogStore.Save(history.Items, config.RememberHistory);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NotificationHistory] 退出时写历史失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 外壳级别的通知偏好（只有「快捷键注册失败」这类没有归属工具的提醒用它）。
    /// <para>各功能自己的通知偏好存在各自配置里（见 <see cref="NotificationSettings"/>），
    /// 这里不集中配置，避免和「每个工具分开设」的设计打架。</para>
    /// </summary>
    private static readonly NotificationSettings DefaultNotificationSettings = new()
    {
        Mode = NotificationMode.InApp,
        Corner = NotificationCorner.BottomRight,
    };

    /// <summary>真正退出程序（托盘「退出」/ 截图模式 / 系统关机）。</summary>
    /// <remarks>
    /// 退出前会先问一遍还活着的工具（<see cref="MainViewModel.ConfirmCloseTools"/>）：
    /// 有未保存的谱面就弹「保存 / 不保存 / 取消」，用户点取消就中止这次退出。
    /// ⚠ 只有**真正退出**走这里；「关闭 = 最小化到托盘」不经过它，所以不会被拦。
    /// </remarks>
    public static void RequestExit()
    {
        if (!ConfirmExitTools()) return;

        IsExiting = true;
        Current?.Shutdown();
    }

    /// <summary>
    /// 退出确认（可能弹窗）。返回 false = 用户取消，别继续退出。
    ///
    /// <para>⚠ 用静态「已确认」标记避免重复弹窗：<see cref="RequestExit"/> 会从好几条路径进来
    /// （托盘 / 任务管理器 / 重启 / 关窗口），一次退出不该弹两次框。</para>
    ///
    /// <para>⚠ <b>截图 / 自动化模式下必须跳过</b>：<c>YLT_SHOT</c> 那条路径会自动
    /// <c>RequestExit()</c>，一旦弹出确认框就没人点，整个自动化会挂在那里等超时。</para>
    /// </summary>
    private static bool ConfirmExitTools()
    {
        if (_exitConfirmed) return true;

        // 截图 / 无头冒烟：没有真人点按钮，直接放行
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("YLT_SHOT")))
        {
            _exitConfirmed = true;
            return true;
        }

        var viewModel = _mainViewModel;
        if (viewModel is null) return true;

        try
        {
            if (!viewModel.ConfirmCloseTools()) return false;
        }
        catch
        {
            // 确认流程本身出错时不要困住用户退出
            return true;
        }

        _exitConfirmed = true;
        return true;
    }

    /// <summary>本次退出已经问过用户了（避免多条退出路径重复弹窗）。</summary>
    private static bool _exitConfirmed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 「主页设置 → 渲染与性能」的「重启本程序」：新进程带着旧 PID 起来，先等旧进程彻底退出
        // （单实例 Mutex / 命名管道 / 托盘图标都是按进程占用的），再走正常启动流程。
        // 隐藏参数在这里被摘掉，后面的文件参数 / 单实例转发都看不到它。
        //
        // 「任务管理器 → 以管理员身份重启」复用同一套机制，额外带一个 --ylt-tool（重启后回到哪个工具页）；
        // 提权回调也在这里注入：工具层不能直接引用 App，靠 AppElevation.ExitApplication 拿到真正退出的入口。
        AppElevation.ExitApplication = RequestExit;
        var argsWithoutToolSwitch = AppElevation.StripToolSwitch(e.Args, out var reopenToolId);
        var launchArgs = RestartLaunchArguments.Strip(argsWithoutToolSwitch, out var restartWaitPid);
        if (restartWaitPid > 0) WaitForPreviousProcess(restartWaitPid);

        // 单实例：已经有窗口在跑时，把命令行（例如双击图片的路径）发给它，自己直接退出。
        // 截图 / 调试进程不参与（见 SingleInstance.IsEnabled），否则测试会被正在使用的窗口吸走。
        if (SingleInstance.IsEnabled)
        {
            _singleInstance = new SingleInstance();
            if (!_singleInstance.IsFirstInstance)
            {
                var outgoing = NormalizeLaunchArguments(launchArgs);
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

        // 渲染 GPU（v0.96.0「主页设置 → 渲染与性能」）：Windows 只在进程启动时按「图形首选项」挑卡，
        // WPF 的软件渲染开关也必须在第一个窗口创建前定下来，所以这里先读外壳配置、应用本次启动的渲染模式。
        // （云间天上的云朵 / 液态玻璃的光晕都靠 WPF 画，被分到核显或软件渲染上就会卡。）
        var startupShell = ToolConfigStore.For("shell").Load<ShellConfig>();
        var startupRenderGpu = ResolveStartupRenderGpu(startupShell);
        GpuRenderPreference.ApplyProcessRenderMode(startupRenderGpu);

        // 退出前把还没落盘的配置写掉（防抖窗口内的改动不会丢），顺手把（可能被全屏藏起来的）任务栏还回去
        Exit += (_, _) =>
        {
            TaskbarHider.Restore();
            // ⚠ 不能用 MainWindow.DataContext 拿 ViewModel：窗口一关 WPF 就把 Application.MainWindow 置空，
            // 退出流程里会静默跳过 ShutdownTools（工具的 OnClosed / 落盘全丢）—— 用启动时存下的引用。
            try { _mainViewModel?.ShutdownTools(); } catch { /* 退出流程忽略 */ }
            ToolConfigStore.FlushAll();
            SaveNotificationHistory();
            try { ShortcutHub.Shutdown(); } catch { /* 退出流程忽略 */ }
            Tray?.Dispose();
            Tray = null;
            _singleInstance?.Dispose();
            _singleInstance = null;
        };
        SessionEnding += (_, _) =>
        {
            // 系统关机 / 注销：时间有限，先尽力问一次（赶不上就自动保存兜底，见工具的 OnClosed）
            try { ConfirmExitTools(); } catch { /* 关机流程忽略 */ }
            IsExiting = true;   // 系统关机 / 注销：别再被「关闭=最小化到托盘」拦住
            TaskbarHider.Restore();
            try { _mainViewModel?.ShutdownTools(); } catch { /* 退出流程忽略 */ }
            ToolConfigStore.FlushAll();
        };

        // 自动发现所有工具（主程序集 + plugins 目录）
        var tools = ToolDiscovery.Discover();

        var vm = new MainViewModel(tools);
        _mainViewModel = vm;   // 退出流程用（见 _mainViewModel 注释：不能靠 Application.MainWindow）

        // 全局快捷键体系（v1.44.0）：引擎 + 汇总注册表注入 Core 的静态入口，
        // 再把实现了 IShortcutProvider 的工具登记进去（秒表 / Kps / 录屏 / Live2D）。
        // ⚠ 必须在建主窗口之前：工具可能在窗口出现前就想注册热键。
        _shortcutEngine = new GlobalShortcutEngine();
        ShortcutHub.Initialize(_shortcutEngine, vm.ShortcutRegistry);
        vm.ShortcutRegistry.ReportProblem = (title, body) =>
            NotificationCenter.Show(DefaultNotificationSettings,
                new NotificationRequest(title, body, NotificationKind.Warning, "shortcuts"));
        foreach (var tool in tools.OfType<IShortcutProvider>()) ShortcutHub.AddProvider(tool);
        ShortcutHub.ApplyAll();

        // 通知通道（v1.44.0）：应用内自绘小窗 + 托盘气泡。
        NotificationCenter.SetSinks(new InAppNotificationSink(), _trayNotificationSink);

        // 通知历史（v1.46.0）：所有工具发出的通知都自动记一条，供「通知管理」工具查看 / 删除。
        // 历史由通知管理工具的配置决定是否落盘（那边页面打开时会读盘并接管），这里先给一份内存实例，
        // 保证「程序刚启动、还没打开过通知管理」期间的通知也不会丢。
        _notificationHistory = new NotificationHistory();
        NotificationCenter.History = _notificationHistory;
        NotificationCenter.RegisterSource("shell", "外壳");

        // 来源显示名：工具自己声明的名字，历史列表里显示成人话（以后新工具自动生效）
        foreach (var tool in tools)
        {
            if (!string.IsNullOrWhiteSpace(tool.Id)) NotificationCenter.RegisterSource(tool.Id, tool.Name);
        }

        // 通知历史落盘：挂在外壳上（不等通知管理页被打开），退出时再写一次
        _notificationHistoryDirty = false;
        _notificationHistory.Changed += (_, _) =>
        {
            _notificationHistoryDirty = true;
        };


        // 「打开方式」同步（v0.79.0）：Release 版开关开着就登记 / 刷新入口副本；关着（或 Debug 构建）
        // 就把 Windows 记住的旧登记清掉。只写 HKCU，失败不影响启动。
        vm.SyncOpenWithAssociationOnStartup();

        // 「开机自启」（v0.99.0）：以设置里的开关为准把注册表那条 Run 项对齐 ——
        // 开着就刷新成当前 exe 路径（程序换目录后也能启动对的那个），关着但还留着就清掉。
        vm.SyncStartWithWindowsOnStartup();

        // 建主窗口之前先应用主题（风格 / 深浅 / 配色）：窗口 XAML 一解析就要用主题里的颜色，
        // 反过来的话第一帧会闪一下默认主题（见 Views/Themes/ThemeManager.cs）。
        vm.ApplyTheme(force: true);

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

        // 开发调试：可选 YLT_SHOT_TOOL / YLT_NAV_TOOL 直接打开某个工具；
        // 另外 --ylt-tool（任务管理器「以管理员身份重启」带回来的）也会自动打开对应工具页。
        var toolId = Environment.GetEnvironmentVariable("YLT_SHOT_TOOL")
                     ?? Environment.GetEnvironmentVariable("YLT_NAV_TOOL")
                     ?? reopenToolId;
        if (!string.IsNullOrWhiteSpace(toolId))
        {
            var tool = tools.FirstOrDefault(t => t.Id == toolId);
            if (tool is not null) vm.OpenToolCommand.Execute(tool);
        }

        // 开发调试：YLT_STATE_SWITCH_TEST=1 验证「切走再切回，工具状态还在」：
        // 打开工具后回主页、再打开回来；截图里如果还是原来那个文件 / 列表，就说明视图缓存生效了。
        if (Environment.GetEnvironmentVariable("YLT_STATE_SWITCH_TEST") is "1" or "on" or "true")
        {
            var tool = tools.FirstOrDefault(t => t.Id == toolId);
            if (tool is not null)
            {
                var switchTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(300),
                };
                var switchStep = 0;
                switchTimer.Tick += (_, _) =>
                {
                    switchStep++;
                    if (switchStep == 1)
                    {
                        vm.GoHomeCommand.Execute(null);   // 切走（工具页应被缓存而不是销毁）
                    }
                    else if (switchStep == 2)
                    {
                        vm.OpenToolCommand.Execute(tool); // 切回来（应原样复用缓存的视图）
                        switchTimer.Stop();
                    }
                };
                switchTimer.Start();
            }
        }

        // 双击图片 / PDF / 右键「打开方式」等启动（文件关联 / 命令行参数）：统一交给路由，
        // 按模式（自动 / 文本 / 压缩包）打开对应工具并交出文件。
        FileLaunchRouter.TryHandle(launchArgs, tools, vm);

        // 开发调试：YLT_OPEN_SETTINGS=1 进入「主页设置」页面（v0.26.0 起设置是页面而不是独立窗口；
        // 配 YLT_SHOT 时截的就是主窗口这一页，命令见 docs/shell.md）
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_OPEN_SETTINGS")))
        {
            vm.OpenSettings();
        }

        // 开发调试：YLT_FULLSCREEN_ROUNDTRIP=1 进全屏 400ms 再退出，截图验收「退出全屏后是正常最大化（带任务栏）」
        if (Environment.GetEnvironmentVariable("YLT_FULLSCREEN_ROUNDTRIP") is "1" or "on" or "true")
        {
            if (Environment.GetEnvironmentVariable("YLT_START_MAXIMIZED") is "1" or "on" or "true")
            {
                window.WindowState = WindowState.Maximized;
            }

            window.SetFullscreen(true);
            var roundTrip = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            roundTrip.Tick += (_, _) =>
            {
                roundTrip.Stop();
                window.SetFullscreen(false);
            };
            roundTrip.Start();
        }

        if (!string.IsNullOrWhiteSpace(shotPath))
        {
            ScheduleDebugPageScroll(window, vm, shotPath);
            window.CaptureTo(shotPath);
        }
        else
        {
            // 正常启动：开管道服务端，之后每次双击图片 / 再启动一次都交给这个窗口处理
            _singleInstance?.StartServer(args => HandleSecondInstance(args, window, vm, tools));

            // 联动启动（v1.41.0）：主窗口已经显示、托盘 / 管道都就绪之后，问一次各工具要不要在启动时干活
            // （目前只有「软件启动器」：把勾了「联动启动」的程序一起拉起来）。
            // ⚠ 截图模式（YLT_SHOT）不触发：自动化跑一遍不该顺手启动一堆外部程序。
            // ⚠ 「重启本程序」也不触发（restartWaitPid > 0）：那是切渲染 GPU / 提权的重启，
            //    程序本来就在运行中，再拉一遍联动程序就是重复启动。
            if (restartWaitPid <= 0) RunStartupTasks(tools);
        }
    }

    /// <summary>
    /// 启动后任务（<see cref="IStartupTask"/>）：外壳统一入口，工具自己判断要不要跑。
    ///
    /// <para>放在窗口显示之后、单实例服务端开好之后：启动别人的程序是次要动作，
    /// 先把用户能看到的东西摆好，实际拉起放到后台（工具实现里就该丢线程池）。</para>
    /// <para>按项隔离异常：一个工具出错不能让后面的工具收不到这次机会，更不能影响启动。</para>
    /// </summary>
    private static void RunStartupTasks(IReadOnlyList<ITool> tools)
    {
        foreach (var tool in tools)
        {
            if (tool is not IStartupTask task) continue;

            try
            {
                if (!task.ShouldRunOnStartup) continue;
                task.RunOnStartup();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Startup] 「{tool.Name}」的启动任务失败：{ex}");
            }
        }
    }

    /// <summary>
    /// 本次启动实际使用的渲染 GPU 模式（v0.96.0）。
    /// <para>配置里有明确值就用它；老配置没有这个字段（<c>null</c>）时，先看 Windows 图形首选项里
    /// 本程序是否已经被手动指定过（Windows 设置 / NVIDIA 控制面板 / 以前的版本），有就继承、没有就「自动」，
    /// 然后落成明确值 —— 免得升级 / 换目录把用户原来的显卡偏好冲掉。</para>
    /// <para>硬件模式顺便把图形首选项补写一次（程序目录搬动 / 注册表被清理后自动修好）。
    /// 「自动」不主动写注册表：没有设置过就保持没有，别去覆盖用户在 Windows 里手动设的值。</para>
    /// </summary>
    private static ShellRenderGpu ResolveStartupRenderGpu(ShellConfig shell)
    {
        var resolved = shell.RenderGpu ?? GpuRenderPreference.ReadWindowsPreference() ?? ShellRenderGpu.Auto;
        if (shell.RenderGpu != resolved) shell.RenderGpu = resolved;

        if (resolved is ShellRenderGpu.HighPerformance or ShellRenderGpu.PowerSaving)
            GpuRenderPreference.TryWriteWindowsPreference(resolved, out _);

        return resolved;
    }

    /// <summary>
    /// 「重启本程序」的新进程：等旧进程退出（最多 8 秒）再往下走。
    /// 旧进程要是卡住没退，就设置 <c>YLT_NO_SINGLE_INSTANCE=1</c>，保证新窗口一定能起来，
    /// 而不是被旧进程的单实例管道当成「第二实例」吞掉。
    /// </summary>
    private static void WaitForPreviousProcess(int processId)
    {
        var stillRunning = false;
        try
        {
            using var previous = Process.GetProcessById(processId);
            stillRunning = !previous.WaitForExit(8000);
        }
        catch
        {
            // 旧进程已经退出了：正常往下走
        }

        if (stillRunning)
        {
            Environment.SetEnvironmentVariable("YLT_NO_SINGLE_INSTANCE", "1");
        }
    }

    /// <summary>
    /// 截图 / 调试：<c>YLT_PAGE_SCROLL=bottom</c>（或像素值）—— 打开页面后把**整页的 ScrollViewer**
    /// 滚到指定位置再截图，用来核对长页面（主区域 + 日志之类的固定行）底部的版式。
    /// 只认「工具页 / 设置页的根内容就是 ScrollViewer」这种页面；不是长页面的页忽略。
    /// 只在截图模式（<c>YLT_SHOT</c>）下挂，不影响正常使用、不写配置。
    /// </summary>
    private static void ScheduleDebugPageScroll(MainWindow window, MainViewModel viewModel, string shotPath)
    {
        var wanted = Environment.GetEnvironmentVariable("YLT_PAGE_SCROLL");
        if (string.IsNullOrWhiteSpace(wanted)) return;

        // 用 ApplicationIdle 而不是 Loaded：个别页面（聊天室）在 Loaded 里会自己把外页滚回顶部
        // （「没有会话 → 回到顶部」），必须等它们跑完再滚，截图才滚得动。CaptureTo 也是
        // ApplicationIdle，但它进回调后会先 Task.Delay(1200)，我们的回调会在这段时间里执行。
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            // 工具页 / 设置页的根内容就是页面自己的 ScrollViewer（工具页外面还有外壳的 ContentControl）
            var scroller = FindPageScrollViewer(viewModel.CurrentView as DependencyObject);
            if (scroller is null) return;
            scroller.UpdateLayout();

            if (wanted.Trim().Equals("bottom", StringComparison.OrdinalIgnoreCase))
            {
                scroller.ScrollToEnd();
            }
            else if (double.TryParse(wanted, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var offset))
            {
                scroller.ScrollToVerticalOffset(Math.Max(0, offset));
            }
        }));
    }

    /// <summary>找「页面自己的 ScrollViewer」：工具页 / 设置页的 UserControl 根内容就是它。</summary>
    private static System.Windows.Controls.ScrollViewer? FindPageScrollViewer(DependencyObject? view)
    {
        return view switch
        {
            System.Windows.Controls.ScrollViewer self => self,
            System.Windows.Controls.UserControl { Content: System.Windows.Controls.ScrollViewer inner } => inner,
            _ => null,
        };
    }

    /// <summary>
    /// 第二实例发来的命令行：先把窗口从托盘 / 最小化恢复并置前；如果带的是「打开方式」请求
    /// （文件 / 模式），就交给 <see cref="FileLaunchRouter"/> 打开对应工具。
    /// </summary>
    private static void HandleSecondInstance(
        string[] args, MainWindow window, MainViewModel viewModel, IReadOnlyList<ITool> tools)
    {
        SingleInstance.ActivateWindow(window);
        FileLaunchRouter.TryHandle(args, tools, viewModel);
    }

    /// <summary>
    /// 第二实例发消息前先把「文件参数」转成绝对路径：两个进程工作目录可能不同，
    /// 直接发相对路径的话第一个实例会按自己的目录解析错。
    /// </summary>
    private static string[] NormalizeLaunchArguments(string[] args)
        => OpenWithArguments.NormalizeFileArguments(args);

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
