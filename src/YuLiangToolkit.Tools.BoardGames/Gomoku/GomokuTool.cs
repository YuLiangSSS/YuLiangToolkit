using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.BoardGames.Gomoku.ViewModels;
using YuLiangToolkit.Tools.BoardGames.Gomoku.Views;

namespace YuLiangToolkit.Tools.BoardGames.Gomoku;

/// <summary>
/// 「联机五子棋」功能入口：主机开一个 TCP 端口等人连，客户端填地址加入；
/// 也能同机双人下。可选黑棋禁手（三三 / 四四 / 长连）、悔棋、求和、认输、再来一局、棋谱。
/// 归入「棋牌」类别（见 docs/dev/pitfalls.md）。
/// </summary>
public class GomokuTool : ITool
{
    /// <summary>功能 Id（同时是 <c>Data\gomoku</c> / <c>Config\gomoku</c> 的文件夹名）。</summary>
    public string Id => GomokuConfig.ToolId;

    public string Name => "联机五子棋";

    public string Description
        => "开一个 TCP 房间和朋友联机下五子棋：可选禁手（三三 / 四四 / 长连）、悔棋、求和、聊天、同机双人";

    public string EmojiIcon => "⚫";

    public string Category => "棋牌";

    public IReadOnlyList<string> CategoryPath => new[] { "棋牌" };

    public IReadOnlyList<string> Tags => new[]
    {
        "五子棋", "连珠", "禁手", "棋牌", "联机", "局域网", "TCP", "双人", "对弈", "悔棋", "棋谱",
    };

    public int Order => 60;

    public FrameworkElement CreateView()
        => new GomokuView { DataContext = new GomokuViewModel() };
}
