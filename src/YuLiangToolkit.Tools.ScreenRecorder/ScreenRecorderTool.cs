using System.Windows;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.ScreenRecorder.ViewModels;
using YuLiangToolkit.Tools.ScreenRecorder.Views;

namespace YuLiangToolkit.Tools.ScreenRecorder;

/// <summary>
/// 「录屏 / GIF」：用系统 FFmpeg 的 gdigrab 采集屏幕，录成 GIF（两遍调色板）或 MP4，
/// 也支持 PNG 截图、区域框选、窗口选择、倒计时、置顶控制条和 F8 全局停止热键。
///
/// <para><b>快捷键</b>：热键本身由 <see cref="ScreenRecorderViewModel"/> 注册（它手里有
/// 录制状态机），这里只把两条热键**声明**给主页设置的快捷键汇总：改键 / 启停 → 写回配置
/// → 让 ViewModel 重新注册。页面没打开过时配置照样能改，下次打开会读。</para>
/// </summary>
public class ScreenRecorderTool : ITool, IShortcutProvider
{
    /// <summary>快捷键 Id（与汇总页、自检共用）。</summary>
    public const string ShortcutIdStartStop = "start-stop-recording";

    /// <summary>见 <see cref="ShortcutIdStartStop"/>。</summary>
    public const string ShortcutIdScreenshot = "screenshot";

    /// <summary>当前活着的页面 ViewModel（页面没打开过时为 null）。</summary>
    private static ScreenRecorderViewModel? _liveViewModel;

    private readonly ShortcutProviderAdapter _adapter;

    /// <summary>构造：两条热键映射到配置字段。</summary>
    public ScreenRecorderTool()
    {
        _adapter = new ShortcutProviderAdapter(BuildSlots, ApplyShortcuts,
            () => ShortcutsChanged?.Invoke(this, EventArgs.Empty));
    }

    public string Id => "screen-recorder";

    public string Name => "录屏 / GIF";

    public string Description
        => "OBS 风格但保持简单的录屏：全屏 / 框选区域 / 窗口 → GIF 或 MP4 / MKV / MOV / AVI / WebM，"
           + "编码器 / 画质 / 码率可选；支持麦克风 + 系统声音（录制中可调音量）、摄像头预览与屏幕叠加、"
           + "可自定义全局录制 / 截图快捷键，也能一键截 PNG。";

    public string EmojiIcon => "🎥";

    public string Category => "媒体";

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "录屏", "GIF", "动图", "屏幕录制", "区域录制", "窗口录制", "摄像头", "麦克风", "系统声音",
        "MP4", "AVI", "WebM", "编码器", "快捷键", "截图", "FFmpeg", "媒体",
    };

    /// <summary>音频工具是 10，录屏接在它后面。</summary>
    public int Order => 11;

    public FrameworkElement CreateView()
    {
        var viewModel = new ScreenRecorderViewModel();
        _liveViewModel = viewModel;
        return new ScreenRecorderView { DataContext = viewModel };
    }

    // ================================================================== IShortcutProvider

    /// <summary>工具 Id。</summary>
    public string ShortcutToolId => Id;

    /// <summary>工具名。</summary>
    public string ShortcutToolName => Name;

    /// <summary>分类路径（录屏没有 CategoryPath，这里给「媒体」目录，和主页卡片一致）。</summary>
    public IReadOnlyList<string> ShortcutCategoryPath => new[] { Category };

    /// <summary>整个工具的快捷键总开关（复用已有的「启用全局快捷键」）。</summary>
    public bool ShortcutsEnabled
    {
        get => CurrentConfig?.EnableGlobalHotkeys ?? true;
        set
        {
            if (CurrentConfig is not { } config || config.EnableGlobalHotkeys == value) return;
            config.EnableGlobalHotkeys = value;
            ApplyShortcuts();
        }
    }

    /// <inheritdoc />
    public event EventHandler? ShortcutsChanged;

    /// <inheritdoc />
    public IReadOnlyList<ShortcutDefinition> GetShortcuts()
        => CurrentConfig is null ? Array.Empty<ShortcutDefinition>() : _adapter.Build(Id);

    /// <inheritdoc />
    public void ApplyShortcuts()
    {
        _liveViewModel?.ApplyHotkeys();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 汇总页 / 自检用的配置。
    /// <para>⚠ 页面没打开过也要能读到（设置页要看 / 改这两条热键），所以没 ViewModel 时
    /// 直接读一次配置 —— <see cref="ToolConfigStore"/> 缓存同一实例，页面打开后拿到的是同一份。</para>
    /// </summary>
    private static ScreenRecorderConfig? CurrentConfig
    {
        get
        {
            if (_liveViewModel?.Cfg is { } live) return live;

            try { return ToolConfigStore.For("screen-recorder").Load<ScreenRecorderConfig>(); }
            catch { return null; }
        }
    }

    private IReadOnlyList<ShortcutProviderAdapter.Slot> BuildSlots()
    {
        var config = CurrentConfig;
        if (config is null) return Array.Empty<ShortcutProviderAdapter.Slot>();

        return new[]
        {
            new ShortcutProviderAdapter.Slot(
                ShortcutIdStartStop, "开始 / 停止录制",
                () => config.StartStopHotkey, value => config.StartStopHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.StartStopHotkeyEnabled,
                WriteEnabled: value => config.StartStopHotkeyEnabled = value,
                Description: "录屏：按一下开始，再按一下停止"),

            new ShortcutProviderAdapter.Slot(
                ShortcutIdScreenshot, "截图（PNG）",
                () => config.ScreenshotHotkey, value => config.ScreenshotHotkey = value,
                ReadSecondary: null, WriteSecondary: null,
                ReadEnabled: () => config.ScreenshotHotkeyEnabled,
                WriteEnabled: value => config.ScreenshotHotkeyEnabled = value,
                Description: "录屏：立刻截一张 PNG"),
        };
    }
}
