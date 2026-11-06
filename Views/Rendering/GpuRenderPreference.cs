using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.Core.Shell;

namespace YuLiangToolkit.App.Rendering;

/// <summary>
/// 渲染 GPU 设置的「落地层」（v0.96.0）：把用户在「主页设置 → 渲染与性能」里选的模式
/// 写成 Windows 图形首选项（和系统设置里的同名选项是同一份注册表数据），
/// 以及启动时决定 WPF 用硬件还是软件渲染。
///
/// <para><b>为什么改完要重启</b>：Windows 只在进程启动、创建图形设备时读取图形首选项；
/// WPF 的 <see cref="RenderOptions.ProcessRenderMode"/> 也必须在第一个窗口渲染之前定下来。
/// 所以这里只负责「写设置 / 启动时应用」，重启动作见 <see cref="TryRestart"/>。</para>
///
/// <para><b>两个层次</b>：</para>
/// <list type="number">
///   <item><b>显卡选择</b>（自动 / 高性能 / 节能）= 注册表 <c>GpuPreference=0/1/2;</c>，
///         写法和 Windows「设置 → 系统 → 显示 → 图形」完全一样；</item>
///   <item><b>软件渲染</b> = 本进程的 WPF 渲染模式，不写注册表、不影响系统里其它程序。</item>
/// </list>
/// </summary>
public static class GpuRenderPreference
{
    /// <summary>
    /// 写图形首选项时用的 exe 路径：入口副本（<c>*.open-text.exe</c>）要还原成主程序 exe，
    /// 否则设置只对那个副本生效、主程序读不到。拿不到路径时返回空串。
    /// </summary>
    public static string ExecutablePathForPreference()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath)) return string.Empty;
            var canonical = OpenWithRegistration.ResolveCanonicalExecutablePath(processPath);
            return string.IsNullOrWhiteSpace(canonical) ? processPath : canonical;
        }
        catch
        {
            return Environment.ProcessPath ?? string.Empty;
        }
    }

    /// <summary>
    /// 读 Windows 图形首选项里本程序那一条（原始字符串，可能是 <c>GpuPreference=2;</c>）。
    /// 没设置过 / 注册表读不到时 value = null；真正的读取失败返回 false（error 带原因）。
    /// </summary>
    public static bool TryReadWindowsPreference(out string? value, out string? error)
    {
        value = null;
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ShellRenderGpuSettings.RegistryKeyPath);
            if (key is null) return true;

            var executable = ExecutablePathForPreference();
            if (string.IsNullOrWhiteSpace(executable)) return true;

            value = key.GetValue(executable) as string;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>读回来并解析成模式；没设置 / 解析不了返回 null。</summary>
    public static ShellRenderGpu? ReadWindowsPreference()
        => TryReadWindowsPreference(out var raw, out _)
           && ShellRenderGpuSettings.TryParsePreferenceValue(raw, out var mode)
            ? mode
            : null;

    /// <summary>
    /// 把模式写进 Windows 图形首选项（软件渲染不写）。成功 / 无需写返回 true；失败返回 false 并给出原因。
    /// <para>只改 <c>GpuPreference=</c> 一段，注册表值里其它系统 / 驱动写的段原样保留。</para>
    /// </summary>
    public static bool TryWriteWindowsPreference(ShellRenderGpu mode, out string? error)
    {
        error = null;
        if (!ShellRenderGpuSettings.UsesWindowsPreference(mode)) return true;

        try
        {
            var executable = ExecutablePathForPreference();
            if (string.IsNullOrWhiteSpace(executable))
            {
                error = "拿不到当前 exe 路径";
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(ShellRenderGpuSettings.RegistryKeyPath);
            if (key is null)
            {
                error = "打不开 HKCU\\" + ShellRenderGpuSettings.RegistryKeyPath;
                return false;
            }

            var existing = key.GetValue(executable) as string;
            key.SetValue(executable,
                ShellRenderGpuSettings.BuildPreferenceValue(mode, existing),
                RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 启动时应用「软件渲染」开关（必须在创建第一个窗口之前调用）。
    /// 硬件模式下保持 WPF 默认（由 Windows / 驱动决定用哪块卡）。
    /// </summary>
    public static void ApplyProcessRenderMode(ShellRenderGpu mode)
    {
        try
        {
            RenderOptions.ProcessRenderMode = mode == ShellRenderGpu.Software
                ? RenderMode.SoftwareOnly
                : RenderMode.Default;
        }
        catch
        {
            // 极老系统 / 奇怪环境里设不了：按默认走，不影响启动
        }
    }

    /// <summary>当前这一个进程实际是硬件还是软件渲染（WPF Tier 2/1 = 硬件，0 = 软件）。</summary>
    public static string DescribeCurrentRenderer()
    {
        var forcedSoftware = false;
        try { forcedSoftware = RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly; }
        catch { /* 读不到按默认 */ }

        var tier = 0;
        try { tier = RenderCapability.Tier >> 16; }
        catch { /* 读不到按 0 */ }

        if (forcedSoftware)
            return "当前渲染：软件渲染（CPU，本次启动按设置强制）";
        if (tier <= 0)
            return "当前渲染：软件渲染（CPU，WPF Tier 0，没有可用的显卡加速）";
        return tier >= 2
            ? "当前渲染：硬件加速（WPF Tier 2，显卡合成）"
            : "当前渲染：部分硬件加速（WPF Tier 1）";
    }

    /// <summary>当前 Windows 图形首选项那句说明（设置页显示用）。</summary>
    public static string DescribeWindowsPreference()
    {
        if (!TryReadWindowsPreference(out var raw, out var error))
            return "Windows 图形首选项：读取失败（" + error + "）";
        return "Windows 图形首选项：" + ShellRenderGpuSettings.DescribePreferenceValue(raw);
    }

    /// <summary>
    /// 重启本程序（先启动一个新进程并让它等旧进程退出，再关掉当前进程）。
    /// 这样单实例管道 / 托盘图标不会打架，新进程也不会被旧进程当成「第二实例」吞掉参数。
    /// </summary>
    public static bool TryRestart(out string? error)
    {
        error = null;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                error = "拿不到当前 exe 路径";
                return false;
            }

            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = RestartLaunchArguments.BuildForProcess(Environment.ProcessId),
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>
/// 「重启本程序」用的隐藏启动参数（v0.96.0）：新进程带着旧进程的 PID 起来，
/// 启动时先等旧进程退出，再照常初始化（否则单实例协调器会把新进程当成第二实例）。
/// </summary>
public static class RestartLaunchArguments
{
    /// <summary>隐藏参数名（用户一般看不到；不要和「打开方式」的文件参数混在一起）。</summary>
    public const string WaitForProcessSwitch = "--ylt-restart-pid";

    /// <summary>给新进程用的参数字符串。</summary>
    public static string BuildForProcess(int processId) => $"{WaitForProcessSwitch} {processId}";

    /// <summary>
    /// 从命令行里摘掉重启参数，返回剩下的参数（文件 / 打开方式参数交给正常启动流程）。
    /// <paramref name="waitForProcessId"/> = 0 表示不是重启启动。
    /// </summary>
    public static string[] Strip(string[]? args, out int waitForProcessId)
    {
        waitForProcessId = 0;
        if (args is null || args.Length == 0) return Array.Empty<string>();

        var kept = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], WaitForProcessSwitch, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(args[i]);
                continue;
            }

            if (i + 1 < args.Length
                && int.TryParse(args[i + 1], out var processId)
                && processId > 0)
            {
                waitForProcessId = processId;
                i++;   // 跳过 PID 本身
            }
        }

        return kept.ToArray();
    }
}
