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

    /// <summary>左键工具：true = 长条，false = 普通键。</summary>
    public bool LongNoteTool { get; set; }

    /// <summary>时间轴默认缩放（每毫秒像素）。</summary>
    public double PxPerMs { get; set; } = 0.15;

    /// <summary>
    /// 音频偏移（毫秒）。**正数 = 播放头往后推**（音频听起来晚了就填正数）。
    ///
    /// <para>每条声卡链路的缓冲延迟都不一样（实测本机 25×3 缓冲下 GetPosition 落后真实时间
    /// 约 64ms），用户耳朵最准：觉得音符整体比音乐早 / 晚，就用这个对齐。</para>
    /// </summary>
    public double AudioOffsetMs { get; set; }

    /// <summary>节拍细分下拉的可选值。</summary>
    public static readonly int[] DivisorOptions = { 1, 2, 3, 4, 6, 8, 12, 16 };
}
