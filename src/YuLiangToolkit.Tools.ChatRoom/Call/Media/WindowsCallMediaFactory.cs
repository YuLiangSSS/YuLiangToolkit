using Windows.Devices.Enumeration;

namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// 真实的通话媒体设备工厂：WinRT 摄像头 + WASAPI 麦克风 / 扬声器。
///
/// <para><b>职责边界</b>：只做「枚举设备」和「new 采集 / 播放对象」。
/// 打开设备、读写音频、采帧都在具体实现里（<see cref="CameraCapture"/> /
/// <see cref="MicrophoneCapture"/> / <see cref="SpeakerOutput"/>）；
/// 这样引擎 / 界面拿到的设备列表和真实设备行为是同一套代码，不会出现「列表里有、点了打不开」的假象。</para>
///
/// <para><b>为什么不抛异常</b>：设备枚举在「没摄像头」「隐私权限被拒」「音频服务没启动」时都会失败，
/// 但这些都属于正常情况（用户可能就想用语音通话），所以统一返回空列表；失败原因由具体实现写进
/// <c>LastError</c>，界面显示成提示。</para>
///
/// <para><b>线程</b>：<see cref="ListCamerasAsync"/> 是异步 WinRT 调用（可在 UI 线程 await，
/// 不阻塞）；音频枚举是同步 COM 调用（通常 &lt;10 ms，直接在调用线程跑）。工厂本身无状态，线程安全。</para>
/// </summary>
public sealed class WindowsCallMediaFactory : ICallMediaFactory
{
    /// <summary>整个程序共用一个（无状态、创建很便宜，但没必要每次 new）。</summary>
    public static WindowsCallMediaFactory Shared { get; } = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<CallDevice>> ListCamerasAsync(CancellationToken ct = default)
    {
        try
        {
            return await QueryCamerasAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 没摄像头 / 权限被拒 / 枚举失败：按契约返回空列表，不抛给界面。
            return Array.Empty<CallDevice>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CallDevice> ListMicrophones() => EnumerateAudioSafe(WasapiInterop.DataFlowCapture);

    /// <inheritdoc />
    public IReadOnlyList<CallDevice> ListSpeakers() => EnumerateAudioSafe(WasapiInterop.DataFlowRender);

    /// <inheritdoc />
    public ICameraSource CreateCamera() => new CameraCapture();

    /// <inheritdoc />
    public IMicrophoneSource CreateMicrophone() => new MicrophoneCapture();

    /// <inheritdoc />
    public IAudioSink CreateSpeaker() => new SpeakerOutput();

    /// <summary>
    /// 枚举系统摄像头（不打开设备）。这里会抛异常，由 <see cref="ListCamerasAsync"/> 兜底；
    /// <see cref="CameraCapture"/> 也复用这个方法来「按 Id 找摄像头」，保证两处算法一致。
    /// </summary>
    /// <param name="ct">取消令牌（引擎退出时用来放弃枚举）。</param>
    internal static async Task<IReadOnlyList<CallDevice>> QueryCamerasAsync(CancellationToken ct)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(ct).ConfigureAwait(false);

        var result = new List<CallDevice>(devices.Count);
        foreach (var device in devices)
        {
            // IsEnabled = 用户在设备管理器里禁用的摄像头不列出来（列了也开不了）。
            if (!device.IsEnabled) continue;
            var name = string.IsNullOrWhiteSpace(device.Name) ? "未命名摄像头" : device.Name;
            result.Add(new CallDevice(device.Id, name));
        }

        return result;
    }

    /// <summary>音频枚举兜底：任何失败都返回空列表（契约要求不抛）。</summary>
    private static IReadOnlyList<CallDevice> EnumerateAudioSafe(int dataFlow)
    {
        try
        {
            return AudioDeviceCatalog.Enumerate(dataFlow);
        }
        catch
        {
            return Array.Empty<CallDevice>();
        }
    }
}
