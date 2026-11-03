using Microsoft.Win32;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.MediaPlayer.ViewModels;

/// <summary>
/// 播放列表那一半：打开文件夹 / 打开文件 / 拖放 / 增删 / 以及「记住上次的列表」。
///
/// <para>扫盘与合并都是**纯逻辑**（<see cref="MediaScanner"/> / <see cref="PlaylistMerger"/>），
/// 这里只负责对话档、切线程、报状态。</para>
/// </summary>
public sealed partial class MediaPlayerViewModel
{
    /// <summary>「记住上次的列表」存在这个文件里（<c>Data\media-player\playlist.json</c>）。</summary>
    public string PlaylistStatePath => Storage.PathOf("playlist.json");

    /// <summary>上次打开的文件夹（设置页 / 空列表提示里显示）。</summary>
    public string LastFolderText => string.IsNullOrEmpty(Cfg.LastFolder) ? "还没打开过文件夹" : Cfg.LastFolder;

    /// <summary>二次确认出口：默认直接同意（无头自检 / 自动化测试靠它跳过弹窗），界面里换成真的问一句。</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;

    /// <summary>
    /// 下一个「添加顺序」序号：条目第一次进列表时分配一次、此后不再变，
    /// 排序里的「默认顺序」按它排。恢复上次列表时从 0 顺着编号，之后新加的接在后面。
    /// </summary>
    private int _nextAddedIndex;

    /// <summary>
    /// 截图演示模式（<c>YLT_PLAYER_DEMO</c>）：列表里是假路径，任何「记住上次」的落盘都必须跳过，
    /// 否则截图 / 调试会把用户真正的 playlist.json / folder-resume.json 覆盖成一堆不存在的文件。
    /// </summary>
    private bool _isDemoMode;

    // ---------------------------------------------------------------- 打开

