using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.OsuMania;

/// <summary>
/// 「osu!mania 谱面编辑器」的配置（<c>Config\osu-mania-editor\config.json</c>，改一下自动保存）。
///
/// <para>只存**编辑器偏好**，绝不存谱面内容：谱面永远以磁盘上的 .osu 为准。</para>
/// </summary>
public sealed class OsuManiaEditorConfig : ToolConfig
{
    /// <summary>数据 / 配置文件夹名（同时是 <c>ITool.Id</c>）。</summary>
    public const string ToolId = "osu-mania-editor";

    /// <summary>上次打开谱面的目录（文件对话框起点）。</summary>
    public string LastFolder { get; set; } = "";

    /// <summary>网格细分（每拍几格）：1 / 2 / 3 / 4 / 6 / 8 / 12 / 16。</summary>
    public int GridDivisor { get; set; } = 4;

    /// <summary>是否吸附网格。</summary>
    public bool SnapToGrid { get; set; } = true;

    /// <summary>音量 0~1。</summary>
    public double Volume { get; set; } = 0.8;

    /// <summary>播放时是否自动跟随播放头滚动时间轴。</summary>
    public bool FollowPlayhead { get; set; } = true;

    /// <summary>上次用的编辑工具（0 = 选择 / 1 = 单点 / 2 = 长条）。</summary>
    public int Tool { get; set; } = 1;

    /// <summary>旧字段：只表示「是不是长条工具」。保留是为了老配置还能读。</summary>
    public bool LongNoteTool { get; set; }

    /// <summary>时间轴默认缩放（每毫秒像素）。</summary>
    public double PxPerMs { get; set; } = 0.15;

    /// <summary>
    /// 音频偏移（毫秒）。**正数 = 播放头往后推**（音符看着比音乐早、就填正数把画面压后）。
    ///
    /// <para>每条声卡链路的缓冲延迟都不一样（本机实测 GetPosition 落后真实时间约 64ms），
    /// 用户耳朵最准：觉得音符整体和音乐错开，就用这个对齐。</para>
    /// </summary>
    public double AudioOffsetMs { get; set; }

    /// <summary>是否开启自动保存（默认开）。</summary>
    public bool AutoSaveEnabled { get; set; } = true;

    /// <summary>
    /// Note 浏览区（轨道）宽度占可用宽度的比例（0.2~1.0，默认 1.0 = 铺满）。
    ///
    /// <para>4K 谱面铺满宽屏时每列能有 280+ 像素，note 看起来又扁又宽；调小这个值能把轨道
    /// 收窄并居中，更像 osu! 编辑器的样子。窗口内与全屏各自按自己的可用宽度算。</para>
    /// </summary>
    public double NoteAreaRatio { get; set; } = 1.0;

    /// <summary>
    /// Note 浏览区的水平对齐（0 = 贴左 / 1 = 居中 / 2 = 贴右）。
    /// 只在「宽度不足铺满」时才看得出来：决定空白落在哪边。
    /// </summary>
    public int NoteAreaAlign { get; set; }

    /// <summary>是否显示谱面背景图（<c>[Events]</c> 里那张）；默认开。</summary>
    public bool ShowBackgroundImage { get; set; } = true;

    /// <summary>背景图不透明度（0.05~1.0，默认 0.75）。</summary>
    public double BackgroundOpacity { get; set; } = 0.75;

    /// <summary>Note 浏览区底色的 ARGB（默认半透明黑，见 <see cref="DefaultLaneBackgroundArgb"/>）。</summary>
    public int LaneBackgroundArgb { get; set; } = DefaultLaneBackgroundArgbInt;

    /// <summary>Note 浏览区默认底色：黑 + 55% 不透明。</summary>
    public const byte DefaultLaneBackgroundAlpha = 140;   // 0x8C

    /// <summary>默认底色的 ARGB 十六进制文字（界面显示 / 恢复默认用）。</summary>
    public const string DefaultLaneBackgroundHex = "#8C000000";

    /// <summary>默认底色的有符号 ARGB（存 JSON 用）。</summary>
    public const int DefaultLaneBackgroundArgbInt = unchecked((int)0x8C000000);

    /// <summary>Note 浏览区底色默认值（恢复默认用）。</summary>
    public static System.Windows.Media.Color DefaultLaneBackgroundColor
        => System.Windows.Media.Color.FromArgb(DefaultLaneBackgroundAlpha, 0, 0, 0);

    /// <summary>
    /// 无操作多少秒后自动保存（默认 10 秒，范围 2~600）。
    /// 「无操作」包括摆放 / 拖动 / 删除 note、滚时间轴、缩放、移动鼠标等（鼠标停在页面上就算在操作）。
    /// </summary>
    public int AutoSaveIdleSeconds { get; set; } = 10;

    /// <summary>自动保存间隔的合法范围（秒）。</summary>
    public const int MinAutoSaveSeconds = 2;

    /// <summary>自动保存间隔的上限（秒）。</summary>
    public const int MaxAutoSaveSeconds = 600;

    /// <summary>节拍细分下拉的可选值。</summary>
    public static readonly int[] DivisorOptions = { 1, 2, 3, 4, 6, 8, 12, 16 };
}
