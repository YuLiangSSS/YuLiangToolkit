using System.IO;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>一张由截图工具贴到桌面上的图，记下它属于哪个贴图组。</summary>
public sealed class ScreenshotPinRecord
{
    /// <summary>图片完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>所属贴图组下标（对应 <see cref="ScreenshotConfig.PinGroupNames"/>）。</summary>
    public int GroupIndex { get; set; }

    /// <summary>创建时间（UTC，排序 / 排错用）。</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 「截图工具」的配置 → <c>Config\screenshot\config.json</c>。
///
/// <para>所有快捷键、输出、编辑默认值和历史设置都在这里。用户改任何一项即时保存，
/// 没有单独“保存”按钮。</para>
/// </summary>
public sealed class ScreenshotConfig : ToolConfig
{
    /// <summary>工具 Id（同时也是 Data / Config / Cache 下的小文件夹名）。</summary>
    public const string ToolId = "screenshot";

    /// <summary>默认文件名前缀。</summary>
    public const string DefaultFilePrefix = "截图";

    /// <summary>历史默认最多保留多少张。</summary>
    public const int DefaultHistoryLimit = 100;

    /// <summary>历史上限允许范围。</summary>
    public const int MaxHistoryLimit = 2000;

    // ---------- 快捷键 ----------

    private bool _enableGlobalHotkeys = true;

    /// <summary>整个工具的全局快捷键总开关。</summary>
    public bool EnableGlobalHotkeys
    {
        get => _enableGlobalHotkeys;
        set => SetAndSave(ref _enableGlobalHotkeys, value);
    }

    private string _captureHotkey = "Ctrl+Shift+F1";

    /// <summary>打开框选截图的全局快捷键。</summary>
    public string CaptureHotkey
    {
        get => _captureHotkey;
        set => SetAndSave(ref _captureHotkey, value);
    }

    private bool _captureHotkeyEnabled = true;

    /// <summary>「截屏」这一条启用。</summary>
    public bool CaptureHotkeyEnabled
    {
        get => _captureHotkeyEnabled;
        set => SetAndSave(ref _captureHotkeyEnabled, value);
    }

    private string _captureCopyHotkey = "Ctrl+Shift+F2";

    /// <summary>截图后直接复制到剪贴板的全局快捷键。</summary>
    public string CaptureCopyHotkey
    {
        get => _captureCopyHotkey;
        set => SetAndSave(ref _captureCopyHotkey, value);
    }

    private bool _captureCopyHotkeyEnabled = true;

    /// <summary>「截屏并复制」这一条启用。</summary>
    public bool CaptureCopyHotkeyEnabled
    {
        get => _captureCopyHotkeyEnabled;
        set => SetAndSave(ref _captureCopyHotkeyEnabled, value);
    }

    private string _repeatHotkey = "Ctrl+Shift+F3";

    /// <summary>按上一次的框选区域直接截图的全局快捷键。</summary>
    public string RepeatHotkey
    {
        get => _repeatHotkey;
        set => SetAndSave(ref _repeatHotkey, value);
    }

    private bool _repeatHotkeyEnabled = true;

    /// <summary>「重复上次区域」这一条启用。</summary>
    public bool RepeatHotkeyEnabled
    {
        get => _repeatHotkeyEnabled;
        set => SetAndSave(ref _repeatHotkeyEnabled, value);
    }

    private string _pasteHotkey = "Ctrl+Shift+F4";

    /// <summary>把剪贴板里的图片贴到桌面上的全局快捷键。</summary>
    public string PasteHotkey
    {
        get => _pasteHotkey;
        set => SetAndSave(ref _pasteHotkey, value);
    }

    private bool _pasteHotkeyEnabled = true;

    /// <summary>「贴图」这一条启用。</summary>
    public bool PasteHotkeyEnabled
    {
        get => _pasteHotkeyEnabled;
        set => SetAndSave(ref _pasteHotkeyEnabled, value);
    }

