namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 播放器抽象：ViewModel 只跟它打交道，**不知道 WPF 的 <c>MediaElement</c> 存在**。
///
/// <para>为什么要这层：<c>MediaElement</c> 是 <see cref="System.Windows.FrameworkElement"/>，
/// 必须活在界面线程上、还要真的解码音频，无头自检根本造不出来。
/// 抽成接口后，界面里的 <c>MediaElementPlayback</c> 是真实现，
/// 自检塞一个 <c>FakeMediaPlayback</c> 就能把「播放 / 暂停 / 快进快退 / 播完自动下一首」
/// 整条链路跑一遍（见 AGENTS.md 2.14）。</para>
///
/// <para>约定：<see cref="Opened"/> / <see cref="Ended"/> / <see cref="Failed"/> 都在**界面线程**上触发
/// （<c>MediaElement</c> 的事件本来就在 UI 线程，实现者别自己开线程）。</para>
/// </summary>
public interface IMediaPlayback
{
    /// <summary>当前已装进播放器的文件路径（空串 / null = 什么都没装）。</summary>
    string? CurrentPath { get; }

    /// <summary>播放器里有没有东西（有才能播放 / 快进快退）。</summary>
    bool IsLoaded { get; }

    /// <summary>这个文件有画面吗（决定中间显示视频还是封面占位）。</summary>
    bool HasVideo { get; }

    /// <summary>是不是正在放（暂停 / 停止时为 false）。</summary>
    bool IsPlaying { get; }

    /// <summary>当前位置。</summary>
    TimeSpan Position { get; }

    /// <summary>总时长（还没打开完时是 <see cref="TimeSpan.Zero"/>）。</summary>
    TimeSpan Duration { get; }

    /// <summary>音量 0.0 ~ 1.0。</summary>
    double Volume { get; set; }

    /// <summary>装一个文件。<paramref name="autoPlay"/> 为 true 时打开成功就自动播。</summary>
    void Load(string path, bool autoPlay);

    /// <summary>继续播放。</summary>
    void Play();

    /// <summary>暂停（位置保留）。</summary>
    void Pause();

    /// <summary>停止（位置回到 0）。</summary>
    void Stop();

    /// <summary>跳到指定位置（调用方负责夹好范围，见 <see cref="MediaTime"/>）。</summary>
    void Seek(TimeSpan position);

    /// <summary>彻底放开当前文件（切换工具 / 界面卸载时用）。</summary>
    void Close();

    /// <summary>文件打开成功（此时 <see cref="Duration"/> / <see cref="HasVideo"/> 才可信）。</summary>
    event EventHandler? Opened;

    /// <summary>放完了。</summary>
    event EventHandler? Ended;

    /// <summary>打不开 / 解码失败（参数是给人看的失败原因）。</summary>
    event EventHandler<string>? Failed;
}
