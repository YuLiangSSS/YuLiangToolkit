using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Core.Notifications;

/// <summary>通知走哪条通道。</summary>
public enum NotificationMode
{
    /// <summary>应用自己画的小窗（右下角等四个角，样式统一、不依赖系统通知设置）。</summary>
    InApp,

    /// <summary>Windows 通知区气泡（进系统通知中心；样式由系统决定）。</summary>
    Windows,
}

/// <summary>应用内通知贴在屏幕哪个角。</summary>
public enum NotificationCorner
{
    /// <summary>左上。</summary>
    TopLeft,

    /// <summary>右上。</summary>
    TopRight,

    /// <summary>左下。</summary>
    BottomLeft,

    /// <summary>右下（默认：和 Windows 自己弹通知的位置一致）。</summary>
    BottomRight,
}

/// <summary>通知的语义类型（决定图标与配色）。</summary>
public enum NotificationKind
{
    /// <summary>一般信息。</summary>
    Info,

    /// <summary>成功 / 完成。</summary>
    Success,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>出错。</summary>
    Error,
}

/// <summary>
/// 一个工具自己的通知偏好（**每个工具各存一份**，不集中到主页设置里）。
///
/// <para>直接作为工具配置里的一个子对象存进它的 <c>config.json</c> 就行，
/// <see cref="YuLiangToolkit.Core.Configuration.ToolConfig"/> 的自动落盘会带上它：</para>
/// <code>
/// public NotificationSettings Notification { get; set; } = new();
/// </code>
///
/// <para>界面上摆一块 <c>Notifications.NotificationSettingsView</c> 即可（见 Views 目录），
/// 用户就能单独给「这个工具」选通道 / 方位 / 时长 —— 秒表弹右下、另一个工具弹左下，互不影响。</para>
/// </summary>
public sealed class NotificationSettings : ObservableObject
{
    private NotificationMode _mode = NotificationMode.InApp;

