using System.Text;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>
/// 中国象棋棋盘：<b>9 列 × 10 行</b>，内部坐标 x=0 最左、y=0 最上；红方在下（y 7~9），黑方在上（y 0~2）。
///
/// <para>这里<b>只管棋子摆在哪</b>，不管走子规则（规则在 <see cref="XiangqiRules"/>）、
/// 也不管对局流程（在 <see cref="XiangqiGame"/>）。所以摆棋、载入快照、自检都能直接造棋盘。</para>
/// </summary>
public sealed class XiangqiBoard
{
    /// <summary>棋盘列数（横向 9 路）。</summary>
    public const int Width = 9;

    /// <summary>棋盘行数（纵向 10 行）。</summary>
    public const int Height = 10;

    private readonly XiangqiPiece[,] _cells = new XiangqiPiece[Width, Height];

    /// <summary>造一个空棋盘（没有任何棋子）。</summary>
    public XiangqiBoard()
    {
    }

    /// <summary>取某格的棋子；越界返回 <see cref="XiangqiPiece.Empty"/>。</summary>
    public XiangqiPiece Get(int x, int y)
        => InBounds(x, y) ? _cells[x, y] : XiangqiPiece.Empty;

    /// <summary>放 / 删棋子（放 <see cref="XiangqiPiece.Empty"/> = 清空该格）；越界忽略。</summary>
    public void Set(int x, int y, XiangqiPiece piece)
    {
        if (!InBounds(x, y)) return;
        _cells[x, y] = piece;
    }

    /// <summary>坐标是否在棋盘内。</summary>
    public static bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    /// <summary>该格是不是空格（越界按空格算）。</summary>
    public bool IsEmpty(int x, int y) => Get(x, y).IsEmpty;

    /// <summary>整个棋盘清空。</summary>
    public void Clear()
    {
        for (var x = 0; x < Width; x++)
        {
            for (var y = 0; y < Height; y++) _cells[x, y] = XiangqiPiece.Empty;
        }
    }

    /// <summary>深拷贝（摆棋 / 试走都靠它，别直接改别人的棋盘）。</summary>
    public XiangqiBoard Clone()
    {
        var copy = new XiangqiBoard();
        Array.Copy(_cells, copy._cells, _cells.Length);
        return copy;
    }

    /// <summary>数某一方还剩多少棋子。</summary>
    public int Count(XiangqiSide side)
    {
        var count = 0;
        for (var x = 0; x < Width; x++)
        {
            for (var y = 0; y < Height; y++)
            {
                var piece = _cells[x, y];
                if (!piece.IsEmpty && piece.Side == side) count++;
            }
        }
        return count;
    }

    /// <summary>数某一方某种棋子还剩多少个。</summary>
    public int Count(XiangqiPieceType type, XiangqiSide side)
    {
        var count = 0;
        for (var x = 0; x < Width; x++)
        {
            for (var y = 0; y < Height; y++)
            {
                var piece = _cells[x, y];
                if (piece.Type == type && piece.Side == side) count++;
            }
        }
        return count;
    }

