using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>一个摄像头设备（枚举结果；枚举本身不会打开摄像头）。</summary>
/// <param name="Id">设备 Id（<c>DeviceInformation.Id</c>，重启后一般不变，适合存进配置）。</param>
/// <param name="Name">设备显示名（空名统一写成「未命名摄像头」）。</param>
public sealed record CameraDeviceInfo(string Id, string Name);

/// <summary>一帧摄像头预览画面。</summary>
public sealed class CameraFrameEventArgs : EventArgs
{
    /// <summary>BGRA32 位图（已经 <c>Freeze()</c>，可以在任意线程读取）。</summary>
    public required BitmapSource Image { get; init; }

    /// <summary>这一帧的时间戳（<see cref="Environment.TickCount64"/>，毫秒）。</summary>
    public long TimestampMs { get; init; }
}

/// <summary>
/// 摄像头预览采集源（Windows 自带 <c>MediaCapture + MediaFrameReader</c>，零新增 NuGet 包）。
///
/// <para>和 Live2D 面部动捕的 <c>MocapCameraSource</c> 同源，但这里只保留「取画面」这一件事：
/// 不做人脸检测、不做任何分析，也**不做镜像**。镜像交给界面用 <c>ScaleTransform</c> 做——
/// 如果服务层先镜像了，录进视频的摄像头覆盖画面就会是反的。</para>
///
/// <para><b>流程</b>：<see cref="StartAsync"/> 打开指定摄像头 → 用 CPU 内存的
/// <see cref="MediaFrameReader"/> 读 BGRA8（摄像头不支持 BGRA8 时退回源格式再 Convert）→
/// 按目标帧率节流 → 按面积平均降采样到目标宽度（保持长宽比、不放大）→ 生成已 Freeze 的
/// <see cref="BitmapSource"/> 并通过 <see cref="FrameArrived"/> 发布。</para>
///
/// <para><b>线程</b>：<see cref="FrameArrived"/> 在 WinRT 线程池线程上触发，界面订阅时
/// 必须自己切回 UI 线程（例如 <c>Dispatcher.BeginInvoke</c>）；位图已 Freeze，所以跨线程读安全。</para>
///
/// <para><b>隐私</b>：只有调用 <see cref="StartAsync"/> 才会打开摄像头；<see cref="StopAsync"/> /
/// <see cref="Dispose"/> 会立即停止并释放设备（录制结束、切走工具页时必须调用）。</para>
/// </summary>
public sealed class CameraPreviewSource : IDisposable
{
    private const int MinTargetWidth = 64;
    private const int MaxTargetWidth = 4096;
    private const int MinTargetFps = 5;
    private const int MaxTargetFps = 30;
    private const string LogPrefix = "[录屏摄像头] ";

    /// <summary>启停串行化（避免按钮连点 / 快速切设备时并发打开同一个摄像头）。</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    /// <summary>保护状态字段（LastError / FrameWidth / FrameHeight 会被不同线程读）。</summary>
    private readonly object _stateGate = new();

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private volatile bool _running;
    private bool _disposed;
    private string? _lastError;
    private int _frameWidth;
    private int _frameHeight;

    // 目标参数（StartAsync 时按次传入，帧回调里只读）
    private int _targetWidth = 640;
    private int _targetFps = 15;
    private long _minFrameIntervalMs = 66;

    /// <summary>上一帧被接受的时间（<see cref="Environment.TickCount64"/>，用 Interlocked 访问）。</summary>
    private long _lastFrameTicks;

    /// <summary>当前是否在采集（停止中、启动失败后都是 false）。</summary>
    public bool IsRunning => _running;

    /// <summary>最近一次失败的中文原因（成功或未启动时为 null）。</summary>
    public string? LastError
    {
        get { lock (_stateGate) return _lastError; }
    }

    /// <summary>最近一帧（降采样后）的宽度；未采集时为 0。</summary>
    public int FrameWidth
    {
        get { lock (_stateGate) return _frameWidth; }
    }

    /// <summary>最近一帧（降采样后）的高度；未采集时为 0。</summary>
    public int FrameHeight
    {
        get { lock (_stateGate) return _frameHeight; }
    }

    /// <summary>采到一帧（BGRA32、已 Freeze）。回调在 WinRT 线程池线程上触发。</summary>
    public event EventHandler<CameraFrameEventArgs>? FrameArrived;

