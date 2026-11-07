using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using YuLiangToolkit.Tools.TaskManager.Models;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 结束任务的「三级后备」执行器（规则见 <see cref="KillPlan"/>）：
/// ① <c>Process.Kill()</c> → ② <c>taskkill /PID … /T /F</c> → ③
/// <c>wmic process where name='…' delete</c>。
///
/// <para><b>为什么要三级</b>：普通进程 <c>Kill()</c> 就够；但以管理员权限启动的进程会对
/// 普通权限的我们「拒绝访问」，这时改用系统自带的 <c>taskkill</c>（它由系统进程执行、/T 还能连
/// 子进程树一起结束）；Windows 11 24H2 起部分系统移除了 <c>wmic</c>，所以最后一级在找不到
/// <c>WMIC.exe</c> 时直接记「已移除 wmic，跳过」，不算异常。</para>
///
/// <para><b>失败不抛异常</b>：每一级失败都记一条 <see cref="KillAttempt"/>（中文原因），
/// 全部失败后由 <see cref="KillOutcome.FromAttempts"/> 给出一句话总结 + 管理员建议；
/// 系统关键进程（csrss / winlogon 等）任何一级都会失败，界面应当提前用
/// <see cref="ProcessProtection"/> 拦下。</para>
///
/// <para><b>超时</b>：taskkill / wmic 各等 5 秒，超时就杀掉子进程并算这一级失败，避免界面卡死。
/// 外部传入的 <see cref="CancellationToken"/> 在每级之间检查，并在 taskkill / wmic 运行期间
/// 尝试结束它们的子进程。</para>
/// </summary>
public sealed class ProcessTerminator
{
    /// <summary>一级结束（<c>Process.Kill</c>）等待进程退出的毫秒数。</summary>
    private const int KillWaitMilliseconds = 1500;

    /// <summary>taskkill / wmic 等待返回的毫秒数。</summary>
    private const int ToolTimeoutMilliseconds = 5000;

    /// <summary>
    /// 结束指定进程（异步：内部用 <see cref="Task.Run(Action)"/> 跑，避免阻塞界面线程）。
    /// 无论如何都会返回一个结果对象，<b>不会向上抛异常</b>；进程已经退出时返回
    /// <see cref="KillOutcome.AlreadyExited"/>。
    /// </summary>
    /// <param name="pid">目标 PID。</param>
    /// <param name="processName">进程名（wmic 兜底要用；空串时自动读一次，读不到就跳过 wmic 这一级）。</param>
    /// <param name="cancellationToken">窗口关闭 / 切换工具时取消后续级别。</param>
    public Task<KillOutcome> KillAsync(int pid, string processName, CancellationToken cancellationToken = default)
    {
        // 用 CancellationToken.None 排队：取消由 KillCore 自己处理（转成失败结果而不是抛 TaskCanceledException），
        // 这样调用方永远只需处理 KillOutcome。
        return Task.Run(() => KillCore(pid, processName ?? "", cancellationToken), CancellationToken.None);
    }

    /// <summary>PID 对应的进程是否还在（不存在 / 已经退出都返回 false，不抛异常）。</summary>
    public static bool ProcessExists(int pid)
    {
        if (pid <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 当前进程是否以管理员身份运行。界面用它提示「结束失败时可以试试以管理员身份重开」。
    /// </summary>
    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 三级流程

    private static KillOutcome KillCore(int pid, string processName, CancellationToken cancellationToken)
    {
        var attempts = new List<KillAttempt>(3);

        if (cancellationToken.IsCancellationRequested)
        {
            attempts.Add(new KillAttempt(KillStage.ProcessKill, false, "操作已取消（窗口已关闭或切换了工具）"));
            return KillOutcome.FromAttempts(pid, attempts);
        }

        if (!ProcessExists(pid))
        {
            return KillOutcome.AlreadyExited(pid);
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = ResolveProcessName(pid);
        }

        // ① Process.Kill()
        var first = TryProcessKill(pid);
        attempts.Add(first);
        if (first.Success) return KillOutcome.FromAttempts(pid, attempts);
        if (cancellationToken.IsCancellationRequested) return KillOutcome.FromAttempts(pid, attempts);

        // ② taskkill /PID … /T /F
        var second = TryTaskkill(pid, cancellationToken);
        attempts.Add(second);
        if (second.Success) return KillOutcome.FromAttempts(pid, attempts);
        if (cancellationToken.IsCancellationRequested) return KillOutcome.FromAttempts(pid, attempts);

        // ③ wmic（找不到 WMIC.exe 就把「已移除」记成这一级的失败信息）
        attempts.Add(TryWmic(processName, pid, cancellationToken));
        return KillOutcome.FromAttempts(pid, attempts);
    }

    /// <summary>① 最直接的 <c>Process.Kill()</c>，等 1.5 秒确认真的退出了。</summary>
    private static KillAttempt TryProcessKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            try
            {
                process.Kill();
                if (process.WaitForExit(KillWaitMilliseconds) || !ProcessExists(pid))
                {
                    return new KillAttempt(KillStage.ProcessKill, true, "Kill() 成功，进程已退出");
                }

                return new KillAttempt(KillStage.ProcessKill, false, "Kill() 已调用，但 1.5 秒内进程没有退出");
            }
            catch (Exception exception)
            {
                return new KillAttempt(KillStage.ProcessKill, false, DescribeException(exception));
            }
        }
        catch (ArgumentException)
        {
            return new KillAttempt(KillStage.ProcessKill, false, "进程已经退出");
        }
        catch (Exception exception)
        {
            return new KillAttempt(KillStage.ProcessKill, false, DescribeException(exception));
        }
    }

