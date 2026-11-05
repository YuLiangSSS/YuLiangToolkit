using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.ScreenSticker.Views;

namespace YuLiangToolkit.Tools.ScreenSticker.ViewModels;

/// <summary>
/// 「🖼 桌面上的贴纸」列表里的一行（界面对象）：包着一张真实的 <see cref="StickerWindow"/>，
/// 负责置顶开关、层级上移 / 下移、单独关闭。
///
/// <para><b>置顶为什么走接口而不是直接改窗口</b>：置顶状态要写回配置并同步 z-order，
/// 这些都在 <see cref="StickerSession"/> 里；这里只是把 CheckBox 的双向绑定转给
/// <see cref="StickerSession.SetTopmost"/>，不自己做持久化。</para>
///
/// <para><b>防止无限递归</b>：会话触发 StickersChanged 后，列表会调 <see cref="SyncFromWindow"/>
/// 刷新显示；刷新期间不能再回写成 <c>SetTopmost</c>（否则改一次置顶会反复触发刷新），
/// 所以 <see cref="SyncFromWindow"/> 里用 <c>_syncing</c> 把 setter 挡住，只发通知。</para>
/// </summary>
public sealed class StickerListItem : ObservableObject
{
    private readonly StickerWindow _window;
    private readonly StickerSession _session;
    private readonly Action<StickerListItem?> _closeRequested;

    private BitmapSource? _thumbnail;
    private bool _syncing;

    public StickerListItem(
        StickerWindow window,
        StickerSession session,
        Action<StickerListItem?> moveUpRequested,
        Action<StickerListItem?> moveDownRequested,
        Action<StickerListItem?> closeRequested)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _closeRequested = closeRequested;

        MoveUpCommand = new RelayCommand(_ => moveUpRequested(this));
        MoveDownCommand = new RelayCommand(_ => moveDownRequested(this));
        CloseCommand = new RelayCommand(_ => _closeRequested(this));
    }

    /// <summary>对应的贴纸窗口（会话操作都要传它）。</summary>
    public StickerWindow Window => _window;

    /// <summary>图片完整路径。</summary>
    public string Path => _window.ImagePath;

    /// <summary>文件名（列表上显示的第一行）。</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>列表上的第二行小字：所在文件夹（长了用 TextTrimming 省略）。</summary>
    public string DetailText => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>缩略图（后台解码完成后回填；null = 还没解出来 / 解码失败）。</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => Set(ref _thumbnail, value);
    }

    /// <summary>
    /// 是否强制置顶（CheckBox 双向绑定）。
    /// setter 直接调 <see cref="StickerSession.SetTopmost"/>，配置与窗口层级一起更新。
    /// </summary>
    public bool Topmost
    {
        get => _window.Entry.Topmost;
        set
        {
            if (_syncing) return;
            if (value == _window.Entry.Topmost) return;

            _session.SetTopmost(_window, value);
            OnPropertyChanged();
        }
    }

    /// <summary>层级上移一层（更靠前）。</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>层级下移一层（更靠后）。</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>关闭这一张贴纸（会同时删掉它的保存记录）。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>后台缩略图解码完成后回填。</summary>
    public void SetThumbnail(BitmapSource? thumbnail) => Thumbnail = thumbnail;

    /// <summary>
    /// 会话里的状态变了（置顶 / 层级 / 窗口列表顺序）：只刷新显示、不回写会话。
    /// 由 <see cref="ScreenStickerViewModel"/> 在列表对得上号时调用。
    /// </summary>
    public void SyncFromWindow()
    {
        _syncing = true;
        try
        {
            OnPropertyChanged(nameof(Topmost));
            OnPropertyChanged(nameof(DetailText));
        }
        finally
        {
            _syncing = false;
        }
    }
}
