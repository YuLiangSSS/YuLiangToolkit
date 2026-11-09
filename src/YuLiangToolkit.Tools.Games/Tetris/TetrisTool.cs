using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Games.Tetris.ViewModels;
using YuLiangToolkit.Tools.Games.Tetris.Views;

namespace YuLiangToolkit.Tools.Games.Tetris;

/// <summary>
/// 「俄罗斯方块」功能入口：7-bag 出块、SRS 旋转与墙踢、幽灵落点、暂存、接下来 3 块预览；
/// 消行得分升级、等级越高落得越快，最高分存进 <c>Config\tetris\config.json</c>。
///
/// <para>归入「游戏」类别（<c>Models/ShellCategories.cs</c> 里配图标 / 描述 / 排序；
/// 工具只认这条路径字符串）。</para>
/// </summary>
public sealed class TetrisTool : ITool
{
    /// <summary>功能 Id（同时是 <c>Data\tetris</c> / <c>Config\tetris</c> 的文件夹名）。</summary>
    public string Id => TetrisConfig.ToolId;

    public string Name => "俄罗斯方块";

    public string Description =>
        "经典 10×20 俄罗斯方块：7-bag 出块、SRS 旋转 + 墙踢、幽灵落点预览、暂存（Hold）、接下来 3 块；"
        + "消行得分升级、等级越高落得越快，最高分自动记住，键盘 / 鼠标都能玩。";

    public string EmojiIcon => "🧱";

    public string Category => "游戏";

    /// <summary>主页「游戏」类别页里的工具（外壳按 CategoryPath 建目录树，见 AGENTS.md）。</summary>
    public IReadOnlyList<string> CategoryPath => new[] { "游戏" };

    public IReadOnlyList<string> Tags => new[]
    {
        "俄罗斯方块", "Tetris", "方块", "小游戏", "游戏", "益智", "单机", "键盘",
    };

    /// <summary>「游戏」类别里的第一块（以后加小游戏按 71、72… 往下排）。</summary>
    public int Order => 70;

    public FrameworkElement CreateView()
        => new TetrisView { DataContext = new TetrisViewModel() };
}
