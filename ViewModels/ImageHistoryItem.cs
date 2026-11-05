using System.Windows.Media;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.App.ViewModels;

/// <summary>
/// 设置页「图片历史」里的一张小缩略图（背景图 / 自定义光标共用）：点一下把这张图重新设为当前图片。
/// 图片文件本身留在数据目录里；点击只会改配置里的当前文件名，不复制、不删除。
/// </summary>
public sealed class ImageHistoryItem : ObservableObject
{
    private bool _isCurrent;

    public ImageHistoryItem(string fileName, ImageSource? thumbnail, bool isCurrent, Action<string> use)
    {
        FileName = fileName;
        Thumbnail = thumbnail;
        _isCurrent = isCurrent;
        UseCommand = new RelayCommand(_ => use(fileName));
    }

    /// <summary>数据目录里的文件名（配置里存的值）。</summary>
    public string FileName { get; }

    /// <summary>缩略图（加载失败为 null，界面隐藏这一项）。</summary>
    public ImageSource? Thumbnail { get; }

    /// <summary>是不是当前正在使用的那张（列表里加主题色描边）。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    /// <summary>点击应用这张历史图片。</summary>
    public RelayCommand UseCommand { get; }
}
