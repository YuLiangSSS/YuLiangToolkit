using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.BoardGames.Checkers.ViewModels;
using YuLiangToolkit.Tools.BoardGames.Checkers.Views;

namespace YuLiangToolkit.Tools.BoardGames.Checkers;

/// <summary>
/// 「跳棋」功能入口：六芒星 121 格、2/3/4/6 人（5 人无合法排布）、
/// 自己选角（第一个选位置的人决定本局组合）、跳与连跳、可选走一格、
/// 占满对面角获胜、防卡出生点、让棋、AI。
/// </summary>
public sealed class CheckersTool : ITool
{
    /// <inheritdoc />
    public string Id => CheckersConfig.ToolId;

    /// <inheritdoc />
    public string Name => "跳棋";

    /// <inheritdoc />
    public string Description
        => "六芒星跳棋：121 个点位，2/3/4/6 人（5 人没有合法排布）；自己选角、第一个选的人决定本局组合；"
        + "跳与连跳（可开「走一格到相邻空位」）；占满对面角全部 10 个点获胜；防卡出生点；可让棋降低难度；带电脑对手。";

    /// <inheritdoc />
    public string EmojiIcon => "⭐";

    /// <inheritdoc />
    public string Category => "棋牌";

    /// <inheritdoc />
    public IReadOnlyList<string> CategoryPath => new[] { "棋牌" };

    /// <inheritdoc />
    public IReadOnlyList<string> Tags => new[] { "跳棋", "棋牌", "六芒星", "多人", "让棋", "电脑对手" };

    /// <inheritdoc />
    public int Order => 66;

    /// <inheritdoc />
    public FrameworkElement CreateView()
        => new CheckersView { DataContext = new CheckersViewModel() };
}
