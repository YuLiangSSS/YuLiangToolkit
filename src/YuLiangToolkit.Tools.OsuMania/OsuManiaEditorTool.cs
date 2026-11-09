using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.OsuMania.ViewModels;
using YuLiangToolkit.Tools.OsuMania.Views;

namespace YuLiangToolkit.Tools.OsuMania;

/// <summary>
/// 「osu!mania 谱面编辑器」：直接读写 .osu，边听音频边摆 note。
///
/// <para>核心在 <c>Core/Editor/</c>：手写的高保真 .osu 读写、设备位置驱动的播放时钟、自绘时间轴。</para>
/// </summary>
public class OsuManiaEditorTool : ITool
{
    public string Id => OsuManiaEditorConfig.ToolId;

    public string Name => "osu!mania 谱面编辑器";

    public string Description => "边听边摆 note：普通键 / 长条 / 右键删除，音频与时间轴精确同步，可编辑红线绿线（SV）";

    public string EmojiIcon => "🎼";

    public string Category => "音游工具";

    /// <summary>类别路径（收进「音游工具」类别页）。</summary>
    public IReadOnlyList<string> CategoryPath => new[] { "音游工具" };

    public IReadOnlyList<string> Tags => new[]
    {
        "osu", "osu!", "mania", "音游", "谱面", "beatmap", "编辑器", "editor", "打谱", "note", "longnote", "ln", "sv",
    };

    public int Order => 50;

    public FrameworkElement CreateView() => new OsuManiaEditorView { DataContext = new OsuManiaEditorViewModel() };
}
