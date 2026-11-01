using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;

namespace YuLiangToolkit.Tools.Live2D.Mocap;

/// <summary>
/// 摄像头面部动捕数据源（Windows 自带 API，零第三方包）：
/// <list type="number">
///   <item><c>Windows.Media.Capture.MediaCapture + MediaFrameReader</c> 采集摄像头画面（CPU 内存，BGRA8）；</item>
///   <item><c>Windows.Media.FaceAnalysis.FaceDetector</c> 每帧找人脸框（系统自带检测器，不需要下载模型）；</item>
///   <item>人脸框位置 → 头转角度（<see cref="MocapMath"/>）；框内像素 → 眨眼 / 张嘴（<see cref="FaceExpressionAnalyzer"/>）；</item>
///   <item>结果做指数平滑后发布成不可变快照，UI / 模型运行时在 UI 线程读最新值。</item>
/// </list>
///
/// <para><b>线程</b>：采集回调在 WinRT 线程池；处理在单独的后台任务里串行做（最多缓存 2 帧、满了丢旧帧）；
/// <see cref="Latest"/> / <see cref="LatestPreview"/> 是整体替换的只读快照，任何线程读都安全。</para>
///
/// <para><b>隐私</b>：只有用户显式打开动捕才会初始化摄像头；关闭后立即 Stop 并释放设备。</para>
/// </summary>
public sealed class MocapCameraSource : IMocapProvider, IDisposable
{
    private const int AnalysisWidth = 320;          // 分析 / 预览用的降采样宽度
    private const int NoFaceFramesBeforeLost = 4;   // 连续多少帧没脸才算「脸离开」（≈0.25 秒 @15fps）
    private const string LogPrefix = "[动捕] ";

    private readonly MocapSettings _settings;
    private readonly FaceExpressionAnalyzer _analyzer = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private FaceDetector? _detector;
    private CancellationTokenSource? _cts;
    private Task? _processTask;
    private Channel<CameraFrame>? _queue;
    private bool _running;
    private bool _disposed;
    private string? _lastError;
    private MocapTrackingResult? _latest;
    private MocapPreviewFrame? _latestPreview;
    private long _lastAcceptedTicks;
    private long _fpsWindowStartTicks;
    private int _fpsWindowFrames;
    private double _measuredFps;
    private double _lastProcessMilliseconds;
    private double _lastDetectMilliseconds;
    private double _lastAnalyzeMilliseconds;
    private (int Width, int Height) _frameSize;
    private int _noFaceFrames;
    private long _lastFrameTicks;

    // 平滑后的输出（角度 + 表情）
    private double _yaw, _pitch, _pupilX, _pupilY;
    private double _eyeLeft = 1, _eyeRight = 1, _mouth;
    private bool _hasSmoothValues;

    public MocapCameraSource(MocapSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public MocapTrackingResult? Latest => Volatile.Read(ref _latest);

    /// <inheritdoc />
    public MocapPreviewFrame? LatestPreview => Volatile.Read(ref _latestPreview);

    /// <inheritdoc />
    public bool IsRunning
    {
        get { lock (this) return _running; }
    }

    /// <inheritdoc />
    public string? LastError
    {
        get { lock (this) return _lastError; }
    }

    /// <inheritdoc />
    public double MeasuredFps
    {
        get { lock (this) return _measuredFps; }
    }

    /// <summary>最近一帧处理耗时（毫秒：降采样 + 人脸检测 + 表情分析 + 预览）。</summary>
    public double LastProcessMilliseconds
    {
        get { lock (this) return _lastProcessMilliseconds; }
    }

    /// <summary>最近一帧「人脸检测」耗时（毫秒）。</summary>
    public double LastDetectMilliseconds
    {
        get { lock (this) return _lastDetectMilliseconds; }
    }

    /// <summary>最近一帧「眨眼 / 张嘴」像素分析耗时（毫秒）。</summary>
    public double LastAnalyzeMilliseconds
    {
        get { lock (this) return _lastAnalyzeMilliseconds; }
    }

    /// <inheritdoc />
    public (int Width, int Height) FrameSize
    {
        get { lock (this) return _frameSize; }
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    // ===================== 摄像头枚举 =====================

    /// <summary>枚举系统里的摄像头（不会打开摄像头）。</summary>
    public static async Task<IReadOnlyList<MocapCameraInfo>> EnumerateCamerasAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devices
                .Where(d => d.IsEnabled)
                .Select(d => new MocapCameraInfo { Id = d.Id, Name = string.IsNullOrWhiteSpace(d.Name) ? "未命名摄像头" : d.Name })
                .ToList();
        }
        catch
        {
            return Array.Empty<MocapCameraInfo>();
        }
    }

