using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.ScreenSticker.ViewModels;

/// <summary>
/// 「🕘 最近使用」面板里的一项（界面对象）：包着配置里的 <see cref="RecentFileEntry"/>，
/// 带缩略图、是否还能打开的标记，以及「再贴一张 / 移除记录 / 在资源管理器里显示」三个命令。
///
/// <para><b>缩略图不在这里解码</b>：由 <see cref="ScreenStickerViewModel"/> 在后台线程用
/// <see cref="StickerImage.LoadThumbnail"/> 解好再回填，这样一次打开上百条记录也不会把界面卡住。</para>
/// </summary>
public sealed class RecentStickerItem : ObservableObject
{
    private BitmapSource? _thumbnail;
    private bool _isAvailable = true;

    public RecentStickerItem(
        RecentFileEntry entry,
        Action<RecentStickerItem?> addRequested,
        Action<RecentStickerItem?> removeRequested,
        Action<RecentStickerItem?> revealRequested)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Path = entry.Path ?? "";
        LastOpenedUtc = entry.LastOpenedUtc;

        AddCommand = new RelayCommand(_ => addRequested(this));
        RemoveCommand = new RelayCommand(_ => removeRequested(this));
        RevealCommand = new RelayCommand(_ => revealRequested(this));
    }

    /// <summary>图片完整路径（配置里存的那一条）。</summary>
    public string Path { get; }

    /// <summary>最近一次贴到桌面的时间（UTC）。</summary>
    public DateTime LastOpenedUtc { get; }

    /// <summary>文件名（列表上显示，长了用 TextTrimming 省略）。</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>所在文件夹（右键 / 悬停提示里用）。</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>「最近使用」时间文本（本地时间，只显示到分钟）。</summary>
    public string LastOpenedText
        => LastOpenedUtc == default ? "" : LastOpenedUtc.ToLocalTime().ToString("MM-dd HH:mm");

    /// <summary>悬停提示：完整路径 + 最近使用时间。</summary>
    public string ToolTipText => LastOpenedText.Length == 0
        ? Path
        : $"{Path}{Environment.NewLine}最近使用：{LastOpenedText}";

    /// <summary>缩略图（后台解码完成后回填；null = 还没解出来 / 解码失败）。</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => Set(ref _thumbnail, value);
    }

    /// <summary>这张图现在还能不能打开（后台用 <see cref="StickerImage.CanOpen"/> 检查后回填）。</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (!Set(ref _isAvailable, value)) return;
            OnPropertyChanged(nameof(AvailabilityText));
        }
    }

    /// <summary>图片已不存在时显示的红字提示；还能打开时为空串。</summary>
    public string AvailabilityText => IsAvailable ? "" : "文件已不存在";

    /// <summary>把这张图贴到桌面（点了缩略图 / 右键「再贴一张」）。</summary>
    public RelayCommand AddCommand { get; }

    /// <summary>从最近使用里移除这条记录（不会删本地图片）。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>在资源管理器里选中这张图片。</summary>
    public RelayCommand RevealCommand { get; }

    /// <summary>后台解码完成后回填缩略图与可用状态。</summary>
    public void UpdateThumbnail(BitmapSource? thumbnail, bool isAvailable)
    {
        Thumbnail = thumbnail;
        IsAvailable = isAvailable;
    }

    /// <summary>点击时才发现图片没了（例如刚被删掉）：标灰并显示「文件已不存在」。</summary>
    public void MarkUnavailable() => IsAvailable = false;
}