    // ===================== 枚举 =====================

    /// <summary>
    /// 枚举系统里的摄像头（不会打开摄像头）。任何失败都返回空列表，不抛异常——
    /// 界面按「空列表 = 没有摄像头」处理即可。
    /// </summary>
    public static async Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync()
    {
        try
        {
            return await EnumerateCoreAsync().ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<CameraDeviceInfo>();
        }
    }

    /// <summary>真正枚举的版本（会抛 WinRT 异常，供 <see cref="StartAsync"/> 区分「枚举失败 / 没有设备」）。</summary>
    private static async Task<IReadOnlyList<CameraDeviceInfo>> EnumerateCoreAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        var result = new List<CameraDeviceInfo>();
        foreach (var device in devices)
        {
            if (!device.IsEnabled) continue;
            result.Add(new CameraDeviceInfo(
                device.Id,
                string.IsNullOrWhiteSpace(device.Name) ? "未命名摄像头" : device.Name));
        }

        return result;
    }

    // ===================== 启停 =====================

    /// <summary>
    /// 打开摄像头开始预览。返回 false 时 <see cref="LastError"/> 有中文原因
    /// （没有摄像头 / 权限被拒 / 被其他程序占用 / 指定设备不存在等），不抛异常。
    /// <para>可重复调用：重复 Start 会先停掉旧会话再按新参数重新打开。</para>
    /// </summary>
    /// <param name="deviceId">设备 Id；为 null / 空 = 用第一个可用摄像头；指定的设备不存在时也退回第一个
    /// （配置里残留的旧设备 Id 在拔掉摄像头后很常见，宁可回退也不能让界面一直打不开）。</param>
    /// <param name="targetWidth">预览降采样目标宽度（默认 640；保持长宽比，不放大）。</param>
    /// <param name="targetFps">回调帧率上限（默认 15，内部夹到 5~30）。</param>
    public async Task<bool> StartAsync(string? deviceId, int targetWidth = 640, int targetFps = 15)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        MediaCapture? pendingCapture = null;
        MediaFrameReader? pendingReader = null;
        try
        {
            if (_disposed)
            {
                SetError("摄像头预览已经释放，请重新创建");
                return false;
            }

            // 重复 Start：先彻底停掉旧会话，保证新参数（换摄像头 / 改帧率）真的生效
            if (_running || _reader is not null) await StopInternalAsync().ConfigureAwait(false);
            SetError(null);

            _targetWidth = Math.Clamp(targetWidth, MinTargetWidth, MaxTargetWidth);
            _targetFps = Math.Clamp(targetFps, MinTargetFps, MaxTargetFps);
            _minFrameIntervalMs = Math.Max(1, (long)Math.Round(1000.0 / _targetFps));
            Interlocked.Exchange(ref _lastFrameTicks, 0);

            IReadOnlyList<CameraDeviceInfo> cameras;
            try
            {
                cameras = await EnumerateCoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetError("枚举摄像头失败：" + ex.Message);
                return false;
            }

            if (cameras.Count == 0)
            {
                SetError("没有找到可用的摄像头（请检查摄像头是否连接，或到 Windows 设置 → 隐私和安全性 → 相机 允许应用使用相机）");
                return false;
            }

            CameraDeviceInfo? camera;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                camera = cameras.FirstOrDefault(c => string.Equals(c.Id, deviceId, StringComparison.Ordinal));
                if (camera is null)
                {
                    // 配置里存的设备可能已经拔掉：退回第一个可用摄像头。界面（SelectedCamera）本来就会
                    // 回退显示第一个设备，这里再报「找不到指定摄像头」只会让用户永远打不开预览。
                    camera = cameras[0];
                    Trace.WriteLine(LogPrefix + "配置的摄像头已不存在，改用第一个可用摄像头：" + camera.Name);
                }
            }
            else
            {
                camera = cameras[0];
            }

            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                VideoDeviceId = camera.Id,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            };

            pendingCapture = new MediaCapture();
            try
            {
                await pendingCapture.InitializeAsync(settings).AsTask().ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                SetError("摄像头权限被拒绝（到 Windows 设置 → 隐私和安全性 → 相机 允许应用使用相机）：" + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                SetError("打开摄像头失败（可能被其他程序占用）：" + ex.Message);
                return false;
            }

            var source = pendingCapture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (source is null)
            {
                SetError("摄像头没有彩色画面源，无法预览");
                return false;
            }

            // ⚠ 不要对源调 SetFormatAsync 去选 640 宽：Live2D 动捕踩过坑——某些摄像头在非默认格式下
            // 由 MediaCapture 做 YUY2→BGRA 转换会得到「绿油油」的错色画面。这里按源默认格式采集，自己降采样。
            try
            {
                pendingReader = await pendingCapture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8)
                    .AsTask().ConfigureAwait(false);
            }
            catch
            {
                // 个别摄像头不支持直接读 BGRA8：退回源格式，在帧回调里自己 Convert 到 BGRA8
                try
                {
                    pendingReader = await pendingCapture.CreateFrameReaderAsync(source).AsTask().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SetError("创建摄像头帧读取器失败：" + ex.Message);
                    return false;
                }
            }

            pendingReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

            // 移交所有权：之后由 StopInternalAsync 负责释放
            _capture = pendingCapture;
            _reader = pendingReader;
            pendingCapture = null;
            pendingReader = null;

            _reader.FrameArrived += OnFrameArrived;

            MediaFrameReaderStartStatus startStatus;
            try
            {
                startStatus = await _reader.StartAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await StopInternalAsync().ConfigureAwait(false);
                SetError("启动摄像头采集失败：" + ex.Message);
                return false;
            }

            if (startStatus != MediaFrameReaderStartStatus.Success)
            {
                await StopInternalAsync().ConfigureAwait(false);
                SetError("摄像头启动失败：" + DescribeStartStatus(startStatus));
                return false;
            }

            _running = true;
            return true;
        }
        catch (Exception ex)
        {
            try { await StopInternalAsync().ConfigureAwait(false); } catch { /* 停止失败不覆盖原始错误 */ }
            SetError("摄像头预览启动失败：" + ex.Message);
            return false;
        }
        finally
        {
            // 还没移交给字段的临时实例（中途 return / 异常）必须在这里释放，否则摄像头会被占着不放
            try { pendingReader?.Dispose(); } catch { /* 忽略 */ }
            try { pendingCapture?.Dispose(); } catch { /* 忽略 */ }
            _lifecycleGate.Release();
        }
    }

    /// <summary>停止采集并释放摄像头（可重复调用；没启动时直接返回）。</summary>
    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>停止并释放（调用方必须已经持有 <see cref="_lifecycleGate"/>）。</summary>
    private async Task StopInternalAsync()
    {
        _running = false;

        var reader = _reader;
        _reader = null;
        if (reader is not null)
        {
            try { reader.FrameArrived -= OnFrameArrived; } catch { /* 已释放 */ }
            try { await reader.StopAsync().AsTask().ConfigureAwait(false); } catch { /* 已经停了 */ }
            try { reader.Dispose(); } catch { /* 忽略 */ }
        }

        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            try { capture.Dispose(); } catch { /* 忽略 */ }
        }

        lock (_stateGate)
        {
            _frameWidth = 0;
            _frameHeight = 0;
        }
    }

    // ===================== 采集回调 =====================

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!_running) return;

        // 按目标帧率节流：相机常见 30fps，我们只要 10~15fps。多个调用线程同时进来时用 CAS 只放行一个。
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastFrameTicks);
        if (now - last < _minFrameIntervalMs) return;
        if (Interlocked.CompareExchange(ref _lastFrameTicks, now, last) != last) return;

        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;

            var pixels = ExtractBgraPixels(bitmap, out var width, out var height);
            if (pixels is null || width < 2 || height < 2) return;

            var scaled = DownscaleBgra(pixels, width, height, _targetWidth, out var scaledWidth, out var scaledHeight);
            var image = BitmapSource.Create(
                scaledWidth, scaledHeight, 96, 96, PixelFormats.Bgra32, null, scaled, scaledWidth * 4);
            image.Freeze(); // Freeze 之后可以安全地跨线程给界面用

            lock (_stateGate)
            {
                _frameWidth = scaledWidth;
                _frameHeight = scaledHeight;
            }

            RaiseFrameArrived(new CameraFrameEventArgs { Image = image, TimestampMs = now });
        }
        catch (Exception ex)
        {
            SetError("读取摄像头画面失败：" + ex.Message);
        }
    }

    /// <summary>触发 <see cref="FrameArrived"/>；订阅方异常只记日志，不能把采集线程带崩。</summary>
    private void RaiseFrameArrived(CameraFrameEventArgs args)
    {
        var handler = FrameArrived;
        if (handler is null) return;
        try
        {
            handler(this, args);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(LogPrefix + "帧回调异常（已忽略）：" + ex.Message);
        }
    }

    /// <summary>
    /// 把 SoftwareBitmap 转成 BGRA32 字节数组（格式不是 Bgra8 时先 Convert）。
    /// ⚠ 不要按「alpha 模式不是 Premultiplied」去 Convert：摄像头的 Bgra8 帧通常是 Straight alpha，
    /// 对同格式再做一次 Convert 会得到错色（Live2D 踩过「绿油油」的坑）；这里只关心 RGB，alpha 后面统一置 255。
    /// </summary>
    private static byte[]? ExtractBgraPixels(SoftwareBitmap bitmap, out int width, out int height)
    {
        width = bitmap.PixelWidth;
        height = bitmap.PixelHeight;
        SoftwareBitmap? converted = null;
        try
        {
            var source = bitmap;
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
                source = converted;
            }

            var length = checked(width * height * 4);
            var buffer = new Windows.Storage.Streams.Buffer((uint)length) { Length = (uint)length };
            source.CopyToBuffer(buffer);
            var pixels = new byte[length];
            using var dataReader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
            dataReader.ReadBytes(pixels);
            return pixels;
        }
        catch
        {
            return null;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>
    /// BGRA 帧按面积平均降采样到指定宽度（保持长宽比；目标宽度比源大时不放大）。
    /// 和 <c>MocapCameraSource.DownscaleBgra</c> 同一套算法：平均比隔点取样干净，缩放后噪点少。
    /// </summary>
    private static byte[] DownscaleBgra(byte[] source, int sourceWidth, int sourceHeight, int targetWidth,
        out int width, out int height)
    {
        width = Math.Min(targetWidth, sourceWidth);
        height = Math.Max(1, (int)Math.Round(sourceHeight * (double)width / sourceWidth));
        var target = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var sy0 = (int)((long)y * sourceHeight / height);
            var sy1 = Math.Max(sy0 + 1, (int)((long)(y + 1) * sourceHeight / height));
            for (var x = 0; x < width; x++)
            {
                var sx0 = (int)((long)x * sourceWidth / width);
                var sx1 = Math.Max(sx0 + 1, (int)((long)(x + 1) * sourceWidth / width));
                long b = 0, g = 0, r = 0;
                var count = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var row = sy * sourceWidth;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var index = (row + sx) * 4;
                        b += source[index];
                        g += source[index + 1];
                        r += source[index + 2];
                        count++;
                    }
                }

                if (count == 0) count = 1;
                var targetIndex = (y * width + x) * 4;
                target[targetIndex] = (byte)(b / count);
                target[targetIndex + 1] = (byte)(g / count);
                target[targetIndex + 2] = (byte)(r / count);
                target[targetIndex + 3] = 255;
            }
        }

        return target;
    }

    // ===================== 状态 / 错误 =====================

    private static string DescribeStartStatus(MediaFrameReaderStartStatus status) => status switch
    {
        MediaFrameReaderStartStatus.DeviceNotAvailable => "摄像头不可用（可能已被拔出，或被其他程序占用）",
        MediaFrameReaderStartStatus.OutputFormatNotSupported => "摄像头不支持当前输出格式",
        MediaFrameReaderStartStatus.ExclusiveControlNotAvailable => "摄像头正被其他程序独占使用",
        MediaFrameReaderStartStatus.UnknownFailure => "未知错误",
        _ => status.ToString(),
    };

    private void SetError(string? message)
    {
        lock (_stateGate) _lastError = message;
        if (message is not null) Trace.WriteLine(LogPrefix + message);
    }

    /// <summary>同步停止兜底（程序退出 / 页面被销毁时没有机会 await）。<b>只能调用一次</b>，之后不能再 Start。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* 退出流程忽略 */ }

        // 故意不 Dispose 这个 SemaphoreSlim：界面收尾时可能在 Dispose 之后又调一次 StopAsync，
        // 已销毁的信号量会抛 ObjectDisposedException（异步任务里没人 await 就成了未观察异常）。
        // SemaphoreSlim 只要没访问过 AvailableWaitHandle 就不持有系统句柄，交给 GC 回收没有代价。
    }
}