    /// <summary>「打开文件夹」按钮：可以一次选多个（选中的全部文件夹当作一批读进来）。</summary>
    private async Task OpenFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "打开音乐 / 视频文件夹（可多选）",
            Multiselect = true,
        };
        if (Directory.Exists(Cfg.LastFolder)) dialog.InitialDirectory = Cfg.LastFolder;
        if (dialog.ShowDialog() != true) return;

        var folders = dialog.FolderNames;
        if (folders.Length == 0) return;
        await AddFoldersAsync(folders);
    }

    /// <summary>「打开文件」按钮：手动挑几个文件（按设置里的读取方式过滤）。</summary>
    private async Task OpenFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开媒体文件",
            Multiselect = true,
            Filter = BuildFileDialogFilter(),
        };
        if (dialog.ShowDialog() != true) return;
        await AddFilesAsync(dialog.FileNames);
    }

    /// <summary>文件对话框的过滤串（跟着「读取方式」设置变）。</summary>
    private string BuildFileDialogFilter()
    {
        var audio = string.Join(";", MediaTypes.AudioExtensions.Select(e => "*" + e));
        var video = string.Join(";", MediaTypes.VideoExtensions.Select(e => "*" + e));
        return Cfg.IncludeMode switch
        {
            MediaIncludeMode.AudioOnly => $"音频文件|{audio}|所有文件|*.*",
            MediaIncludeMode.VideoOnly => $"视频文件|{video}|所有文件|*.*",
            _ => $"媒体文件|{audio};{video}|音频文件|{audio}|视频文件|{video}|所有文件|*.*",
        };
    }

    /// <summary>读一批文件夹（界面按钮与拖放都走这里）。</summary>
    public async Task AddFoldersAsync(IReadOnlyList<string> folders, IReadOnlyList<string>? extraFiles = null)
    {
        if (folders.Count == 0 && (extraFiles is null || extraFiles.Count == 0)) return;

        IsBusy = true;
        try
        {
            var include = Cfg.IncludeMode;
            var recursive = Cfg.ScanRecursive;
            var fileList = extraFiles?.ToList() ?? new List<string>();
            StatusText = folders.Count <= 1
                ? "正在读取文件夹…"
                : $"正在读取 {folders.Count} 个文件夹…";
            Log($"读取文件夹（{(recursive ? "含子文件夹" : "只看本层")} / {MediaTypes.Describe(include)}）：{string.Join("；", folders)}");

            var scanned = await Task.Run(() =>
            {
                var all = new List<string>();
                foreach (var folder in folders) all.AddRange(MediaScanner.EnumerateFiles(folder, recursive, include));
                all.AddRange(fileList);
                return all;
            });

            // 过滤 / 去重 / 读修改时间与大小也放后台：几百上千个文件的元数据读取不能卡界面（见 MediaScanner.FromFiles）
            var items = await Task.Run(() => MediaScanner.FromFiles(scanned, include));
            if (items.Count == 0)
            {
                StatusText = $"没有读到文件（当前读取方式：{MediaTypes.Describe(include)}）";
                Log("一个可读取的音频 / 视频都没有：检查「设置 → 打开文件夹时读取哪些文件」，或者文件夹里确实只有别的类型");
                return;
            }

            var source = folders.Count switch
            {
                0 => "拖入的文件",
                1 => Path.GetFileName(folders[0]) is { Length: > 0 } name ? name : folders[0],
                _ => $"{folders.Count} 个文件夹",
            };
            AppendItems(items, Cfg.AppendWhenOpeningFolder, source);

            // v0.56.0：每个文件夹各自记住「上次听到哪首 / 哪儿」——重新打开这个文件夹时恢复
            RestoreFolderResumeState(items);

            if (folders.Count > 0)
            {
                Cfg.LastFolder = folders[0];
                OnPropertyChanged(nameof(LastFolderText));
            }
        }
        catch (Exception ex)
        {
            StatusText = $"读取文件夹失败：{ex.Message}";
            Log($"[错误] 读取文件夹失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>读一批文件（「打开文件」按钮；拖进来的文件也走这里）。</summary>
    public async Task AddFilesAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        var items = await Task.Run(() => MediaScanner.FromFiles(files, Cfg.IncludeMode));
        if (items.Count == 0)
        {
            StatusText = $"这几个文件不是可读取的媒体（当前读取方式：{MediaTypes.Describe(Cfg.IncludeMode)}）";
            return;
        }

        // 手动打开文件一律「追加」：设置里那个覆盖/追加说的是「再开一个文件夹」，
        // 否则用户挑两个文件第二次就白挑了。
        AppendItems(items, append: true, source: files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} 个文件");
    }

    /// <summary>拖放进来的东西（文件夹 + 文件混着也行）：文件夹按设置扫，文件直接加。</summary>
    public async Task AddDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var folders = paths.Where(p => Directory.Exists(p)).ToList();
        var files = paths.Where(p => File.Exists(p)).ToList();

        if (folders.Count == 0)
        {
            await AddFilesAsync(files);
            return;
        }
        await AddFoldersAsync(folders, files);
    }

    // ---------------------------------------------------------------- 合并

    /// <summary>
    /// 把一批条目并进列表（<see cref="PlaylistMerger"/> 决定覆盖还是追加、去掉重复），
    /// 按当前排序方式排好，并把「正在播的是哪首 / 选中哪一行」按路径恢复回去。
    /// </summary>
    public void AppendItems(IReadOnlyList<MediaItem> incoming, bool append, string source)
    {
        if (incoming.Count == 0) return;

        var selectedPath = SelectedItem?.FullPath;
        var currentPath = CurrentItem?.FullPath;

        var report = PlaylistMerger.Merge(Items.ToList(), incoming, append);

        // 新条目分配「添加顺序」序号（老条目第一次进列表时已经分配过，保持不变）
        foreach (var item in report.Items) AssignAddedIndex(item);

        // 排序方式不是「默认顺序」时，新加进来的直接排到对应位置
        var ordered = PlaylistSorter.Sort(report.Items.ToList(), PlaylistSort, PlaylistSortDescending);
        Items.Clear();
        foreach (var item in ordered) Items.Add(item);

        _queue.Reset();
        RefreshPlaylistState();

        SelectedItem = selectedPath is null
            ? (Items.Count == 1 ? Items[0] : null)
            : Items.FirstOrDefault(i => PathEquals(i.FullPath, selectedPath));

        var current = currentPath is null ? null : Items.FirstOrDefault(i => PathEquals(i.FullPath, currentPath));
        if (current is not null)
        {
            CurrentItem = current;
            _queue.SetCurrent(Items.IndexOf(current));
        }
        else if (CurrentItem is not null)
        {
            // 覆盖模式下正在播的那首被顶掉了：停掉，别让界面显示着已经不存在的曲目
            StopPlayback();
            CurrentItem = null;
        }

        StatusText = report.Describe(append);
        Log($"[{source}] {report.Describe(append)}");
        SavePlaylistState();
    }

    /// <summary>给还没编号的条目分配「添加顺序」序号（= 它第一次进列表的先后）。</summary>
    private void AssignAddedIndex(MediaItem item)
    {
        if (item.AddedIndex >= 0) return;
        item.AddedIndex = _nextAddedIndex++;
    }

    /// <summary>
    /// 用户换了排序方式 / 方向：立刻重排列表。
    /// <para>⚠ 正在播的那首**不打断**（播放器不重载、进度不动），只把播放队列对齐到新下标；
    /// 列表循环 / 随机从此按新列表走（随机的一轮会重洗）。</para>
    /// </summary>
    private void ApplyPlaylistSort(bool announce = true)
    {
        var selectedPath = SelectedItem?.FullPath;
        var current = CurrentItem;

        if (Items.Count > 1)
        {
            var ordered = PlaylistSorter.Sort(Items.ToList(), PlaylistSort, PlaylistSortDescending);
            Items.Clear();
            foreach (var item in ordered) Items.Add(item);
        }

        _queue.Reset();
        RefreshPlaylistState();

        SelectedItem = selectedPath is null ? null : Items.FirstOrDefault(i => PathEquals(i.FullPath, selectedPath));
        if (current is not null && Items.Contains(current))
        {
            CurrentItem = current;
            _queue.SetCurrent(Items.IndexOf(current));
        }

        SavePlaylistState();
        if (!announce) return;

        StatusText = $"已按{PlaylistSorter.Describe(PlaylistSort, PlaylistSortDescending)}排列（{Items.Count} 个）";
        Log(StatusText);
    }

    // ---------------------------------------------------------------- 增删

    /// <summary>把选中的那行从列表里拿掉（**不动磁盘上的文件**）。</summary>
    public void RemoveSelected()
    {
        var item = SelectedItem;
        if (item is null) return;

        var index = Items.IndexOf(item);
        if (index < 0) return;

        Items.Remove(item);
        if (ReferenceEquals(CurrentItem, item))
        {
            StopPlayback();
            CurrentItem = null;
        }

        _queue.Reset();
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        RefreshPlaylistState();
        SavePlaylistState();
        StatusText = $"已从列表移除：{item.Title}";
    }

    /// <summary>清空列表（**不动磁盘上的文件**；会先问一句）。</summary>
    public void ClearPlaylist()
    {
        if (Items.Count == 0) return;
        if (!Confirm($"清空播放列表？（{Items.Count} 个文件，不会删磁盘上的文件）")) return;

        StopPlayback();
        Items.Clear();
        SelectedItem = null;
        CurrentItem = null;
        _queue.Reset();
        RefreshPlaylistState();
        SavePlaylistState();
        StatusText = "播放列表已清空";
    }

    /// <summary>在资源管理器里选中当前选中的那一行。</summary>
    private void RevealSelected()
    {
        var item = SelectedItem;
        if (item is null) return;
        try
        {
            if (File.Exists(item.FullPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.FullPath}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                StatusText = $"文件不在了：{item.FullPath}";
            }
        }
        catch (Exception ex)
        {
            Log($"[错误] 打开资源管理器失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 记住上次

    /// <summary>
    /// 把当前列表与播放位置写进 <c>Data\media-player\playlist.json</c>（增删 / 排序 / 关工具时调）。
    /// <para>路径**按添加顺序**存（不是界面显示顺序）：下次打开先恢复添加顺序，再套用配置里的
    /// 排序方式，所以「默认顺序」永远回得到最初那副样子。</para>
    /// </summary>
    public void SavePlaylistState()
    {
        // 演示模式（YLT_PLAYER_DEMO）的列表是假路径：写进去会覆盖用户真正的播放快照
        if (_isDemoMode) return;

        try
        {
            var state = new MediaPlaylistState
            {
                Paths = Items
                    .OrderBy(i => i.AddedIndex < 0 ? int.MaxValue : i.AddedIndex)
                    .Select(i => i.FullPath)
                    .ToList(),
                CurrentPath = CurrentItem?.FullPath ?? "",
                // 刚恢复还没真正打开时 _lastPosition 是 0，这里要把「准备跳到的位置」算进去，
                // 否则「打开工具 → 什么都不点 → 关掉」会把上次的进度清零（见 CurrentPersistPositionSeconds）
                PositionSeconds = CurrentPersistPositionSeconds(),
            };
            JsonFileStore.Write(PlaylistStatePath, state);
        }
        catch (Exception ex)
        {
            Log($"[错误] 保存播放列表快照失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 打开工具时把上次的列表捞回来（<c>RestorePlaylist</c>）。文件已经不在的条目顺手丢掉；
    /// 恢复完按配置里的排序方式排好，<c>ResumeLastPlayed</c> 打开时只「选中 + 记住位置」，
    /// **不自动出声**（免得一进工具就吵）。
    /// </summary>
    private void RestorePlaylistState()
    {
        var state = JsonFileStore.Read<MediaPlaylistState>(PlaylistStatePath);
        if (state?.Paths is not { Count: > 0 }) return;

        var items = new List<MediaItem>();
        var missing = 0;
        foreach (var path in state.Paths)
        {
            if (!File.Exists(path))
            {
                missing++;
                continue;
            }
            if (MediaTypes.KindOf(path) is not { } kind) continue;
            var (modified, size) = MediaItem.ReadMetadata(path);
            // 快照是按添加顺序存的：顺着编号，之后新加的文件接在后面
            items.Add(new MediaItem(path, kind, modified, size) { AddedIndex = items.Count });
        }

        if (items.Count == 0)
        {
            Log("上次的播放列表里的文件都不在了，没有恢复");
            return;
        }

        _nextAddedIndex = items.Count;
        var ordered = PlaylistSorter.Sort(items, PlaylistSort, PlaylistSortDescending);
        foreach (var item in ordered) Items.Add(item);
        RefreshPlaylistState();

        var suffix = missing > 0 ? $"（{missing} 个已不存在，已跳过）" : "";
        Log($"已恢复上次的播放列表：{items.Count} 个文件{suffix}");

        if (!Cfg.ResumeLastPlayed) return;

        // 当前曲目：先认 playlist.json 里记的那首；没有 / 文件不在了就退回「文件夹记忆」里最近更新的那首。
        // v0.65.1：播放器在后台保活时关应用不会走 OnClosed（外壳走 StopBackgroundPlayback），
        // 老数据里 playlist.json 的 CurrentPath 可能是空的，只有 folder-resume.json 记得是对的。
        var current = string.IsNullOrEmpty(state.CurrentPath)
            ? null
            : Items.FirstOrDefault(i => PathEquals(i.FullPath, state.CurrentPath));

        if (current is null)
        {
            var newest = FindNewestFolderResumeEntry(Items.Select(i => i.FullPath));
            if (newest is null) return;
            current = Items.FirstOrDefault(i => PathEquals(i.FullPath, newest.CurrentPath));
        }
        if (current is null) return;

        SelectedItem = current;
        CurrentItem = current;
        _queue.SetCurrent(Items.IndexOf(current));

        // 进度取「playlist.json 记的」与「文件夹记忆里更靠后的」：异常退出时两边可能一新一旧
        var position = PathEquals(current.FullPath, state.CurrentPath)
            ? TimeSpan.FromSeconds(Math.Max(0, state.PositionSeconds))
            : TimeSpan.Zero;
        if (TryGetFolderProgress(current, out var folderPosition) && folderPosition > position)
        {
            position = folderPosition;
        }
        _pendingResume = position;
        _pendingResumePath = current.FullPath;   // 这个恢复进度只属于这首，换歌别套用
        UpdatePosition(position, TimeSpan.Zero);
        StatusText = $"已恢复上次的播放列表（{Items.Count} 个），按播放继续";
        Log($"已恢复上次的曲目：{current.Title} @ {MediaTime.Format(position)}");
    }

    /// <summary>路径比较（Windows 大小写不敏感）。</summary>
    private static bool PathEquals(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
