using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 「视频 / 音乐播放器」的强类型配置 → <c>Config\media-player\config.json</c>。
///
/// <para>分三块：**读取**（打开文件夹时收哪些文件、要不要进子文件夹、覆盖还是追加）、
/// **播放**（音量、播放顺序、列表排序、快进快退步长）、**恢复**（记住上次的列表与位置）。</para>
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

    /// <summary>播放列表怎么排：默认顺序 / 名称 / 修改日期 / 文件大小（只改显示与切歌顺序，不动文件）。</summary>
    public PlaylistSortKey PlaylistSort { get; set; } = PlaylistSortKey.Added;

    /// <summary>排序方向：false = 正序（默认），true = 反序。</summary>
    public bool PlaylistSortDescending { get; set; }

    /// <summary>进度条左边那个时间怎么显示：正向计时（默认）/ 剩余倒计时（还差多少播完）。</summary>
    public TimeDisplayMode TimeDisplayMode { get; set; } = TimeDisplayMode.Elapsed;

    /// <summary>快进 / 快退一步多少秒（**0.1 秒精度**，0.1 ~ 600）。</summary>
    public double SeekStepSeconds { get; set; } = 5.0;

    // ---------- 恢复上次 ----------

    /// <summary>下次打开工具时把上次的播放列表捞回来（列表存在 <c>Data\media-player\playlist.json</c>）。</summary>
    public bool RestorePlaylist { get; set; } = true;

    /// <summary>捞回列表时顺便恢复上次听到的那一首（只选中 + 记住位置，**不自动出声**）。</summary>
    public bool ResumeLastPlayed { get; set; } = true;

    /// <summary>
    /// 恢复上次听到的位置之后**直接开始播放**（默认关：打开工具不会突然出声）。
    /// <para>两种恢复都管：① 工具启动恢复整个列表；② 重新打开某个文件夹时恢复那个文件夹上次听到的位置
    /// （每个文件夹分别记，见 <see cref="MediaFolderResumeState"/>）。</para>
    /// </summary>
    public bool AutoResumePlayback { get; set; }

    // ---------- 后台播放 ----------

    /// <summary>
    /// 切到别的工具 / 回主页时**继续播放**（并在外壳左下角显示一个小迷你播放条）。
    /// 关掉就是老行为：切走即停。
    /// </summary>
    public bool BackgroundPlayback { get; set; } = true;

    // ---------- 全局快捷键（在别的窗口也能切歌 / 暂停） ----------
    //
    // 三条：上一首 / 下一首 / 播放暂停。每条存「组合文本 + 第二步文本 + 启用位」，
    // 形态（单步 / 分步）由第二步是否为空决定；组合文本的格式见 Core/Input/KeyCombo.cs。
    // 默认给「Ctrl+Alt+方向键 / 空格」，避开系统里常见的单键热键，用户可在主页设置 → 快捷键里改。

    private string _shortcutPrevious = "Ctrl+Alt+Left";
    /// <summary>上一首快捷键（单段组合，或分步的第一步）。</summary>
    public string ShortcutPrevious
    {
        get => _shortcutPrevious;
        set => Set(ref _shortcutPrevious, value ?? "");
    }

    private string _shortcutPreviousSecond = "";
    /// <summary>分步时上一首快捷键的第二步（空 = 单步）。</summary>
    public string ShortcutPreviousSecond
    {
        get => _shortcutPreviousSecond;
        set => Set(ref _shortcutPreviousSecond, value ?? "");
    }

    private bool _shortcutPreviousEnabled = true;
    /// <summary>「上一首」快捷键启用。</summary>
    public bool ShortcutPreviousEnabled
    {
        get => _shortcutPreviousEnabled;
        set => Set(ref _shortcutPreviousEnabled, value);
    }

    private string _shortcutNext = "Ctrl+Alt+Right";
    /// <summary>下一首快捷键（单段组合，或分步的第一步）。</summary>
    public string ShortcutNext
    {
        get => _shortcutNext;
        set => Set(ref _shortcutNext, value ?? "");
    }

    private string _shortcutNextSecond = "";
    /// <summary>分步时下一首快捷键的第二步（空 = 单步）。</summary>
    public string ShortcutNextSecond
    {
        get => _shortcutNextSecond;
        set => Set(ref _shortcutNextSecond, value ?? "");
    }

    private bool _shortcutNextEnabled = true;
    /// <summary>「下一首」快捷键启用。</summary>
    public bool ShortcutNextEnabled
    {
        get => _shortcutNextEnabled;
        set => Set(ref _shortcutNextEnabled, value);
    }

    private string _shortcutPlayPause = "Ctrl+Alt+Space";
    /// <summary>播放 / 暂停快捷键（单段组合，或分步的第一步）。</summary>
    public string ShortcutPlayPause
    {
        get => _shortcutPlayPause;
        set => Set(ref _shortcutPlayPause, value ?? "");
    }

    private string _shortcutPlayPauseSecond = "";
    /// <summary>分步时播放 / 暂停快捷键的第二步（空 = 单步）。</summary>
    public string ShortcutPlayPauseSecond
    {
        get => _shortcutPlayPauseSecond;
        set => Set(ref _shortcutPlayPauseSecond, value ?? "");
    }

    private bool _shortcutPlayPauseEnabled = true;
    /// <summary>「播放 / 暂停」快捷键启用。</summary>
    public bool ShortcutPlayPauseEnabled
    {
        get => _shortcutPlayPauseEnabled;
        set => Set(ref _shortcutPlayPauseEnabled, value);
    }

    private bool _shortcutsEnabled = true;
    /// <summary>这个工具快捷键的总开关（关掉 = 三条全部不注册，各自状态保留）。</summary>
    public bool ShortcutsEnabled
    {
        get => _shortcutsEnabled;
        set => Set(ref _shortcutsEnabled, value);
    }

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
