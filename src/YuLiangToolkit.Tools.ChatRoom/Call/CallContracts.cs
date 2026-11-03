namespace YuLiangToolkit.Tools.ChatRoom.Call;

// ============================================================================
// 房间内群语音 / 群视频通话的「契约层」：
//   · 线上怎么编（CallProtocolRules / CallSettings）
//   · 引擎与设备之间怎么分工（ICallMediaFactory / ICameraSource / IMicrophoneSource / IAudioSink）
//   · 引擎跟网络之间怎么分工（ICallTransport）
//
// 这一层只放接口与纯数据，**不放实现**。实现分别在：
//   Call/Codec/*       —— 编解码（μ-law / 重采样 / 混音 / JPEG / 降采样）
//   Call/Media/*       —— 真实设备（WinRT 摄像头 / WASAPI 麦克风与扬声器）
//   Call/CallEngine.cs —— 把「采集 → 编码 → 发送」「接收 → 解码 → 播放 / 显示」串起来
//   Net/CallRelay.cs   —— 主机侧中转（媒体连接注册 / 一进多出转发）
//   Net/CallTransport.cs —— ICallTransport 的两个实现（客户端 / 主机）
//
// 设计约束（改之前先读）：
//   1. **媒体走单独一条 TCP 连接**（跟聊天/文件那条分开）：文件传输会长时间占住写锁，
//      混在一条连接上会把声音卡成 PPT。媒体连接连的是主机**同一个监听端口**，
//      因为内网穿透通常只映射一个端口（另开端口在穿透环境下连不上）。
//   2. 媒体流量**不计入**「单次文件流量上限」（否则开个语音通话几分钟就把额度烧光）；
//      通话自己的收发速率显示在通话面板里。
//   3. 音频：16 kHz 单声道 μ-law，40 ms 一包（640 字节）—— 8 位对数量化，
//      比 16 位 PCM 省一半带宽，语音质量明显好于 8 kHz 电话音。
//   4. 视频：MJPEG（逐帧 JPEG）。不自带编解码器、不引第三方库，WPF 自带 JPEG 编解码，
//      局域网 / 小规模群聊足够用；带宽就是「帧大小 × 帧率」。
//   5. 没有回声消除（AEC）：文档里必须提醒戴耳机，否则会啸叫。这是 v1 的已知边界。
// ============================================================================

/// <summary>通话画质档位（只影响视频，音频固定 16 kHz μ-law）。</summary>
public enum CallQualityPreset
{
    /// <summary>省流量：240×180 / 6 fps。</summary>
    Saver = 0,

    /// <summary>标准：320×240 / 10 fps（默认）。</summary>
    Standard = 1,

    /// <summary>高清：480×360 / 12 fps（带宽约翻倍）。</summary>
    High = 2,
}

/// <summary>
/// 通话相关的协议常量与纯规则（自检可直接断言，不碰任何设备）。
/// </summary>
public static class CallProtocolRules
{
    /// <summary>音频采样率（单声道，μ-law 编码后 1 字节 / 采样）。</summary>
    public const int AudioSampleRate = 16_000;

    /// <summary>一包音频的时长（毫秒）。40 ms = 25 包/秒，兼顾延迟与包头开销。</summary>
    public const int AudioFrameMilliseconds = 40;

    /// <summary>一包音频的采样数 / 字节数（μ-law 1 字节一个采样）。</summary>
    public const int AudioSamplesPerFrame = AudioSampleRate * AudioFrameMilliseconds / 1000;

    /// <summary>一包 μ-law 音频的字节数。</summary>
    public const int AudioBytesPerFrame = AudioSamplesPerFrame;

    /// <summary>一次通话最多几个人（主机也算一个）。再多中转带宽和 CPU 都撑不住。</summary>
    public const int MaxParticipants = 8;

    /// <summary>画质档位对应的视频参数（宽 / 高 / 帧率 / JPEG 质量）。</summary>
    public static (int Width, int Height, int Fps, int JpegQuality) VideoProfile(CallQualityPreset preset) => preset switch
    {
        CallQualityPreset.Saver => (240, 180, 6, 52),
        CallQualityPreset.High => (480, 360, 12, 66),
        _ => (320, 240, 10, 58),
    };

