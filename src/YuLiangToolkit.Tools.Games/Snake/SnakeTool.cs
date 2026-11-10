using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Games.Snake.ViewModels;
using YuLiangToolkit.Tools.Games.Snake.Views;

namespace YuLiangToolkit.Tools.Games.Snake;

/// <summary>
/// 「贪吃蛇」功能入口：两种对战玩法（共享果 / 大混战）、三种边缘规则、加速、九档 AI、
/// 局域网联机（房主权威）。
/// </summary>
public sealed class SnakeTool : ITool
{
    /// <inheritdoc />
    public string Id => SnakeConfig.ToolId;

    /// <inheritdoc />
    public string Name => "贪吃蛇";

    /// <inheritdoc />
    public string Description
        => "贪吃蛇对战：共享果（分区域抢同一颗果）与大混战（多蛇多果、蛇命、出生点爆炸）；"
        + "可调棋盘 / 果子上限 / 命数 / 限时 / 边缘规则 / 按住加速；九档 AI（参考九条蛇算法）；"
        + "评分按吃果 60% + 存活 20% + 连吃 20%；支持局域网联机。";

    /// <inheritdoc />
    public string EmojiIcon => "🐍";

    /// <inheritdoc />
    public string Category => "游戏";

    /// <inheritdoc />
    public IReadOnlyList<string> CategoryPath => new[] { "游戏" };

    /// <inheritdoc />
    public IReadOnlyList<string> Tags => new[]
    {
        "贪吃蛇", "游戏", "联机", "局域网", "AI", "大混战", "共享果", "加速",
    };

    /// <inheritdoc />
    public int Order => 72;

    /// <inheritdoc />
    public FrameworkElement CreateView()
        => new SnakeView { DataContext = new SnakeViewModel() };
}
