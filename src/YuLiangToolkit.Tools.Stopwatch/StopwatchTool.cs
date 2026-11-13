using System.Windows;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Stopwatch.ViewModels;
using YuLiangToolkit.Tools.Stopwatch.Views;

namespace YuLiangToolkit.Tools.Stopwatch;

/// <summary>
/// 「秒表」工具入口。
///
/// <para><b>它实现了三个接口</b>：</para>
/// <list type="bullet">
///   <item><see cref="ITool"/>：主页卡片 / 工具页；</item>
///   <item><see cref="IShortcutProvider"/>：把自己的三条快捷键暴露给主页设置的快捷键汇总，
///         用户在那里就能改键、拆分 / 合并、单条禁用；</item>
///   <item><see cref="IShortcutActionHost"/>：拿来当快捷键引擎的回调入口（<c>TriggerShortcut</c>）。</item>
/// </list>
///
/// <para><b>为什么快捷键注册在工具实例上而不是页面 ViewModel 上</b>：页面是懒加载的、
/// 切走还可能被缓存淘汰；「在别的程序前面按快捷键也能用」要求注册与页面无关。</para>
///
/// <para>会话（<see cref="ViewModel"/>）也只建一次并常驻：这样切页面回来计时还在走。</para>
/// </summary>
public sealed class StopwatchTool : ITool, IShortcutProvider, IShortcutActionHost
{
    private readonly StopwatchViewModel _viewModel;

    /// <summary>构造：建常驻的 ViewModel（它自己负责读配置、起刷新计时器）。</summary>
    public StopwatchTool()
    {
        // 先把 ViewModel 建好再挂事件：ViewModel 构造期间会改判配置（初始化声音下拉等），
        // 那时 _viewModel 还没赋值，事件里访问 Config 就会 NRE。
        _viewModel = new StopwatchViewModel(this);
        _config = _viewModel.Cfg;

        _config.PropertyChanged += (_, args) =>
        {
            // 配置变了（在秒表页改，或在主页设置汇总页改）都要重新注册一次，
            // 否则「改了键但旧键还生效」这种 bug 特别难查。
            if (args.PropertyName is null or nameof(StopwatchConfig.ShortcutsEnabled)
                or nameof(StopwatchConfig.Start) or nameof(StopwatchConfig.StartSecond)
                or nameof(StopwatchConfig.Pause) or nameof(StopwatchConfig.PauseSecond)
                or nameof(StopwatchConfig.Stop) or nameof(StopwatchConfig.StopSecond)
                or nameof(StopwatchConfig.StartEnabled) or nameof(StopwatchConfig.PauseEnabled)
                or nameof(StopwatchConfig.StopEnabled))
            {
                ApplyShortcuts();
            }
        };
    }

    private readonly StopwatchConfig _config;

    /// <summary>秒表的强类型配置。</summary>
    public StopwatchConfig Config => _config;

    /// <summary>
    /// 工具是不是已经构造完（<see cref="_viewModel"/> 已赋值、可以安全回调到本对象）。
    /// <para>ViewModel 构造期间会调 <c>SyncShortcuts</c>，那时这里还是 false —— 用它挡住
    /// 「回调工具 → 工具再回调 ViewModel」的空引用。</para>
    /// </summary>
    public bool IsFullyConstructed => _viewModel is not null;

    /// <summary>常驻的 ViewModel（工具页与快捷键共用同一个）。</summary>
    public StopwatchViewModel ViewModel => _viewModel;

    // ================================================================== ITool

    public string Id => StopwatchConfig.ToolId;

    public string Name => "秒表";

    public string Description =>
        "开始 / 暂停 / 停止 / 计次，大号数字好认；可设全局快捷键，在任何程序前面都能按，"
        + "软件在后台时按快捷键会在屏幕角落弹通知；开始 / 暂停 / 停止三段声音各自可设。";

    public string EmojiIcon => "⏱️";

    public string Category => "系统";

    public IReadOnlyList<string> CategoryPath => new[] { "系统" };

    public IReadOnlyList<string> Tags => new[]
    {
        "秒表", "计时", "计时器", "stopwatch", "快捷键", "热键", "计次",
    };

    /// <summary>主页排序：排在「系统」分类里其它工具之后。</summary>
    public int Order => 170;

    public FrameworkElement CreateView() => new StopwatchView { DataContext = _viewModel };

    // ================================================================== IShortcutProvider

