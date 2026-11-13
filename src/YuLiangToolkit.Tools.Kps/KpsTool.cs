using System.Windows;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Kps.ViewModels;
using YuLiangToolkit.Tools.Kps.Views;

namespace YuLiangToolkit.Tools.Kps;

/// <summary>
/// 「KPS 按键统计」工具入口：把全局键盘输入变成实时 KPS / 每键计数 / 键位可视化，并提供置顶透明的 Overlay。
///
/// <para>功能灵感来自开源项目 <b>DmNote</b>（<see href="https://github.com/DmNote-App/DmNote"/>，
/// GPL-3.0）：本实现是面向 WPF / YuLiangToolkit 的<b>独立重写</b>，没有复制它的源码；
/// 来源与致敬写在 <c>docs/tools/kps.md</c>。未实现直播插件 / OBS 浏览器源 / 自定义 CSS / JS 插件。</para>
///
/// <para><b>快捷键</b>：四条热键由 <see cref="KpsSession"/> 注册（与监听 / Overlay 的生命周期绑在一起），
/// 这里只把它们**声明**给主页设置的快捷键汇总（<see cref="IShortcutProvider"/>）：
/// 汇总页改键 / 启停 → 写回 <see cref="KpsConfig"/> → 调会话重新注册。</para>
/// </summary>
public sealed class KpsTool : ITool, IShortcutProvider
{
    /// <summary>功能 Id（= Data / Config 下的小文件夹名，也是 Overlay 配置的归属）。</summary>
    public const string ToolId = "kps";

    /// <summary>快捷键 Id（与汇总页、自检共用）。</summary>
    public const string ShortcutIdOverlay = "toggle-overlay";

    /// <summary>见 <see cref="ShortcutIdOverlay"/>。</summary>
    public const string ShortcutIdLock = "toggle-lock";

    /// <summary>见 <see cref="ShortcutIdOverlay"/>。</summary>
    public const string ShortcutIdReset = "reset-stats";

    /// <summary>见 <see cref="ShortcutIdOverlay"/>。</summary>
    public const string ShortcutIdListening = "toggle-listening";

    private readonly ShortcutProviderAdapter _adapter;

    /// <summary>构造：把四条热键映射到配置字段上。</summary>
    public KpsTool()
    {
        _adapter = new ShortcutProviderAdapter(BuildSlots, ApplyShortcuts, () => ShortcutsChanged?.Invoke(this, EventArgs.Empty));
    }

    public string Id => ToolId;

    public string Name => "KPS 按键统计";

    public string Description =>
        "实时 KPS / 每键计数 / 键位可视化 + 置顶透明 Overlay；支持 4K~8K 预设、改键、按键图片、音符效果、键音与全局热键（灵感来源 DmNote）";

    public string EmojiIcon => "🎹";

    public string Category => "音游工具";

    public IReadOnlyList<string> CategoryPath => new[] { "音游工具" };

    public IReadOnlyList<string> Tags => new[]
    {
        "KPS", "按键统计", "键位", "键 viewer", "Overlay", "音游", "DJMAX", "DmNote",
    };

    public int Order => 150;

    public FrameworkElement CreateView()
    {
        // 监听 / 统计 / Overlay 归会话管：页面重开时复用同一个 ViewModel（统计与 Overlay 不断）
        var session = KpsSession.Current;
        var viewModel = session.EnsureViewModel();
        session.Activate(viewModel);
        return new KpsView { DataContext = viewModel };
    }

    // ================================================================== IShortcutProvider

    /// <summary>工具 Id。</summary>
    public string ShortcutToolId => Id;

    /// <summary>工具名（汇总页分组标题）。</summary>
    public string ShortcutToolName => Name;

    /// <summary>分类路径。</summary>
    public IReadOnlyList<string> ShortcutCategoryPath => CategoryPath;

    /// <summary>整个工具的快捷键总开关。</summary>
    public bool ShortcutsEnabled
    {
        get => CurrentConfig?.ShortcutsEnabled ?? true;
        set
        {
            if (CurrentConfig is not { } config || config.ShortcutsEnabled == value) return;
            config.ShortcutsEnabled = value;
            ApplyShortcuts();
        }
    }

    /// <inheritdoc />
    public event EventHandler? ShortcutsChanged;

    /// <inheritdoc />
    public IReadOnlyList<ShortcutDefinition> GetShortcuts()
        => CurrentConfig is null ? Array.Empty<ShortcutDefinition>() : _adapter.Build(ToolId);

    /// <inheritdoc />
    public void ApplyShortcuts()
    {
        // 会话活着就按新配置重注册；没活（页面没打开过）时配置已经写好了，下次激活会读
        KpsSession.Current.RefreshHotkeysQuietly();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>会话里那份配置（页面没打开过时为 null，此时汇总页不显示这个工具）。</summary>
    private static KpsConfig? CurrentConfig => KpsSession.Current.Config;

    /// <summary>四条热键与配置字段的对应关系（enable 位 / 文本都在 <see cref="KpsConfig"/> 上）。</summary>
    private IReadOnlyList<ShortcutProviderAdapter.Slot> BuildSlots()
    {
        var config = CurrentConfig;
        if (config is null) return Array.Empty<ShortcutProviderAdapter.Slot>();

        return new[]
        {
            new ShortcutProviderAdapter.Slot(
                ShortcutIdOverlay, "显示 / 隐藏 Overlay",
                () => config.HotkeyToggleOverlay, value => config.HotkeyToggleOverlay = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.HotkeyToggleOverlayEnabled,
                WriteEnabled: value => config.HotkeyToggleOverlayEnabled = value,
                Description: "KPS：把键位 Overlay 显示 / 藏起来"),

            new ShortcutProviderAdapter.Slot(
                ShortcutIdLock, "锁定 / 解锁 Overlay",
                () => config.HotkeyToggleLock, value => config.HotkeyToggleLock = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.HotkeyToggleLockEnabled,
                WriteEnabled: value => config.HotkeyToggleLockEnabled = value,
                Description: "KPS：解锁后可以用鼠标拖动 / 滚轮缩放"),

            new ShortcutProviderAdapter.Slot(
                ShortcutIdReset, "重置统计",
                () => config.HotkeyResetStats, value => config.HotkeyResetStats = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.HotkeyResetStatsEnabled,
                WriteEnabled: value => config.HotkeyResetStatsEnabled = value,
                Description: "KPS：清零 KPS 与每键计数"),

            new ShortcutProviderAdapter.Slot(
                ShortcutIdListening, "暂停 / 继续监听",
                () => config.HotkeyToggleListening, value => config.HotkeyToggleListening = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.HotkeyToggleListeningEnabled,
                WriteEnabled: value => config.HotkeyToggleListeningEnabled = value,
                Description: "KPS：暂停后所有键都不计数"),
        };
    }
}
