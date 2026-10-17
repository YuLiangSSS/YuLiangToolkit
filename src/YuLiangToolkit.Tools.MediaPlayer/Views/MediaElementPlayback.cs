using System.Windows;
using System.Windows.Controls;

namespace YuLiangToolkit.Tools.MediaPlayer.Views;

/// <summary>
/// 真播放器：把 WPF 的 <see cref="MediaElement"/> 包成 <see cref="IMediaPlayback"/>。
///
/// <para>只做「转发 + 把几个 WPF 的脾气磨平」，不含任何业务判断：</para>
/// <list type="bullet">
///   <item><c>LoadedBehavior</c> / <c>UnloadedBehavior</c> 都设成 <c>Manual</c> ——
///   否则 <c>MediaElement</c> 自己会在加载时开始播、卸载时自己关掉，跟 ViewModel 的命令打架；</item>
///   <item><c>ScrubbingEnabled = true</c> —— 暂停状态下拖动进度条也能看到画面（不然画面停在原地）；</item>
///   <item>时长要等 <see cref="MediaElement.MediaOpened"/> 之后才可信（之前 <c>NaturalDuration</c> 是空的）；</item>
///   <item><c>Position</c> 超出范围会抛，所以 <see cref="Seek"/> 先夹一遍。</item>
/// </list>
///
/// <para>⚠ 能不能解码取决于系统装了哪些 Media Foundation 编解码器（flac / mkv 之类在老系统上可能失败），
/// 失败会走 <c>MediaFailed</c> → <see cref="Failed"/>，界面上会显示一句人话。</para>
/// </summary>
internal sealed class MediaElementPlayback : IMediaPlayback
{
    private readonly MediaElement _media;
    private bool _playing;

    public MediaElementPlayback(MediaElement media)
    {
        _media = media;
        _media.LoadedBehavior = MediaState.Manual;
        _media.UnloadedBehavior = MediaState.Manual;
        _media.ScrubbingEnabled = true;

        _media.MediaOpened += (_, _) => Opened?.Invoke(this, EventArgs.Empty);
        _media.MediaEnded += (_, _) =>
        {
            _playing = false;
            Ended?.Invoke(this, EventArgs.Empty);
        };
        _media.MediaFailed += (_, e) =>
        {
            _playing = false;
            Failed?.Invoke(this, DescribeFailure(e));
        };
    }

    public string? CurrentPath { get; private set; }

    public bool IsLoaded => !string.IsNullOrEmpty(CurrentPath);

    public bool HasVideo => _media.HasVideo;

    public bool IsPlaying => _playing;

    public TimeSpan Position => _media.Position;

    public TimeSpan Duration => _media.NaturalDuration.HasTimeSpan
        ? _media.NaturalDuration.TimeSpan
        : TimeSpan.Zero;

    public double Volume
    {
        get => _media.Volume;
        set => _media.Volume = Math.Clamp(value, 0, 1);
    }

    public void Load(string path, bool autoPlay)
    {
        try
        {
            _media.Stop();
            CurrentPath = path;
            _playing = autoPlay;
            _media.Source = new Uri(path, UriKind.Absolute);
            if (autoPlay) _media.Play();
        }
        catch (Exception ex)
        {
            _playing = false;
            Failed?.Invoke(this, $"打不开这个文件：{ex.Message}");
        }
    }

    public void Play()
    {
        try
        {
            _media.Play();
            _playing = true;
        }
        catch (Exception ex)
        {
            _playing = false;
            Failed?.Invoke(this, $"播放失败：{ex.Message}");
        }
    }

    public void Pause()
    {
        try { _media.Pause(); } catch { /* 忽略：没加载时暂停没意义 */ }
        _playing = false;
    }

    public void Stop()
    {
        try { _media.Stop(); } catch { /* 忽略 */ }
        _playing = false;
    }

    public void Seek(TimeSpan position)
    {
        try
        {
            _media.Position = MediaTime.Clamp(position, Duration);
        }
        catch
        {
            // 还没打开完 / 位置非法：忽略这一次跳转（下一次计时器 tick 会纠正界面）
        }
    }

    public void Close()
    {
        try
        {
            _media.Stop();
            _media.Close();
        }
        catch
        {
            // 卸载流程里忽略
        }

        CurrentPath = null;
        _playing = false;
    }

    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler<string>? Failed;

    /// <summary>把 <c>MediaFailed</c> 翻成一句人话（原始异常信息通常只有 HRESULT）。</summary>
    private static string DescribeFailure(ExceptionRoutedEventArgs e)
    {
        var reason = e.ErrorException?.Message;
        return string.IsNullOrWhiteSpace(reason)
            ? "系统解不了这个格式（可能缺少解码器，或文件损坏）"
            : $"{reason}（也可能是系统缺少这个格式的解码器）";
    }
}
