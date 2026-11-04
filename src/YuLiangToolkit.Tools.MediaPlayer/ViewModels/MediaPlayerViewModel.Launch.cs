namespace YuLiangToolkit.Tools.MediaPlayer.ViewModels;

/// <summary>
/// 「打开方式（用本工具播这个文件）」那一半：把主程序交过来的文件加进播放列表，并**立刻播放这一首**。
///
/// <para><b>为什么要排队</b>：主程序处理「打开方式」时是先打开工具页、再把路径交给 ViewModel 的，
/// 这个时刻视图可能刚创建、还没 Loaded，<see cref="Player"/> 仍然是 null（要等 <c>MediaPlayerView</c>
/// 的 <c>Loaded</c> 里 <c>AttachPlayer</c>）。所以文件路径先记进 <see cref="_pendingLaunchPath"/>，
/// 等播放器接上之后由 <see cref="TryPlayPendingLaunchOnPlayerAttached"/> 消费——
/// 跟「恢复上次播放列表」用的 <c>_pendingResume</c> / <c>_autoResumePending</c> 是同一套思路。</para>
///
/// <para>约定（见 <see cref="YuLiangToolkit.Core.Shell.IFileLaunchTarget"/>）：文件不存在 / 路径非法 /
/// 根本不是媒体文件时只写状态栏与日志，绝不抛异常给主程序；可以重复调用，每次切到新文件并播放它。</para>
/// </summary>
public sealed partial class MediaPlayerViewModel
{
    /// <summary>
    /// 「打开方式」交过来、但播放器还没接上（视图还没 Loaded）的文件；只保留**最后一个**请求：
    /// 连续用「打开方式」打开几个文件时，只播最新的那一个。
    /// </summary>
    private string? _pendingLaunchPath;

    /// <summary>
    /// 主程序通过 Windows「打开方式」交过来一个文件（见 <see cref="YuLiangToolkit.Core.Shell.IFileLaunchTarget"/>）：
    /// 加进播放列表并**立刻播放这一首**（不是只加列表）。
    ///
    /// <para>播放器还没接上（视图刚创建）时先排队，<c>AttachPlayer</c> 之后自动消费；
    /// 已经接上就直接追加 + 播放。重复调用会切换到最新的那个文件。</para>
    /// </summary>
    public void OpenLaunchedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "打开方式：没有拿到文件路径";
            Log("[打开方式] 没有拿到文件路径，忽略");
            return;
        }

        string fullPath;
        try
        {
            // 主程序一般给绝对路径，但仍容忍带引号 / 相对路径的写法
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            StatusText = $"打开方式：路径不合法（{path}）";
            Log($"[打开方式] 路径不合法：{path} · {ex.Message}");
            return;
        }

        if (!File.Exists(fullPath))
        {
            StatusText = $"文件不存在：{fullPath}";
            Log($"[打开方式] 文件不存在：{fullPath}");
            return;
        }

        if (!MediaTypes.IsMedia(fullPath))
        {
            StatusText = $"播放器只认音频 / 视频文件：{fullPath}";
            Log($"[打开方式] 不是音频 / 视频文件：{fullPath}");
            return;
        }

        if (Player is null)
        {
            // 视图刚创建：播放器还没接上，先排队（不能只加列表，否则用户看到“开了却没播”）
            _pendingLaunchPath = fullPath;
            _autoResumePending = false;   // 用户明确要打开新文件了，别再自动续播上次那首
            StatusText = $"正在准备播放器，稍后播放 {Path.GetFileName(fullPath)}…";
            Log($"[打开方式] 排队等播放器就绪：{fullPath}");
            return;
        }

        _pendingLaunchPath = null;   // 播放器已经就绪，旧的排队请求作废
        AddAndPlayLaunchedFile(fullPath);
    }

    /// <summary>
    /// 播放器接上（视图 Loaded）后消费排队的「打开方式」文件；有排队就返回 true。
    /// <para>由 <c>AttachPlayer</c> 调用；返回 true 时调用方跳过「恢复后自动播放」，
    /// 避免「先响一下上次那首、再切到刚打开的文件」。</para>
    /// </summary>
    private bool TryPlayPendingLaunchOnPlayerAttached()
    {
        var path = _pendingLaunchPath;
        if (path is null) return false;

        _pendingLaunchPath = null;
        _autoResumePending = false;
        PlaybackTrace($"[打开方式] 播放器已就绪，开始播放排队文件：{path}");
        AddAndPlayLaunchedFile(path);
        return true;
    }

    /// <summary>
    /// 把文件追加进列表并播放它（「打开方式」的唯一执行入口：直接播 / 排队后播都走这里）。
    ///
    /// <para>元数据只有两次文件系统调用（只读一个文件，不值得为它异步排队；真正出声的
    /// <c>MediaElement.Load</c> 本来就是异步的），所以这里同步追加，保证方法返回时列表与
    /// 当前曲目已经切换好，不会出现“打开了却没反应”的窗口。</para>
    /// </summary>
    private void AddAndPlayLaunchedFile(string fullPath)
    {
        try
        {
            // 排队期间文件可能被删 / 被移走：别往列表里塞一条已经播不了的东西
            if (!File.Exists(fullPath))
            {
                StatusText = $"文件不存在：{fullPath}";
                Log($"[打开方式] 文件不存在（准备播放时）：{fullPath}");
                return;
            }

            // 显式打开不看设置里的「读取方式」：双击 / 右键选本工具就是“我就要播这个”，
            // 被「仅音频 / 仅视频」静默拒掉反而是 bug（那个设置管的是扫文件夹与手动挑文件）。
            var kind = MediaTypes.KindOf(fullPath);
            if (kind is null) return;   // OpenLaunchedFile 已经拦过，这里只兜底

            var (modified, size) = MediaItem.ReadMetadata(fullPath);
            var item = new MediaItem(fullPath, kind.Value, modified, size);

            // 追加（已存在同一路径时只跳过、不会变成两份）；列表原有的曲目 / 进度恢复状态不变
            AppendItems(new[] { item }, append: true, source: "打开方式");

            // 按路径找回列表里的那一条（AppendItems 会按当前排序重排，不一定是刚 new 的对象）
            var current = Items.FirstOrDefault(i => PathEquals(i.FullPath, fullPath)) ?? item;

            PlaybackTrace($"[打开方式] 追加并播放：{Path.GetFileName(fullPath)}（列表 {Items.Count} 个）");
            PlayItem(current);   // 切歌 + 立刻播放 + 落盘当前曲目（单实例转发时就是“切换到刚打开的文件”）
        }
        catch (Exception ex)
        {
            // 打开方式不能把异常抛回主程序：写状态栏 + 日志就够了
            StatusText = $"打开方式打开失败：{ex.Message}";
            Log($"[打开方式] 打开失败：{fullPath} · {ex.Message}");
        }
    }
}
