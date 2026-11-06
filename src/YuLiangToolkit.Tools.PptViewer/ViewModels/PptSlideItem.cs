using System.Windows.Media;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.PptViewer.Parsing;

namespace YuLiangToolkit.Tools.PptViewer.ViewModels;

/// <summary>
/// 左侧缩略图列表里的一项：包装一张 <see cref="PptSlide"/>，并负责**懒加载**缩略图。
///
/// <para>缩略图要到界面真的绑到 <see cref="Thumbnail"/> 时才渲染（打开大演示文稿时不会一次性
/// 把几百页都画出来）；渲染委托由 ViewModel 提供，主视图和缩略图共用同一份 DrawingGroup。</para>
/// </summary>
public sealed class PptSlideItem : ObservableObject
{
    private readonly Func<PptSlide, DrawingImage?> _thumbnailProvider;
    private DrawingImage? _thumbnail;
    private bool _thumbnailRequested;
    private bool _isCurrent;

    /// <param name="slide">解析好的幻灯片（只读模型）。</param>
    /// <param name="thumbnailProvider">渲染缩略图（一般指向 ViewModel 的 LRU 缓存）。</param>
    /// <param name="fallbackIndex">模型里 Index 无效（0）时用的页号（按列表位置 + 1）。</param>
    public PptSlideItem(PptSlide slide, Func<PptSlide, DrawingImage?> thumbnailProvider, int fallbackIndex = 0)
    {
        Slide = slide ?? throw new ArgumentNullException(nameof(slide));
        _thumbnailProvider = thumbnailProvider ?? (_ => null);
        FallbackIndex = fallbackIndex;
    }

    /// <summary>对应的只读模型。</summary>
    public PptSlide Slide { get; }

    /// <summary>列表位置（1 开始；模型 Index 有效时以模型为准）。</summary>
    public int FallbackIndex { get; }

    /// <summary>第几页（1 开始）。</summary>
    public int Index => Slide.Index > 0 ? Slide.Index : Math.Max(1, FallbackIndex);

    /// <summary>幻灯片标题（可能为空）。</summary>
    public string Title => Slide.Title;

    /// <summary>显示名：「3. 标题」，没有标题就「第 3 页」。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? $"第 {Index} 页" : $"{Index}. {Title}";

    /// <summary>是不是隐藏幻灯片（浏览时照常显示，缩略图上标一下）。</summary>
    public bool IsHidden => Slide.IsHidden;

    /// <summary>这一页有没有演讲者备注（缩略图上标一个小图标）。</summary>
    public bool HasNotes => Slide.HasNotes;

    /// <summary>缩略图（首次访问时渲染；失败 / 无内容时为 null，界面显示空白）。</summary>
    public DrawingImage? Thumbnail
    {
        get
        {
            if (_thumbnailRequested) return _thumbnail;
            _thumbnailRequested = true;

            try
            {
                _thumbnail = _thumbnailProvider(Slide);
            }
            catch
            {
                // 单页渲染失败不应该影响整个列表（详情进 ViewModel 日志）
                _thumbnail = null;
            }

            return _thumbnail;
        }
    }

    /// <summary>是不是当前页（列表里高亮；和 ListBox 的选中状态联动）。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    /// <summary>悬停提示：页号 + 标题 + 隐藏 / 备注标记。</summary>
    public string ToolTipText
    {
        get
        {
            var text = DisplayName;
            if (IsHidden) text += Environment.NewLine + "这张幻灯片在放映时会被隐藏";
            if (HasNotes) text += Environment.NewLine + "这一页有演讲者备注";
            return text;
        }
    }

    /// <summary>重新渲染缩略图（目前没有用到；留给以后换主题 / 强制刷新缩略图）。</summary>
    public void InvalidateThumbnail()
    {
        _thumbnailRequested = false;
        _thumbnail = null;
        OnPropertyChanged(nameof(Thumbnail));
    }
}
