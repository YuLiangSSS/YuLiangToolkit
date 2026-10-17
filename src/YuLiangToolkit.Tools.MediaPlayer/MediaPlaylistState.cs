namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 播放列表的「记住上次」快照 → <c>Data\media-player\playlist.json</c>
/// （走 <c>JsonFileStore</c>：原子写 + 损坏自动备份并回退，见 AGENTS.md 2.3）。
///
/// <para>只存路径与位置，不存别的：下次打开时文件不在了就顺手丢掉（列表里不会留死条目）。</para>
/// </summary>
public sealed class MediaPlaylistState
{
    /// <summary>列表里所有文件的完整路径（按列表顺序）。</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>上次选中的那首（空 = 没选）。</summary>
    public string CurrentPath { get; set; } = "";

    /// <summary>上次听到第几秒（配合设置项「恢复上次听到的位置」）。</summary>
    public double PositionSeconds { get; set; }
}
