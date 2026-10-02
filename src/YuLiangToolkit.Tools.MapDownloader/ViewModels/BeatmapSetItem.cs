using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.MapDownloader.Core;

namespace YuLiangToolkit.Tools.MapDownloader.ViewModels;

/// <summary>
/// 结果列表里的一行：一个谱面集 + 界面状态（勾没勾上）。
///
/// <para>展示用的字符串在构造时算好（<c>FmtDate</c> / <c>FmtCs</c> 这些是纯函数，见
/// <see cref="BeatmapFilter"/>），列表滚动时不用反复算。</para>
/// </summary>
public sealed class BeatmapSetItem : ObservableObject
{
    private bool _isSelected;

    public BeatmapSetItem(BeatmapSet set)
    {
        Set = set;
        Id = set.Id;
        Title = BeatmapFilter.DisplayTitle(set);
        Artist = set.Artist?.Trim() ?? "";
        Creator = set.Creator?.Trim() ?? "";
        StatusText = BeatmapFilter.StatusText(set);
        DateText = BeatmapFilter.FormatDate(set);
        DifficultyCount = BeatmapFilter.DifficultyCount(set);
        KeyRangeText = BeatmapFilter.FormatKeyRange(set);
    }

    public BeatmapSet Set { get; }

    public int Id { get; }
    public string Title { get; }
    public string Artist { get; }
    public string Creator { get; }
    public string StatusText { get; }
    public string DateText { get; }
    public int DifficultyCount { get; }
    public string KeyRangeText { get; }

    /// <summary>这一行要不要下载（原版是 DataGridView 的多选行，这里改成每行一个勾选框）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>交给下载器的条目（Id + 标题当文件名）。</summary>
    public OszItem ToOszItem() => new(Id, Title);

    /// <summary>悬停提示：把界面上没显示的字段（游玩数 / 收藏数 / 难度名）补上。</summary>
    public string Tooltip
    {
        get
        {
            var parts = new List<string> { $"{Artist} - {Title}", $"by {Creator} · {StatusText} {DateText}" };
            if (Set.PlayCount > 0) parts.Add($"游玩 {Set.PlayCount:N0} 次");
            if (Set.FavouriteCount > 0) parts.Add($"收藏 {Set.FavouriteCount:N0}");
            var versions = Set.Beatmaps?.Select(b => b.Version).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
            if (versions is { Length: > 0 }) parts.Add("难度：" + string.Join(" / ", versions));
            return string.Join("\n", parts);
        }
    }

    /// <summary>重写 ToString：UI 自动化 / 读屏软件看到的是"Id 标题"，而不是类型全名。</summary>
    public override string ToString() => $"{Id} {Title}";
}
