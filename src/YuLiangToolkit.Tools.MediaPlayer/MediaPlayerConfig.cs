using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 「视频 / 音乐播放器」的强类型配置 → <c>Config\media-player\config.json</c>。
///
/// <para>分三块：**读取**（打开文件夹时收哪些文件、要不要进子文件夹、覆盖还是追加）、
/// **播放**（音量、播放顺序、快进快退步长）、**恢复**（记住上次的列表与位置）。</para>
///
/// <para>⚠ 「播放顺序」这类枚举按字符串存（<c>"RepeatOne"</c>），加减成员不会让老配置错位；
/// 带 setter 的计算属性都标了 <c>[JsonIgnore]</c>（见 AGENTS.md）。</para>
/// </summary>
public sealed class MediaPlayerConfig : ToolConfig
{
    /// <summary>功能 Id（= <c>Config\media-player\</c> 与 <c>Data\media-player\</c> 的文件夹名）。</summary>
    public const string ToolId = "media-player";

    // ---------- 读取（打开文件夹时怎么收文件） ----------

    /// <summary>收哪些：音频 + 视频 / 仅音频 / 仅视频（默认都收）。</summary>
    public MediaIncludeMode IncludeMode { get; set; } = MediaIncludeMode.AudioAndVideo;

    /// <summary>要不要连子文件夹一起读（默认读：音乐一般按专辑分文件夹）。</summary>
    public bool ScanRecursive { get; set; } = true;

    /// <summary>再打开一个文件夹时：true = 只往列表后面追加（默认），false = 先清空再装。</summary>
    public bool AppendWhenOpeningFolder { get; set; } = true;

    /// <summary>上次打开过的文件夹（界面上的路径提示 + 下次对话框从这里开始）。</summary>
    public string LastFolder { get; set; } = "";

    // ---------- 播放 ----------

    /// <summary>音量 0 ~ 100。</summary>
    public double Volume { get; set; } = 80;

    /// <summary>播放顺序：列表循环 / 单曲循环 / 随机播放。</summary>
    public PlayOrderMode PlayOrder { get; set; } = PlayOrderMode.LoopAll;

    /// <summary>进度条左边那个时间怎么显示：正向计时（默认）/ 剩余倒计时（还差多少播完）。</summary>
    public TimeDisplayMode TimeDisplayMode { get; set; } = TimeDisplayMode.Elapsed;

    /// <summary>快进 / 快退一步多少秒（**0.1 秒精度**，0.1 ~ 600）。</summary>
    public double SeekStepSeconds { get; set; } = 5.0;

    // ---------- 恢复上次 ----------

    /// <summary>下次打开工具时把上次的播放列表捞回来（列表存在 <c>Data\media-player\playlist.json</c>）。</summary>
    public bool RestorePlaylist { get; set; } = true;

    /// <summary>捞回列表时顺便恢复上次听到的那一首（只选中 + 记住位置，**不自动出声**）。</summary>
    public bool ResumeLastPlayed { get; set; } = true;

    // ---------- 后台播放 ----------

    /// <summary>
    /// 切到别的工具 / 回主页时**继续播放**（并在外壳左下角显示一个小迷你播放条）。
    /// 关掉就是老行为：切走即停。
    /// </summary>
    public bool BackgroundPlayback { get; set; } = true;

    // ---------- 计算属性（只读 → 不会被写进 JSON） ----------

    /// <summary>实际生效的步长：规整到 0.1 秒精度并夹在允许范围内。</summary>
    [JsonIgnore]
    public double SeekStepEffective => MediaTime.RoundStep(SeekStepSeconds);

    /// <summary>实际生效的音量（0 ~ 100）。</summary>
    [JsonIgnore]
    public double VolumeEffective => double.IsNaN(Volume) ? 80 : Math.Clamp(Math.Round(Volume), 0, 100);

    /// <summary>「5.0 秒」这种给人看的文本。</summary>
    [JsonIgnore]
    public string SeekStepText => $"{SeekStepEffective:0.0} 秒";

    /// <summary>丢在设置页里的那一行说明（哪些扩展名会被收下）。</summary>
    [JsonIgnore]
    public string IncludeModeHint => MediaTypes.DescribeExtensions(IncludeMode);
}