    /// <summary>找出某一方某种棋子的全部坐标（按 y、x 顺序，摆棋 / 记谱用）。</summary>
    public IReadOnlyList<(int X, int Y)> FindPieces(XiangqiSide side, XiangqiPieceType type)
    {
        var list = new List<(int X, int Y)>(8);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var piece = _cells[x, y];
                if (piece.Type == type && piece.Side == side) list.Add((x, y));
            }
        }
        return list;
    }

    // ---------- 标准开局 ----------

    /// <summary>
    /// 标准开局棋盘（红方在下、黑方在上）。等于解析 <see cref="XiangqiSetup.InitialFen"/> 得到的局面。
    /// </summary>
    public static XiangqiBoard CreateInitial()
    {
        var (board, _, _, error) = ParseFen(XiangqiSetup.InitialFen);
        if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException($"标准开局 FEN 不合法：{error}");
        return board;
    }

    // ---------- FEN ----------

    /// <summary>
    /// 输出 FEN（中国象棋惯例：行从上到下、红大写黑小写）：
    /// <c>rheakaehr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RHEAKAEHR w - - 0 1</c>。
    /// 后两个数字是「无吃子半回合数」和「回合数」（回合数仅做格式占位，棋局本身不依赖它）。
    /// </summary>
    public string ToFen(XiangqiSide turn, int halfMovesSinceCapture = 0, int fullMoveNumber = 1)
    {
        var builder = new StringBuilder(128);
        for (var y = 0; y < Height; y++)
        {
            var empty = 0;
            for (var x = 0; x < Width; x++)
            {
                var piece = _cells[x, y];
                if (piece.IsEmpty)
                {
                    empty++;
                    continue;
                }
                if (empty > 0)
                {
                    builder.Append(empty);
                    empty = 0;
                }
                builder.Append(piece.FenChar);
            }
            if (empty > 0) builder.Append(empty);
            if (y < Height - 1) builder.Append('/');
        }
        builder.Append(' ').Append(turn.ToFenChar());
        builder.Append(" - - ").Append(Math.Max(0, halfMovesSinceCapture));
        builder.Append(' ').Append(Math.Max(1, fullMoveNumber));
        return builder.ToString();
    }

    /// <summary>
    /// 解析 FEN。至少要有「棋盘」这一段；行棋方 / 无吃子计数可省略（默认红先、0）。
    /// 失败时返回空棋盘 + 中文错误原因（不抛异常）。
    /// </summary>
    public static (XiangqiBoard Board, XiangqiSide Turn, int HalfMovesSinceCapture, string Error) ParseFen(string? fen)
    {
        var emptyBoard = new XiangqiBoard();
        if (string.IsNullOrWhiteSpace(fen)) return (emptyBoard, XiangqiSide.Red, 0, "FEN 是空的");

        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ranks = parts[0].Split('/');
        if (ranks.Length != Height)
        {
            return (emptyBoard, XiangqiSide.Red, 0, $"FEN 应该有 {Height} 行棋盘（现在 {ranks.Length} 行）");
        }

        var board = new XiangqiBoard();
        for (var row = 0; row < ranks.Length; row++)
        {
            var x = 0;
            foreach (var c in ranks[row])
            {
                if (char.IsAsciiDigit(c))
                {
                    x += c - '0';
                    if (x > Width) return (emptyBoard, XiangqiSide.Red, 0, $"FEN 第 {row + 1} 行格子数超过 {Width}");
                    continue;
                }
                var piece = XiangqiPiece.FromFenChar(c);
                if (piece.IsEmpty)
                {
                    return (emptyBoard, XiangqiSide.Red, 0, $"FEN 第 {row + 1} 行有无法识别的棋子字符「{c}」");
                }
                if (x >= Width) return (emptyBoard, XiangqiSide.Red, 0, $"FEN 第 {row + 1} 行格子数超过 {Width}");
                board.Set(x, row, piece);
                x++;
            }
            if (x != Width)
            {
                return (emptyBoard, XiangqiSide.Red, 0, $"FEN 第 {row + 1} 行格子数不是 {Width}（现在 {x}）");
            }
        }

        var turn = XiangqiSide.Red;
        if (parts.Length > 1)
        {
            var parsed = XiangqiSides.Parse(parts[1]);
            if (parsed is null) return (emptyBoard, XiangqiSide.Red, 0, $"FEN 的行棋方「{parts[1]}」认不出来（应为 w/b）");
            turn = parsed.Value;
        }

        var halfMoves = 0;
        if (parts.Length > 4 && int.TryParse(parts[4], out var parsedHalfMoves)) halfMoves = Math.Max(0, parsedHalfMoves);

        return (board, turn, halfMoves, "");
    }

    /// <summary>给调试 / 日志用的多行文本棋盘（红方在下）。</summary>
    public string DescribeText()
    {
        var builder = new StringBuilder();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var piece = _cells[x, y];
                builder.Append(piece.IsEmpty ? '·' : piece.DisplayChar[0]);
                if (x < Width - 1) builder.Append(' ');
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }
}
