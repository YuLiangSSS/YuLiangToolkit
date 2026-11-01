using Windows.Graphics.Imaging;

namespace YuLiangToolkit.Tools.Live2D.Mocap;

/// <summary>一个摄像头设备（Id 在不同机器 / 不同插拔顺序下会变，所以界面上同时显示名字）。</summary>
public sealed class MocapCameraInfo
{
    /// <summary>设备 Id（DeviceInformation.Id）。</summary>
    public required string Id { get; init; }

    /// <summary>设备显示名（例如 "HD User Facing" / "DroidCam Video"）。</summary>
    public required string Name { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// 动捕运行参数（从 <see cref="Live2DConfig"/> 拷出来的快照；改配置后重新 Start 生效）。
/// </summary>
public sealed class MocapSettings
{
    /// <summary>摄像头设备 Id（空 = 自动选第一个）。</summary>
    public string CameraId { get; init; } = "";

    /// <summary>摄像头显示名（设备 Id 失效时按名字兜底找）。</summary>
    public string CameraName { get; init; } = "";

    /// <summary>是否镜像（前置摄像头默认镜像，像照镜子）。</summary>
    public bool Mirror { get; init; } = true;

    /// <summary>目标帧率（10 / 15 / 24）。</summary>
    public int Fps { get; init; } = 15;

    /// <summary>是否生成预览帧（测试页小窗用；关掉省 CPU / 内存）。</summary>
    public bool EnablePreview { get; init; } = true;

    /// <summary>平滑系数（0~0.95，越大越稳）。</summary>
    public double Smoothing { get; init; } = 0.45;

    /// <summary>中性表情时脸中心位置（0~1；校准用）。</summary>
    public double NeutralFaceX { get; init; } = 0.5;

    /// <summary>中性表情时脸中心位置（0~1；校准用）。</summary>
    public double NeutralFaceY { get; init; } = 0.5;

    /// <summary>从工具配置生成动捕参数。</summary>
    public static MocapSettings FromConfig(Live2DConfig config) => new()
    {
        CameraId = config.MocapCameraId,
        CameraName = config.MocapCameraName,
        Mirror = config.MocapMirror,
        Fps = Math.Clamp(config.MocapFps, 10, 24),
        EnablePreview = config.MocapShowPreview,
        Smoothing = Math.Clamp(config.MocapSmoothing, 0, 0.95),
        NeutralFaceX = config.MocapNeutralFaceX,
        NeutralFaceY = config.MocapNeutralFaceY,
    };
}

/// <summary>
/// 一帧动捕结果（不可变快照；相机线程写、UI 线程读，所以只读 + 整体引用替换）。
/// <para>角度单位是「度」，已经减掉中性位置（正面 = 0）；<see cref="EyeOpenLeft"/>/<see cref="EyeOpenRight"/>/
/// <see cref="MouthOpen"/> 是 0~1 的开合度（1 = 完全睁开 / 张大嘴）。</para>
/// </summary>
public sealed class MocapTrackingResult
{
    /// <summary>这一帧有没有检测到人脸。</summary>
    public bool HasFace { get; init; }

    /// <summary>人脸中心在画面里的归一化位置（0~1，左上为 0）。</summary>
    public double FaceX { get; init; }

    /// <summary>人脸中心在画面里的归一化位置（0~1，左上为 0）。</summary>
    public double FaceY { get; init; }

    /// <summary>人脸宽度 / 画面宽度（0~1，可用来判断远近）。</summary>
    public double FaceScale { get; init; }

    /// <summary>左右转头（度，正 = 画面右侧）。</summary>
    public double Yaw { get; init; }

    /// <summary>上下点头（度，正 = 抬头）。</summary>
    public double Pitch { get; init; }

    /// <summary>头部侧倾（度；当前实现无可靠来源，恒 0，留给以后扩展）。</summary>
    public double Roll { get; init; }

    /// <summary>左眼开合度（0~1）。</summary>
    public double EyeOpenLeft { get; init; }

    /// <summary>右眼开合度（0~1）。</summary>
    public double EyeOpenRight { get; init; }

    /// <summary>张嘴开合度（0~1）。</summary>
    public double MouthOpen { get; init; }

    /// <summary>眼球左右（-1~1，由转头方向近似）。</summary>
    public double PupilX { get; init; }

    /// <summary>眼球上下（-1~1，由点头方向近似）。</summary>
    public double PupilY { get; init; }

    /// <summary>采样时间（<see cref="Environment.TickCount64"/> 毫秒）。</summary>
    public long TimestampMs { get; init; }

    /// <summary>没检测到人脸时用的空结果。</summary>
    public static MocapTrackingResult NoFace(long timestampMs) => new() { HasFace = false, TimestampMs = timestampMs };
}

/// <summary>
/// 摄像头预览帧快照（测试页「动捕」卡片的小窗用）：BGRA32 像素 + 人脸框（归一化坐标）。
/// 每帧新数组（约 240×135×4 ≈ 130 KB），最多 15 帧/秒，UI 线程按需取最新一帧。
/// </summary>
public sealed class MocapPreviewFrame
{
    /// <summary>BGRA32 像素（长度 = Width × Height × 4）。</summary>
    public required byte[] Bgra { get; init; }

    /// <summary>宽（像素）。</summary>
    public required int Width { get; init; }

    /// <summary>高（像素）。</summary>
    public required int Height { get; init; }

    /// <summary>这一帧有没有检测到人脸。</summary>
    public bool HasFace { get; init; }

    /// <summary>人脸框（相对预览图的归一化坐标 0~1，左手边是 0）。</summary>
    public double FaceX { get; init; }

    /// <summary>人脸框（归一化坐标）。</summary>
    public double FaceY { get; init; }

    /// <summary>人脸框（归一化宽度）。</summary>
    public double FaceWidth { get; init; }

    /// <summary>人脸框（归一化高度）。</summary>
    public double FaceHeight { get; init; }

    /// <summary>采样时间（Environment.TickCount64 毫秒）。</summary>
    public long TimestampMs { get; init; }
}

/// <summary>
/// 动捕数据源契约：模型运行时（<c>ModelRuntime</c>）只依赖这个接口读结果，不关心数据来自摄像头还是测试假数据。
/// </summary>
public interface IMocapProvider
{
    /// <summary>最近一帧动捕结果（null = 还没开始 / 还没有数据）。</summary>
    MocapTrackingResult? Latest { get; }

    /// <summary>最近一帧预览（没开启预览 / 还没出帧时为 null）。</summary>
    MocapPreviewFrame? LatestPreview { get; }

    /// <summary>是否正在采集。</summary>
    bool IsRunning { get; }

    /// <summary>最近一次错误（成功时为 null）。</summary>
    string? LastError { get; }

    /// <summary>实测帧率（约 1 秒更新一次）。</summary>
    double MeasuredFps { get; }

    /// <summary>摄像头画面尺寸（未启动为 0×0）。</summary>
    (int Width, int Height) FrameSize { get; }

    /// <summary>状态变化（启动成功 / 停止 / 出错）时触发，可能在后台线程。</summary>
    event EventHandler? StateChanged;
}
