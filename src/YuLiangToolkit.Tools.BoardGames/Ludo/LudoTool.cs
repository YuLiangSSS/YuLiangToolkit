using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.BoardGames.Ludo.ViewModels;
using YuLiangToolkit.Tools.BoardGames.Ludo.Views;

namespace YuLiangToolkit.Tools.BoardGames.Ludo;

/// <summary>
/// 「飞行棋」功能入口：4 色自选座位（可退出让位）、可开关规则
/// （掷 6 起飞 / 掷 5 也能起飞的后门 / 掷 6 再掷 / 精确到终点 / 踩子 / 飞行线踢子）、
/// 局域网联机。
/// </summary>
public sealed class LudoTool : ITool
{
    /// <inheritdoc />
    public string Id => LudoConfig.ToolId;

    /// <inheritdoc />
    public string Name => "飞行棋";

    /// <inheritdoc />
    public string Description
        => "飞行棋：红黄蓝绿四色自选座位（可退出让位），掷 6 起飞 / 后门 5 点起飞 / 掷 6 再掷 / "
        + "临近终点必须掷到刚好点数（掷多了往回退）/ 踩子踢回家 / 经过中央飞行线踢子；支持局域网联机。";

    /// <inheritdoc />
    public string EmojiIcon => "✈️";

    /// <inheritdoc />
    public string Category => "棋牌";

    /// <inheritdoc />
    public IReadOnlyList<string> CategoryPath => new[] { "棋牌" };

    /// <inheritdoc />
    public IReadOnlyList<string> Tags => new[]
    {
        "飞行棋", "棋牌", "联机", "局域网", "骰子", "四色", "座位自选",
    };

    /// <inheritdoc />
    public int Order => 65;

    /// <inheritdoc />
    public FrameworkElement CreateView()
        => new LudoView { DataContext = new LudoViewModel() };
}
