using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.BoardGames.Doudizhu.ViewModels;
using YuLiangToolkit.Tools.BoardGames.Doudizhu.Views;

namespace YuLiangToolkit.Tools.BoardGames.Doudizhu;

/// <summary>
/// 「斗地主」功能入口：经典 / 双副牌（炸弹多）/ 底牌明牌三种发牌模式，
/// 联机 3 人房间（房主 + 2 人，多出来的人排队等换人）。
/// </summary>
public sealed class DoudizhuTool : ITool
{
    /// <inheritdoc />
    public string Id => DoudizhuConfig.ToolId;

    /// <inheritdoc />
    public string Name => "斗地主";

    /// <inheritdoc />
    public string Description
        => "经典斗地主：叫地主、出牌、炸弹翻倍；支持经典 / 双副牌（炸弹多）/ 底牌明牌三种模式；"
        + "局域网 3 人联机，房主可设换人方式（输的下场 / 全场换人），多出来的人排队上场。";

    /// <inheritdoc />
    public string EmojiIcon => "🃏";

    /// <inheritdoc />
    public string Category => "棋牌";

    /// <inheritdoc />
    public IReadOnlyList<string> CategoryPath => new[] { "棋牌" };

    /// <inheritdoc />
    public IReadOnlyList<string> Tags => new[]
    {
        "斗地主", "牌类", "棋牌", "联机", "局域网", "炸弹", "双副牌", "三人",
    };

    /// <inheritdoc />
    public int Order => 62;

    /// <inheritdoc />
    public FrameworkElement CreateView()
        => new DoudizhuView { DataContext = new DoudizhuViewModel() };
}
