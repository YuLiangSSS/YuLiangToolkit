using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.MemeBoard.ViewModels;

/// <summary>
/// 收藏夹里的一张图片（一项）。
///
/// <para><b>职责</b>：只管一张图的数据与展示（缩略图 / 文件名 / 大小 / 提示文本）；
/// 导入、删除、点击动作等跨项操作都在 <see cref="MemeBoardViewModel"/> 里。</para>
///
/// <para><b>线程</b>：<see cref="LoadThumbnail"/> / <see cref="LoadFullImage"/> 是纯静态方法，
/// 在后台线程解码并 <c>Freeze()</c>，界面线程再通过 <see cref="ApplyThumbnail"/> 赋给属性，
/// 不会触发 WPF 跨线程异常。</para>
/// </summary>
public sealed class MemeItem : ObservableObject
{
    private ImageSource? _thumbnail;

    /// <summary>包装一条数据文件夹里扫到的文件。</summary>
    public MemeItem(StorageFile file)
        : this(file.FullPath, file.RelativePath, file.Name, file.SizeBytes, file.LastWriteTime)
    {
    }

    /// <summary>直接给字段构造（拖入 / 剪贴板导入后手动补一项时用）。</summary>
    public MemeItem(string fullPath, string relativePath, string name, long sizeBytes, DateTime modifiedAt)
    {
        FullPath = fullPath ?? "";
        RelativePath = relativePath ?? "";
        var resolvedName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(FullPath) ?? "" : name;
        Name = resolvedName.Length > 0 ? resolvedName : "未命名图片";
        SizeBytes = sizeBytes;
        ModifiedAt = modifiedAt;
    }

    /// <summary>磁盘上的完整路径。</summary>
    public string FullPath { get; }

    /// <summary>相对数据文件夹的路径（<c>images\xxx.png</c>），删除时交给 ToolStorage。</summary>
    public string RelativePath { get; }

    /// <summary>文件名（界面显示用；最多两行，超出省略号）。</summary>
    public string Name { get; }

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; }

    /// <summary>文件修改时间。</summary>
    public DateTime ModifiedAt { get; }

    /// <summary>悬停提示：完整路径 + 大小 + 修改时间。</summary>
    public string HintText =>
        $"{FullPath}{Environment.NewLine}{FileSize.Format(SizeBytes)} · {ModifiedAt:yyyy-MM-dd HH:mm}{Environment.NewLine}"
        + "单击：复制到剪贴板（并按设置粘贴到 QQ）；右键：更多操作";

    /// <summary>缩略图（<c>DecodePixelWidth=160</c>，已冻结；加载中 / 失败时为 null，界面显示占位图标）。</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (!Set(ref _thumbnail, value)) return;
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    /// <summary>有没有缩略图（没有时显示 🖼️ 占位）。</summary>
    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>后台加载完缩略图后回填（必须在 UI 线程调用）。传 null 表示加载失败，界面保持占位。</summary>
    public void ApplyThumbnail(ImageSource? thumbnail) => Thumbnail = thumbnail;

    // ---------------------------------------------------------------- 静态解码

    /// <summary>
    /// 加载缩略图：宽度解码到 160px（保持等比），整帧读进内存后冻结，方便跨线程传递。
    /// GIF 只取第一帧。失败返回 null。
    /// </summary>
    public static BitmapSource? LoadThumbnail(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 160;
            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 加载完整图片（用于复制到剪贴板）：整帧读进内存后冻结，后台线程解码也安全。
    /// GOP / GIF 按第一帧处理（系统剪贴板只存静态图）。失败返回 null。
    /// </summary>
    public static BitmapSource? LoadFullImage(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return null;
        try
        {
            using var stream = File.OpenRead(fullPath);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames[0];
            if (frame.CanFreeze) frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }
}