    /// <summary>② 调系统 <c>taskkill.exe</c>（参数用 <see cref="KillPlan.BuildTaskkillArgumentList"/> 的 ArgumentList，避免引号问题）。</summary>
    private static KillAttempt TryTaskkill(int pid, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveSystemTool("taskkill.exe", "taskkill.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in KillPlan.BuildTaskkillArgumentList(pid))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new KillAttempt(KillStage.Taskkill, false, "无法启动 taskkill.exe");
            }

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                    // 取消时尽力而为，失败不影响主流程
                }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ToolTimeoutMilliseconds))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }

                // 读一下管道，避免未观测的 Task 异常；进程已被杀，读不到就算了
                ReadQuietly(stdoutTask);
                ReadQuietly(stderrTask);
                return new KillAttempt(KillStage.Taskkill, false, "taskkill 5 秒内没有返回，已放弃");
            }

            var exitCode = process.ExitCode;
            var exited = !ProcessExists(pid);
            if (exitCode == 0 || exited)
            {
                var detail = exitCode == 0
                    ? "退出码 0"
                    : $"进程已退出（taskkill 退出码 {exitCode}）";
                return new KillAttempt(KillStage.Taskkill, true, detail);
            }

            var errorText = Summarize(ReadQuietly(stderrTask));
            return new KillAttempt(
                KillStage.Taskkill,
                false,
                errorText.Length == 0 ? $"taskkill 退出码 {exitCode}" : $"taskkill 退出码 {exitCode}：{errorText}");
        }
        catch (Exception exception)
        {
            return new KillAttempt(KillStage.Taskkill, false, DescribeException(exception));
        }
    }

    /// <summary>
    /// ③ 最后兜底：<c>wmic process where name='…' delete</c>。
    /// Windows 11 24H2 起 wmic 是可卸载组件，找不到就直接记「已移除」。
    /// </summary>
    private static KillAttempt TryWmic(string processName, int pid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(KillPlan.NormalizeProcessName(processName)))
        {
            return new KillAttempt(KillStage.Wmic, false, "拿不到进程名，wmic 无法定位进程，跳过");
        }

        var wmicPath = Path.Combine(Environment.SystemDirectory, "wbem", "WMIC.exe");
        if (!File.Exists(wmicPath))
        {
            return new KillAttempt(KillStage.Wmic, false, "此 Windows 已移除 wmic（找不到 WMIC.exe），跳过");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = wmicPath,
                // wmic 只接受一整串参数（where 子句里可能有空格），所以用 Arguments 而不是 ArgumentList。
                Arguments = KillPlan.BuildWmicArguments(processName),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new KillAttempt(KillStage.Wmic, false, "无法启动 WMIC.exe");
            }

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ToolTimeoutMilliseconds))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }

                ReadQuietly(stdoutTask);
                ReadQuietly(stderrTask);
                return new KillAttempt(KillStage.Wmic, false, "wmic 5 秒内没有返回，已放弃");
            }

            var exitCode = process.ExitCode;
            var exited = !ProcessExists(pid);
            if (exitCode == 0 || exited)
            {
                var detail = exitCode == 0
                    ? "退出码 0"
                    : $"进程已退出（wmic 退出码 {exitCode}）";
                return new KillAttempt(KillStage.Wmic, true, detail);
            }

            var errorText = Summarize(ReadQuietly(stderrTask));
            return new KillAttempt(
                KillStage.Wmic,
                false,
                errorText.Length == 0 ? $"wmic 退出码 {exitCode}" : $"wmic 退出码 {exitCode}：{errorText}");
        }
        catch (Exception exception)
        {
            return new KillAttempt(KillStage.Wmic, false, DescribeException(exception));
        }
    }

    // ------------------------------------------------------------------ 辅助

    /// <summary>参数传空时补一次进程名（wmic 需要）；读不到返回空串。</summary>
    private static string ResolveProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>优先用 <c>System32</c> 下的完整路径（PATH 被改坏的机器上也能找到系统工具）。</summary>
    private static string ResolveSystemTool(string fileName, string fallback)
    {
        try
        {
            var fullPath = Path.Combine(Environment.SystemDirectory, fileName);
            return File.Exists(fullPath) ? fullPath : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>有界等待读取重定向输出（进程已被杀 / 管道没关时最多等 1.5 秒）。</summary>
    private static string ReadQuietly(Task<string> task)
    {
        try
        {
            return task.Wait(1500) ? task.Result : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>把多行 stderr 压成一行摘要（太长时截断，避免状态栏撑爆）。</summary>
    private static string Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var line = string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 160 ? line : line[..159] + "…";
    }

    /// <summary>把异常翻译成中文原因；「拒绝访问」是最常见的（受保护 / 管理员进程）。</summary>
    private static string DescribeException(Exception exception)
    {
        switch (exception)
        {
            case Win32Exception win32 when win32.NativeErrorCode == 5:
                return "拒绝访问（错误码 5：进程受保护或权限不足）";
            case UnauthorizedAccessException:
                return "拒绝访问（权限不足）";
            case InvalidOperationException:
                return "进程已经退出";
            case NotSupportedException:
                return "当前系统不支持这个操作";
            default:
                return string.IsNullOrWhiteSpace(exception.Message)
                    ? exception.GetType().Name
                    : exception.Message;
        }
    }
}
