using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.Screenshot;

namespace YuLiangToolkit.Tools.Screenshot.ViewModels;

/// <summary>截图历史列表的一行（缩略图 + 文件信息）。</summary>
public sealed class ScreenshotHistoryItem
{
    /// <summary>历史文件。</summary>
    public required ScreenshotHistoryEntry Entry { get; init; }

    /// <summary>缩略图（已冻结，可跨线程）。</summary>
    public BitmapSource? Thumbnail { get; init; }

    /// <summary>显示文件名。</summary>
    public string FileName => Entry.FileName;

    /// <summary>显示时间。</summary>
    public string TimeText => Entry.TimeText;

    /// <summary>文件大小。</summary>
    public string SizeText => Entry.SizeText;

    /// <summary>完整路径（贴图 / 打开 / 删除用）。</summary>
    public string FullPath => Entry.FullPath;
}
