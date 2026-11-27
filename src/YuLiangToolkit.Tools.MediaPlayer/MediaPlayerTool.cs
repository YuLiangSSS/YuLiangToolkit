using System.Windows;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.MediaPlayer.ViewModels;
using YuLiangToolkit.Tools.MediaPlayer.Views;

namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 「视频 / 音乐播放器」——左中右三栏：中间播放（视频 / 封面 + 进度条 + 播放控制），右边播放列表。
///
/// <para>能力：打开文件夹 / 文件 / 拖放（可按音频 / 视频 / 一并过滤、可递归、可选择追加还是覆盖）、
/// 列表循环 / 单曲循环 / 随机播放、可调步长的快进快退（**左键快进快退、右键上一首下一首**）、
/// 音量、记住上次的列表与位置。</para>
///
/// <para><b>全局快捷键</b>（v1.50.0）：实现 <see cref="IShortcutProvider"/> +
/// <see cref="IShortcutActionHost"/>，把「上一首 / 下一首 / 播放暂停」三条快捷键暴露到
/// 主页设置 → 快捷键汇总；在别的程序窗口前面也能切歌 / 暂停。
/// <b>为什么播放器要有常驻 ViewModel</b>：快捷键回调必须在「页面没打开」时也能用，
/// 所以 <see cref="CreateView"/> 复用同一个 <see cref="ViewModel"/>（见类字段说明）。
/// 声明 + 配线细节见 docs/dev/shortcuts.md。</para>
///
/// <para>分层与踩过的坑见 <c>docs/dev/pitfalls.md</c>。</para>
/// </summary>
public class MediaPlayerTool : ITool, IShortcutProvider, IShortcutActionHost
{
    /// <summary>
    /// 常驻的 ViewModel：工具实例只建它一次。页面是新开还是从缓存取回，都绑这同一个 VM ——
    /// 这样快捷键（无论页面开没开）和界面操作的是同一份播放状态。
    /// <para>⚠ 构造顺序：先建 VM（它会读配置、恢复上次列表），再建快捷键适配器，
    /// 因为适配器的槽位要读 <see cref="ViewModel"/> 的 Cfg。</para>
    /// </summary>
    private readonly MediaPlayerViewModel _viewModel;

    /// <summary>快捷键适配器：把配置里的「组合文本」翻译成汇总页要的 <see cref="ShortcutDefinition"/>。</summary>
    private readonly ShortcutProviderAdapter _shortcutAdapter;