    // ---------- 截图输出 ----------

    private bool _autoCopyOnConfirm = true;

    /// <summary>完成框选时（Enter / 双击 / 点“复制”）自动把成品写入剪贴板。</summary>
    public bool AutoCopyOnConfirm
    {
        get => _autoCopyOnConfirm;
        set => SetAndSave(ref _autoCopyOnConfirm, value);
    }

    private bool _autoSaveOnCapture;

    /// <summary>每次截图完成后自动按「输出文件夹 + 前缀」保存一份 PNG。</summary>
    public bool AutoSaveOnCapture
    {
        get => _autoSaveOnCapture;
        set => SetAndSave(ref _autoSaveOnCapture, value);
    }

    private string _saveFolder = "";

    /// <summary>输出文件夹（空 = 首次使用时用「图片\YuLiangToolkit 截图」）。</summary>
    public string SaveFolder
    {
        get => _saveFolder;
        set
        {
            SetAndSave(ref _saveFolder, value);
            OnPropertyChanged(nameof(EffectiveSaveFolder));
        }
    }

    private string _filePrefix = DefaultFilePrefix;

    /// <summary>输出文件名前缀（会自动追加日期时间和序号）。</summary>
    public string FilePrefix
    {
        get => _filePrefix;
        set => SetAndSave(ref _filePrefix, value);
    }

    private string _imageFormat = "png";

    /// <summary>输出格式：<c>png</c> / <c>jpg</c>。</summary>
    public string ImageFormat
    {
        get => _imageFormat;
        set => SetAndSave(ref _imageFormat, value);
    }

    private int _jpgQuality = 92;

    /// <summary>JPEG 质量（1~100，PNG 时忽略）。</summary>
    public int JpgQuality
    {
        get => _jpgQuality;
        set => SetAndSave(ref _jpgQuality, Math.Clamp(value, 1, 100));
    }

    private string _compressionMode = ScreenshotCompression.None;

    /// <summary>成品压缩档位：none / light / standard / high（非 none 时统一输出 JPG）。</summary>
    public string CompressionMode
    {
        get => _compressionMode;
        set => SetAndSave(ref _compressionMode, value is ScreenshotCompression.Light
            or ScreenshotCompression.Standard or ScreenshotCompression.High
            ? value : ScreenshotCompression.None);
    }

    [JsonIgnore]
    public bool CompressionEnabled => CompressionMode != ScreenshotCompression.None;

    private bool _openFolderAfterSave;

    /// <summary>保存后自动打开所在文件夹。</summary>
    public bool OpenFolderAfterSave
    {
        get => _openFolderAfterSave;
        set => SetAndSave(ref _openFolderAfterSave, value);
    }

    private string _customCaptureTarget = "region";

    /// <summary>「自定义截屏」的默认目标：<c>region</c> / <c>fullscreen</c> / <c>active-window</c>。</summary>
    public string CustomCaptureTarget
    {
        get => _customCaptureTarget;
        set => SetAndSave(ref _customCaptureTarget, value);
    }

    private int _customCaptureDelaySeconds;

    /// <summary>「自定义截屏」的延迟秒数（0~30）。</summary>
    public int CustomCaptureDelaySeconds
    {
        get => _customCaptureDelaySeconds;
        set => SetAndSave(ref _customCaptureDelaySeconds, Math.Clamp(value, 0, 30));
    }

    private int _lastRegionX;
    private int _lastRegionY;
    private int _lastRegionWidth;
    private int _lastRegionHeight;

    /// <summary>上一次成功截图的物理像素区域（用于「重复上次区域」）。</summary>
    public int LastRegionX
    {
        get => _lastRegionX;
        set => SetAndSave(ref _lastRegionX, value);
    }

    /// <summary>见 <see cref="LastRegionX"/>。</summary>
    public int LastRegionY
    {
        get => _lastRegionY;
        set => SetAndSave(ref _lastRegionY, value);
    }