    /// <summary>重新校准表情（清空眨眼 / 张嘴的自动分位数基准；用户换环境或光线后调用）。
    public void ResetCalibration() => _analyzer.ResetCalibration();

    // ===================== 启停 =====================

    /// <summary>启动采集。返回 false 时 <see cref="LastError"/> 有原因（没摄像头 / 权限被拒 / 被占用等）。</summary>
    public async Task<bool> StartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) { SetError("已释放"); return false; }
            if (_running) return true;
            SetError(null);

            var camera = await PickCameraAsync(_settings).ConfigureAwait(false);
            if (camera is null) { SetError("没有找到可用的摄像头"); return false; }

            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                VideoDeviceId = camera.Id,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            };

            var capture = new MediaCapture();
            try
            {
                await capture.InitializeAsync(settings).AsTask().ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                capture.Dispose();
                SetError("摄像头权限被拒绝（Windows 设置 → 隐私和安全性 → 相机）: " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                capture.Dispose();
                SetError("打开摄像头失败：" + ex.Message);
                return false;
            }

            MediaFrameSource? source = capture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (source is null)
            {
                capture.Dispose();
                SetError("摄像头没有彩色画面源");
                return false;
            }

            // ⚠ 不要对源调 SetFormatAsync 去选 640 宽：这台机器上它抛 COMException，而且某些摄像头在
            // 非默认格式下由 MediaCapture 做 YUY2→BGRA 转换会得到「绿油油」的错色画面（用户报过）。
            // 默认格式（多为 NV12）由系统转换到 BGRA8，颜色正确；分辨率大一点就自己在后面降采样。
            MediaFrameReader reader;
            try
            {
                reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().ConfigureAwait(false);
            }
            catch
            {
                // 个别摄像头不支持 BGRA8 读取：退回源格式，再在回调里自己转 BGRA
                reader = await capture.CreateFrameReaderAsync(source).AsTask().ConfigureAwait(false);
            }

            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

            FaceDetector? detector = null;
            if (FaceDetector.IsSupported)
            {
                try { detector = await FaceDetector.CreateAsync().AsTask().ConfigureAwait(false); }
                catch { detector = null; }
            }

            _capture = capture;
            _reader = reader;
            _detector = detector;
            _cts = new CancellationTokenSource();
            _queue = Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            _noFaceFrames = 0;
            _hasSmoothValues = false;
            _lastAcceptedTicks = 0;
            _fpsWindowFrames = 0;
            _fpsWindowStartTicks = Environment.TickCount64;
            _analyzer.ResetCalibration();

            reader.FrameArrived += OnFrameArrived;
            var startStatus = await reader.StartAsync().AsTask().ConfigureAwait(false);
            if (startStatus != MediaFrameReaderStartStatus.Success)
            {
                await StopInternalAsync().ConfigureAwait(false);
                SetError("摄像头启动失败：" + startStatus);
                return false;
            }

            _processTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
            SetRunning(true);
            RaiseStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            SetError("动捕启动失败：" + ex.Message);
            try { await StopInternalAsync().ConfigureAwait(false); } catch { }
            return false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>停止采集并释放摄像头（可重复调用）。</summary>
    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_running && _reader is null) return;
            await StopInternalAsync().ConfigureAwait(false);
            SetRunning(false);
            RaiseStateChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }

        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try { await _reader.StopAsync().AsTask().ConfigureAwait(false); } catch { }
            try { _reader.Dispose(); } catch { }
            _reader = null;
        }

        if (_queue is not null)
        {
            _queue.Writer.TryComplete();
            _queue = null;
        }

        var processTask = _processTask;
        _processTask = null;
        if (processTask is not null)
        {
            try { await processTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }

        _detector = null;

        if (_capture is not null)
        {
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }

        cts?.Dispose();
        Volatile.Write(ref _latest, MocapTrackingResult.NoFace(Environment.TickCount64));
        Volatile.Write(ref _latestPreview, null);
        _frameSize = (0, 0);
        _hasSmoothValues = false;
    }

    // ===================== 采集 =====================

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!_running && !_processTaskReady) return;

        // 按目标帧率节流（相机可能 30fps，我们只要 10~24fps；检测比较重，多余帧直接丢）
        var now = Environment.TickCount64;
        var interval = 1000.0 / Math.Clamp(_settings.Fps, 5, 30);
        if (now - _lastAcceptedTicks < interval) return;
        _lastAcceptedTicks = now;

        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var softwareBitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (softwareBitmap is null) return;

            var pixels = ExtractBgraPixels(softwareBitmap, out var width, out var height);
            if (pixels is null || width < 64 || height < 48) return;
            _frameSize = (width, height);

            _queue?.Writer.TryWrite(new CameraFrame(pixels, width, height, now));
        }
        catch (Exception ex)
        {
            SetError("读取摄像头帧失败：" + ex.Message);
        }
    }

    private bool _processTaskReady => _queue is not null;

    /// <summary>
    /// 把 SoftwareBitmap 转成 BGRA32 字节数组（格式不是 Bgra8 时先 Convert）。
    /// ⚠ 不要按「alpha 模式不是 Premultiplied」去 Convert：摄像头的 Bgra8 帧通常是 Straight alpha，
    /// 对同格式再做一次 Convert 会得到错色（用户报过「绿油油」）；这里只关心 RGB，alpha 后面统一置 255。
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

    private async Task ProcessLoopAsync(CancellationToken token)
    {
        var queue = _queue;
        if (queue is null) return;
        try
        {
            await foreach (var cameraFrame in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try { await ProcessFrameAsync(cameraFrame, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { SetError("动捕分析失败：" + ex.Message); }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex) { SetError("动捕处理线程退出：" + ex.Message); }
    }

    private async Task ProcessFrameAsync(CameraFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var processStarted = Stopwatch.GetTimestamp();

        var analysis = DownscaleBgra(frame.Pixels, frame.Width, frame.Height, AnalysisWidth, out var analysisWidth, out var analysisHeight);
        var now = Environment.TickCount64;

        // 人脸检测直接在降采样后的分析帧上做（640×360 → 320×180，检测快 3~4 倍，框坐标也正好是分析帧坐标）
        DetectedFace? bestFace = null;
        if (_detector is not null)
        {
            var detectStarted = Stopwatch.GetTimestamp();
            using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                analysis.AsBuffer(), BitmapPixelFormat.Bgra8, analysisWidth, analysisHeight, BitmapAlphaMode.Premultiplied);
            var faces = await _detector.DetectFacesAsync(softwareBitmap).AsTask(token).ConfigureAwait(false);
            foreach (var face in faces)
            {
                if (bestFace is null || face.FaceBox.Width > bestFace.FaceBox.Width) bestFace = face;
            }
            _lastDetectMilliseconds = MillisecondsSince(detectStarted);
        }

        var hasFace = bestFace is not null;
        if (!hasFace)
        {
            _noFaceFrames++;
            if (_noFaceFrames >= NoFaceFramesBeforeLost)
            {
                Volatile.Write(ref _latest, MocapTrackingResult.NoFace(now));
                _hasSmoothValues = false;
            }

            if (_settings.EnablePreview)
                Volatile.Write(ref _latestPreview, BuildPreview(analysis, analysisWidth, analysisHeight, null, frame.Width));
            PublishFps(now);
            _lastFrameTicks = now;
            _lastProcessMilliseconds = MillisecondsSince(processStarted);
            return;
        }

        _noFaceFrames = 0;
        var box = bestFace!.FaceBox;
        var faceBox = new FaceExpressionAnalyzer.FaceBox(
            (int)box.X, (int)box.Y, Math.Max(1, (int)box.Width), Math.Max(1, (int)box.Height));

        var analyzeStarted = Stopwatch.GetTimestamp();
        var expression = _analyzer.Analyze(analysis, analysisWidth, analysisHeight, faceBox);
        _lastAnalyzeMilliseconds = MillisecondsSince(analyzeStarted);

        // 人脸中心归一化（相对分析帧，也就是整幅画面）
        var faceCenterX = (box.X + box.Width * 0.5) / analysisWidth;
        var faceCenterY = (box.Y + box.Height * 0.5) / analysisHeight;
        if (_settings.Mirror) faceCenterX = 1.0 - faceCenterX;

        var (yaw, pitch) = MocapMath.MapHead(faceCenterX, faceCenterY, _settings.NeutralFaceX, _settings.NeutralFaceY);
        var (pupilX, pupilY) = MocapMath.MapPupil(yaw, pitch);

        var smoothing = Math.Clamp(_settings.Smoothing, 0, 0.95);
        if (!_hasSmoothValues)
        {
            _yaw = yaw;
            _pitch = pitch;
            _pupilX = pupilX;
            _pupilY = pupilY;
            _eyeLeft = expression.EyeOpenLeft;
            _eyeRight = expression.EyeOpenRight;
            _mouth = expression.MouthOpen;
            _hasSmoothValues = true;
        }
        else
        {
            _yaw = MocapMath.Smooth(_yaw, yaw, smoothing);
            _pitch = MocapMath.Smooth(_pitch, pitch, smoothing);
            _pupilX = MocapMath.Smooth(_pupilX, pupilX, smoothing);
            _pupilY = MocapMath.Smooth(_pupilY, pupilY, smoothing);
            _eyeLeft = MocapMath.Smooth(_eyeLeft, expression.EyeOpenLeft, smoothing * 0.5);
            _eyeRight = MocapMath.Smooth(_eyeRight, expression.EyeOpenRight, smoothing * 0.5);
            _mouth = MocapMath.Smooth(_mouth, expression.MouthOpen, smoothing * 0.7);
        }

        var result = new MocapTrackingResult
        {
            HasFace = true,
            FaceX = faceCenterX,
            FaceY = faceCenterY,
            FaceScale = box.Width / (double)analysisWidth,
            Yaw = _yaw,
            Pitch = _pitch,
            Roll = 0,
            EyeOpenLeft = _eyeLeft,
            EyeOpenRight = _eyeRight,
            MouthOpen = _mouth,
            PupilX = _pupilX,
            PupilY = _pupilY,
            TimestampMs = now,
        };
        Volatile.Write(ref _latest, result);

        if (_settings.EnablePreview)
            Volatile.Write(ref _latestPreview, BuildPreview(analysis, analysisWidth, analysisHeight, (result, faceBox), frame.Width));

        PublishFps(now);
        _lastFrameTicks = now;
        _lastProcessMilliseconds = MillisecondsSince(processStarted);
    }

    private static double MillisecondsSince(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private void PublishFps(long now)
    {
        _fpsWindowFrames++;
        var elapsed = now - _fpsWindowStartTicks;
        if (elapsed < 1000) return;
        lock (this) _measuredFps = _fpsWindowFrames * 1000.0 / elapsed;
        _fpsWindowFrames = 0;
        _fpsWindowStartTicks = now;
    }

    /// <summary>生成预览快照（镜像在这里做，分析用的缓冲区保持原始方向）。</summary>
    private MocapPreviewFrame BuildPreview(byte[] analysisBgra, int width, int height,
        (MocapTrackingResult Result, FaceExpressionAnalyzer.FaceBox Box)? face, int cameraWidth)
    {
        var preview = new byte[width * height * 4];
        if (_settings.Mirror)
        {
            // 行内左右翻转
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                var srcRow = analysisBgra.AsSpan(row * 4, width * 4);
                var dstRow = preview.AsSpan(row * 4, width * 4);
                for (var x = 0; x < width; x++)
                {
                    var srcIndex = (width - 1 - x) * 4;
                    dstRow[x * 4] = srcRow[srcIndex];
                    dstRow[x * 4 + 1] = srcRow[srcIndex + 1];
                    dstRow[x * 4 + 2] = srcRow[srcIndex + 2];
                    dstRow[x * 4 + 3] = 255;
                }
            }
        }
        else
        {
            analysisBgra.AsSpan(0, preview.Length).CopyTo(preview);
            for (var i = 3; i < preview.Length; i += 4) preview[i] = 255;
        }

        double faceX = 0, faceY = 0, faceW = 0, faceH = 0;
        var hasFace = face is not null;
        if (face is { } found)
        {
            faceX = found.Box.X / (double)width;
            faceY = found.Box.Y / (double)height;
            faceW = found.Box.Width / (double)width;
            faceH = found.Box.Height / (double)height;
            if (_settings.Mirror)
            {
                // 框坐标也镜像
                faceX = 1.0 - (faceX + faceW);
            }
        }

        return new MocapPreviewFrame
        {
            Bgra = preview,
            Width = width,
            Height = height,
            HasFace = hasFace,
            FaceX = faceX,
            FaceY = faceY,
            FaceWidth = faceW,
            FaceHeight = faceH,
            TimestampMs = Environment.TickCount64,
        };
    }

    /// <summary>BGRA 帧按面积平均降采样到指定宽度（保持长宽比）。</summary>
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

    private static async Task<MocapCameraInfo?> PickCameraAsync(MocapSettings settings)
    {
        var cameras = await EnumerateCamerasAsync().ConfigureAwait(false);
        if (cameras.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(settings.CameraId))
        {
            var byId = cameras.FirstOrDefault(c => string.Equals(c.Id, settings.CameraId, StringComparison.Ordinal));
            if (byId is not null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(settings.CameraName))
        {
            var byName = cameras.FirstOrDefault(c => string.Equals(c.Name, settings.CameraName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }

        return cameras[0];
    }

    private void SetRunning(bool value)
    {
        lock (this) _running = value;
    }

    private void SetError(string? error)
    {
        lock (this) _lastError = error;
        if (error is not null) Trace.WriteLine(LogPrefix + error);
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); } catch { /* 订阅方异常不影响采集 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _lifecycleGate.Dispose();
    }

    private sealed class CameraFrame
    {
        public CameraFrame(byte[] pixels, int width, int height, long timestampMs)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            TimestampMs = timestampMs;
        }

        public byte[] Pixels { get; }

        public int Width { get; }

        public int Height { get; }

        public long TimestampMs { get; }
    }
}
