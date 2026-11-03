using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// 摄像头采集（WinRT 的 <c>MediaCapture + MediaFrameReader</c>，系统中自带，不引第三方包）：
/// 只负责「把画面一帧一帧交出来」，降采样 / 节流 / JPEG 编码全部由通话引擎做。
///
/// <para><b>为什么用 MediaFrameReader 而不是预览 / 录像</b>：帧读取器能拿到 CPU 内存里的
/// BGRA8 帧，不依赖 WPF 预览控件，也不写文件；CPU 内存模式才能让引擎直接读像素做 JPEG。
/// 共享模式（SharedReadOnly）允许别的程序同时用摄像头（例如用户开着会议软件）。</para>
///
/// <para><b>为什么不对源 SetFormatAsync</b>：项目踩过坑 —— 某些摄像头被强制换到非默认格式后，
/// MediaCapture 的 YUY2→BGRA 转换会输出「绿油油」的错色画面。这里始终用源的默认格式，
/// 分辨率大了由引擎自己降采样（<c>BgraDownscaler</c>）。</para>
///
/// <para><b>线程</b>：<see cref="StartAsync"/> 在调用线程（通常 UI 线程）完成初始化；
/// <see cref="FrameArrived"/> 在 WinRT 线程池上触发，订阅方（引擎）必须快速返回、自己切线程。
/// 每个事件带一份新的 <c>byte[]</c>（不池化：订阅方可能排队，复用会被写到正在编码的帧上）。</para>
///
/// <para><b>已知边界</b>：没有分辨率 / 帧率控制（由设备默认格式决定）；
/// 没有「设备被拔掉」事件 —— 采集到一半拔掉摄像头时事件会停，最后一次错误写进 <see cref="LastError"/>；
/// 契约层没有设备变化通知，由引擎按需重建实例。</para>
/// </summary>
public sealed class CameraCapture : ICameraSource
{
    private const string LogPrefix = "[通话摄像头] ";

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile string? _lastError;

    /// <summary>采到一帧（WinRT 线程池上触发；字节数组每帧新建，订阅方可以安全排队）。</summary>
    public event Action<CameraFrameData>? FrameArrived;

    /// <summary>最近一次错误（空 = 没有）。</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// 打开摄像头开始采集。空的设备 Id = 列表里的第一个摄像头。
    /// <para>可以重复调用：会先关掉上一次的采集。</para>
    /// </summary>
    public async Task<(bool Ok, string Error)> StartAsync(string deviceId)
    {
        if (_disposed) return (false, "摄像头已释放");

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return (false, "摄像头已释放");
            if (_running) return (true, string.Empty);

            SetError(null);

            // 上一次可能是异常退出（引擎取消 / 上手 Start 没走完 Stop）：先收干净再开。
            await StopInternalAsync().ConfigureAwait(false);

            var (cameraId, resolveError) = await ResolveCameraAsync(deviceId).ConfigureAwait(false);
            if (cameraId is null)
            {
                // 选设备阶段的失败也算「最近一次错误」，界面 / 日志能直接看到原因。
                SetError(resolveError);
                return (false, resolveError);
            }

            MediaCapture? capture = null;
            MediaFrameReader? reader = null;
            try
            {
                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    VideoDeviceId = cameraId,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                };

                capture = new MediaCapture();
                try
                {
                    await capture.InitializeAsync(settings).AsTask().ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return await FailStartAsync(
                        "摄像头权限被拒绝（Windows 设置 → 隐私和安全性 → 相机）：" + ex.Message,
                        capture, null).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return await FailStartAsync("打开摄像头失败：" + ex.Message, capture, null).ConfigureAwait(false);
                }

                // 只要彩色画面源（Depth / Infrared 源没有可用颜色，画出来也没意义）。
                var source = capture.FrameSources.Values
                    .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
                if (source is null)
                    return await FailStartAsync("摄像头没有彩色画面源（可能是红外 / 深度摄像头）", capture, null)
                        .ConfigureAwait(false);

                try
                {
                    reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8)
                        .AsTask().ConfigureAwait(false);
                }
                catch
                {
                    // 个别摄像头不支持直接按 BGRA8 读：退回源格式，回调里再自己转 BGRA。
                    reader = await capture.CreateFrameReaderAsync(source).AsTask().ConfigureAwait(false);
                }

                // Realtime：始终只给最新帧，处理慢了就丢旧帧（通话要的是低延迟，不是完整视频）。
                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

                reader.FrameArrived += OnFrameArrived;
                var startStatus = await reader.StartAsync().AsTask().ConfigureAwait(false);
                if (startStatus != MediaFrameReaderStartStatus.Success)
                {
                    return await FailStartAsync("摄像头启动失败：" + startStatus, capture, reader)
                        .ConfigureAwait(false);
                }

