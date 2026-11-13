using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace YuLiangToolkit.Core.Notifications;

/// <summary>
/// 应用内通知窗：屏幕某个角落的一张小卡片（标题 + 正文 + 类型色条），可叠多条、淡入淡出、点击关闭。
///
/// <para><b>为什么不抢焦点</b>：用 <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c> 建窗口，
/// 不加任务栏按钮、不抢输入焦点（正在打字 / 玩游戏时不会被弹窗抢走键盘）。</para>
///
/// <para><b>颜色</b>：拿应用当前的主题资源（<c>CardBackgroundBrush</c> / <c>TextPrimaryBrush</c> …），
/// 找不到就用一套内置深色兜底 —— 所以即使主题还没加载也能正常显示，也不会在浅色主题下变成黑块。</para>
///
/// <para><b>堆叠</b>：同一角的多条按顺序往屏幕内侧堆，关闭时其余几条自动补位。</para>
/// </summary>
public sealed class InAppToastWindow : Window
{
    /// <summary>同一角最多同时显示几条（再多就把最早的关掉，避免糊满屏幕）。</summary>
    private const int MaxStack = 4;

    private const double Gap = 10;

    /// <summary>同角当前显示着的通知（按创建顺序，最早的在最外/最上）。</summary>
    private static readonly Dictionary<NotificationCorner, List<InAppToastWindow>> Stacks = new();

    private readonly DispatcherTimer _closeTimer;
    private Storyboard? _fadeOut;

    private readonly NotificationCorner _corner;

    /// <summary>这条通知贴哪个角（同一角的位置由静态堆叠表统一算）。</summary>
    public NotificationCorner Corner => _corner;

    /// <summary>显示一条通知（<paramref name="settings"/> 决定角落与停留时间）。</summary>
    public static void ShowToast(NotificationRequest request, NotificationSettings settings)
    {
        var corner = settings?.Corner ?? NotificationCorner.BottomRight;
        var duration = Math.Clamp(settings?.DurationMs ?? 3500, 1000, 15000);

        if (!Stacks.TryGetValue(corner, out var stack))
        {
            stack = new List<InAppToastWindow>();
            Stacks[corner] = stack;
        }

        // 超过上限：先把最早的那条淡出（不阻塞新通知）
        while (stack.Count >= MaxStack) stack[0].CloseAnimated();

        var toast = new InAppToastWindow(request, corner, duration);
        stack.Add(toast);
        toast.Closed += (_, _) =>
        {
            stack.Remove(toast);
            Relayout(corner);
        };

        toast.ShowInPlace();
        Relayout(corner);
    }

    private InAppToastWindow(NotificationRequest request, NotificationCorner corner, int durationMs)
    {
        _corner = corner;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 360;
        // 不抢焦点：窗口显示出来也不会把光标从别的程序夺走
        Focusable = false;

        Content = BuildContent(request);

        _closeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(durationMs),
        };
        _closeTimer.Tick += (_, _) => CloseAnimated();

