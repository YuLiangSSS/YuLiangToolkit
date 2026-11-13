using System.Windows;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Media;
using YuLiangToolkit.Core.Notifications;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Countdown.ViewModels;
using YuLiangToolkit.Tools.Countdown.Views;

namespace YuLiangToolkit.Tools.Countdown;

/// <summary>
/// 「倒计时」工具入口：设定时长 → 开始倒数 → 到点弹通知 + 响铃。
/// 开始 / 暂停 / 重置 / +1 分钟都可有全局快捷键（在别的前台程序里也能按）。
/// </summary>
public sealed class CountdownTool : ITool, IShortcutProvider, IShortcutActionHost
{
    /// <summary>快捷键 Id 常量（与汇总页、自检共用）。</summary>
    public const string ShortcutIdStart = "start";

    /// <summary>见 <see cref="ShortcutIdStart"/>。</summary>
    public const string ShortcutIdPause = "pause";

    /// <summary>见 <see cref="ShortcutIdStart"/>。</summary>
    public const string ShortcutIdReset = "reset";

    /// <summary>见 <see cref="ShortcutIdStart"/>。</summary>
    public const string ShortcutIdAddMinute = "add-minute";

    private readonly CountdownViewModel _viewModel;
    private readonly CountdownConfig _config;

    /// <summary>构造：建常驻 ViewModel（它自己读配置、起刷新计时器）。</summary>
    public CountdownTool()
    {
        // ⚠ 顺序很重要：ViewModel 构造期间会读配置、也可能触发 ApplyShortcuts，
        // 那时 _viewModel / _config 还没赋值，GetShortcuts 会 NRE（自检踩过）。
        _viewModel = new CountdownViewModel(this);
        _config = _viewModel.Cfg;

        _config.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is null or nameof(CountdownConfig.ShortcutsEnabled)
                or nameof(CountdownConfig.StartHotkey) or nameof(CountdownConfig.PauseHotkey)
                or nameof(CountdownConfig.ResetHotkey) or nameof(CountdownConfig.AddMinuteHotkey)
                or nameof(CountdownConfig.StartEnabled) or nameof(CountdownConfig.PauseEnabled)
                or nameof(CountdownConfig.ResetEnabled) or nameof(CountdownConfig.AddMinuteEnabled))
            {
                ApplyShortcuts();
            }
        };
    }

    /// <summary>强类型配置。</summary>
    public CountdownConfig Config => _config;

    /// <summary>常驻 ViewModel（工具页与快捷键共用）。</summary>
    public CountdownViewModel ViewModel => _viewModel;

    /// <summary>是不是已经构造完（可以安全回调到本对象）。</summary>
    public bool IsFullyConstructed => _viewModel is not null && _config is not null;

    // ================================================================== ITool

    public string Id => CountdownConfig.ToolId;

    public string Name => "倒计时";

    public string Description =>
        "任意时长倒计时（5 分钟 / 25 分钟番茄钟 / 自定义），支持开始、暂停、重置、+1 分钟，"
        + "到点弹通知并响铃；四个操作都能设全局快捷键，在任何程序前面都能按。";

    public string EmojiIcon => "⏳";

    public string Category => "系统";

    public IReadOnlyList<string> CategoryPath => new[] { "系统" };

    public IReadOnlyList<string> Tags => new[]
    {
        "倒计时", "计时", "番茄钟", "countdown", "快捷键", "提醒",
    };

    /// <summary>排在系统分类里秒表 (170) 与通知管理 (172) 之间。</summary>
    public int Order => 171;

    public FrameworkElement CreateView() => new CountdownView { DataContext = _viewModel };

    // ================================================================== IShortcutProvider / IShortcutActionHost

    public string ShortcutToolId => Id;

    public string ShortcutToolName => Name;

    public IReadOnlyList<string> ShortcutCategoryPath => CategoryPath;

    public bool ShortcutsEnabled
    {
        get => Config.ShortcutsEnabled;
        set
        {
            if (!IsFullyConstructed || Config.ShortcutsEnabled == value) return;
            Config.ShortcutsEnabled = value;
            ApplyShortcuts();
        }
    }

    /// <inheritdoc />
    public event EventHandler? ShortcutsChanged;

    /// <inheritdoc />
    public IReadOnlyList<ShortcutDefinition> GetShortcuts()
    {
        // 构造期间（自检里 new 出来就会问）配置还没接上：返回空列表，构造完再问就有
        if (!IsFullyConstructed) return Array.Empty<ShortcutDefinition>();

        return new[]
        {
            Build(ShortcutIdStart, "开始 / 继续", _config.StartHotkey, _config.StartEnabled),
            Build(ShortcutIdPause, "暂停", _config.PauseHotkey, _config.PauseEnabled),
            Build(ShortcutIdReset, "重置", _config.ResetHotkey, _config.ResetEnabled),
            Build(ShortcutIdAddMinute, "+1 分钟", _config.AddMinuteHotkey, _config.AddMinuteEnabled),
        };
    }

    /// <inheritdoc />
    public void ApplyShortcuts()
    {
        if (!IsFullyConstructed) return;

        _viewModel.SyncShortcuts();
        ShortcutHub.ApplyAll();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void InvokeShortcut(string shortcutId)
    {
        switch (shortcutId)
        {
            case ShortcutIdStart:
                _viewModel.StartFromShortcut();
                break;
            case ShortcutIdPause:
                _viewModel.PauseFromShortcut();
                break;
            case ShortcutIdReset:
                _viewModel.ResetFromShortcut();
                break;
            case ShortcutIdAddMinute:
                _viewModel.AddMinuteFromShortcut();
                break;
        }
    }

    private ShortcutDefinition Build(string id, string name, string comboText, bool enabled)
    {
        KeyCombo.TryParse(comboText, out var primary);

        return new ShortcutDefinition(
            id, Id, name, primary,
            secondary: null,
            enabled, canSplit: false,
            setCombo: (combo, _) =>
            {
                var value = combo.IsEmpty ? "" : combo.ToString();
                switch (id)
                {
                    case ShortcutIdStart: _config.StartHotkey = value; break;
                    case ShortcutIdPause: _config.PauseHotkey = value; break;
                    case ShortcutIdReset: _config.ResetHotkey = value; break;
                    default: _config.AddMinuteHotkey = value; break;
                }

                ApplyShortcuts();
            },
            setEnabled: value =>
            {
                switch (id)
                {
                    case ShortcutIdStart: _config.StartEnabled = value; break;
                    case ShortcutIdPause: _config.PauseEnabled = value; break;
                    case ShortcutIdReset: _config.ResetEnabled = value; break;
                    default: _config.AddMinuteEnabled = value; break;
                }

                ApplyShortcuts();
            },
            description: "倒计时：" + name);
    }
}
