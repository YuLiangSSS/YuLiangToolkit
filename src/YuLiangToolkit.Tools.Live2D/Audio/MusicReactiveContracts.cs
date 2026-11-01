namespace YuLiangToolkit.Tools.Live2D.Audio;

/// <summary>
/// 当前播放内容的类型（由 <c>MusicAnalyzer</c> 用「多特征融合 + 时序滞后」判定，不是简单音量阈值）。
/// </summary>
public enum MusicContentKind
{
    /// <summary>还没分析出来 / 数据不足。</summary>
    Unknown = 0,

    /// <summary>没有在播放（或静音）。</summary>
    Silent = 1,

    /// <summary>纯音乐 / 无歌词：不跟唱，只轻微摇晃。</summary>
    Instrumental = 2,

    /// <summary>带歌词的歌：跟唱（嘴型）+ 摇晃。</summary>
    VocalSong = 3,
}

/// <summary>
/// 一帧音频分析结果（不可变快照；由音频线程发布，UI / 运行时只读）。
/// 所有 0~1 的值都已经过平滑 / 归一化，可直接驱动参数。
/// </summary>
public sealed class MusicAnalysisSnapshot
{
    /// <summary>发布时刻（<see cref="Environment.TickCount64"/>，毫秒）。</summary>
    public long TimestampMs { get; init; }

    /// <summary>现在是否检测到有声音在播（带 500ms 挂起，避免句间空隙闪光）。</summary>
    public bool IsPlaying { get; init; }

    /// <summary>内容类型（自动判定结果）。</summary>
    public MusicContentKind Kind { get; init; }

    /// <summary>分类置信度（0~1，越大越确定）。</summary>
    public double Confidence { get; init; }

    /// <summary>人声存在的原始融合分数（0~1，调试 / 自检用）。</summary>
    public double VocalScore { get; init; }

    /// <summary>响度 RMS（0~1，对数压缩 + 平滑）。</summary>
    public double Rms { get; init; }

    /// <summary>低频能量（60~200Hz，0~1）。</summary>
    public double BassEnergy { get; init; }

    /// <summary>人声频带能量（300~3400Hz，0~1）。</summary>
    public double VoiceEnergy { get; init; }

    /// <summary>唇形张开度（0~1，只对人声频带包络做包络跟踪；纯音乐时也照常给，由运行时决定用不用）。</summary>
    public double MouthOpen { get; init; }

    /// <summary>嘴型明暗（-1~1，来自人声频带频谱质心，让嘴型别只会一张一合）。</summary>
    public double MouthForm { get; init; }

    /// <summary>当前节拍相位（0~1，循环；身体摇晃按它做正弦）。</summary>
    public double BeatPhase { get; init; }

    /// <summary>估计的 BPM（0 = 还没估出来）。</summary>
    public double Bpm { get; init; }

    /// <summary>节拍强度（0~1，强拍位置更大；用来做幅度包络）。</summary>
    public double BeatStrength { get; init; }

    /// <summary>基频是否可信（YIN 检测成功且能量够）。</summary>
    public bool PitchValid { get; init; }

    /// <summary>基频 Hz（<see cref="PitchValid"/> = false 时无意义）。</summary>
    public double PitchHz { get; init; }

    /// <summary>立体声中心声道在「人声频带」里的能量占比（0~1）——流行歌曲主唱通常在中间。</summary>
    public double CenterVoiceRatio { get; init; }

    /// <summary>音节 / 颤音调制强度（0~1，4~8Hz 包络起伏；念字比纯乐器更明显）。</summary>
    public double SyllabicModulation { get; init; }

    /// <summary>全静音 / 无数据时的快照。</summary>
    public static MusicAnalysisSnapshot Silent(long timestampMs) => new()
    {
        TimestampMs = timestampMs,
        IsPlaying = false,
        Kind = MusicContentKind.Silent,
        Confidence = 0,
    };
}

/// <summary>
/// 音乐响应的数据源（<c>Audio/MusicReactiveSource</c> 实现：WASAPI loopback 采集系统正在播放的声音，
/// 交给 <c>MusicAnalyzer</c> 分析）。运行时每帧读 <see cref="Latest"/>。
/// </summary>
public interface IMusicReactiveProvider
{
    /// <summary>最新一帧分析结果（没有数据时为 null）。</summary>
    MusicAnalysisSnapshot? Latest { get; }

    /// <summary>数据源是否在运行（采集线程已启动）。</summary>
    bool IsRunning { get; }

    /// <summary>最近一次错误（空 = 没有错误）。</summary>
    string? LastError { get; }

    /// <summary>SMTC 报告的当前媒体标题（没有则空串）。</summary>
    string MediaTitle { get; }

    /// <summary>SMTC 报告的歌手 / 作者（没有则空串）。</summary>
    string MediaArtist { get; }

    /// <summary>SMTC 报告的播放器应用（没有则空串）。</summary>
    string MediaApp { get; }

    /// <summary>状态变化（启动 / 停止 / 出错 / 换歌）。</summary>
    event EventHandler? StateChanged;
}
