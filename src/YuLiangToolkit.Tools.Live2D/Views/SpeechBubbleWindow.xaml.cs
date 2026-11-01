using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.Live2D.Views;

/// <summary>
/// 聊天气泡的定位算法（<b>纯静态函数</b>，不依赖窗口，方便自检 / 单测）。
/// <para>规则：默认居中放在桌宠头顶上方 <c>gap</c> 像素；左右夹取到屏幕工作区内；
/// 上面放不下时翻到桌宠脚下；仍然放不下就贴屏幕工作区顶部。</para>
/// </summary>
public static class SpeechBubblePlacement
{
    /// <summary>气泡（含尾巴）与桌宠之间的间距（像素）。</summary>
    public const double Gap = 8;

    /// <summary>
    /// 计算气泡左上角坐标（DIP / 像素，调用方保证单位一致）。
    /// </summary>
    /// <param name="petLeft">桌宠窗口左边（屏幕坐标）。</param>
    /// <param name="petTop">桌宠窗口上边。</param>
    /// <param name="petWidth">桌宠窗口宽。</param>
    /// <param name="petHeight">桌宠窗口高。</param>
    /// <param name="bubbleWidth">气泡窗口宽（含尾巴留白）。</param>
    /// <param name="bubbleHeight">气泡窗口高。</param>
    /// <param name="workAreaX">屏幕工作区左边。</param>
    /// <param name="workAreaY">屏幕工作区上边。</param>
    /// <param name="workAreaW">屏幕工作区宽。</param>
    /// <param name="workAreaH">屏幕工作区高。</param>
    /// <returns>气泡左上角（left, top），已夹取在屏幕工作区内。</returns>
    public static (double Left, double Top) Calculate(
        double petLeft,
        double petTop,
        double petWidth,
        double petHeight,
        double bubbleWidth,
        double bubbleHeight,
        double workAreaX,
        double workAreaY,
        double workAreaW,
        double workAreaH)
    {
        if (bubbleWidth <= 1) bubbleWidth = 320;
        if (bubbleHeight <= 1) bubbleHeight = 90;
        if (workAreaW <= 1) workAreaW = 1920;
        if (workAreaH <= 1) workAreaH = 1080;

        // 1) 默认：桌宠头顶居中
        var left = petLeft + (petWidth - bubbleWidth) / 2.0;
        var top = petTop - bubbleHeight - Gap;

        // 2) 左右夹取到屏幕工作区（留 4px 边距）
        var minLeft = workAreaX + 4;
        var maxLeft = workAreaX + workAreaW - bubbleWidth - 4;
        left = maxLeft < minLeft ? minLeft : Math.Clamp(left, minLeft, maxLeft);

        // 3) 顶部放不下 → 翻到桌宠下方；再夹取（实在不行贴顶）
        var minTop = workAreaY + 4;
        var maxTop = workAreaY + workAreaH - bubbleHeight - 4;
        if (top < minTop)
        {
            var below = petTop + petHeight + Gap;
            top = below <= maxTop ? below : minTop;
        }
        if (top > maxTop) top = Math.Max(minTop, maxTop);

        return (left, top);
    }
}

/// <summary>
/// 「说话 / 轻对话」的聊天气泡窗口：透明、置顶、不抢焦点、点击穿透、圆角气泡 + 小尾巴。
/// <para><b>不抢焦点 / 不挡操作</b>：<c>ShowActivated=False</c> + <c>WS_EX_NOACTIVATE</c>，
/// 鼠标事件靠 <c>WS_EX_TRANSPARENT</c> 直接穿透到桌宠 / 桌面。</para>
/// <para><b>跟随桌宠</b>：会话帧循环每帧调 <see cref="PlaceAbove"/> 重算位置；
/// 说话结束约 1.5 秒后由会话调 <see cref="HideBubble"/> 淡出。</para>
/// </summary>
public partial class SpeechBubbleWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>气泡当前是否处于「显示中」（淡出过程中为 false）。</summary>
    public bool IsBubbleVisible { get; private set; }

    public SpeechBubbleWindow()
    {
        InitializeComponent();
        ShowActivated = false;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle,
                style | WsExNoActivate | WsExTransparent | WsExToolWindow);
        }
        catch
        {
            // 拿不到窗口句柄：退化成普通气泡（不影响显示）
        }
    }

    /// <summary>显示气泡（文本会自动截断到 ~120 字，避免气泡拉太长）。</summary>
    public void ShowBubble(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var shown = text.Trim();
        if (shown.Length > 120) shown = shown[..120] + "…";
        MessageText.Text = shown;

        IsBubbleVisible = true;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        UpdateLayout();
    }

    /// <summary>淡出并隐藏气泡（约 160ms）。</summary>
    public void HideBubble()
    {
        if (!IsBubbleVisible)
        {
            if (IsVisible) Hide();
            return;
        }

        IsBubbleVisible = false;
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        animation.Completed += (_, _) =>
        {
            try
            {
                if (!IsBubbleVisible) Hide();
            }
            catch
            {
                // 窗口已关闭：忽略
            }
        };
        BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>把气泡放到桌宠窗口上方（居中 + 屏幕边界夹取 + 多屏支持）。</summary>
    public void PlaceAbove(Rect petRect)
    {
        if (!IsVisible) return;

        UpdateLayout();
        var width = ActualWidth > 1 ? ActualWidth : 320;
        var height = ActualHeight > 1 ? ActualHeight : 90;
        var (workX, workY, workW, workH) = GetWorkAreaDip(petRect);

        var (left, top) = SpeechBubblePlacement.Calculate(petRect.Left, petRect.Top, petRect.Width, petRect.Height,
            width, height, workX, workY, workW, workH);

        Left = left;
        Top = top;
    }

    /// <summary>把气泡内容渲染成位图（透明底；调试截图 / 自检用）。</summary>
    public BitmapSource? RenderBubbleBitmap()
    {
        try
        {
            UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(Root.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(Root.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Root);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取桌宠所在屏幕的工作区（DIP；多屏用桌宠中心点找 monitor）。</summary>
    private (double X, double Y, double W, double H) GetWorkAreaDip(Rect petRect)
    {
        try
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0.01) scale = 1;

            var point = new NativePoint
            {
                X = (int)Math.Round((petRect.Left + petRect.Width / 2.0) * scale),
                Y = (int)Math.Round((petRect.Top + petRect.Height / 2.0) * scale),
            };
            var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var work = info.rcWork;
                    return (work.Left / scale, work.Top / scale,
                        (work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale);
                }
            }
        }
        catch
        {
            // 退化到主屏工作区
        }

        var area = SystemParameters.WorkArea;
        return (area.X, area.Y, area.Width, area.Height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr handle, int index, int newStyle);
}
