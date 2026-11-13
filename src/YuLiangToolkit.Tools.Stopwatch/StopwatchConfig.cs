using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.Stopwatch;

/// <summary>
/// 秒表的强类型配置 → <c>Config\stopwatch\config.json</c>。
///
/// <para>三条快捷键各存「组合文本 + 启用位」；形态（同时 / 分步）由组合里有几段决定：
/// <see cref="Start"/> 单段 + <see cref="StartSecond"/> 空 = 同时，两段都有 = 分步。</para>
/// </summary>
public sealed class StopwatchConfig : ToolConfig
{
    /// <summary>工具 Id（Data / Config / Cache 目录名）。</summary>
    [JsonIgnore]
    public static string ToolId => "stopwatch";

    // ---------- 快捷键 ----------

    private string _start = "Ctrl+Alt+F9";

    /// <summary>开始 / 继续（单段组合，或分步的第一步）。</summary>
    public string Start
    {
        get => _start;
        set => Set(ref _start, value ?? "");
    }

    private string _startSecond = "";

    /// <summary>分步时开始快捷键的第二步（空 = 单步）。</summary>
    public string StartSecond
    {
        get => _startSecond;
        set => Set(ref _startSecond, value ?? "");
    }

    private string _pause = "F10";

    /// <summary>暂停（单段，或分步的第一步）。</summary>
    public string Pause
    {
        get => _pause;
        set => Set(ref _pause, value ?? "");
    }

    private string _pauseSecond = "";

    /// <summary>分步时暂停快捷键的第二步（空 = 单步）。</summary>
    public string PauseSecond
    {
        get => _pauseSecond;
        set => Set(ref _pauseSecond, value ?? "");
    }

    private string _stop = "F11";

    /// <summary>停止并归零（单段，或分步的第一步）。</summary>
    public string Stop
    {
        get => _stop;
        set => Set(ref _stop, value ?? "");
    }

    private string _stopSecond = "";

    /// <summary>分步时停止快捷键的第二步（空 = 单步）。</summary>
    public string StopSecond
    {
        get => _stopSecond;
        set => Set(ref _stopSecond, value ?? "");
    }

    private bool _startEnabled = true;

    /// <summary>「开始」快捷键启用。</summary>
    public bool StartEnabled
    {
        get => _startEnabled;
        set => Set(ref _startEnabled, value);
    }

    private bool _pauseEnabled = true;

    /// <summary>「暂停」快捷键启用。</summary>
    public bool PauseEnabled
    {
        get => _pauseEnabled;
        set => Set(ref _pauseEnabled, value);
    }

    private bool _stopEnabled = true;

    /// <summary>「停止」快捷键启用。</summary>
    public bool StopEnabled
    {
        get => _stopEnabled;
        set => Set(ref _stopEnabled, value);
    }

    private bool _shortcutsEnabled = true;

    /// <summary>这个工具的快捷键总开关（关掉 = 三条全部不注册，各自状态保留）。</summary>
    public bool ShortcutsEnabled
    {
        get => _shortcutsEnabled;
        set => Set(ref _shortcutsEnabled, value);
    }

    private int _sequenceTimeoutMs = 2000;

    /// <summary>
    /// 分步快捷键两步之间等多久（500~5000ms）。
    /// <para>⚠ v1.45.0 起**不再使用**：分步等待时长改成全程序统一设置（主页设置 → 快捷键），
    /// 存 <c>ShellConfig.ShortcutSequenceTimeoutMs</c>。这里保留字段只是为了能读老配置、
    /// 不破坏已有 JSON。</para>
    /// </summary>
    public int SequenceTimeoutMs
    {
        get => _sequenceTimeoutMs;
        set => Set(ref _sequenceTimeoutMs, Math.Clamp(value, 500, 5000));
    }

    // ---------- 声音 ----------

    private string _startSound = "system:SystemNotification";

    /// <summary>「开始」提示音（<c>none</c> / <c>system:别名</c> / <c>file:路径</c>）。</summary>
    public string StartSound
    {
        get => _startSound;
        set => Set(ref _startSound, value ?? Core.Media.SoundPlayer.NoneId);
    }

    private string _pauseSound = "system:SystemAsterisk";

    /// <summary>「暂停」提示音。</summary>
    public string PauseSound
    {
        get => _pauseSound;
        set => Set(ref _pauseSound, value ?? Core.Media.SoundPlayer.NoneId);
    }

    private string _stopSound = "system:SystemExclamation";

    /// <summary>「停止」提示音。</summary>
    public string StopSound
    {
        get => _stopSound;
        set => Set(ref _stopSound, value ?? Core.Media.SoundPlayer.NoneId);
    }

    private bool _muted;

    /// <summary>一键静音（保留三个声音的选择，但都不播）。</summary>
    public bool Muted
    {
        get => _muted;
        set => Set(ref _muted, value);
    }

    // ---------- 显示 ----------

    private bool _showMilliseconds = true;

    /// <summary>显示毫秒（关掉 = 只显示到「秒.十分之一」）。</summary>
    public bool ShowMilliseconds
    {
        get => _showMilliseconds;
        set => Set(ref _showMilliseconds, value);
    }

    private bool _showLaps = true;

    /// <summary>显示计次（分段）列表。</summary>
    public bool ShowLaps
    {
        get => _showLaps;
        set => Set(ref _showLaps, value);
    }

    // ---------- 通知（这个工具自己的一份，不与其他工具共享） ----------

    private NotificationSettings _notification = new();

    /// <summary>通知偏好：通道 / 方位 / 时长（按你的要求，每个工具分开设）。</summary>
    public NotificationSettings Notification
    {
        get => _notification;
        set => Set(ref _notification, value ?? new NotificationSettings());
    }

    // ---------- 计算属性 ----------

    /// <summary>任何一条快捷键是分步形态（界面提示「有个等待时长」用）。</summary>
    [JsonIgnore]
    public bool AnySequence => !string.IsNullOrWhiteSpace(StartSecond)
                               || !string.IsNullOrWhiteSpace(PauseSecond)
                               || !string.IsNullOrWhiteSpace(StopSecond);
}
