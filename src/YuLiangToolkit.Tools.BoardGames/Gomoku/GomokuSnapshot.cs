namespace YuLiangToolkit.Tools.BoardGames.Gomoku;

/// <summary>
/// 五子棋局面的可序列化快照（联机时由主机发给客户端；也用于「悔棋恢复」之类的场景）。
///
/// <para>字段刻意都用「字符串 / 基本类型」，方便 JSON 与协议两处共用：
/// <see cref="Turn"/> 是 <c>"black"</c> / <c>"white"</c>，<see cref="Result"/> 是
/// <c>"playing"</c> / <c>"blackWin"</c> / <c>"whiteWin"</c> / <c>"draw"</c>，
/// <see cref="Cells"/> 是「从左到右、从上到下」一格一个字符：<c>.</c> 空 / <c>b</c> 黑 / <c>w</c> 白。</para>
/// </summary>
public sealed class GomokuSnapshot
{
    /// <summary>棋盘路数（15 / 19）。</summary>
    public int Size { get; set; } = GomokuBoard.DefaultSize;

    /// <summary>这一局开没开禁手（黑棋三三 / 四四 / 长连判负）。</summary>
    public bool ForbiddenEnabled { get; set; }

    /// <summary>对局有没有开始（房间等「开始对局」时为 false，界面不可下）。</summary>
    public bool Started { get; set; } = true;

    /// <summary>轮到谁走：<c>"black"</c> / <c>"white"</c>。</summary>
    public string Turn { get; set; } = "black";

    /// <summary>结果：playing / blackWin / whiteWin / draw。</summary>
    public string Result { get; set; } = "playing";

    /// <summary>棋子布局：长度 = <c>Size * Size</c>，字符 <c>.</c> / <c>b</c> / <c>w</c>。</summary>
    public string Cells { get; set; } = "";

    /// <summary>完整棋谱（按手数顺序）。</summary>
    public List<GomokuMove> Moves { get; set; } = new();

    /// <summary>最近一次禁手判负的点（界面标红）；没有就是 null。</summary>
    public GomokuPoint? LastForbiddenPoint { get; set; }

    /// <summary>获胜的连子（正好五连给 5 颗，长连按规则给完整线）；没有就是 null。</summary>
    public List<GomokuPoint>? WinningLine { get; set; }
}