    /// <summary>画质档位的中文名（界面下拉 / 文档）。</summary>
    public static string LabelOf(CallQualityPreset preset) => preset switch
    {
        CallQualityPreset.Saver => "省流量（240×180 / 6 帧）",
        CallQualityPreset.High => "高清（480×360 / 12 帧）",
        _ => "标准（320×240 / 10 帧）",
    };

    /// <summary>画质档位的一句话带宽说明（界面提示用；估算值，真实取决于画面复杂度）。</summary>
    public static string BandwidthHint(CallQualityPreset preset) => preset switch
    {
        CallQualityPreset.Saver => "视频每路约 0.3~0.6 Mbps，适合人多 / 流量紧张",
        CallQualityPreset.High => "视频每路约 1.5~3 Mbps，适合两三人的局域网",
        _ => "视频每路约 0.6~1.2 Mbps，适合大多数情况",
    };

    /// <summary>μ-law 每包字节数（<see cref="AudioBytesPerFrame"/>）对应的毫秒数（反过来验算用）。</summary>
    public static int MillisecondsOf(int audioBytes) => audioBytes * 1000 / AudioSampleRate;

    /// <summary>把任意字节数包成「多少包 40 ms 音频」（自检与统计用）。</summary>
    public static int PacketsOf(long audioBytes)
        => audioBytes <= 0 ? 0 : (int)((audioBytes + AudioBytesPerFrame - 1) / AudioBytesPerFrame);
}

/// <summary>
/// 一次通话的本地设置（从 <c>ChatRoomConfig</c> 来；自检可以直接 new 一个假的）。
/// </summary>
public sealed class CallSettings
{
    /// <summary>摄像头设备 Id（空 = 系统默认第一个）。WinRT 的 <c>DeviceInformation.Id</c>。</summary>
    public string CameraDeviceId { get; set; } = "";

    /// <summary>麦克风设备 Id（空 = 系统默认）。WASAPI 端点 Id。</summary>
    public string MicrophoneDeviceId { get; set; } = "";

    /// <summary>扬声器设备 Id（空 = 系统默认）。WASAPI 端点 Id。</summary>
    public string SpeakerDeviceId { get; set; } = "";

    /// <summary>视频画质档位。</summary>
    public CallQualityPreset Quality { get; set; } = CallQualityPreset.Standard;

    /// <summary>要不要开摄像头（语音通话 = false）。</summary>
    public bool EnableVideo { get; set; } = true;

    /// <summary>要不要开麦克风（理论上语音通话都开；留一个关的入口给「只听」）。</summary>
    public bool EnableAudio { get; set; } = true;

    /// <summary>当前画质档位的视频参数。</summary>
    public (int Width, int Height, int Fps, int JpegQuality) Video => CallProtocolRules.VideoProfile(Quality);

    /// <summary>音频采样率（固定 16 kHz）。</summary>
    public int AudioSampleRate => CallProtocolRules.AudioSampleRate;

    /// <summary>一包音频的字节数（固定 640）。</summary>
    public int AudioBytesPerFrame => CallProtocolRules.AudioBytesPerFrame;

    /// <summary>复制一份（引擎里会按需改，别把配置对象交出去）。</summary>
    public CallSettings Clone() => new()
    {
        CameraDeviceId = CameraDeviceId,
        MicrophoneDeviceId = MicrophoneDeviceId,
        SpeakerDeviceId = SpeakerDeviceId,
        Quality = Quality,
        EnableVideo = EnableVideo,
        EnableAudio = EnableAudio,
    };
}

/// <summary>一个可选的媒体设备（摄像头 / 麦克风 / 扬声器）。</summary>
/// <param name="Id">设备 Id（摄像头是 WinRT Id；音频是 WASAPI 端点 Id）。</param>
/// <param name="Name">给人看的名字。</param>
public sealed record CallDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>媒体包类型（音频 / 视频）。</summary>
public enum CallMediaKind
{
    Audio = 0,
    Video = 1,
}

