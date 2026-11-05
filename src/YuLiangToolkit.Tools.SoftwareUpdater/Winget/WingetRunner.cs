using System.Diagnostics;
using System.Text;

namespace YuLiangToolkit.Tools.SoftwareUpdater.Winget;

/// <summary>一次 winget 调用的结果。</summary>
public sealed record WingetRunResult(
    int ExitCode,
    string Output,
    bool Canceled,
    bool TimedOut,
    string? Error)
{
    /// <summary>winget 正常跑完且退出码为 0。</summary>
    public bool Success => !Canceled && !TimedOut && Error is null && ExitCode == 0;
}

/// <summary>
/// 真正启动 winget.exe 的地方：无窗口、UTF-8 捕获 stdout/stderr、逐行回调、可取消、可超时。
///
/// <para>winget 是「Windows 应用安装程序」自带的命令行（App Execution Alias），不需要管理员权限、
/// 也不需要额外安装 NuGet 包；扫描只读、升级才由用户主动触发。输出重定向时 winget 用 UTF-8
/// （本机 v1.29 实测），所以这里显式指定 StandardOutputEncoding / StandardErrorEncoding。</para>
/// </summary>
public static class WingetRunner
{
    /// <summary>优先用当前用户的 App Execution Alias，找不到就交给 PATH 解析 "winget"。</summary>
    public static string ExecutablePath()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                var alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
                if (File.Exists(alias)) return alias;
            }
        }
        catch { /* 环境变量异常时退回 PATH */ }
        return "winget";
    }

    /// <summary>winget 是否可用（只做版本探测，UI 上给友好提示用）。</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ExecutablePath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (process is null) return false;
            process.WaitForExit(5000);
            return !process.HasExited || process.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 跑一条 winget 命令。<paramref name="arguments"/> 是已经拆好的参数列表（第一项通常是 list / upgrade）。
    /// <paramref name="onLine"/> 在后台读取线程上回调，界面状态要走 <c>Ui(...)</c> / <c>Log(...)</c>。
    /// </summary>
    public static async Task<WingetRunResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var output = new StringBuilder();
        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        CancellationTokenSource? linked = null;
        if (timeout is { } limit && limit > TimeSpan.Zero)
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(limit);
        }
        var token = linked?.Token ?? cancellationToken;

        void OnLine(string? raw)
        {
            var line = WingetOutputParser.CleanLine(raw);
            if (line.Length == 0) return;
            lock (output) output.AppendLine(line);
            onLine?.Invoke(line);
        }

        try
        {
            if (!process.Start())
                return new WingetRunResult(-1, "", false, false, "无法启动 winget（进程没有启动）");

            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 取消 / 超时时连子进程一起结束，避免安装程序还挂在后台。
            using var registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* 进程可能刚好退出 */ }
            });

            await process.WaitForExitAsync(token);

            var text = output.ToString();
            return new WingetRunResult(process.ExitCode, text, false, false, null);
        }
        catch (OperationCanceledException)
        {
            var text = output.ToString();
            var timedOut = timeout is not null && !cancellationToken.IsCancellationRequested;
            return new WingetRunResult(-1, text, true, timedOut, null);
        }
        catch (Exception ex)
        {
            return new WingetRunResult(-1, output.ToString(), false, false, ex.Message);
        }
        finally
        {
            linked?.Dispose();
        }
    }
}
