using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.BoardGames.Xiangqi.ViewModels;
using YuLiangToolkit.Tools.BoardGames.Xiangqi.Views;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>
/// 「联机象棋」功能入口：主机开 TCP 房间 / 客户端填地址加入，也可以同机双人。
/// 支持完整中国象棋规则、自由摆棋 + 一键默认开局、6 种让子、让先 0~5 步、走法提示、自动翻转、
/// 中文记谱、悔棋 / 认输 / 求和 / 再来一局。归入「棋牌」类别（见 AGENTS.md 的 2.15 / 2.18）。
/// </summary>
public class XiangqiTool : ITool
{
    /// <summary>功能 Id（同时是 Data\Config 下的小文件夹名）。</summary>
    public string Id => "xiangqi";

    public string Name => "联机象棋";

    public string Description
        => "开一个 TCP 房间，和朋友联机下中国象棋：自由摆棋 / 一键默认开局 / 让子 / 让先、"
           + "走法提示、自动翻转、中文记谱、悔棋 / 认输 / 求和 / 再来一局，也可以同机双人";

    public string EmojiIcon => "🀄";

    public string Category => "棋牌";

    /// <summary>归入主页「棋牌」类别。</summary>
    public IReadOnlyList<string> CategoryPath => new[] { "棋牌" };

    public IReadOnlyList<string> Tags => new[]
    {
        "象棋", "中国象棋", "棋牌", "联机", "局域网", "TCP", "双人", "对弈", "摆棋", "让子", "让先", "悔棋",
        "中文记谱", "残局", "自由摆棋",
    };

    public int Order => 61;

    public FrameworkElement CreateView()
        => new XiangqiView { DataContext = new XiangqiViewModel() };
}
