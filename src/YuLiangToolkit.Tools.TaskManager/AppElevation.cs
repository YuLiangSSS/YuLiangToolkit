using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using YuLiangToolkit.Tools.TaskManager.Native;

namespace YuLiangToolkit.Tools.TaskManager;

/// <summary>
/// 「按需管理员权限」：平时普通权限跑，需要结束管理员进程 / 修改系统级开机启动项时，
/// 弹 UAC 把<b>整个程序</b>以管理员身份重新启动，并自动回到「任务管理器」页面。
///
/// <para><b>为什么重启整个程序，而不是在后台开一个提权小助手？</b></para>
/// <list type="bullet">
///   <item>单实例管道 / 托盘图标 / 配置加载都只承认一个进程，重启本程序是最稳的做法
///         （和「主页设置 → 渲染与性能 → 重启本程序」共用同一套 <c>--ylt-restart-pid</c> 机制）；</item>
///   <item>不用额外常驻一个管理员权限的进程 / 服务，攻击面更小；</item>
///   <item>提权后所有功能（结束任务、HKLM 启动项）一次性都可用，不用每次操作都弹一次 UAC。</item>
/// </list>
///
/// <para><b>流程</b>：<see cref="TryRestartElevated"/> 用 <c>runas</c> 启动自己的
/// <see cref="Environment.ProcessPath"/>，带上「等旧进程退出（--ylt-restart-pid）」和
/// 「重启后打开哪个工具（--ylt-tool）」两个隐藏参数；新进程退出旧进程后再正常初始化，
/// 旧进程由 <see cref="ExitApplication"/> 回调真正退出（绕过「关闭 = 最小化到托盘」）。</para>
/// </summary>
public static class AppElevation
{
    /// <summary>等旧进程退出的隐藏参数（与主程序的 <c>RestartLaunchArguments</c> 保持一致）。</summary>
    public const string RestartPidSwitch = "--ylt-restart-pid";

    /// <summary>重启后要打开哪个工具（工具 Id）的隐藏参数。</summary>
    public const string ReopenToolSwitch = "--ylt-tool";

    private static readonly Lazy<bool> Elevated = new(DetectElevated);

    /// <summary>当前进程是不是已经以管理员身份运行。</summary>
    public static bool IsCurrentProcessElevated => Elevated.Value;

    /// <summary>
    /// 主程序启动时注入的「真正退出」回调（<c>App.RequestExit</c>）。
    /// 必须走它，否则窗口的 <c>OnClosing</c> 会因为「关闭 = 最小化到托盘」把退出拦下来。
    /// </summary>
    public static Action? ExitApplication { get; set; }

    /// <summary>
    /// 拼「以管理员身份重启」用的启动参数（纯函数，自检可直接断言）：
    /// <c>--ylt-restart-pid 1234 --ylt-tool task-manager</c>。
    /// </summary>
    public static string BuildRestartArguments(int processId, string? reopenToolId)
    {
        var arguments = RestartPidSwitch + " " + processId.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(reopenToolId))
        {
            arguments += " " + ReopenToolSwitch + " " + Quote(reopenToolId.Trim());
        }
        return arguments;
    }

    /// <summary>
    /// 从命令行里摘掉 <c>--ylt-tool {工具Id}</c>，剩下的原样返回（继续交给单实例 / 文件参数处理）。
    /// 值缺失时只是忽略这个开关，不抛异常。
    /// </summary>
    public static string[] StripToolSwitch(string[]? args, out string? toolId)
    {
        toolId = null;
        if (args is null || args.Length == 0) return Array.Empty<string>();

        var kept = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], ReopenToolSwitch, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(args[i]);
                continue;
            }

            if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                toolId = args[i + 1].Trim();
                i++;   // 跳过工具 Id 本身
            }
        }

        return kept.ToArray();
    }

    /// <summary>
    /// 以管理员身份重新启动本程序（弹 UAC）。成功返回 true，调用方随后应调 <see cref="RequestExit"/> 退出旧进程。
    /// <paramref name="error"/> 是给用户看的中文原因（取消 UAC 也走这里，不算崩溃）。
    /// </summary>
    public static bool TryRestartElevated(string? reopenToolId, out string? error)
    {
        error = null;

        if (IsCurrentProcessElevated)
        {
            error = "当前已经是管理员权限，不需要重新启动。";
            return false;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            error = "拿不到当前程序的 exe 路径。";
            return false;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",                       // 关键：请求管理员权限，Windows 弹 UAC
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = BuildRestartArguments(Environment.ProcessId, reopenToolId),
            });

            if (process is null)
            {
                error = "系统没有启动新进程。";
                return false;
            }
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "已取消管理员授权（UAC 弹窗被关闭或点了「否」）。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 真正退出当前进程：优先走主程序注入的 <see cref="ExitApplication"/>（会落盘配置 / 还任务栏 /
    /// 走「关闭 = 最小化到托盘」的例外），没有注入时退回 WPF 的 Shutdown。
    /// </summary>
    public static void RequestExit()
    {
        try
        {
            if (ExitApplication is { } exit)
            {
                exit();
                return;
            }
            Application.Current?.Shutdown();
        }
        catch
        {
            // 退出流程不再抛给调用方
        }
    }

    private static string Quote(string value)
        => value.Contains(' ') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private static bool DetectElevated()
    {
        try
        {
            // 复用底层的判定（WindowsPrincipal.IsInRole），保持一致
            return ProcessTerminator.IsCurrentProcessElevated();
        }
        catch
        {
            return false;
        }
    }
}
