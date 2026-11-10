namespace YuLiangToolkit.Tools.Games.Tetris;

/// <summary>
/// 一局俄罗斯方块的**完整快照**（纯数据，可 JSON 序列化）：
/// 联机时房主把本端引擎的状态拍成它广播给客户端，客户端据此原样渲染。
///
/// <para>字段刻意用扁平的公开可写属性（而不是 record 的 init）：
/// <c>System.Text.Json</c> 的反序列化对两者都支持，但可写属性在协议演进时更宽容
/// （对方少发一个字段不会整个快照解析失败）。</para>
/// </summary>
public sealed class TetrisEngineSnapshot
{
    /// <summary>棋盘格子（<c>Width × Height</c>，一维，<c>null</c> = 空）。</summary>
    public TetrominoKind?[] Cells { get; set; } = Array.Empty<TetrominoKind?>();

    /// <summary>当前方块。</summary>
    public TetrisPiece? Current { get; set; }

    /// <summary>暂存的方块。</summary>
    public TetrominoKind? Held { get; set; }

    /// <summary>分数。</summary>
    public int Score { get; set; }

    /// <summary>已消行。</summary>
    public int Lines { get; set; }

    /// <summary>等级。</summary>
    public int Level { get; set; } = 1;

    /// <summary>还欠着的垃圾行。</summary>
    public int PendingGarbage { get; set; }

    /// <summary>这一局送出的垃圾行。</summary>
    public int GarbageSent { get; set; }

    /// <summary>这一局收到并插进棋盘的垃圾行。</summary>
    public int GarbageTaken { get; set; }

    /// <summary>是否已经顶死。</summary>
    public bool IsGameOver { get; set; }

    /// <summary>接下来 3 块。</summary>
    public TetrominoKind[] Next { get; set; } = Array.Empty<TetrominoKind>();
}