    /// <summary>见 <see cref="LastRegionX"/>。</summary>
    public int LastRegionWidth
    {
        get => _lastRegionWidth;
        set => SetAndSave(ref _lastRegionWidth, value);
    }

    /// <summary>见 <see cref="LastRegionX"/>。</summary>
    public int LastRegionHeight
    {
        get => _lastRegionHeight;
        set => SetAndSave(ref _lastRegionHeight, value);
    }

    // ---------- 截图 / 编辑默认值 ----------

    private bool _magnifierEnabled = true;

    /// <summary>截图时显示放大镜 / 取色信息。</summary>
    public bool MagnifierEnabled
    {
        get => _magnifierEnabled;
        set => SetAndSave(ref _magnifierEnabled, value);
    }

    private bool _showSizeBadge = true;

    /// <summary>框选时显示尺寸标签。</summary>
    public bool ShowSizeBadge
    {
        get => _showSizeBadge;
        set => SetAndSave(ref _showSizeBadge, value);
    }

    private string _defaultColor = "#FFE53935";

    /// <summary>标注默认颜色（#AARRGGBB / #RRGGBB）。</summary>
    public string DefaultColor
    {
        get => _defaultColor;
        set => SetAndSave(ref _defaultColor, value);
    }

    private double _defaultThickness = 3;

    /// <summary>标注默认线宽。</summary>
    public double DefaultThickness
    {
        get => _defaultThickness;
        set => SetAndSave(ref _defaultThickness, value);
    }

    private bool _defaultFill;

    /// <summary>矩形 / 椭圆默认填充半透明色。</summary>
    public bool DefaultFill
    {
        get => _defaultFill;
        set => SetAndSave(ref _defaultFill, value);
    }

    private string _defaultTool = "select";

    /// <summary>打开截图后默认选中的标注工具（`select` / `rectangle` / `ellipse` / `arrow` / `pen` / `text` / `mosaic`）。</summary>
    public string DefaultTool
    {
        get => _defaultTool;
        set => SetAndSave(ref _defaultTool, value);
    }

    private bool _showCursorHint = true;

    /// <summary>框选前显示操作提示。</summary>
    public bool ShowCursorHint
    {
        get => _showCursorHint;
        set => SetAndSave(ref _showCursorHint, value);
    }

    // ---------- 历史 ----------

    private bool _keepHistory = true;

    /// <summary>每次截图保留一份缩略图历史（供页面查看 / 重新复制 / 重新贴图）。</summary>
    public bool KeepHistory
    {
        get => _keepHistory;
        set => SetAndSave(ref _keepHistory, value);
    }

    private int _historyLimit = DefaultHistoryLimit;

    /// <summary>历史最多保留多少张（0 = 不保留）。</summary>
    public int HistoryLimit
    {
        get => _historyLimit;
        set => SetAndSave(ref _historyLimit, Math.Clamp(value, 0, MaxHistoryLimit));
    }

    // ---------- 贴图 ----------

    private bool _pinTopmost = true;

    /// <summary>新贴到桌面的图是否强制置顶。</summary>
    public bool PinTopmost
    {
        get => _pinTopmost;
        set => SetAndSave(ref _pinTopmost, value);
    }

    private List<string> _pinGroupNames = new() { "默认", "参考图", "对照图" };

    /// <summary>贴图组名称（固定 3 组，可在设置里改）。</summary>
    public List<string> PinGroupNames
    {
        get => _pinGroupNames;
        set => SetAndSave(ref _pinGroupNames, value is { Count: > 0 } ? value : new List<string> { "默认" });
    }

    private int _currentPinGroup;

    /// <summary>当前贴图组下标。</summary>
    public int CurrentPinGroup
    {
        get => _currentPinGroup;
        set => SetAndSave(ref _currentPinGroup, Math.Max(0, value));
    }

