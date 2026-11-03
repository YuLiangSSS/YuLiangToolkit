using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.MediaPlayer.ViewModels;

/// <summary>
/// 「按文件夹记住上次听到哪首 / 哪儿」那一半（v0.56.0，见 <see cref="MediaFolderResumeState"/>）。
///
/// <para><b>记</b>：播放时每次进度更新都先写进内存（不落盘）；每 10 秒最多落盘一次，
/// 暂停 / 停止 / 换歌 / 切走 / 退出时立刻落盘 —— 进度每 200ms 变一次，不能每次都写盘。</para>
///
/// <para><b>恢复</b>（只在设置里「恢复上次听到的位置」打开时生效）：
/// <list type="bullet">
///   <item>重新打开某个文件夹 → 在这批新加进来的文件里挑出**最近一次播放过**的那首，
///         选中并把进度定在上次听到的位置；设置里打开「恢复后自动播放」就直接接着放。</item>
///   <item>工具启动 → 还是走 <c>playlist.json</c> 恢复整个列表（行为不变）；如果文件夹记忆里的
///         进度更新（上次异常退出时 playlist.json 可能更旧），同路径就用文件夹记忆里的进度。</item>
/// </list></para>
/// </summary>
public sealed partial class MediaPlayerViewModel
{
    /// <summary>文件夹记忆快照（<c>Data\media-player\folder-resume.json</c>）。</summary>
    public string FolderResumeStatePath => Storage.PathOf("folder-resume.json");

    /// <summary>两次自动落盘之间至少隔这么久（进度每 200ms 更新一次，不能每次写盘）。</summary>
    private static readonly TimeSpan FolderResumeSaveInterval = TimeSpan.FromSeconds(10);

    /// <summary>内存里的文件夹记录（键 = 归一后的文件夹路径，大小写不敏感）。</summary>
    private readonly Dictionary<string, MediaFolderResumeEntry> _folderResume = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _folderResumeLastSaveUtc = DateTime.MinValue;
    private bool _folderResumeLoaded;

    /// <summary>工具启动时恢复了上次听到的位置、且设置要求自动播放：等播放器接上就跑。</summary>
    private bool _autoResumePending;

    // ---------------------------------------------------------------- 读 / 写

    /// <summary>把磁盘上的文件夹记录读进内存（幂等：只读一次）。</summary>
    private void LoadFolderResumeState()
    {
        if (_folderResumeLoaded) return;
        _folderResumeLoaded = true;

        var state = JsonFileStore.Read<MediaFolderResumeState>(FolderResumeStatePath, null,
            message => Log($"[错误] {message}"));
        if (state?.Entries is not { Count: > 0 }) return;

        foreach (var entry in state.Entries)
        {
            var folder = MediaFolderResumeState.Normalize(entry.Folder);
            if (folder.Length == 0 || string.IsNullOrWhiteSpace(entry.CurrentPath)) continue;
            entry.Folder = folder;
            _folderResume[folder] = entry;
        }

        PruneFolderResumeState();
    }

    /// <summary>
    /// 记下「当前这首播到哪儿」到内存（每 200ms 一次）。播放器还没接上时（工具启动恢复 / 演示数据）
    /// 不记：那时没有真实进度，记进去只会把好数据覆盖成 0。
    /// </summary>
    private void CaptureFolderProgress()
    {
        if (_player is null || CurrentItem is not { } item) return;
        var folder = MediaFolderResumeState.Normalize(item.Folder);
        if (folder.Length == 0) return;

        if (!_folderResume.TryGetValue(folder, out var entry))
        {
            entry = new MediaFolderResumeEntry();
            _folderResume[folder] = entry;
        }

        entry.Folder = folder;
        entry.CurrentPath = item.FullPath;
        // 媒体还没打开完时界面位置是 0，但这次可能要跳到 _pendingSeek / 恢复出来的 _pendingResume：
        // 别把恢复进度记成 0（见 PlayItem 与 CurrentPersistPositionSeconds 的注释）
        entry.PositionSeconds = CurrentPersistPositionSeconds();
        entry.UpdatedAtUtc = DateTime.UtcNow;
        MaybeSaveFolderResumeState();
    }

    /// <summary>距离上次落盘不足 <see cref="FolderResumeSaveInterval"/> 就只在内存里攒着。</summary>
    private void MaybeSaveFolderResumeState()
    {
        if (DateTime.UtcNow - _folderResumeLastSaveUtc < FolderResumeSaveInterval) return;
        SaveFolderResumeState();
    }

    /// <summary>
    /// 立刻把内存里的文件夹记录落盘（暂停 / 停止 / 换歌 / 切走工具 / 退出时调；平时由
    /// <see cref="MaybeSaveFolderResumeState"/> 节流）。写失败只记日志，不影响播放。
    /// </summary>
    public void SaveFolderResumeState()
    {
        // 演示模式（YLT_PLAYER_DEMO）的列表是假路径：别把假数据写进用户的真实进度文件
        if (_isDemoMode) return;

        _folderResumeLastSaveUtc = DateTime.UtcNow;
        PruneFolderResumeState();

        var state = new MediaFolderResumeState
        {
            Entries = _folderResume.Values
                .OrderByDescending(e => e.UpdatedAtUtc)
                .Select(e => new MediaFolderResumeEntry
                {
                    Folder = e.Folder,
                    CurrentPath = e.CurrentPath,
                    PositionSeconds = e.PositionSeconds,
                    UpdatedAtUtc = e.UpdatedAtUtc,
                })
                .ToList(),
        };
        JsonFileStore.Write(FolderResumeStatePath, state, message => Log($"[错误] {message}"));
    }