/// <summary>
/// 通话引擎与网络之间的唯一接口。
/// <para>实现方（<see cref="Net.ClientCallTransport"/> / <see cref="Net.HostCallTransport"/>）负责：
/// 把本地包发出去、把别人的包收进来、上报连接断开。引擎不关心底层是 TCP 还是别的。</para>
/// <para>⚠ <see cref="PacketReceived"/> 会在网络读线程上触发（多路连接时可能并发），
/// 订阅方（引擎的 <c>HandlePacket</c>）必须快速返回、自己保证线程安全。</para>
/// </summary>
public interface ICallTransport : IDisposable
{
    /// <summary>这条媒体连接还通着没有。</summary>
    bool IsOpen { get; }

    /// <summary>发送队列因为拥塞丢掉的视频帧数（引擎统计用；不做丢帧的通道返回 0）。</summary>
    int DroppedVideoFrames { get; }

    /// <summary>发一包 μ-law 音频（长度应为 <see cref="CallProtocolRules.AudioBytesPerFrame"/>）。</summary>
    void SendAudio(ReadOnlySpan<byte> ulaw);

    /// <summary>发一帧 JPEG 视频。</summary>
    void SendVideo(ReadOnlySpan<byte> jpeg);

    /// <summary>上报本端的麦克风 / 摄像头开关（null = 这一项不变）。</summary>
    void SendControl(bool? micOn, bool? cameraOn);

    /// <summary>收到别人的媒体包：发送方成员 Id、类型、裸字节、对端时间戳（毫秒）。</summary>
    event Action<string, CallMediaKind, byte[], long>? PacketReceived;

    /// <summary>媒体连接断开（参数是可读原因）。</summary>
    event Action<string>? Closed;
}

/// <summary>摄像头采集到的一帧（BGRA32、行优先、自上而下）。</summary>
public sealed record CameraFrameData(byte[] Bgra, int Width, int Height, long TimestampMs);

/// <summary>
/// 通话引擎产出的原始帧（还没变成 WPF 图像）：
/// <see cref="IsJpeg"/> = true 是收到的远端帧（JPEG），false 是本机摄像头预览（BGRA）。
/// 交给界面后由 ViewModel 解码 / 转 <c>BitmapSource</c>（引擎自己保持无 WPF 依赖，方便无头自检）。
/// </summary>
public sealed record CallRawFrame(string MemberId, byte[] Bytes, bool IsJpeg, int Width, int Height, long TimestampMs);

/// <summary>
/// 通话统计（引擎每约 1 秒发一次快照，界面直接显示）。
/// </summary>
public sealed class CallStats
{
    /// <summary>本端已发送 / 已接收的媒体字节数（不含协议头）。</summary>
    public long SentBytes { get; set; }

    public long ReceivedBytes { get; set; }

    /// <summary>发出的音频 / 视频包数。</summary>
    public int SentAudioPackets { get; set; }

    public int SentVideoPackets { get; set; }

    /// <summary>收到的音频 / 视频包数。</summary>
    public int ReceivedAudioPackets { get; set; }

    public int ReceivedVideoPackets { get; set; }

    /// <summary>最近 1 秒的发送 / 接收码率（kbps）。</summary>
    public double SendKbps { get; set; }

    public double ReceiveKbps { get; set; }

    /// <summary>本端视频实际发出帧率（帧/秒）。</summary>
    public double SendVideoFps { get; set; }

    /// <summary>因为跟不上而丢掉的视频帧数（音频不丢，丢了也没意义）。</summary>
    public int DroppedVideoFrames { get; set; }

    /// <summary>「↑ 1.2 Mbps　↓ 2.3 Mbps」这种一行摘要。</summary>
    public string Summary => SendKbps <= 0 && ReceiveKbps <= 0
        ? "↑ 0 kbps　↓ 0 kbps"
        : $"↑ {FormatRate(SendKbps)}　↓ {FormatRate(ReceiveKbps)}";

    /// <summary>更细的说明（通话面板底部 / 日志用）。</summary>
    public string Detail =>
        $"音频 {ReceivedAudioPackets} 包 / 视频 {ReceivedVideoPackets} 帧　本端视频 {SendVideoFps:0.#} fps　丢帧 {DroppedVideoFrames}";

