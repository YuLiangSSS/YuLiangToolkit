using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.BoardGames.Junqi.ViewModels;
using YuLiangToolkit.Tools.BoardGames.Junqi.Views;

namespace YuLiangToolkit.Tools.BoardGames.Junqi;

/// <summary>
/// 「军棋」功能入口：二人军棋（陆战棋）与四国军棋，暗棋自动裁判，
/// 联机房间（二人 2 席 / 四国 4 席，多出来的人排队等换人）。
/// </summary>
public sealed class JunqiTool : ITool
{
    /// <inheritdoc />
    public string Id => JunqiConfig.ToolId;

    /// <inheritdoc />
    public string Name => "军棋";

    /// <inheritdoc />
    public string Description
        => "军棋（陆战棋）：司令到工兵、地雷、炸弹、军旗，暗棋自动裁判、铁路走子、工兵拐弯、行营安全；"
        + "支持二人军棋与四国军棋；局域网联机，房主可设换人方式，多出来的人排队上场。";

    /// <inheritdoc />
    public string EmojiIcon => "🎖️";

    /// <inheritdoc />
    public string Category => "棋牌";

    /// <inheritdoc />
    public IReadOnlyList<string> CategoryPath => new[] { "棋牌" };

    /// <inheritdoc />
    public IReadOnlyList<string> Tags => new[]
    {
        "军棋", "陆战棋", "四国军棋", "棋牌", "联机", "局域网", "暗棋", "司令", "工兵", "地雷",
    };

    /// <inheritdoc />
    public int Order => 63;

    /// <inheritdoc />
    public FrameworkElement CreateView()
        => new JunqiView { DataContext = new JunqiViewModel() };
}
