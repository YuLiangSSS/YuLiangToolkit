using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Vsrg.ViewModels;
using YuLiangToolkit.Tools.Vsrg.Views;

namespace YuLiangToolkit.Tools.Vsrg;

/// <summary>
/// 「VSRG」功能入口：竖直下落式音游，读 osu!mania 谱面（只认 <c>Mode: 3</c>），
/// 把 <c>.osz</c> 拖进窗口导入成 <c>Data\vsrg\Mapset\</c> 下的谱面集；
/// 1K~10K 都能玩，可改键位 / 判定档 / 滚动速度 / 音频偏移，支持全屏与暂停。
///
/// <para>归入「游戏」类别（<c>Models/ShellCategories.cs</c> 配图标 / 描述 / 排序；
/// 工具只认这条路径字符串）。</para>
/// </summary>
public sealed class VsrgTool : ITool
{
    /// <summary>功能 Id（同时是 <c>Data\vsrg</c> / <c>Config\vsrg</c> 的文件夹名）。</summary>
    public string Id => VsrgConfig.ToolId;

    /// <inheritdoc />
    public string Name => "VSRG";

    /// <inheritdoc />
    public string Description =>
        "竖直下落式音游：把 osu!mania 谱面集的 .osz 拖进窗口就能导入（只认 mode 3），整理成 Mapset 谱面集；"
        + "1K~10K 都能玩，可改键位 / 判定档（默认 Standard 四档）/ 面条尾判宽度乘数 / 滚动速度 / 音频偏移，"
        + "支持全屏游玩与暂停退出。";

    /// <inheritdoc />
    public string EmojiIcon => "🎹";

    /// <inheritdoc />
    public string Category => "游戏";

    /// <inheritdoc />
    public IReadOnlyList<string> CategoryPath => new[] { "游戏" };

    /// <inheritdoc />
    public IReadOnlyList<string> Tags => new[]
    {
        "VSRG", "音游", "osu", "osu!mania", "mania", "osz", "mapset", "谱面", "下落式", "4K", "7K", "游戏",
    };

    /// <summary>「游戏」类别里排在俄罗斯方块 / 贪吃蛇之后（Tetris 70 / 贪吃蛇 72，这里取 74）。</summary>
    public int Order => 74;

    /// <inheritdoc />
    public FrameworkElement CreateView()
        => new VsrgView { DataContext = new VsrgViewModel() };
}