    /// <summary>把 kbps 格式化成人能读的单位。</summary>
    public static string FormatRate(double kbps) => kbps switch
    {
        <= 0 => "0 kbps",
        < 1000 => $"{kbps:0} kbps",
        _ => $"{kbps / 1000:0.##} Mbps",
    };
}

/// <summary>摄像头采集接口（真实实现：WinRT <c>MediaCapture + MediaFrameReader</c>）。</summary>
public interface ICameraSource : IAsyncDisposable
{
    /// <summary>
    /// 打开摄像头。内部按源默认格式采集（不要自己 SetFormat，某些摄像头会出「绿油油」的错色），
    /// 尺寸由引擎自己降采样到目标大小。
    /// </summary>
    /// <returns>(是不是起来了, 失败原因)。</returns>
    Task<(bool Ok, string Error)> StartAsync(string deviceId);

    /// <summary>采到一帧（可能在 WinRT 线程池上触发，订阅方自己切线程）。</summary>
    event Action<CameraFrameData>? FrameArrived;

    /// <summary>最近一次错误（空 = 没有）。</summary>
    string? LastError { get; }
}

/// <summary>麦克风采集接口（真实实现：WASAPI 共享模式）。</summary>
public interface IMicrophoneSource : IDisposable
{
    /// <summary>
    /// 打开麦克风（空的设备 Id = 系统默认）。采集到的是**设备混音格式**：
    /// 单声道 float（-1~1，各通道已混成单声道）、<see cref="SampleRate"/> 采样率。
    /// 重采样到通话用的 16 kHz 由引擎自己按 <see cref="PcmResampler"/> 做（采集层保持“笨”）。
    /// </summary>
    (bool Ok, string Error) Start(string deviceId);

    /// <summary>采到一段单声道样本（缓冲会复用，订阅方必须当场消费）。</summary>
    event Action<float[], int>? SamplesAvailable;

    /// <summary>设备混音采样率（一般 48000 / 44100；启动失败是 0）。</summary>
    int SampleRate { get; }

    /// <summary>最近一次错误。</summary>
    string? LastError { get; }
}

/// <summary>
/// 扬声器播放接口（真实实现：WASAPI 共享模式）。
/// <para>采用「拉」模型：实现方的播放线程按自己的节奏调用 <c>pull(frames)</c> 要数据，
/// 引擎在里面把所有人的声音混好交出去 —— 这样时钟由声卡决定，不会越放越飘。</para>
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>打开扬声器。<paramref name="pullStereo"/> 返回 <c>frames × 2</c> 的立体声 interleaved float。</summary>
    (bool Ok, string Error) Start(string deviceId, Func<int, float[]> pullStereo);

    /// <summary>设备混音采样率（pull 要按这个速率给数据）。</summary>
    int SampleRate { get; }

    /// <summary>最近一次错误。</summary>
    string? LastError { get; }
}

/// <summary>
/// 媒体设备的工厂（真实实现见 <c>WindowsCallMediaFactory</c>）。
/// 单独抽出接口是为了自检能塞「假摄像头 / 假麦克风 / 假扬声器」，在没有设备的机器上也能跑完整链路。
/// </summary>
public interface ICallMediaFactory
{
    /// <summary>枚举摄像头（WinRT）。没摄像头 / 权限被拒时返回空列表，不要抛。</summary>
    Task<IReadOnlyList<CallDevice>> ListCamerasAsync(CancellationToken ct = default);

    /// <summary>枚举麦克风（WASAPI 采集端点）。</summary>
    IReadOnlyList<CallDevice> ListMicrophones();

    /// <summary>枚举扬声器（WASAPI 播放端点）。</summary>
    IReadOnlyList<CallDevice> ListSpeakers();

    /// <summary>新建一个摄像头采集器（没打开设备之前很便宜）。</summary>
    ICameraSource CreateCamera();

    /// <summary>新建一个麦克风采集器。</summary>
    IMicrophoneSource CreateMicrophone();

    /// <summary>新建一个扬声器播放器。</summary>
    IAudioSink CreateSpeaker();
}
