using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.NotificationLog;

/// <summary>「通知管理」的强类型配置 → <c>Config\notification-history\config.json</c>。</summary>
public sealed class NotificationLogConfig : ToolConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "notification-history";

    /// <summary>历史 JSON 的文件名（与配置同一个目录）。</summary>
    [JsonIgnore]
    public const string HistoryFileName = "history.json";

    private bool _rememberHistory = true;

    /// <summary>
    /// 记住历史（写盘，重启后还在）。关掉后只保留本次运行期间的记录。
    /// </summary>
    public bool RememberHistory
    {
        get => _rememberHistory;
        set => Set(ref _rememberHistory, value);
    }

    private int _capacity = NotificationHistory.DefaultCapacity;

    /// <summary>最多保留多少条（100~5000；超出丢最旧的）。</summary>
    public int Capacity
    {
        get => _capacity;
        set => Set(ref _capacity, Math.Clamp(value, 100, 5000));
    }

    private NotificationSettings _notification = new()
    {
        Mode = NotificationMode.InApp,
        Corner = NotificationCorner.TopRight,
    };

    /// <summary>本工具自己的通知偏好（用于「已清空」这类自己的提示）。</summary>
    public NotificationSettings Notification
    {
        get => _notification;
        set => Set(ref _notification, value ?? new NotificationSettings());
    }

    /// <summary>容量描述（界面滑块旁显示）。</summary>
    [JsonIgnore]
    public string CapacityText => $"{Capacity} 条";
}