    /// <summary>通道：应用内小窗 / Windows 气泡。默认应用内（效果稳定、不依赖系统通知设置）。</summary>
    public NotificationMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsInApp));
            OnPropertyChanged(nameof(IsWindows));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private NotificationCorner _corner = NotificationCorner.BottomRight;

    /// <summary>应用内通知贴哪个角（Windows 气泡由系统决定位置，这一项不生效）。</summary>
    public NotificationCorner Corner
    {
        get => _corner;
        set
        {
            if (!Set(ref _corner, value)) return;
            OnPropertyChanged(nameof(IsTopLeft));
            OnPropertyChanged(nameof(IsTopRight));
            OnPropertyChanged(nameof(IsBottomLeft));
            OnPropertyChanged(nameof(IsBottomRight));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private int _durationMs = 3500;

    /// <summary>应用内通知停留多久（毫秒，1000~15000；Windows 气泡由系统控制，这一项不生效）。</summary>
    public int DurationMs
    {
        get => _durationMs;
        set => Set(ref _durationMs, Math.Clamp(value, 1000, 15000));
    }

    private bool _enabled = true;

    /// <summary>这个工具要不要发通知（关掉 = 按快捷键时不弹，界面照常更新）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    // ---------- 界面绑定用的布尔快捷属性（单选按钮直接绑） ----------

    /// <summary>当前是不是应用内通道。</summary>
    public bool IsInApp
    {
        get => Mode == NotificationMode.InApp;
        set { if (value) Mode = NotificationMode.InApp; }
    }

    /// <summary>当前是不是 Windows 气泡通道。</summary>
    public bool IsWindows
    {
        get => Mode == NotificationMode.Windows;
        set { if (value) Mode = NotificationMode.Windows; }
    }

    /// <summary>贴左上。</summary>
    public bool IsTopLeft
    {
        get => Corner == NotificationCorner.TopLeft;
        set { if (value) Corner = NotificationCorner.TopLeft; }
    }

    /// <summary>贴右上。</summary>
    public bool IsTopRight
    {
        get => Corner == NotificationCorner.TopRight;
        set { if (value) Corner = NotificationCorner.TopRight; }
    }

    /// <summary>贴左下。</summary>
    public bool IsBottomLeft
    {
        get => Corner == NotificationCorner.BottomLeft;
        set { if (value) Corner = NotificationCorner.BottomLeft; }
    }

    /// <summary>贴右下。</summary>
    public bool IsBottomRight
    {
        get => Corner == NotificationCorner.BottomRight;
        set { if (value) Corner = NotificationCorner.BottomRight; }
    }

    /// <summary>一句话摘要（设置块底部显示：「通知：应用内 · 右下角 · 3.5 秒」）。</summary>
    public string SummaryText
    {
        get
        {
            if (!Enabled) return "这个工具的通知已关闭";
            var mode = Mode == NotificationMode.InApp ? "应用内小窗" : "Windows 通知";
            var corner = CornerName(Corner);
            return Mode == NotificationMode.InApp
                ? $"通知：{mode} · {corner} · {DurationMs / 1000.0:0.#} 秒"
                : $"通知：{mode}（位置由系统决定）";
        }
    }

    /// <summary>角名的中文写法。</summary>
    public static string CornerName(NotificationCorner corner) => corner switch
    {
        NotificationCorner.TopLeft => "左上角",
        NotificationCorner.TopRight => "右上角",
        NotificationCorner.BottomLeft => "左下角",
        _ => "右下角",
    };

    /// <summary>复制一份（沙箱 / 测试用）。</summary>
    public NotificationSettings Clone() => new()
    {
        _mode = _mode,
        _corner = _corner,
        _durationMs = _durationMs,
        _enabled = _enabled,
    };
}

/// <summary>一条要发出去的通知。</summary>
/// <param name="Title">标题（短，一行）。</param>
/// <param name="Body">正文（可多行）。</param>
/// <param name="Kind">语义类型。</param>
/// <param name="SourceId">哪个工具发的（便于以后过滤 / 打标签）。</param>
public sealed record NotificationRequest(
    string Title,
    string Body,
    NotificationKind Kind = NotificationKind.Info,
    string SourceId = "")
{
    /// <summary>转成 Windows 气泡要用的文本（标题 + 正文，用换行分隔）。</summary>
    public string ToBalloonText() => string.IsNullOrWhiteSpace(Body) ? Title : $"{Title}{Environment.NewLine}{Body}";
}

/// <summary>
/// 一条通知通道（应用内 / Windows 气泡 / 以后可能加的别的）。
///
/// <para>加新通道 = 实现这个接口 + 在 <see cref="NotificationCenter.SetSinks"/> 里多注册一个，
/// 调用方（各个工具）一行都不用改。</para>
/// </summary>
public interface INotificationSink
{
    /// <summary>这条通道能不能用（例如托盘图标没建起来时 Windows 通道不可用）。</summary>
    bool IsAvailable { get; }

    /// <summary>发一条通知（不抛异常；发不出去就返回 false）。</summary>
    bool Send(NotificationRequest request, NotificationSettings settings);
}

/// <summary>
/// 通知的统一入口。
///
/// <para><b>调用方（各工具）只写一行</b>：<c>NotificationCenter.Show(settings, request)</c>，
/// 不关心当前是应用内还是 Windows 通道。</para>
///
/// <para><b>为什么放 Core</b>：外壳不认识具体工具，工具也不该直接碰托盘 / 窗口。
/// 外壳在启动时用 <see cref="SetSinks"/> 注入两条通道即可。</para>
///
/// <para><b>兜底</b>：请求的通道不可用（例如托盘建不出来、或系统禁用了通知）时，
/// 自动换另一条通道发；两条都不行就写 Debug 输出，绝不静默吞掉也不抛异常。</para>
/// </summary>
public static class NotificationCenter
{
    private static INotificationSink? _inApp;
    private static INotificationSink? _windows;

    /// <summary>实际发出了通知（调试 / 自检 / 日志用）。</summary>
    public static event EventHandler<NotificationRequest>? Shown;

    /// <summary>外壳启动时注入两条通道（任一可为 null）。</summary>
    public static void SetSinks(INotificationSink? inApp, INotificationSink? windows)
    {
        _inApp = inApp;
        _windows = windows;
    }

    /// <summary>按工具自己的偏好发一条通知；返回是否真的发出去了。</summary>
    public static bool Show(NotificationSettings settings, NotificationRequest request)
    {
        if (settings is null || request is null) return false;
        if (!settings.Enabled) return false;

        var sink = Resolve(settings.Mode);
        if (sink is not null && sink.Send(request, settings))
        {
            Raise(request);
            return true;
        }

        // 首选通道不可用 → 换另一条
        var fallback = Resolve(settings.Mode == NotificationMode.InApp
            ? NotificationMode.Windows
            : NotificationMode.InApp);
        if (fallback is not null && fallback.Send(request, settings))
        {
            Raise(request);
            return true;
        }

        System.Diagnostics.Debug.WriteLine($"[Notification] 没有可用通道，通知丢弃：{request.Title}");
        return false;
    }

    /// <summary>直接按指定通道发（设置页的「发一条测试通知」用）。</summary>
    public static bool ShowWith(NotificationMode mode, NotificationSettings settings, NotificationRequest request)
    {
        var sink = Resolve(mode);
        if (sink is null || !sink.Send(request, settings)) return false;
        Raise(request);
        return true;
    }

    private static INotificationSink? Resolve(NotificationMode mode)
    {
        var sink = mode == NotificationMode.InApp ? _inApp : _windows;
        return sink is { IsAvailable: true } ? sink : null;
    }

    private static void Raise(NotificationRequest request)
    {
        try { Shown?.Invoke(null, request); } catch { /* 订阅方异常不影响发送 */ }
    }
}
