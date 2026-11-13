using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.ScreenRecorder;

/// <summary>
/// 「录屏」功能配置 → <c>Config\screen-recorder\config.json</c>。
///
/// <para>所有界面选项都写在这里（改一下由框架自动落盘，没有保存按钮）。
/// 值统一存“干净值”（英文枚举 / 数字），中文只出现在界面的 <c>OptionItem.Label</c> 里，
/// 这样以后改文案不会把老配置读坏。</para>
///
/// <para>⚠ 需要联动的属性（来源 / 格式 / 帧率 / 宽度 / GIF / MP4 / 倒计时 / 最长时长 / 输出文件夹 /
/// 热键开关）一律用 <see cref="ToolConfig.SetAndSave{T}"/> 写，不能写成自动属性：
/// 自动属性虽然也能自动落盘，但**不发 PropertyChanged**，ViewModel 里的联动（切换 GIF / MP4 选项、
/// 显示窗口选择面板…）会全部失效。</para>
/// </summary>
public sealed class ScreenRecorderConfig : ToolConfig
{
    /// <summary>工具 Id（同时是 Data / Config / Cache 下的小文件夹名）。</summary>
    public const string ToolId = "screen-recorder";

    /// <summary>默认文件名前缀。</summary>
    public const string DefaultFilePrefix = "录屏";

    // ---------- 录制来源 ----------

    private string _sourceKind = "region";

    /// <summary>来源：<c>fullscreen</c>（主显示器）/ <c>region</c>（框选区域）/ <c>window</c>（窗口）。</summary>
    public string SourceKind
    {
        get => _sourceKind;
        set => SetAndSave(ref _sourceKind, value);
    }

    private int _regionX;
    private int _regionY;
    private int _regionWidth;
    private int _regionHeight;

    /// <summary>框选区域的屏幕坐标（物理像素，允许负值 = 副屏在左边 / 上边）。</summary>
    public int RegionX
    {
        get => _regionX;
        set => SetAndSave(ref _regionX, value);
    }

    /// <summary>框选区域左上角 Y。</summary>
    public int RegionY
    {
        get => _regionY;
        set => SetAndSave(ref _regionY, value);
    }

    /// <summary>框选区域宽度。</summary>
    public int RegionWidth
    {
        get => _regionWidth;
        set => SetAndSave(ref _regionWidth, value);
    }

    /// <summary>框选区域高度。</summary>
    public int RegionHeight
    {
        get => _regionHeight;
        set => SetAndSave(ref _regionHeight, value);
    }

    private string _windowTitle = "";

    /// <summary>「窗口」来源记住的窗口标题（下次打开时按标题重新定位；窗口换标题就重新选一次）。</summary>
    public string WindowTitle
    {
        get => _windowTitle;
        set => SetAndSave(ref _windowTitle, value);
    }

    // ---------- 输出 ----------

    private string _format = "gif";

    /// <summary>输出格式：<c>gif</c>（动图）/ <c>mp4</c>（视频）。</summary>
    public string Format
    {
        get => _format;
        set => SetAndSave(ref _format, value);
    }

    private int _fps = 15;

    /// <summary>录制帧率（GIF 建议 10~15，MP4 可以 30）。</summary>
    public int Fps
    {
        get => _fps;
        set => SetAndSave(ref _fps, value);
    }

    private int _scaleWidth = 960;

    /// <summary>输出宽度（0 = 原始分辨率；GIF 会按这个值等比缩小；不会放大）。</summary>
    public int ScaleWidth
    {
        get => _scaleWidth;
        set => SetAndSave(ref _scaleWidth, value);
    }

    private int _gifColors = 256;

    /// <summary>GIF 调色板颜色数（2~256；越大越像原图、文件越大）。</summary>
    public int GifColors
    {
        get => _gifColors;
        set => SetAndSave(ref _gifColors, value);
    }

    private string _gifDither = "floyd_steinberg";

    /// <summary>GIF 抖动：<c>floyd_steinberg</c> / <c>bayer</c> / <c>none</c>。</summary>
    public string GifDither
    {
        get => _gifDither;
        set => SetAndSave(ref _gifDither, value);
    }

    private bool _gifLoop = true;

    /// <summary>GIF 是否无限循环播放（false = 只播一遍）。</summary>
    public bool GifLoop
    {
        get => _gifLoop;
        set => SetAndSave(ref _gifLoop, value);
    }

    private int _videoQuality = 23;

    /// <summary>视频画质（质量优先模式下的 CRF / CQ：0~51，越小越清晰也越大）。</summary>
    public int VideoQuality
    {
        get => _videoQuality;
        set => SetAndSave(ref _videoQuality, value);
    }

    private string _videoCodec = "libx264";

