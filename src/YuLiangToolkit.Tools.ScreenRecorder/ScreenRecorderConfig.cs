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

    private int _mp4Quality = 23;

    /// <summary>MP4 画质（H.264 的 CRF：18 高画质 / 23 标准 / 28 小体积）。</summary>
    public int Mp4Quality
    {
        get => _mp4Quality;
        set => SetAndSave(ref _mp4Quality, value);
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

    /// <summary>录制时显示置顶控制条（全屏录制时它可能被一起录进去；关掉后用 F8 停止）。</summary>
    public bool ShowControlBar
    {
        get => _showControlBar;
        set => SetAndSave(ref _showControlBar, value);
    }

    private bool _useStopHotkey = true;

    /// <summary>录制中允许按全局热键 F8 停止（热键和别的软件冲突时关掉它，用界面按钮停止）。</summary>
    public bool UseStopHotkey
    {
        get => _useStopHotkey;
        set => SetAndSave(ref _useStopHotkey, value);
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
