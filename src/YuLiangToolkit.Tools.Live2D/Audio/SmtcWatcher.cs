using Windows.Media.Control;

namespace YuLiangToolkit.Tools.Live2D.Audio;

/// <summary>
/// 读取 Windows「媒体会话」（SMTC，System Media Transport Controls）里当前正在播放的
/// 歌名 / 歌手 / 播放器应用 + 播放状态，给音乐卡片显示用。
///
/// <para>只是**辅助信息**：音乐跟唱 / 律动的判定完全来自音频本身（WASAPI 回环），
/// 所以 SMTC 不可用、或播放器不支持 SMTC 时功能照常工作，只是界面上看不到歌名。</para>
/// </summary>
public sealed class SmtcWatcher : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Thread? _thread;
    private volatile bool _stopping;
    private string _title = "";
    private string _artist = "";
    private string _app = "";
    private bool _isPlaying;

    /// <summary>歌名（没有则空串）。</summary>
    public string Title => _title;

    /// <summary>歌手 / 作者（没有则空串）。</summary>
    public string Artist => _artist;

    /// <summary>播放器应用 id（例如 <c>Spotify.exe</c>；没有则空串）。</summary>
    public string App => _app;

    /// <summary>SMTC 报告的播放状态（只用于卡片显示；音频是否在响由回环数据判断）。</summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>媒体信息 / 播放状态变化时触发（后台线程）。</summary>
    public event EventHandler? Changed;

    /// <summary>启动后台轮询（失败也不影响音频分析，只是没有媒体信息）。</summary>
    public void Start()
    {
        if (_thread is { IsAlive: true }) return;
        _stopping = false;
        _thread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "live2d-smtc-watcher",
        };
        _thread.Start();
    }

    private void PollLoop()
    {
        try
        {
            _manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            return; // 旧系统 / 被策略禁用：忽略
        }

        while (!_stopping)
        {
            try
            {
                PollOnce();
            }
            catch
            {
                // 播放器退出 / 会话变化时可能抛异常，忽略并继续
            }

            for (var i = 0; i < 20 && !_stopping; i++) Thread.Sleep(100); // 2 秒一次
        }
    }

    private void PollOnce()
    {
        var manager = _manager;
        if (manager is null) return;

        GlobalSystemMediaTransportControlsSession? best = null;
        foreach (var session in manager.GetSessions())
        {
            var info = session.GetPlaybackInfo();
            if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                best = session;
                break;
            }
        }
        best ??= manager.GetCurrentSession();

        var isPlaying = false;
        var title = "";
        var artist = "";
        var app = "";
        if (best is not null)
        {
            app = best.SourceAppUserModelId ?? "";
            isPlaying = best.GetPlaybackInfo().PlaybackStatus is
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            // 媒体属性是异步的：这里在后台线程上同步等（最多一次请求，不会卡 UI）
            var props = best.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            if (props is not null)
            {
                title = props.Title ?? "";
                artist = props.Artist ?? "";
            }
        }

        var changed = !string.Equals(_title, title, StringComparison.Ordinal)
            || !string.Equals(_artist, artist, StringComparison.Ordinal)
            || !string.Equals(_app, app, StringComparison.Ordinal)
            || _isPlaying != isPlaying;
        _title = title;
        _artist = artist;
        _app = app;
        _isPlaying = isPlaying;
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();

    /// <summary>停止后台轮询。</summary>
    public void Stop()
    {
        _stopping = true;
        var thread = _thread;
        _thread = null;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try { thread.Join(TimeSpan.FromSeconds(1.5)); } catch { }
        }
        _manager = null;
        _title = "";
        _artist = "";
        _app = "";
        _isPlaying = false;
    }
}
