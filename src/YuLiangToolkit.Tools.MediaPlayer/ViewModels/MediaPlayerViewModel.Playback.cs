namespace YuLiangToolkit.Tools.MediaPlayer.ViewModels;

/// <summary>
/// 播放控制那一半：播放 / 暂停 / 停止 / 快进快退 / 上一首下一首 / 播完自动接下一首。
///
/// <para>所有声画操作都转发给 <see cref="IMediaPlayback"/>（界面里是 <c>MediaElement</c>，
/// 自检里是假播放器），所以这里没有一行 WPF 代码，可以无头跑（见 docs/dev/pitfalls.md）。</para>
/// </summary>
public sealed partial class MediaPlayerViewModel
{
    private IMediaPlayback? _player;
    private TimeSpan _lastPosition;
    private TimeSpan _lastDuration;

    /// <summary>从 playlist.json 恢复的「上次听到哪儿」：等这首真正打开后跳过去（只消费一次）。</summary>
    private TimeSpan _pendingResume;

    /// <summary>本次 Load 之前记下的目标位置（打开成功后由 <see cref="OnPlayerOpened"/> 消费）。</summary>
    private TimeSpan _pendingSeek;

    /// <summary>当前挂着的播放器（没接上之前是 null —— 命令照样能点，只是会提示一句）。</summary>
    public IMediaPlayback? Player => _player;

    // ---------------------------------------------------------------- 接入 / 摘下播放器

    /// <summary>界面加载完后把真播放器接进来；自检里接的是假播放器（同一条代码路径）。</summary>
    public void AttachPlayer(IMediaPlayback player)
    {
        if (ReferenceEquals(_player, player)) return;

        DetachPlayer();
        _player = player;
        player.Volume = VolumeValue / 100.0;
        player.Opened += OnPlayerOpened;
        player.Ended += OnPlayerEnded;
        player.Failed += OnPlayerFailed;
        Log("播放器已就绪");
    }

    /// <summary>摘掉播放器（切走工具 / 界面卸载时）。</summary>
    public void DetachPlayer()
    {
        if (_player is null) return;
        _player.Opened -= OnPlayerOpened;
        _player.Ended -= OnPlayerEnded;
        _player.Failed -= OnPlayerFailed;
        try { _player.Close(); } catch { /* 卸载流程忽略 */ }
        _player = null;
    }

    // ---------------------------------------------------------------- 播放

    /// <summary>播这一首（列表双击 / 上一首下一首 / 播完自动接下一首都走它）。</summary>
    public void PlayItem(MediaItem? item)
    {
        if (item is null) return;

        var index = Items.IndexOf(item);
        if (index < 0) return;

        if (!File.Exists(item.FullPath))
        {
            StatusText = $"文件不在了：{item.FullPath}";
            Log($"[错误] 文件不在了：{item.FullPath}");
            return;
        }

        SelectedItem = item;
        CurrentItem = item;
        // 队列里已经是这一首就别再 SetCurrent（随机会被当成「用户跳着点」，这一轮的顺序就作废了）
        if (index != _queue.Current) _queue.SetCurrent(index);
        MarkPlaying(item);

        _lastDuration = TimeSpan.Zero;
        HasVideo = false;
        UpdatePosition(TimeSpan.Zero, TimeSpan.Zero);
        IsPlaying = true;

        if (_player is null)
        {
            IsPlaying = false;
            StatusText = "播放器还没准备好（界面还没加载完）";
            return;
        }

        _pendingSeek = _pendingResume;
        _pendingResume = TimeSpan.Zero;
        _player.Volume = VolumeValue / 100.0;
        _player.Load(item.FullPath, autoPlay: true);
        StatusText = $"正在播放：{item.Title}";
    }

    /// <summary>播放 / 暂停（空格键、中间那个大按钮）。</summary>
    public void TogglePlayPause()
    {
        if (Items.Count == 0)
        {
            StatusText = "列表是空的：先打开一个文件夹";
            return;
        }

        if (CurrentItem is null)
        {
            PlayItem(SelectedItem ?? Items[0]);
            return;
        }

        if (_player is null)
        {
            StatusText = "播放器还没准备好（界面还没加载完）";
            return;
        }

        // 当前这首还没装进播放器（例如刚恢复上次的列表）：装进去再放
        if (!PathEquals(_player.CurrentPath, CurrentItem.FullPath) || !_player.IsLoaded)
        {
            PlayItem(CurrentItem);
            return;
        }

        if (_player.IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            StatusText = $"已暂停：{CurrentItem.Title}";
        }
        else
        {
            _player.Play();
            IsPlaying = true;
            StatusText = $"正在播放：{CurrentItem.Title}";
        }
    }