    public MediaPlayerTool()
    {
        _viewModel = new MediaPlayerViewModel();

        _shortcutAdapter = new ShortcutProviderAdapter(
            slots: BuildShortcutSlots,
            apply: ApplyShortcuts,
            notifyChanged: () => ShortcutsChanged?.Invoke(this, EventArgs.Empty));

        // 在秒表页或主页设置里改了键 / 启停位 → 重新注册一次（否则「改了键旧键还生效」的 bug 极难查）
        _viewModel.Cfg.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is null
                or nameof(MediaPlayerConfig.ShortcutsEnabled)
                or nameof(MediaPlayerConfig.ShortcutPrevious) or nameof(MediaPlayerConfig.ShortcutPreviousSecond)
                or nameof(MediaPlayerConfig.ShortcutNext) or nameof(MediaPlayerConfig.ShortcutNextSecond)
                or nameof(MediaPlayerConfig.ShortcutPlayPause) or nameof(MediaPlayerConfig.ShortcutPlayPauseSecond)
                or nameof(MediaPlayerConfig.ShortcutPreviousEnabled)
                or nameof(MediaPlayerConfig.ShortcutNextEnabled)
                or nameof(MediaPlayerConfig.ShortcutPlayPauseEnabled))
            {
                ApplyShortcuts();
            }
        };
    }

    /// <summary>常驻的 ViewModel（工具页与快捷键共用同一个）。</summary>
    public MediaPlayerViewModel ViewModel => _viewModel;

    // ================================================================== ITool

    public string Id => MediaPlayerConfig.ToolId;

    public string Name => "视频 / 音乐播放器";

    public string Description => "本地播放器：打开文件夹或文件就能听歌看片，支持列表 / 单曲 / 随机、可调步长的快进快退、音量与上次进度恢复；可设全局快捷键在别的窗口切歌 / 暂停";

    public string EmojiIcon => "🎬";

    public string Category => "媒体";

    public IReadOnlyList<string> CategoryPath => new[] { "媒体" };

    public IReadOnlyList<string> Tags => new[]
    {
        "播放器", "播放", "音乐", "视频", "音频", "mp3", "flac", "mp4", "MKV",
        "播放列表", "随机播放", "单曲循环", "列表循环", "快进", "快退", "音量", "本地",
        "快捷键", "热键", "切歌", "暂停",
    };

    public int Order => 15;

    /// <summary>页面始终绑常驻 VM（见 <see cref="ViewModel"/> 注释）。</summary>
    public FrameworkElement CreateView() => new MediaPlayerView { DataContext = _viewModel };

    // ================================================================== IShortcutProvider

    /// <summary>三条快捷键的工具内 Id 常量（设置页 / 自检用）。</summary>
    public const string ShortcutIdPrevious = "previous";

    /// <summary>见 <see cref="ShortcutIdPrevious"/>。</summary>
    public const string ShortcutIdNext = "next";

    /// <summary>见 <see cref="ShortcutIdPrevious"/>。</summary>
    public const string ShortcutIdPlayPause = "play-pause";

    public string ShortcutToolId => Id;

    public string ShortcutToolName => Name;

    public IReadOnlyList<string> ShortcutCategoryPath => new[] { "媒体" };

    public bool ShortcutsEnabled
    {
        get => _viewModel.Cfg.ShortcutsEnabled;
        set
        {
            if (_viewModel.Cfg.ShortcutsEnabled == value) return;
            _viewModel.Cfg.ShortcutsEnabled = value;
            ApplyShortcuts();
        }
    }

    /// <summary>快捷键定义变了（设置页刷新用）。</summary>
    public event EventHandler? ShortcutsChanged;

    /// <summary>按当前配置生成三条快捷键的实时快照。</summary>
    public IReadOnlyList<ShortcutDefinition> GetShortcuts() => _shortcutAdapter.Build(Id);

    /// <summary>三个槽位：名字 + 读 / 写组合 + 启用位。</summary>
    private IReadOnlyList<ShortcutProviderAdapter.Slot> BuildShortcutSlots() => new[]
    {
        new ShortcutProviderAdapter.Slot(
            ShortcutIdPrevious, "上一首",
            ReadPrimary: () => _viewModel.Cfg.ShortcutPrevious,
            WritePrimary: v => _viewModel.Cfg.ShortcutPrevious = v,
            ReadSecondary: () => _viewModel.Cfg.ShortcutPreviousSecond,
            WriteSecondary: v => _viewModel.Cfg.ShortcutPreviousSecond = v,
            ReadEnabled: () => _viewModel.Cfg.ShortcutPreviousEnabled,
            WriteEnabled: v => _viewModel.Cfg.ShortcutPreviousEnabled = v,
            CanSplit: true,
            Description: "播放器：上一首"),
        new ShortcutProviderAdapter.Slot(
            ShortcutIdNext, "下一首",
            ReadPrimary: () => _viewModel.Cfg.ShortcutNext,
            WritePrimary: v => _viewModel.Cfg.ShortcutNext = v,
            ReadSecondary: () => _viewModel.Cfg.ShortcutNextSecond,
            WriteSecondary: v => _viewModel.Cfg.ShortcutNextSecond = v,
            ReadEnabled: () => _viewModel.Cfg.ShortcutNextEnabled,
            WriteEnabled: v => _viewModel.Cfg.ShortcutNextEnabled = v,
            CanSplit: true,
            Description: "播放器：下一首"),
        new ShortcutProviderAdapter.Slot(
            ShortcutIdPlayPause, "播放 / 暂停",
            ReadPrimary: () => _viewModel.Cfg.ShortcutPlayPause,
            WritePrimary: v => _viewModel.Cfg.ShortcutPlayPause = v,
            ReadSecondary: () => _viewModel.Cfg.ShortcutPlayPauseSecond,
            WriteSecondary: v => _viewModel.Cfg.ShortcutPlayPauseSecond = v,
            ReadEnabled: () => _viewModel.Cfg.ShortcutPlayPauseEnabled,
            WriteEnabled: v => _viewModel.Cfg.ShortcutPlayPauseEnabled = v,
            CanSplit: true,
            Description: "播放器：播放 / 暂停"),
    };

    /// <summary>按当前配置重新注册（引擎由外壳提供，通过 Core 的静态入口 <see cref="ShortcutHub"/> 拿）。</summary>
    public void ApplyShortcuts()
    {
        ShortcutHub.ApplyAll();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ================================================================== IShortcutActionHost

    /// <summary>某条快捷键被按下（引擎在 UI 线程回调）。</summary>
    public void InvokeShortcut(string shortcutId)
    {
        switch (shortcutId)
        {
            case ShortcutIdPrevious:
                _viewModel.MoveTrack(-1);
                break;
            case ShortcutIdNext:
                _viewModel.MoveTrack(+1);
                break;
            case ShortcutIdPlayPause:
                _viewModel.TogglePlayPause();
                break;
        }
    }
}
