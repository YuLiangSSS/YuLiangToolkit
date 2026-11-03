using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>
/// 通话浮层里的一块「参与者磁贴」：显示这个人的画面 / 头像、名字、麦克风与摄像头状态。
///
/// <para><b>为什么单独抽一个类</b>：视频帧每秒会来好几次，直接改界面会疯狂刷新整个面板；
/// 把每个人做成一个 <see cref="ObservableObject"/>，帧到了只改对应磁贴的 <see cref="VideoImage"/>，
/// 别的人、别的地方一点不刷新（也是「别每秒重建 Image」的落点）。</para>
///
/// <para><b>线程</b>：本类的属性只允许在 UI 线程改（ViewModel 里解完码后用 <c>Ui(...)</c> 赋值）；
/// <see cref="VideoImage"/> 拿到的是已经 <c>Freeze()</c> 的 <see cref="BitmapSource"/>，
/// WPF 允许它跨线程传递，但赋值给它 / 绑到 Image 的动作仍然要留在 UI 线程。</para>
/// </summary>
public sealed class CallTileItem : ObservableObject
{
    /// <summary>磁贴固定宽度（像素）。窄了名字会挤成一团，宽了 8 个人放不下。</summary>
    public const double TileWidth = 216;

    /// <summary>磁贴固定高度（像素）：216×152 接近 4:3，跟通话画面比例接近。</summary>
    public const double TileHeight = 152;

    private string _name;
    private string _emoji;
    private bool _micOn = true;
    private bool _cameraOn = true;
    private bool _isSpeaking;
    private BitmapSource? _videoImage;

    /// <summary>造一块磁贴（成员加入通话时由 ViewModel 调）。</summary>
    /// <param name="memberId">成员 Id（视频帧靠它找到这块磁贴）。</param>
    /// <param name="name">显示名。</param>
    /// <param name="emoji">头像 emoji（没有画面时显示的就是它）。</param>
    /// <param name="isSelf">是不是本机自己（名字后面加「（我）」）。</param>
    public CallTileItem(string memberId, string name, string emoji, bool isSelf)
    {
        MemberId = memberId ?? "";
        _name = name ?? "";
        _emoji = string.IsNullOrWhiteSpace(emoji) ? "🙂" : emoji;
        IsSelf = isSelf;
    }

    /// <summary>成员 Id（同一个人只对应一块磁贴）。</summary>
    public string MemberId { get; }

    /// <summary>是不是本机自己。</summary>
    public bool IsSelf { get; }

    /// <summary>磁贴宽度（常量包成实例属性，XAML 直接绑 Bind 用）。</summary>
    public double Width => TileWidth;

    /// <summary>磁贴高度（同上）。</summary>
    public double Height => TileHeight;

    /// <summary>显示名（状态广播会改它）。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value ?? "")) return;
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>头像 emoji。</summary>
    public string Emoji
    {
        get => _emoji;
        set
        {
            var text = string.IsNullOrWhiteSpace(value) ? "🙂" : value;
            if (!Set(ref _emoji, text)) return;
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>名字条上显示的那一行（自己会多一个「（我）」）。</summary>
    public string DisplayName => IsSelf ? $"{Emoji} {Name}（我）" : $"{Emoji} {Name}";

    /// <summary>麦克风开着没有（关 = 已静音）。</summary>
    public bool MicOn
    {
        get => _micOn;
        set
        {
            if (!Set(ref _micOn, value)) return;
            OnPropertyChanged(nameof(MicText));
            OnPropertyChanged(nameof(MicMark));
        }
    }

    /// <summary>摄像头开着没有（关 = 磁贴只显示头像）。</summary>
    public bool CameraOn
    {
        get => _cameraOn;
        set
        {
            if (!Set(ref _cameraOn, value)) return;
            OnPropertyChanged(nameof(IsVideoOn));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>现在有没有画面可显示（摄像头开着 + 收到过至少一帧）。</summary>
    public bool IsVideoOn => CameraOn && VideoImage is not null;

    /// <summary>当前这一帧（null = 还没收到；已经 Freeze，可跨线程持有）。</summary>
    public BitmapSource? VideoImage
    {
        get => _videoImage;
        set
        {
            if (!Set(ref _videoImage, value)) return;
            OnPropertyChanged(nameof(IsVideoOn));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>摄像头关 / 还在等第一帧时，磁贴中间那句小字。</summary>
    public string StatusText => CameraOn ? (IsVideoOn ? "" : "等待画面…") : "摄像头关";

    /// <summary>名字条右侧的麦克风标记。</summary>
    public string MicText => MicOn ? "🎙" : "🔇";

    /// <summary>静音时名字条右边的中文说明（截图 / 无障碍都看得懂）。</summary>
    public string MicMark => MicOn ? "" : "已静音";

    /// <summary>正在说话（麦克风电平超阈值；v1 引擎还没把电平接到界面，先留字段）。</summary>
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set => Set(ref _isSpeaking, value);
    }

    /// <summary>
    /// 用通话状态里那个人最新的一行信息更新磁贴（名字 / 头像 / 麦克风 / 摄像头）。
    /// <b>不动</b> <see cref="VideoImage"/>：状态每秒都会广播，画面不能在状态刷新时被清掉。
    /// </summary>
    public void UpdateFromWire(string name, string emoji, bool micOn, bool cameraOn)
    {
        Name = name;
        Emoji = emoji;
        MicOn = micOn;
        CameraOn = cameraOn;
    }

    /// <summary>换一帧画面（调用方保证 <see cref="BitmapSource"/> 已 Freeze，且当前在 UI 线程）。</summary>
    public void SetVideo(BitmapSource? image) => VideoImage = image;

    public override string ToString() => DisplayName;
}
