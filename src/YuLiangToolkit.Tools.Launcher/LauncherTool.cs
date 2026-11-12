using System.Diagnostics;
using System.Windows;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Launcher.Models;
using YuLiangToolkit.Tools.Launcher.Services;
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
///
/// <para><b>联动启动</b>（v1.41.0）：额外实现 <see cref="IStartupTask"/>，外壳在主窗口显示后会问一次
/// 「要不要随工具集一起启动」——勾了「联动启动」的启动项就在那时候被拉起来。
/// 本类不碰界面，只读配置 + 调 <see cref="LauncherStartService"/>；打开工具页时把这次的启动结果写进日志。</para>
/// </summary>
public sealed class LauncherTool : ITool, IStartupTask
{
    /// <summary>
    /// 最近一次「随工具集启动」执行的说明（成功 / 失败的每一项）。只留在内存里，
    /// 供之后打开工具页时写进运行日志；不落盘（免得每次启动都改一次配置文件）。
    /// </summary>
    private static string _lastStartupSummary = "";

    public string Id => "launcher";

    public string Name => "软件启动器";

    public string Description =>
        "把常用的 exe / 快捷方式收进一个图标墙，双击就能启动；右侧看完整软件名与路径，可重命名、换图标、删除，还能一键清掉失效快捷方式和已卸载的软件。勾上「联动启动」的程序会随本工具集一起启动。";

    public string EmojiIcon => "🚀";

    public string Category => "系统";

    /// <summary>主页标签：点标签可快速筛出同类工具。</summary>
    public IReadOnlyList<string> Tags => new[] { "启动器", "快捷方式", "桌面", "图标", "exe", "联动启动" };

    public int Order => 44;

    public FrameworkElement CreateView()
        => new LauncherView { DataContext = new LauncherViewModel() };

    // ---------------------------------------------------------------- 联动启动

    /// <summary>最近一次随工具集启动的结果说明（空 = 这次启动没有联动启动任何程序）。</summary>
    internal static string LastStartupSummary => _lastStartupSummary;

    /// <summary>
    /// 要不要在本次程序启动时联动启动：总开关开着，并且至少有一项勾了联动启动。
    /// <para>只读配置做判断，不启动任何程序（外壳先问这个，再决定要不要调 <see cref="RunOnStartup"/>）。</para>
    /// <para>⚠ 配置文件还不存在（从没打开过启动器）时直接返回 false，<b>不要为一次判断把配置建出来</b>
    /// —— 项目约定「数据目录用到才建」，启动流程不该给没用过的功能留文件。</para>
    /// </summary>
    public bool ShouldRunOnStartup
    {
        get
        {
            try
            {
                var store = ToolConfigStore.For("launcher");
                if (!store.Exists) return false;

                var config = store.Load<LauncherConfig>();
                return config.StartLinkedOnToolkitStartup && config.LinkedItemCount > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Launcher] 读取联动启动配置失败：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 执行一次联动启动：挑出「勾了开关 + 当前可用」的项逐个启动，失败按项隔离。
    /// <para>⚠ 外壳在 UI 线程上同步调用它，所以这里立刻把活丢到线程池，自己马上返回。</para>
    /// </summary>
    public void RunOnStartup()
    {
        _lastStartupSummary = "";

        LauncherConfig config;
        try
        {
            config = LoadStartupConfig();
        }
        catch (Exception ex)
        {
            _lastStartupSummary = $"读取配置失败：{ex.Message}";
            Debug.WriteLine($"[Launcher] 联动启动失败：{ex.Message}");
            return;
        }

        // 拷贝一份再丢后台：配置对象随时可能被工具页改动。
        // （拷贝里带上 StartWithToolkit，SelectStartupItems 的过滤条件在后台是一致的）
        var items = config.Items
            .Where(item => item is not null && item.StartWithToolkit)
            .Select(item => new LauncherItemConfig
            {
                Id = item.Id,
                Name = item.Name,
                Path = item.Path,
                StartWithToolkit = true,
            })
            .ToList();
        if (items.Count == 0) return;

        _ = Task.Run(() =>
        {
            var probe = new SystemLauncherProbe();
            var selected = LauncherStartService.SelectStartupItems(items, probe);
            var skipped = items.Count - selected.Count;
            var outcome = LauncherStartService.StartMany(selected, SystemLauncherProcessStarter.Shared);

            var summary = $"联动启动：成功 {outcome.Started} 项"
                          + (skipped > 0 ? $"，跳过失效 {skipped} 项" : "")
                          + (outcome.Failures.Count > 0 ? $"；失败 {outcome.Failures.Count} 项（{string.Join(" / ", outcome.Failures)}）" : "");
            _lastStartupSummary = summary;
            Debug.WriteLine($"[Launcher] {summary}");
        });
    }

    /// <summary>
    /// 读联动启动用的配置：走同一个 <see cref="ToolConfigStore"/>，工具页打开时拿到的是同一份对象
    /// （<see cref="ToolConfigStore.Load{T}"/> 按类型缓存实例），不会出现「两套配置互相覆盖」。
    /// </summary>
    private static LauncherConfig LoadStartupConfig() => ToolConfigStore.For("launcher").Load<LauncherConfig>();
}