                _capture = capture;
                _reader = reader;
                _running = true;
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return await FailStartAsync("启动摄像头失败：" + ex.Message, capture, reader).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 停止采集并释放摄像头（可重复调用）。
    /// <para>接口 <see cref="ICameraSource"/> 只要求 <see cref="IAsyncDisposable"/>（引擎关摄像头 = 释放实例），
    /// 这个公开方法给「同一个实例再 Start」和调试用。</para>
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    // ===================== 采集中间层 =====================

    /// <summary>真正停采集：先摘事件（防止在途回调再进来），再停 reader、释放 MediaCapture。</summary>
    private async Task StopInternalAsync()
    {
        _running = false;

        var reader = _reader;
        _reader = null;
        if (reader is not null)
        {
            try { reader.FrameArrived -= OnFrameArrived; } catch { }
            try { await reader.StopAsync().AsTask().ConfigureAwait(false); } catch { }
            try { reader.Dispose(); } catch { }
        }

        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            try { capture.Dispose(); } catch { }
        }
    }

    /// <summary>Start 中途失败：写中文原因、关掉已经拿到的局部资源，返回失败。</summary>
    private async Task<(bool Ok, string Error)> FailStartAsync(string message, MediaCapture? capture,
        MediaFrameReader? reader)
    {
        SetError(message);

        if (reader is not null)
        {
            try { reader.FrameArrived -= OnFrameArrived; } catch { }
            try { await reader.StopAsync().AsTask().ConfigureAwait(false); } catch { }
            try { reader.Dispose(); } catch { }
        }

        if (capture is not null)
        {
            try { capture.Dispose(); } catch { }
        }

        return (false, message);
    }

    /// <summary>
    /// 选摄像头：指定了 Id 就按 Id 找（找不到就报错，不偷偷换成别的摄像头 ——
    /// 用户选错设备比「打开了不是自己想要的画面」更容易发现）；没指定就用第一个。
    /// </summary>
    private static async Task<(string? Id, string Error)> ResolveCameraAsync(string deviceId)
    {
        IReadOnlyList<CallDevice> cameras;
        try
        {
            cameras = await WindowsCallMediaFactory.QueryCamerasAsync(default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, "枚举摄像头失败：" + ex.Message);
        }

        if (cameras.Count == 0) return (null, "没有找到可用的摄像头");

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var wanted = deviceId.Trim();
            foreach (var camera in cameras)
            {
                if (string.Equals(camera.Id, wanted, StringComparison.OrdinalIgnoreCase))
                    return (camera.Id, string.Empty);
            }

            return (null, "找不到选择的摄像头（可能已拔出或被禁用）");
        }

        return (cameras[0].Id, string.Empty);
    }

    // ===================== 帧回调 =====================

    /// <summary>WinRT 帧回调：只做「取最新帧 → 拷成 BGRA 字节」，降采样 / 编码由引擎负责。</summary>
    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!_running) return;

        byte[]? pixels;
        int width;
        int height;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;
            pixels = ExtractBgraPixels(bitmap, out width, out height);
        }
        catch (Exception ex)
        {
            // 停相机时在途回调可能撞上已释放的帧：只在运行中记录错误。
            if (_running) SetError("读取摄像头帧失败：" + ex.Message);
            return;
        }

        if (pixels is null || width <= 0 || height <= 0) return;

        try
        {
            FrameArrived?.Invoke(new CameraFrameData(pixels, width, height, Environment.TickCount64));
        }
        catch (Exception ex)
        {
            // 订阅方（引擎）异常不能反把 WinRT 回调线程弄崩。
            SetError("摄像头帧订阅方异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 把 SoftwareBitmap 拷成 BGRA32 字节数组（行优先、自上而下）。
    /// <para>⚠ 只有格式不是 Bgra8 时才 Convert，且 alpha 用 Straight：摄像头的 Bgra8 帧通常就是
    /// Straight alpha，对同格式再 Convert 会得到错色（项目里踩过的「绿油油」）；alpha 不参与 JPEG，
    /// 引擎编码时会忽略。</para>
    /// </summary>
    private static byte[]? ExtractBgraPixels(SoftwareBitmap bitmap, out int width, out int height)
    {
        width = bitmap.PixelWidth;
        height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0) return null;

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
            // 转换 / 拷贝失败（帧格式极端、显存帧已失效）：本帧直接丢掉，下一帧再来。
            return null;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private void SetError(string? message)
    {
        _lastError = message;
        if (message is not null) Trace.WriteLine(LogPrefix + message);
    }
}
