using System.Diagnostics;
using System.Text;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>一次采集结束时的结果（进程是否退出 / 退出码 / FFmpeg 错误尾部）。</summary>
internal sealed record CaptureStopResult(bool Exited, int ExitCode, string ErrorTail);

/// <summary>
/// 一次屏幕采集会话：启动一个 FFmpeg <c>gdigrab</c> 进程把画面写进中间 MKV，停止时向它的 stdin 写
/// <c>q</c> 让它**正常收尾**（直接 Kill 的话 MKV 可能缺索引、读不出来）。
///
/// <para>为什么不用 Core 的 <see cref="YuLiangToolkit.Core.Services.FFmpegRunner"/>：
/// 那个封装不开放 stdin，天然只适合“跑完就退出”的转换任务；录制是“一直跑、由用户决定什么时候停”，
/// 必须能优雅地送 q。所以这里写了一个很小的专用进程封装，转换 / 导出仍然复用 Core 的 FFmpegRunner。</para>
/// </summary>
internal sealed class ScreenCaptureSession : IDisposable
{
    private const int MaxErrorLines = 12;

    private readonly Process _process;
    private readonly object _gate = new();
    private readonly Queue<string> _errorLines = new();
    private bool _stopSent;
    private bool _disposed;

    private ScreenCaptureSession(Process process, string intermediatePath)
    {
        _process = process;
        IntermediatePath = intermediatePath;
    }

    /// <summary>中间 MKV 路径（录制结束后交给导出器转 GIF / MP4，成功后由调用方删除）。</summary>
    public string IntermediatePath { get; }

    /// <summary>采集进程是否已经退出。</summary>
    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch { return true; }
        }
    }

    /// <summary>FFmpeg stderr 的最后几行（gdigrab 抓不到画面 / 区域越界时看这里）。</summary>
    public string ErrorTail
    {
        get
        {
            lock (_gate) return string.Join(Environment.NewLine, _errorLines);
        }
    }

    /// <summary>启动采集；失败时抛出带原因的异常，由 ViewModel 转成状态栏文案。</summary>
    public static ScreenCaptureSession Start(string ffmpegPath, CapturePlan plan, string intermediatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(intermediatePath);

        var folder = Path.GetDirectoryName(intermediatePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        TryDelete(intermediatePath);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in FfmpegArguments.BuildCapture(intermediatePath, plan))
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var session = new ScreenCaptureSession(process, intermediatePath);
        process.OutputDataReceived += static (_, _) => { /* 排空 stdout，防止管道写满卡住 FFmpeg */ };
        process.ErrorDataReceived += session.OnErrorLine;

        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg 进程没有启动");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            // Start 成功之后 Begin*ReadLine 才失败时，进程已经在跑了：必须先杀掉，
            // 否则会留下一个没人持有、也停不掉的 FFmpeg 采集进程。
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            try { process.Dispose(); } catch { /* 忽略 */ }
            throw new InvalidOperationException($"启动 FFmpeg 失败：{ex.Message}", ex);
        }

        return session;
    }

    /// <summary>等待进程自己退出（达到最长录制时长 / 出错）。正常用户停止请用 <see cref="StopAsync"/>。</summary>
    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            // 让异步 stderr 事件把最后几行读完（WaitForExitAsync 不保证排空）
            try { _process.WaitForExit(500); } catch { /* 忽略 */ }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 进程已经没了：按退出处理 */ }
    }

    /// <summary>停止采集：送 q，最多等 <paramref name="timeout"/>；超时强制结束进程树。</summary>
    public async Task<CaptureStopResult> StopAsync(TimeSpan timeout)
    {
        if (!HasExited)
        {
            SendQuit();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill();
                // Kill 也可能因为权限 / 句柄问题失败：再等最多 3 秒，绝不无限挂起调用方。
                using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _process.WaitForExitAsync(drainCts.Token).ConfigureAwait(false); }
                catch { /* 忽略：退出码 / 退出状态由 BuildResult 如实报告 */ }
            }
            catch { /* 已经退出 */ }
        }

        return BuildResult();
    }

    /// <summary>同步停止（程序退出 / ViewModel 被销毁时用：那时没机会 await）。</summary>
    public CaptureStopResult StopAndWait(TimeSpan timeout)
    {
        if (!HasExited)
        {
            SendQuit();
            try
            {
                if (!_process.WaitForExit((int)Math.Max(0, timeout.TotalMilliseconds))) Kill();
            }
            catch { /* 忽略 */ }
            try { _process.WaitForExit(2000); } catch { /* 忽略 */ }
        }

        return BuildResult();
    }

    /// <summary>强制结束进程树（取消 / 超时兜底）。</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch { /* 进程已退出 */ }
    }

    private void SendQuit()
    {
        lock (_gate)
        {
            if (_stopSent) return;
            _stopSent = true;
        }

        try
        {
            _process.StandardInput.WriteLine("q");
            _process.StandardInput.Flush();
        }
        catch
        {
            // stdin 已经关了：交给超时后的 Kill
        }
    }

    private void OnErrorLine(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        lock (_gate)
        {
            _errorLines.Enqueue(e.Data.TrimEnd());
            while (_errorLines.Count > MaxErrorLines) _errorLines.Dequeue();
        }
    }

    private CaptureStopResult BuildResult()
    {
        var exited = HasExited;
        var exitCode = -1;
        try { if (exited) exitCode = _process.ExitCode; } catch { /* 忽略 */ }
        return new CaptureStopResult(exited, exitCode, ErrorTail);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        try { _process.Dispose(); } catch { /* 忽略 */ }
    }
}