        MouseLeftButtonDown += (_, _) => CloseAnimated();
    }

    // ---------------------------------------------------------------- 外观

    private UIElement BuildContent(NotificationRequest request)
    {
        var accent = AccentBrush(request.Kind);

        var card = new Border
        {
            Background = Brush("CardBackgroundBrush", Color.FromArgb(0xF2, 0x22, 0x25, 0x2C)),
            BorderBrush = Brush("BorderBrush", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0),
            // 外面留一点空间给阴影
            Margin = new Thickness(0, 0, 0, 0),
        };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 2,
            Opacity = 0.35,
            Color = Colors.Black,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧类型色条
        grid.Children.Add(new Border { Background = accent, CornerRadius = new CornerRadius(10, 0, 0, 10) });

        var text = new StackPanel { Margin = new Thickness(14, 11, 16, 12) };
        text.Children.Add(new TextBlock
        {
            Text = Glyph(request.Kind) + "  " + request.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush", Color.FromRgb(0xF2, 0xF4, 0xF8)),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            text.Children.Add(new TextBlock
            {
                Text = request.Body,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = Brush("TextSecondaryBrush", Color.FromRgb(0xB8, 0xBE, 0xC9)),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 120,
            });
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        card.Child = grid;
        return card;
    }

    private static string Glyph(NotificationKind kind) => kind switch
    {
        NotificationKind.Success => "✅",
        NotificationKind.Warning => "⚠️",
        NotificationKind.Error => "❌",
        _ => "ℹ️",
    };

    private static Brush AccentBrush(NotificationKind kind)
    {
        var color = kind switch
        {
            NotificationKind.Success => Color.FromRgb(0x3F, 0xB9, 0x50),
            NotificationKind.Warning => Color.FromRgb(0xE5, 0x9F, 0x2E),
            NotificationKind.Error => Color.FromRgb(0xE0, 0x4C, 0x4C),
            _ => Color.FromRgb(0x4C, 0x8D, 0xFF),
        };

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>取应用主题里的画刷；拿不到（无 Application / 主题没加载）就用兜底色。</summary>
    private static Brush Brush(string resourceKey, Color fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(resourceKey) is Brush brush) return brush;
        }
        catch { /* 资源字典还没就绪，走兜底 */ }

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    // ---------------------------------------------------------------- 定位

    /// <summary>按工作区（不含任务栏）贴到对应的角。</summary>
    private (Rect WorkArea, double Scale) WorkArea()
    {
        var screen = SystemParameters.WorkArea;
        return (new Rect(screen.Left, screen.Top, screen.Width, screen.Height), 1);
    }

    /// <summary>算出某个角、第 <paramref name="index"/> 条（0 = 最靠外/最上）的位置。</summary>
    internal static Point CalculatePosition(NotificationCorner corner, double width, double height,
        int index, Rect workArea)
    {
        var margin = 12.0;
        var offset = index * (height + Gap);

        return corner switch
        {
            NotificationCorner.TopLeft => new Point(workArea.Left + margin, workArea.Top + margin + offset),
            NotificationCorner.TopRight => new Point(workArea.Right - margin - width, workArea.Top + margin + offset),
            NotificationCorner.BottomLeft => new Point(workArea.Left + margin, workArea.Bottom - margin - height - offset),
            _ => new Point(workArea.Right - margin - width, workArea.Bottom - margin - height - offset),
        };
    }

    private void ShowInPlace()
    {
        var (area, _) = WorkArea();
        // 先给个初始位置（避免第一帧出现在屏幕左上角）
        var (x, y) = (area.Right - Width - 12, area.Bottom - 80);
        Left = x;
        Top = y;

        Opacity = 0;
        Show();

        // 不抢焦点：Show() 之后再补一次扩展样式（无激活）
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
        }

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _closeTimer.Start();
    }

    /// <summary>同一角的位置重算（某条关掉 / 新增一条之后调用）。</summary>
    private static void Relayout(NotificationCorner corner)
    {
        if (!Stacks.TryGetValue(corner, out var stack)) return;

        var area = new Rect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top,
            SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);

        for (var i = 0; i < stack.Count; i++)
        {
            var toast = stack[i];
            if (!toast.IsLoaded) continue;

            var target = CalculatePosition(corner, toast.Width, toast.ActualHeight, i, area);
            toast.Left = target.X;
            toast.Top = target.Y;
        }
    }

    /// <summary>淡出后关闭（重复调用安全）。</summary>
    public void CloseAnimated()
    {
        try
        {
            _closeTimer.Stop();

            _fadeOut ??= CreateFadeOut(this);
            _fadeOut.Begin(this, true);
        }
        catch
        {
            try { Close(); } catch { /* 忽略 */ }
        }
    }

    private static Storyboard CreateFadeOut(Window window)
    {
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };

        Storyboard.SetTarget(animation, window);
        Storyboard.SetTargetProperty(animation, new PropertyPath(OpacityProperty));

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            try { window.Close(); } catch { /* 忽略 */ }
        };
        return storyboard;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        try { _closeTimer.Stop(); } catch { /* 忽略 */ }
        base.OnClosed(e);
    }

    // ---------------------------------------------------------------- Win32

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}

/// <summary>应用内通知通道（<see cref="INotificationSink"/> 的实现，外壳启动时注入 <see cref="NotificationCenter"/>）。</summary>
public sealed class InAppNotificationSink : INotificationSink
{
    /// <summary>只要有 WPF 应用就能画，总是可用。</summary>
    public bool IsAvailable => Application.Current is not null;

    /// <inheritdoc />
    public bool Send(NotificationRequest request, NotificationSettings settings)
    {
        try
        {
            var app = Application.Current;
            if (app is null) return false;

            // 统一回 UI 线程（通知可能从后台线程触发）
            if (app.Dispatcher.CheckAccess()) InAppToastWindow.ShowToast(request, settings);
            else app.Dispatcher.BeginInvoke(new Action(() => InAppToastWindow.ShowToast(request, settings)));

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Notification] 应用内通知失败：{ex}");
            return false;
        }
    }
}
