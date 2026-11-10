using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.BoardGames.Go.ViewModels;
using YuLiangToolkit.Tools.BoardGames.Go.Views;

namespace YuLiangToolkit.Tools.BoardGames.Go;

/// <summary>
/// 「围棋」功能入口：9 / 13 / 19 路、贴目可调，提子 / 禁自杀 / 禁打劫 / 数子计分，
/// 电脑对手（四档难度），局域网联机 + 观战 + 换人。
/// </summary>
public sealed class GoTool : ITool
{
    /// <inheritdoc />
    public string Id => GoConfig.ToolId;

    /// <inheritdoc />
    public string Name => "围棋";

    /// <inheritdoc />
    public string Description
        => "标准围棋：提子、禁自杀、禁打劫、双方停一手终局，中国规则数子（可调贴目）；"
        + "9 / 13 / 19 路；电脑对手四档难度；局域网联机，支持观战与房主安排上场 / 自动换人。";

    /// <inheritdoc />
    public string EmojiIcon => "⚫";

    /// <inheritdoc />
    public string Category => "棋牌";

    /// <inheritdoc />
    public IReadOnlyList<string> CategoryPath => new[] { "棋牌" };

    /// <inheritdoc />
    public IReadOnlyList<string> Tags => new[]
    {
        "围棋", "棋牌", "联机", "局域网", "打到", "数子", "贴目", "电脑对手",
    };

    /// <inheritdoc />
    public int Order => 64;

    /// <inheritdoc />
    public FrameworkElement CreateView()
        => new GoView { DataContext = new GoViewModel() };
}