    /// <summary>
    /// 立刻把「当前这首 + 听到哪儿」同时写进 <c>playlist.json</c> 与 <c>folder-resume.json</c>。
    /// <para>程序退出 / 外壳丢弃后台视图前调用：后台保活时外壳走的是
    /// <see cref="StopBackgroundPlayback"/>，**不会**调 <c>NotifyClosed</c>
    /// （见 <c>MainViewModel.ShutdownTools</c>），这条退出路径必须自己兜底落盘。</para>
    /// </summary>
    public void PersistPlaybackState()
    {
        CaptureFolderProgress();
        SavePlaylistState();
        SaveFolderResumeState();
        PlaybackTrace($"PersistPlaybackState：{CurrentItem?.Title ?? "(无)"} @ {CurrentPersistPositionSeconds():0.###}s");
    }

    private void PruneFolderResumeState()
    {
        if (_folderResume.Count <= MediaFolderResumeState.MaxEntries) return;
        foreach (var stale in _folderResume.Values
                     .OrderByDescending(e => e.UpdatedAtUtc)
                     .Skip(MediaFolderResumeState.MaxEntries)
                     .ToList())
        {
            _folderResume.Remove(stale.Folder);
        }
    }

    // ---------------------------------------------------------------- 恢复

    /// <summary>
    /// 打开文件夹之后调：在这批**新加进来的文件**里，找最近一次播放过的文件夹记录，
    /// 选中那首并把进度定在上次听到的位置；设置里「恢复后自动播放」打开时直接接着放。
    /// </summary>
    public void RestoreFolderResumeState(IReadOnlyList<MediaItem> opened)
    {
        if (!Cfg.ResumeLastPlayed || opened.Count == 0) return;

        var openedPaths = new HashSet<string>(opened.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
        var best = _folderResume.Values
            .Where(e => openedPaths.Contains(e.CurrentPath))
            .OrderByDescending(e => e.UpdatedAtUtc)
            .FirstOrDefault();
        if (best is null) return;

        var item = Items.FirstOrDefault(i => PathEquals(i.FullPath, best.CurrentPath));
        if (item is null) return;

        SelectedItem = item;
        CurrentItem = item;
        _queue.SetCurrent(Items.IndexOf(item));
        _pendingResume = TimeSpan.FromSeconds(Math.Max(0, best.PositionSeconds));
        _pendingResumePath = item.FullPath;   // 这个恢复进度只属于这首，之后换歌别套用
        UpdatePosition(_pendingResume, TimeSpan.Zero);

        // 恢复出来的这首就是「当前曲目」：顺手写进 playlist.json，下次启动工具也能接着这首/这个进度
        SavePlaylistState();
        Log($"已恢复文件夹上次听到的位置：{item.Title} @ {MediaTime.Format(_pendingResume)}（{item.Folder}）");

        if (Cfg.AutoResumePlayback && _player is not null)
        {
            StatusText = $"继续上次：{item.Title}（{MediaTime.Format(_pendingResume)}）";
            PlaybackTrace($"文件夹恢复：{item.Title} pendingResume={_pendingResume.TotalSeconds:0.###}s（自动播放）");
            PlayItem(item);   // PlayItem 会把 _pendingResume 带进 _pendingSeek，打开成功后跳过去
            return;
        }

        StatusText = $"已恢复这个文件夹上次听到的位置：{item.Title} @ {MediaTime.Format(_pendingResume)}，点播放继续";
    }

    /// <summary>
    /// 工具启动恢复列表后：如果设置要求自动播放、且确实恢复了上次的曲目，就置一个「等播放器接上再放」的标记。
    /// <para>为什么不在这里直接放：这时 View 还没 Loaded、<see cref="Player"/> 还是 null
    /// （见 <c>AttachPlayer</c>）。</para>
    /// </summary>
    private void ArmAutoResumeIfConfigured()
    {
        _autoResumePending = Cfg.AutoResumePlayback
                             && Cfg.ResumeLastPlayed
                             && CurrentItem is not null;
    }

    /// <summary>播放器刚接上时由 <c>AttachPlayer</c> 调：该自动继续就去放（只消费一次）。</summary>
    private void TryAutoResumeOnPlayerAttached()
    {
        if (!_autoResumePending) return;
        _autoResumePending = false;
        if (CurrentItem is null || IsPlaying) return;
        PlayItem(CurrentItem);
    }

    /// <summary>
    /// 在「这次能恢复的路径」里挑文件夹记忆里最近更新的那一条（工具启动恢复时兜底用：
    /// playlist.json 的当前曲目可能是空的 / 那首文件被删了，但 folder-resume.json 仍然记着上次听的那首）。
    /// </summary>
    private MediaFolderResumeEntry? FindNewestFolderResumeEntry(IEnumerable<string> paths)
    {
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return null;
        return _folderResume.Values
            .Where(e => wanted.Contains(e.CurrentPath))
            .OrderByDescending(e => e.UpdatedAtUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// 某个曲目的文件夹记忆（只在路径对得上时返回）：工具启动恢复时用来挑「比 playlist.json 更新」的进度。
    /// </summary>
    private bool TryGetFolderProgress(MediaItem item, out TimeSpan position)
    {
        position = TimeSpan.Zero;
        var folder = MediaFolderResumeState.Normalize(item.Folder);
        if (folder.Length == 0 || !_folderResume.TryGetValue(folder, out var entry)) return false;
        if (!PathEquals(entry.CurrentPath, item.FullPath)) return false;
        position = TimeSpan.FromSeconds(Math.Max(0, entry.PositionSeconds));
        return true;
    }
}
