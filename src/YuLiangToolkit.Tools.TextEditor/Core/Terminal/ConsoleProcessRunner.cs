using System.Diagnostics;

namespace YuLiangToolkit.Tools.TextEditor.Core.Terminal;

/// <summary>
/// 真正启动 CMD / PowerShell 进程并流式回收输出（一次跑一条命令）。
///
/// <para><b>为什么不是完整终端</b>：WPF 里嵌一个真正的交互式终端要做 ConPTY + 跨进程 IO + ANSI 渲染，
/// 成本很高；这里用「每次命令起一个 <c>cmd /c</c> / <c>powershell -Command</c> 子进程、异步逐行读输出」的
/// 轻量方案，覆盖编译 / 跑脚本 / git / 目录操作这些最常用场景。需要交互的程序（vim、REPL）请用系统终端。</para>
///
/// <para><b>根进程唯一</b>：每次执行用独立进程，停止时 <c>Kill(entireProcessTree: true)</c> 连子进程一起收掉；
/// 输出统一按 UTF-8 解码（命令前会先 chcp / 设 Console 编码，见 <see cref="ConsoleCommandLine.PrepareCommandText"/>）。</para>
/// </summary>
public static class ConsoleProcessRunner
{
    /// <summary>
    /// 执行一条命令并等待结束，返回进程退出码。
    /// <paramref name="onOutputLine"/> 会在后台线程被逐行回调（不保证行序完全严格交错），调用方需自己切回 UI 线程。
    /// </summary>
    public static async Task<int> RunAsync(
        ConsoleShellKind kind,
        string command,
        string workingDirectory,
        Action<string> onOutputLine,
        CancellationToken cancellationToken)
    {
        var encoding = ConsoleCommandLine.OutputEncodingFor(kind);
        var startInfo = new ProcessStartInfo
        {
            FileName = ConsoleCommandLine.ResolveExecutable(kind),
            WorkingDirectory = ConsoleCommandLine.NormalizeWorkingDirectory(workingDirectory),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };

        foreach (var argument in ConsoleCommandLine.BuildArguments(kind, ConsoleCommandLine.PrepareCommandText(kind, command)))
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) onOutputLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) onOutputLine(e.Data);
        };
        process.Exited += (_, _) => exited.TrySetResult();

        if (!process.Start()) throw new InvalidOperationException("控制台进程没有启动");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能刚好自己退出了 / 没权限提权 → 停止失败就算了
            }
        });

        await exited.Task.ConfigureAwait(false);

        // 等异步读取把缓冲区里的输出全部回调完（Exited 时输出可能还没读完）
        try
        {
            process.WaitForExit();
        }
        catch
        {
            // 极端情况下进程被强杀，拿不到 ExitCode 也没关系
        }

        return process.ExitCode;
    }
}