    /// <summary>视频编码器 Id（见 <see cref="VideoCatalog"/>；硬件编码器需本机 FFmpeg 支持）。</summary>
    public string VideoCodec
    {
        get => _videoCodec;
        set
        {
            // ComboBox 在 ItemsSource 重建的瞬间可能回写 null：不要把已选编码器清掉
            if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(_videoCodec)) return;
            SetAndSave(ref _videoCodec, value);
        }
    }

    private string _videoRateMode = "quality";

    /// <summary>视频速率控制：<c>quality</c>（画质优先 / CRF）或 <c>bitrate</c>（码率优先 / Mbps）。</summary>
    public string VideoRateMode
    {
        get => _videoRateMode;
        set => SetAndSave(ref _videoRateMode, value);
    }

    private int _videoBitrateMbps = 8;

    /// <summary>码率优先模式下的目标码率（Mbps，1~100）。</summary>
    public int VideoBitrateMbps
    {
        get => _videoBitrateMbps;
        set => SetAndSave(ref _videoBitrateMbps, value);
    }

    // ---------- 声音 ----------

    private bool _recordMicrophone;

    /// <summary>录制麦克风（默认关；开启后停止录制时会把麦克风混进视频）。</summary>
    public bool RecordMicrophone
    {
        get => _recordMicrophone;
        set => SetAndSave(ref _recordMicrophone, value);
    }

    private int _microphoneVolume = 100;

    /// <summary>麦克风音量百分比（0~200，录制中拖滑块会实时改增益）。</summary>
    public int MicrophoneVolume
    {
        get => _microphoneVolume;
        set => SetAndSave(ref _microphoneVolume, value);
    }

    private bool _recordSystemAudio;

    /// <summary>录制系统扬声器（WASAPI 回环，默认关）。</summary>
    public bool RecordSystemAudio
    {
        get => _recordSystemAudio;
        set => SetAndSave(ref _recordSystemAudio, value);
    }

    private int _speakerVolume = 100;

    /// <summary>系统声音音量百分比（0~200，录制中拖滑块会实时改增益）。</summary>
    public int SpeakerVolume
    {
        get => _speakerVolume;
        set => SetAndSave(ref _speakerVolume, value);
    }

    private int _audioBitrateKbps = 192;

    /// <summary>最终视频里音频流的码率（kbps，128 / 192 / 256）。</summary>
    public int AudioBitrateKbps
    {
        get => _audioBitrateKbps;
        set => SetAndSave(ref _audioBitrateKbps, value);
    }

    // ---------- 摄像头 ----------

    private bool _enableCamera;

    /// <summary>启用摄像头（预览 / 屏幕覆盖 / 录进画面都以此为前提）。</summary>
    public bool EnableCamera
    {
        get => _enableCamera;
        set => SetAndSave(ref _enableCamera, value);
    }

    private string _cameraDeviceId = "";

    /// <summary>选中的摄像头设备 Id（空 = 第一个可用摄像头）。</summary>
    public string CameraDeviceId
    {
        get => _cameraDeviceId;
        set => SetAndSave(ref _cameraDeviceId, value);
    }

    private string _cameraDeviceName = "";

    /// <summary>选中的摄像头名称（显示用；设备 Id 变了也不影响找设备）。</summary>
    public string CameraDeviceName
    {
        get => _cameraDeviceName;
        set => SetAndSave(ref _cameraDeviceName, value);
    }

    private bool _cameraMirror = true;

    /// <summary>摄像头画面左右镜像（自拍习惯；录进画面时也是镜像后的样子）。</summary>
    public bool CameraMirror
    {
        get => _cameraMirror;
        set => SetAndSave(ref _cameraMirror, value);
    }

    private bool _cameraOverlayEnabled;

    /// <summary>是否把摄像头画面作为置顶小窗覆盖在屏幕 / 录制区域上（录制时会一起录进去）。</summary>
    public bool CameraOverlayEnabled
    {
        get => _cameraOverlayEnabled;
        set => SetAndSave(ref _cameraOverlayEnabled, value);
    }

    private int _cameraOverlayX = -1;

    /// <summary>摄像头屏幕覆盖位置 X（WPF DIP；-1 = 自动放在屏幕右下角）。</summary>
    public int CameraOverlayX
    {
        get => _cameraOverlayX;
        set => SetAndSave(ref _cameraOverlayX, value);
    }

    private int _cameraOverlayY = -1;

    /// <summary>摄像头屏幕覆盖位置 Y（WPF DIP；-1 = 自动）。</summary>
    public int CameraOverlayY
    {
        get => _cameraOverlayY;
        set => SetAndSave(ref _cameraOverlayY, value);
    }

    private int _cameraOverlayWidth = 320;

    /// <summary>摄像头屏幕覆盖宽度（WPF DIP，120~800；高度按摄像头比例自动算）。</summary>
    public int CameraOverlayWidth
    {
        get => _cameraOverlayWidth;
        set => SetAndSave(ref _cameraOverlayWidth, value);
    }

    private int _cameraOverlayOpacity = 100;

    /// <summary>摄像头屏幕覆盖“浓度”百分比（20~100）：按画面和黑底混合实现，不是窗口穿透。</summary>
    public int CameraOverlayOpacity
    {
        get => _cameraOverlayOpacity;
        set => SetAndSave(ref _cameraOverlayOpacity, value);
    }

    // ---------- 全局快捷键 ----------

    private bool _enableGlobalHotkeys = true;

    /// <summary>启用全局录制快捷键（开始/停止、截图）。</summary>
    public bool EnableGlobalHotkeys
    {
        get => _enableGlobalHotkeys;
        set => SetAndSave(ref _enableGlobalHotkeys, value);
    }

    private string _startStopHotkey = "F8";

    /// <summary>开始 / 停止录制的全局快捷键文本（例如 F8、Ctrl+Shift+F9）。</summary>
    public string StartStopHotkey
    {
        get => _startStopHotkey;
        set => SetAndSave(ref _startStopHotkey, value);
    }

    private string _screenshotHotkey = "";

    /// <summary>截图的全局快捷键文本（空 = 不注册）。</summary>
    public string ScreenshotHotkey
    {
        get => _screenshotHotkey;
        set => SetAndSave(ref _screenshotHotkey, value);
    }

    // ---------- 单条启用位（主页设置 → 快捷键汇总用；总开关见 EnableGlobalHotkeys） ----------

    private bool _startStopHotkeyEnabled = true;

    /// <summary>「开始 / 停止录制」这一条启用。</summary>
    public bool StartStopHotkeyEnabled
    {
        get => _startStopHotkeyEnabled;
        set => SetAndSave(ref _startStopHotkeyEnabled, value);
    }

    private bool _screenshotHotkeyEnabled = true;

    /// <summary>「截图」这一条启用。</summary>
    public bool ScreenshotHotkeyEnabled
    {
        get => _screenshotHotkeyEnabled;
        set => SetAndSave(ref _screenshotHotkeyEnabled, value);
    }

    // ---------- 录制行为 ----------

    private bool _drawMouse = true;

    /// <summary>录制时是否画鼠标光标。</summary>
    public bool DrawMouse
    {
        get => _drawMouse;
        set => SetAndSave(ref _drawMouse, value);
    }

    private int _countdownSeconds = 3;

    /// <summary>开始前倒计时秒数（0 = 立刻开始）。</summary>
    public int CountdownSeconds
    {
        get => _countdownSeconds;
        set => SetAndSave(ref _countdownSeconds, value);
    }

    private int _maxSeconds;

    /// <summary>最长录制秒数（0 = 不限制；到时间自动停止并生成文件）。</summary>
    public int MaxSeconds
    {
        get => _maxSeconds;
        set => SetAndSave(ref _maxSeconds, value);
    }

    private bool _showControlBar = true;

    /// <summary>录制时显示置顶控制条（全屏录制时它可能被一起录进去；关掉后用快捷键停止）。</summary>
    public bool ShowControlBar
    {
        get => _showControlBar;
        set => SetAndSave(ref _showControlBar, value);
    }

    private bool _openFolderAfterSave = true;

    /// <summary>生成文件后自动打开输出文件夹。</summary>
    public bool OpenFolderAfterSave
    {
        get => _openFolderAfterSave;
        set => SetAndSave(ref _openFolderAfterSave, value);
    }

    private string _outputFolder = "";

    /// <summary>输出文件夹（空 = 首次运行时自动用「视频\YuLiangToolkit 录屏」）。</summary>
    public string OutputFolder
    {
        get => _outputFolder;
        set => SetAndSave(ref _outputFolder, value);
    }

    private string _filePrefix = DefaultFilePrefix;

    /// <summary>文件名前缀（还会自动加日期时间，重名自动加序号）。</summary>
    public string FilePrefix
    {
        get => _filePrefix;
        set => SetAndSave(ref _filePrefix, value);
    }

    // ---------- 只读计算属性（不写进 JSON） ----------

    /// <summary>当前格式是不是 GIF（界面用它显示 / 隐藏 GIF 专属选项）。</summary>
    [JsonIgnore]
    public bool IsGif => string.Equals(Format, "gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>当前格式是不是 MP4。</summary>
    [JsonIgnore]
    public bool IsMp4 => !IsGif;

    /// <summary>当前格式是不是视频（MP4 / MKV / MOV / AVI / WebM）。</summary>
    [JsonIgnore]
    public bool IsVideo => !IsGif;

    /// <summary>有没有框选过区域（宽高都大于 0）。</summary>
    [JsonIgnore]
    public bool HasRegion => RegionWidth > 0 && RegionHeight > 0;

    /// <summary>来源是不是「区域」。</summary>
    [JsonIgnore]
    public bool IsRegionSource => string.Equals(SourceKind, "region", StringComparison.OrdinalIgnoreCase);

    /// <summary>来源是不是「窗口」。</summary>
    [JsonIgnore]
    public bool IsWindowSource => string.Equals(SourceKind, "window", StringComparison.OrdinalIgnoreCase);

    /// <summary>来源是不是「全屏」。</summary>
    [JsonIgnore]
    public bool IsFullScreenSource => string.Equals(SourceKind, "fullscreen", StringComparison.OrdinalIgnoreCase);
}
