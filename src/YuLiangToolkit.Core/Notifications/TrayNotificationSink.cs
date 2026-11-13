using YuLiangToolkit.Core.Services;

namespace YuLiangToolkit.Core.Notifications;

/// <summary>
/// Windows 通知通道：把通知交给任务栏托盘气泡（Win10/11 会显示成系统通知并进通知中心）。
///
/// <para>托盘图标由外壳持有（<c>App.Tray</c>），所以这里用一个「取托盘」的委托注入，
/// Core 不反过来依赖外壳。</para>
///
/// <para>托盘建不出来（极端环境）或图标不在通知区时 <see cref="IsAvailable"/> 为 false，
/// <see cref="NotificationCenter"/> 会自动退回应用内通知。</para>
/// </summary>
public sealed class TrayNotificationSink : INotificationSink
{
    private readonly Func<TrayIcon?> _trayProvider;

    /// <summary>构造。</summary>
    /// <param name="trayProvider">返回当前托盘图标（没有就返回 null）。</param>
    public TrayNotificationSink(Func<TrayIcon?> trayProvider)
    {
        _trayProvider = trayProvider ?? throw new ArgumentNullException(nameof(trayProvider));
    }

    /// <inheritdoc />
    public bool IsAvailable => _trayProvider()?.IsVisible == true;

    /// <inheritdoc />
    public bool Send(NotificationRequest request, NotificationSettings settings)
    {
        var tray = _trayProvider();
        if (tray is null || !tray.IsVisible) return false;

        try
        {
            return tray.ShowBalloon(Truncate(request.Title, 63), Truncate(request.Body, 250),
                ToBalloonKind(request.Kind));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Notification] Windows 通知失败：{ex.Message}");
            return false;
        }
    }

    private static TrayBalloonKind ToBalloonKind(NotificationKind kind) => kind switch
    {
        NotificationKind.Success => TrayBalloonKind.Info,
        NotificationKind.Warning => TrayBalloonKind.Warning,
        NotificationKind.Error => TrayBalloonKind.Error,
        _ => TrayBalloonKind.Info,
    };

    private static string Truncate(string? text, int max)
    {
        var value = text ?? "";
        return value.Length <= max ? value : value[..max];
    }
}
