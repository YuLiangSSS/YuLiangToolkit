using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.Countdown;

/// <summary>「倒计时」的强类型配置 → <c>Config\countdown\config.json</c>。</summary>
public sealed class CountdownConfig : ToolConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "countdown";

    // ---------- 时长 ----------

    private int _durationSeconds = 300;

    /// <summary>上次设定的时长（秒）。下次打开沿用（不保留"还剩多少"——倒计时不该跨进程续跑）。</summary>
    public int DurationSeconds
    {
        get => _durationSeconds;
        set => Set(ref _durationSeconds, Math.Clamp(value, CountdownEngine.MinSeconds, CountdownEngine.MaxSeconds));
    }

    private string _customPresets = "60,300,600,1800";

    /// <summary>快捷预设（秒，逗号分隔）。界面上点一下就填进时长框。</summary>
    public string CustomPresets
    {
        get => _customPresets;
        set => Set(ref _customPresets, value ?? "");
    }

    /// <summary>解析出来的预设列表（非法项忽略）。</summary>
    [JsonIgnore]
    public IReadOnlyList<int> Presets
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_customPresets)) return Array.Empty<int>();

            return _customPresets
                .Split(new[] { ',', '，', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part.Trim(), out var seconds) ? seconds : -1)
                .Where(seconds => seconds >= CountdownEngine.MinSeconds && seconds <= CountdownEngine.MaxSeconds)
                .Distinct()
                .ToList();
        }
    }

    // ---------- 快捷键 ----------

    private string _startHotkey = "Ctrl+Alt+D";

    /// <summary>开始 / 继续的全局快捷键。</summary>
    public string StartHotkey
    {
        get => _startHotkey;
        set => Set(ref _startHotkey, value ?? "");
    }

    private string _pauseHotkey = "";

    /// <summary>暂停的全局快捷键（空 = 不设置）。</summary>
    public string PauseHotkey
    {
        get => _pauseHotkey;
        set => Set(ref _pauseHotkey, value ?? "");
    }

    private string _resetHotkey = "";

    /// <summary>重置的全局快捷键（空 = 不设置）。</summary>
    public string ResetHotkey
    {
        get => _resetHotkey;
        set => Set(ref _resetHotkey, value ?? "");
    }

    private string _addMinuteHotkey = "";

    /// <summary>+1 分钟的全局快捷键（空 = 不设置）。</summary>
    public string AddMinuteHotkey
    {
        get => _addMinuteHotkey;
        set => Set(ref _addMinuteHotkey, value ?? "");
    }

    private bool _shortcutsEnabled = true;

    /// <summary>这个工具的快捷键总开关。</summary>
    public bool ShortcutsEnabled
    {
        get => _shortcutsEnabled;
        set => Set(ref _shortcutsEnabled, value);
    }

    private bool _startEnabled = true;

    /// <summary>「开始 / 继续」这条启用。</summary>
    public bool StartEnabled
    {
        get => _startEnabled;
        set => Set(ref _startEnabled, value);
    }

    private bool _pauseEnabled = true;

    /// <summary>「暂停」这条启用。</summary>
    public bool PauseEnabled
    {
        get => _pauseEnabled;
        set => Set(ref _pauseEnabled, value);
    }

    private bool _resetEnabled = true;

    /// <summary>「重置」这条启用。</summary>
    public bool ResetEnabled
    {
        get => _resetEnabled;
        set => Set(ref _resetEnabled, value);
    }

    private bool _addMinuteEnabled = true;

    /// <summary>「+1 分钟」这条启用。</summary>
    public bool AddMinuteEnabled
    {
        get => _addMinuteEnabled;
        set => Set(ref _addMinuteEnabled, value);
    }

    // ---------- 到点提醒 ----------

    private string _finishSound = "system:SystemExclamation";

    /// <summary>到点提示音（<c>none</c> / <c>system:别名</c> / <c>file:路径</c>）。</summary>
    public string FinishSound
    {
        get => _finishSound;
        set => Set(ref _finishSound, value ?? Core.Media.SoundPlayer.NoneId);
    }

    private bool _muted;

    /// <summary>静音（保留声音选择，只是不播）。</summary>
    public bool Muted
    {
        get => _muted;
        set => Set(ref _muted, value);
    }

    private NotificationSettings _notification = new()
    {
        Mode = NotificationMode.InApp,
        Corner = NotificationCorner.BottomRight,
    };

    /// <summary>通知偏好（到点提醒 + 快捷键操作提示）。</summary>
    public NotificationSettings Notification
    {
        get => _notification;
        set => Set(ref _notification, value ?? new NotificationSettings());
    }

    // ---------- 显示 ----------

    private bool _showProgressRing = true;

    /// <summary>显示进度环（关掉 = 只显示数字）。</summary>
    public bool ShowProgressRing
    {
        get => _showProgressRing;
        set => Set(ref _showProgressRing, value);
    }
}
