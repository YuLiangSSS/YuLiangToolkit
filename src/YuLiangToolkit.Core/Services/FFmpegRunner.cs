using System.Diagnostics;
using System.Text;

namespace YuLiangToolkit.Core.Services;

/// <summary>FFmpeg 进程封装：异步执行、流式捕获输出、支持取消（杀进程树）。</summary>
public static class FFmpegRunner
{
    public sealed record RunResult(int ExitCode, string Output, string Error)
    {
        public bool Success => ExitCode == 0;
    }

    public static async Task<RunResult> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (error) error.AppendLine(e.Data);
            onErrorLine?.Invoke(e.Data);
        };

        using var killOnCancel = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* 进程已退出 */ }
        });

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            throw;
        }

        return new RunResult(process.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>快速获取 FFmpeg 版本号（用于状态栏展示）。</summary>
    public static async Task<string?> GetVersionAsync(string ffmpegPath)
    {
        try
        {
            var result = await RunAsync(ffmpegPath, new[] { "-version" });
            var m = System.Text.RegularExpressions.Regex.Match(
                result.Output + result.Error, @"ffmpeg version (\S+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }
}