    public string ShortcutToolId => Id;

    public string ShortcutToolName => Name;

    public IReadOnlyList<string> ShortcutCategoryPath => CategoryPath;

    public bool ShortcutsEnabled
    {
        get => Config.ShortcutsEnabled;
        set
        {
            if (Config.ShortcutsEnabled == value) return;
            Config.ShortcutsEnabled = value;
            ApplyShortcuts();
        }
    }

    /// <summary>快捷键定义变了（设置页刷新用）。</summary>
    public event EventHandler? ShortcutsChanged;

    /// <summary>按当前配置生成三条快捷键的实时快照。</summary>
    public IReadOnlyList<ShortcutDefinition> GetShortcuts()
    {
        // 构造期间（自检里 new 出来就会问）配置还没接上：返回空列表，等构造完再问就有
        if (!IsFullyConstructed) return Array.Empty<ShortcutDefinition>();

        return new[]
        {
            Build(ShortcutIdStart, "开始 / 继续", Config.Start, Config.StartSecond,
                Config.StartEnabled, nameof(StopwatchConfig.Start)),
            Build(ShortcutIdPause, "暂停", Config.Pause, Config.PauseSecond,
                Config.PauseEnabled, nameof(StopwatchConfig.Pause)),
            Build(ShortcutIdStop, "停止并归零", Config.Stop, Config.StopSecond,
                Config.StopEnabled, nameof(StopwatchConfig.Stop)),
        };
    }

    /// <summary>快捷键 Id 常量（设置页 / 自检用）。</summary>
    public const string ShortcutIdStart = "start";

    /// <summary>见 <see cref="ShortcutIdStart"/>。</summary>
    public const string ShortcutIdPause = "pause";

    /// <summary>见 <see cref="ShortcutIdStart"/>。</summary>
    public const string ShortcutIdStop = "stop";

    private ShortcutDefinition Build(string id, string name, string primaryText, string secondaryText,
        bool enabled, string configPropertyPrefix)
    {
        KeyCombo.TryParse(primaryText, out var primary);
        KeyCombo? secondary = KeyCombo.TryParse(secondaryText, out var parsedSecond) ? parsedSecond : null;

        return new ShortcutDefinition(
            id,
            Id,
            name,
            primary,
            secondary,
            enabled,
            canSplit: true,
            setCombo: (combo, second) =>
            {
                var primaryValue = combo.IsEmpty ? "" : combo.ToString();
                var secondaryValue = second is null || second.IsEmpty ? "" : second.ToString();

                if (configPropertyPrefix == nameof(StopwatchConfig.Start))
                {
                    Config.Start = primaryValue;
                    Config.StartSecond = secondaryValue;
                }
                else if (configPropertyPrefix == nameof(StopwatchConfig.Pause))
                {
                    Config.Pause = primaryValue;
                    Config.PauseSecond = secondaryValue;
                }
                else
                {
                    Config.Stop = primaryValue;
                    Config.StopSecond = secondaryValue;
                }

                ApplyShortcuts();
            },
            setEnabled: value =>
            {
                if (configPropertyPrefix == nameof(StopwatchConfig.Start)) Config.StartEnabled = value;
                else if (configPropertyPrefix == nameof(StopwatchConfig.Pause)) Config.PauseEnabled = value;
                else Config.StopEnabled = value;

                ApplyShortcuts();
            },
            description: "秒表：" + name);
    }

    /// <summary>按当前配置重新注册（引擎由外壳提供，这里通过静态入口拿）。</summary>
    public void ApplyShortcuts()
    {
        if (!IsFullyConstructed) return;   // 构造期间只写配置，注册交给之后的那次调用
        _viewModel.SyncShortcuts();
        ShortcutHub.ApplyAll();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ================================================================== IShortcutActionHost

    /// <summary>分步快捷键两步之间等多久（用户可在秒表设置里调）。</summary>
    public int SequenceTimeoutMs
    {
        get => Config.SequenceTimeoutMs;
        set
        {
            if (Config.SequenceTimeoutMs == value) return;
            Config.SequenceTimeoutMs = value;
            if (!IsFullyConstructed) return;   // 构造期间只写配置，不回调
            _viewModel.SyncShortcuts();
            ShortcutHub.ApplyAll();
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>某条快捷键被按下（引擎在 UI 线程回调）。</summary>
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
            case ShortcutIdStop:
                _viewModel.StopFromShortcut();
                break;
        }
    }
}
