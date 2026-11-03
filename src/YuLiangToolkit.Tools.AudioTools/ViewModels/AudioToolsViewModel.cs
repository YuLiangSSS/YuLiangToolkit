using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.AudioTools.ViewModels;

/// <summary>
/// 「音频工具」页面的父 ViewModel：只负责顶部两个页签（视频转音频 / 分段视频合成）和子 ViewModel 生命周期。
/// 两个子功能各自仍是 <see cref="ToolViewModelBase"/>，配置 / Data / 自检都互不干扰。
/// </summary>
public sealed class AudioToolsViewModel : ToolViewModelBase
{
    private bool _isVideoTab = true;

    public AudioToolsViewModel() : base("audio-tools")
    {
        VideoToAudio = new VideoToAudioViewModel();
        SegmentMerge = new SegmentMergeViewModel();

        ShowVideoTabCommand = new RelayCommand(_ => IsVideoTab = true);
        ShowMergeTabCommand = new RelayCommand(_ => IsVideoTab = false);
        if (string.Equals(Environment.GetEnvironmentVariable("YLT_AUDIO_TAB"), "merge",
                StringComparison.OrdinalIgnoreCase))
        {
            IsVideoTab = false;
        }
        StatusText = "就绪";
    }

    /// <summary>视频转音频子页（ToolId 仍是 video-to-audio，老配置直接复用）。</summary>
    public VideoToAudioViewModel VideoToAudio { get; }

    /// <summary>分段视频合成子页（ToolId：video-merge）。</summary>
    public SegmentMergeViewModel SegmentMerge { get; }

    /// <summary>当前是否显示「视频转音频」页签。</summary>
    public bool IsVideoTab
    {
        get => _isVideoTab;
        private set
        {
            if (!Set(ref _isVideoTab, value)) return;
            OnPropertyChanged(nameof(IsMergeTab));
            RefreshCommandState();
        }
    }

    /// <summary>当前是否显示「分段视频合成」页签。</summary>
    public bool IsMergeTab => !_isVideoTab;

    public RelayCommand ShowVideoTabCommand { get; }
    public RelayCommand ShowMergeTabCommand { get; }

    /// <summary>工具被真正关闭：把两个子 ViewModel 也关掉（落盘配置 / 取消 FFmpeg 进程）。</summary>
    protected override void OnClosed()
    {
        try { VideoToAudio.Dispose(); } catch { /* 清理失败不影响主流程 */ }
        try { SegmentMerge.Dispose(); } catch { /* 同上 */ }
    }

    private static void RefreshCommandState() => CommandManager.InvalidateRequerySuggested();
}
