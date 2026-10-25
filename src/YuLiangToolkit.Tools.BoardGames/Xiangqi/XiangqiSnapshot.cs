namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>
/// 一局象棋的可传输快照（主机发给客户端的就是它，JSON 化后放进房间协议的 <c>snapshot.game</c> 里）。
///
/// <para><see cref="Fen"/> 只包含「棋子摆位 + 轮到谁」；无吃子计数、让先状态、棋谱另列字段，
/// 这样客户端既能完整重建棋局，也能只拿 FEN 画棋盘。</para>
/// </summary>
public sealed class XiangqiSnapshot
{
    /// <summary>局面 FEN（棋盘 + 行棋方）。</summary>
    public string Fen { get; set; } = "";

    /// <summary>是否已经开始对局（房间「开始对局」前为 false）。</summary>
    public bool Started { get; set; }

    /// <summary>结果："playing" / "redWin" / "blackWin" / "draw"。</summary>
    public string Result { get; set; } = "playing";

    /// <summary>结果原因（将死 / 困毙 / 自然限着判和 / 认输……；进行中为空）。</summary>
    public string? ResultReason { get; set; }

    /// <summary>连续无吃子的半回合数。</summary>
    public int HalfMovesSinceCapture { get; set; }

    /// <summary>让先还剩几步。</summary>
    public int OpeningFreeMovesRemaining { get; set; }

    /// <summary>让先时被让的一方："red" / "black"（没让先为 null）。</summary>
    public string? OpeningSide { get; set; }

    /// <summary>完整棋谱（客户端要显示棋谱、还要能悔棋）。</summary>
    public List<XiangqiMoveInfo> History { get; set; } = new();
}