    /// <summary>停止（位置回到 0，当前曲目还在，按播放可以再来一遍）。</summary>
    public void StopPlayback()
    {
        _player?.Stop();
        IsPlaying = false;
        HasVideo = false;
        UpdatePosition(TimeSpan.Zero, TimeSpan.Zero);
        MarkPlaying(CurrentItem);
        StatusText = "已停止";
    }

    /// <summary>上一首 / 下一首（右键快退 / 右键快进；键盘也走它）。</summary>
    public void MoveTrack(int delta)
    {
        if (Items.Count == 0)
        {
            StatusText = "列表是空的";
            return;
        }

        var index = delta >= 0
            ? _queue.Next(Items.Count, autoAdvance: false)
            : _queue.Previous(Items.Count);

        if (index < 0 || index >= Items.Count) return;
        PlayItem(Items[index]);
    }

    // ---------------------------------------------------------------- 进度 / 快进快退

    /// <summary>快进（正数）/ 快退（负数）一步；步长来自设置（0.1 秒精度）。</summary>
    public void SeekBy(double deltaSeconds)
    {
        if (_player is null || !_player.IsLoaded || CurrentItem is null)
        {
            StatusText = "先播放一首再快进快退";
            return;
        }

        var duration = _player.Duration;
        var target = MediaTime.Seek(_player.Position, duration, deltaSeconds);
        _player.Seek(target);
        UpdatePosition(target, duration);
        StatusText = deltaSeconds >= 0
            ? $"快进 {deltaSeconds:0.0} 秒 → {MediaTime.Format(target)}"
            : $"快退 {Math.Abs(deltaSeconds):0.0} 秒 → {MediaTime.Format(target)}";
    }

    /// <summary>跳到进度条的某个比例（点 / 拖进度条时由界面调）。</summary>
    public void SeekToRatio(double ratio)
    {
        if (_player is null || !_player.IsLoaded) return;
        var duration = _player.Duration;
        if (duration <= TimeSpan.Zero) return;

        var target = MediaTime.FromRatio(ratio, duration);
        _player.Seek(target);
        UpdatePosition(target, duration);
    }

    /// <summary>
    /// 界面上的计时器（200ms 一次）把播放器当前位置推进来。
    /// 自检里直接调它，就等于「播了几秒」。
    /// </summary>
    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero) _lastDuration = duration;

        _lastPosition = position;
        PositionText = MediaTimeDisplay.Format(position, _lastDuration, Cfg.TimeDisplayMode);
        DurationText = _lastDuration > TimeSpan.Zero ? MediaTime.Format(_lastDuration) : "--:--";
        ProgressRatio = MediaTime.Ratio(position, _lastDuration);
    }

    // ---------------------------------------------------------------- 播放器回调

    private void OnPlayerOpened(object? sender, EventArgs e) => Ui(() =>
    {
        var duration = _player?.Duration ?? TimeSpan.Zero;
        HasVideo = _player?.HasVideo ?? false;

        if (_pendingSeek > TimeSpan.Zero)
        {
            var target = MediaTime.Clamp(_pendingSeek, duration);
            _player?.Seek(target);
            UpdatePosition(target, duration);
            _pendingSeek = TimeSpan.Zero;
            StatusText = $"从 {MediaTime.Format(target)} 继续：{CurrentItem?.Title}";
            return;
        }

        UpdatePosition(_player?.Position ?? TimeSpan.Zero, duration);
    });

    private void OnPlayerEnded(object? sender, EventArgs e) => Ui(() =>
    {
        // 单曲循环：留在原地重放（注意这里必须自己来，PlayQueue 只管「下一首是谁」）
        if (_queue.Mode == PlayOrderMode.RepeatOne)
        {
            _player?.Seek(TimeSpan.Zero);
            _player?.Play();
            IsPlaying = true;
            UpdatePosition(TimeSpan.Zero, _player?.Duration ?? TimeSpan.Zero);
            StatusText = $"单曲循环：{CurrentItem?.Title}";
            return;
        }

        var next = _queue.Next(Items.Count, autoAdvance: true);
        if (next < 0 || next >= Items.Count)
        {
            IsPlaying = false;
            StatusText = "列表播完了";
            return;
        }

        PlayItem(Items[next]);
    });

    private void OnPlayerFailed(object? sender, string reason) => Ui(() =>
    {
        IsPlaying = false;
        StatusText = $"播放失败：{reason}";
        Log($"[错误] 播放失败（{CurrentItem?.Title}）：{reason}");
    });

    // ---------------------------------------------------------------- 小工具

    /// <summary>列表行高亮：正在播的那行显示 ▶。</summary>
    private void MarkPlaying(MediaItem? item)
    {
        foreach (var entry in Items) entry.IsPlaying = ReferenceEquals(entry, item);
    }
}