    /// <summary>截图工具创建过的贴图记录（路径 → 组）。</summary>
    public List<ScreenshotPinRecord> PinRecords { get; set; } = new();

    // ---------- 计算属性 ----------

    /// <summary>生效的输出目录：配置为空时落到「图片\YuLiangToolkit 截图」。</summary>
    [JsonIgnore]
    public string EffectiveSaveFolder =>
        string.IsNullOrWhiteSpace(SaveFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "YuLiangToolkit 截图")
            : SaveFolder.Trim();

    /// <summary>历史保留上限的净洁值。</summary>
    [JsonIgnore]
    public int EffectiveHistoryLimit => Math.Clamp(HistoryLimit, 0, MaxHistoryLimit);

    /// <summary>当前贴图组下标（防止手改配置超过组数）。</summary>
    [JsonIgnore]
    public int EffectivePinGroup
    {
        get
        {
            var count = PinGroupNames.Count;
            if (count <= 0) return 0;
            var value = CurrentPinGroup % count;
            return value < 0 ? value + count : value;
        }
    }

    /// <summary>当前贴图组名称。</summary>
    [JsonIgnore]
    public string CurrentPinGroupName => PinGroupNames.Count == 0
        ? "默认"
        : PinGroupNames[Math.Clamp(EffectivePinGroup, 0, PinGroupNames.Count - 1)];

    /// <summary>上一次的物理像素选区；从没截过图时为空。</summary>
    [JsonIgnore]
    public ScreenshotPixelRect LastRegion => LastRegionWidth > 0 && LastRegionHeight > 0
        ? new ScreenshotPixelRect(LastRegionX, LastRegionY, LastRegionWidth, LastRegionHeight)
        : ScreenshotPixelRect.Empty;

    /// <summary>把一个路径归到贴图组（找不到时算第 0 组）。</summary>
    public int GetPinGroup(string? path)
    {
        var full = NormalizePath(path);
        if (full.Length == 0) return 0;
        PinRecords ??= new List<ScreenshotPinRecord>();
        var record = PinRecords.FirstOrDefault(r =>
            string.Equals(NormalizePath(r.Path), full, StringComparison.OrdinalIgnoreCase));
        return record is null ? 0 : Math.Clamp(record.GroupIndex, 0, Math.Max(0, PinGroupNames.Count - 1));
    }

    /// <summary>是不是截图工具自己创建的贴图（不碰「屏幕贴纸」工具自己的图片）。</summary>
    public bool IsScreenshotPin(string? path)
    {
        var full = NormalizePath(path);
        if (full.Length == 0) return false;
        PinRecords ??= new List<ScreenshotPinRecord>();
        return PinRecords.Any(r =>
            string.Equals(NormalizePath(r.Path), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>给一张贴图登记组（同路径替换旧记录）。</summary>
    public void RememberPin(string? path, int groupIndex)
    {
        var full = NormalizePath(path);
        if (full.Length == 0) return;

        PinRecords ??= new List<ScreenshotPinRecord>();
        PinRecords.RemoveAll(r => string.Equals(NormalizePath(r.Path), full, StringComparison.OrdinalIgnoreCase));
        PinRecords.Add(new ScreenshotPinRecord
        {
            Path = full,
            GroupIndex = Math.Clamp(groupIndex, 0, Math.Max(0, PinGroupNames.Count - 1)),
            CreatedUtc = DateTime.UtcNow,
        });
    }

    /// <summary>记下最近一次成功截图的区域。</summary>
    public void RememberRegion(ScreenshotPixelRect region)
    {
        if (region.IsEmpty) return;
        LastRegionX = region.X;
        LastRegionY = region.Y;
        LastRegionWidth = region.Width;
        LastRegionHeight = region.Height;
    }

    /// <summary>规范化路径（空 / 非法返回空串）。</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path.Trim()); }
        catch { return ""; }
    }
}
