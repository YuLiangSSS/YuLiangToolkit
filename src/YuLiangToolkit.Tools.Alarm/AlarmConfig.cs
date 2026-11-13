using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.Alarm;

/// <summary>「闹钟」的强类型配置 → <c>Config\alarm\config.json</c>。</summary>
public sealed class AlarmConfig : ToolConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "alarm";

    // ---------- 闹钟列表 ----------

    private ObservableCollection<AlarmEntry> _alarms = new();

    /// <summary>全部闹钟（顺序 = 界面顺序）。</summary>
    public ObservableCollection<AlarmEntry> Alarms
    {
        get => _alarms;
        set => Set(ref _alarms, value ?? new ObservableCollection<AlarmEntry>());
    }

    // ---------- 响铃 ----------

    private string _sound = "system:SystemExclamation";

    /// <summary>响铃音（<c>none</c> / <c>system:别名</c> / <c>file:路径</c>）。</summary>
    public string Sound
    {
        get => _sound;
        set => Set(ref _sound, value ?? Core.Media.SoundPlayer.NoneId);
    }

    private bool _muted;

    /// <summary>静音（保留选择，只是不播）。</summary>
    public bool Muted
    {
        get => _muted;
        set => Set(ref _muted, value);
    }

    private int _snoozeMinutes = AlarmSchedule.DefaultSnoozeMinutes;

    /// <summary>贪睡推迟多少分钟（1~60）。</summary>
    public int SnoozeMinutes
    {
        get => _snoozeMinutes;
        set => Set(ref _snoozeMinutes, Math.Clamp(value, 1, 60));
    }

    private bool _repeatSound;

    /// <summary>响铃时反复播放（直到用户停止 / 贪睡）；关掉 = 只响一声。</summary>
    public bool RepeatSound
    {
        get => _repeatSound;
        set => Set(ref _repeatSound, value);
    }

    private NotificationSettings _notification = new()
    {
        Mode = NotificationMode.InApp,
        Corner = NotificationCorner.TopRight,
    };

    /// <summary>通知偏好（到点提醒）。</summary>
    public NotificationSettings Notification
    {
        get => _notification;
        set => Set(ref _notification, value ?? new NotificationSettings());
    }

    // ---------- 计算属性 ----------

    /// <summary>有没有启用的闹钟（界面空状态 / 页头统计用）。</summary>
    [JsonIgnore]
    public int EnabledCount => Alarms.Count(a => a.Enabled);
}
