using System.Diagnostics;
using YuLiangToolkit.Tools.AppManager.Models;

namespace YuLiangToolkit.Tools.AppManager.Services;

/// <summary>启动结果：成功时带上进程（可监听它退出），失败时给出中文原因。</summary>
public sealed class LaunchResult
{
    /// <summary>是否成功把进程启动起来。</summary>
    public bool Started { get; init; }

    /// <summary>启动起来的进程（<c>UseShellExecute</c> 下可能为 null，例如 MSI 自修复会转给 msiexec）。</summary>
    public Process? Process { get; init; }

    /// <summary>失败原因（中文，直接显示给用户）。</summary>
    public string Error { get; init; } = "";

    /// <summary>实际执行的命令行（写进日志用）。</summary>
    public string CommandLine { get; init; } = "";
}

/// <summary>
/// 卸载 / 修改的启动器：把注册表里的命令拆成「exe + 参数」交给 Windows 启动。
///
/// <para><b>为什么不自己拼 cmd /c 或强制 runas</b>：</para>
/// <list type="bullet">
///   <item>用 <c>UseShellExecute = true</c> + 分开的 <c>FileName</c> / <c>Arguments</c>，
///         让 Shell 自己解析路径、应用目标 exe 自带的清单（很多卸载程序声明了
///         <c>requireAdministrator</c>，会自动弹 UAC），不需要我们提权；</item>
///   <item>不经过 <c>cmd.exe</c>：少一层引号转义地狱，也不会让 <c>&amp; | ^</c> 这类字符被当成命令分隔符；</item>
///   <item>不静默卸载：不带 <c>/S</c> / <c>/quiet</c>，让安装程序自己的界面问用户「确定要卸载吗」——
///         本工具只负责把它叫起来，最终决定权在用户和安装程序手里。</item>
/// </list>
/// </summary>
public sealed class AppProcessLauncher
{
    /// <summary>
    /// 启动一条卸载 / 修改命令。任何异常（路径不存在、权限不足、用户取消 UAC）都折成
    /// <see cref="LaunchResult.Error"/>，不抛给界面。
    /// </summary>
    public LaunchResult Start(string? commandLine, string? workingDirectory = null)
    {
        var parsed = AppCatalogRules.SplitCommandLine(commandLine);
        var display = (commandLine ?? "").Trim();
        if (!parsed.HasExecutable)
            return new LaunchResult { Started = false, Error = "这个应用没有可执行的命令。", CommandLine = display };

        var startInfo = new ProcessStartInfo(parsed.Executable)
        {
            Arguments = parsed.Arguments,
            UseShellExecute = true,   // 交给 Shell：解析路径 / 应用目标清单（UAC）/ 处理 UAC 取消
            ErrorDialog = false,
        };

        // 起始目录优先用安装目录（有些卸载程序靠相对路径找自己的文件），不存在就不设。
        var working = (workingDirectory ?? "").Trim();
        if (working.Length > 0)
        {
            try
            {
                if (System.IO.Directory.Exists(working)) startInfo.WorkingDirectory = working;
            }
            catch
            {
                // 路径非法就忽略，用默认起始目录。
            }
        }

        try
        {
            var process = Process.Start(startInfo);
            return new LaunchResult { Started = true, Process = process, CommandLine = display };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new LaunchResult
            {
                Started = false,
                Error = "已取消管理员授权（UAC 弹窗被关闭或点了「否」）。",
                CommandLine = display,
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Started = false, Error = ex.Message, CommandLine = display };
        }
    }
}
