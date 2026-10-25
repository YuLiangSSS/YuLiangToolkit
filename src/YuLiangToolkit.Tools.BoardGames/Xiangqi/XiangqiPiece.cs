namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>
/// 对弈双方。红方先走；棋盘内部坐标里 <b>红方在下（y 大）、黑方在上（y 小）</b>。
/// </summary>
public enum XiangqiSide
{
    /// <summary>红方（先手，棋盘下方）。</summary>
    Red = 0,

    /// <summary>黑方（后手，棋盘上方）。</summary>
    Black = 1,
}

/// <summary>棋子种类（<see cref="Empty"/> 表示空格）。</summary>
public enum XiangqiPieceType
{
    /// <summary>空格。</summary>
    Empty = 0,

    /// <summary>帅 / 将。</summary>
    King,

    /// <summary>仕 / 士。</summary>
    Advisor,

    /// <summary>相 / 象。</summary>
    Elephant,

    /// <summary>马。</summary>
    Horse,

    /// <summary>车。</summary>
    Rook,

    /// <summary>炮。</summary>
    Cannon,

    /// <summary>兵 / 卒。</summary>
    Pawn,
}

/// <summary>
/// 一枚棋子 = 种类 + 颜色。<see cref="Empty"/> 就是「空格」。
///
/// <para>FEN 字母按本轮契约：红方大写 <c>K/A/E/H/R/C/P</c>、黑方小写；
/// 解析时额外容忍国际通用的 <c>N</c>（马）与 <c>B</c>（象）写法，方便和其它软件/棋谱交换。</para>
/// </summary>
public readonly record struct XiangqiPiece(XiangqiPieceType Type, XiangqiSide Side)
{
    /// <summary>空格子（没有棋子）。</summary>
    public static XiangqiPiece Empty => default;

    /// <summary>是不是空格子。</summary>
    public bool IsEmpty => Type == XiangqiPieceType.Empty;

    /// <summary>是不是红方棋子（空格返回 false）。</summary>
    public bool IsRed => !IsEmpty && Side == XiangqiSide.Red;

    /// <summary>是不是黑方棋子（空格返回 false）。</summary>
    public bool IsBlack => !IsEmpty && Side == XiangqiSide.Black;

    /// <summary>FEN 字母：红大写 / 黑小写；空格返回空字符串。</summary>
    public string FenChar => (Type, Side) switch
    {
        (XiangqiPieceType.Empty, _) => "",
        (XiangqiPieceType.King, XiangqiSide.Red) => "K",
        (XiangqiPieceType.Advisor, XiangqiSide.Red) => "A",
        (XiangqiPieceType.Elephant, XiangqiSide.Red) => "E",
        (XiangqiPieceType.Horse, XiangqiSide.Red) => "H",
        (XiangqiPieceType.Rook, XiangqiSide.Red) => "R",
        (XiangqiPieceType.Cannon, XiangqiSide.Red) => "C",
        (XiangqiPieceType.Pawn, XiangqiSide.Red) => "P",
        (XiangqiPieceType.King, _) => "k",
        (XiangqiPieceType.Advisor, _) => "a",
        (XiangqiPieceType.Elephant, _) => "e",
        (XiangqiPieceType.Horse, _) => "h",
        (XiangqiPieceType.Rook, _) => "r",
        (XiangqiPieceType.Cannon, _) => "c",
        _ => "p",
    };

    /// <summary>界面显示用的单个汉字：帅仕相马车炮兵 / 将士象马车炮卒；空格返回空字符串。</summary>
    public string DisplayChar => (Type, Side) switch
    {
        (XiangqiPieceType.Empty, _) => "",
        (XiangqiPieceType.King, XiangqiSide.Red) => "帅",
        (XiangqiPieceType.Advisor, XiangqiSide.Red) => "仕",
        (XiangqiPieceType.Elephant, XiangqiSide.Red) => "相",
        (XiangqiPieceType.Horse, _) => "马",
        (XiangqiPieceType.Rook, _) => "车",
        (XiangqiPieceType.Cannon, _) => "炮",
        (XiangqiPieceType.Pawn, XiangqiSide.Red) => "兵",
        (XiangqiPieceType.King, _) => "将",
        (XiangqiPieceType.Advisor, _) => "士",
        (XiangqiPieceType.Elephant, _) => "象",
        _ => "卒",
    };

    /// <summary>中文名（含颜色）：红方车 / 黑方炮 ……；空格返回「空格」。</summary>
    public string DisplayName => IsEmpty ? "空格" : (Side == XiangqiSide.Red ? "红方" : "黑方") + DisplayChar;

    /// <summary>按 FEN 字符还原棋子；无法识别返回 <see cref="Empty"/>。</summary>
    public static XiangqiPiece FromFenChar(char c)
    {
        var side = char.IsUpper(c) ? XiangqiSide.Red : XiangqiSide.Black;
        var type = char.ToUpperInvariant(c) switch
        {
            'K' => XiangqiPieceType.King,
            'A' => XiangqiPieceType.Advisor,
            'E' or 'B' => XiangqiPieceType.Elephant,   // E = 本轮契约，B = 国际通用写法
            'H' or 'N' => XiangqiPieceType.Horse,       // H = 本轮契约，N = 国际通用写法
            'R' => XiangqiPieceType.Rook,
            'C' => XiangqiPieceType.Cannon,
            'P' => XiangqiPieceType.Pawn,
            _ => XiangqiPieceType.Empty,
        };
        return type == XiangqiPieceType.Empty ? Empty : new XiangqiPiece(type, side);
    }

    /// <summary>按显示汉字还原棋子（帅仕相马车炮兵 / 将士象马车炮卒）；无法识别返回 <see cref="Empty"/>。</summary>
    public static XiangqiPiece FromDisplayChar(char c) => c switch
    {
        '帅' => new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Red),
        '将' => new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Black),
        '仕' => new XiangqiPiece(XiangqiPieceType.Advisor, XiangqiSide.Red),
        '士' => new XiangqiPiece(XiangqiPieceType.Advisor, XiangqiSide.Black),
        '相' => new XiangqiPiece(XiangqiPieceType.Elephant, XiangqiSide.Red),
        '象' => new XiangqiPiece(XiangqiPieceType.Elephant, XiangqiSide.Black),
        '马' => new XiangqiPiece(XiangqiPieceType.Horse, XiangqiSide.Red),
        '车' => new XiangqiPiece(XiangqiPieceType.Rook, XiangqiSide.Red),
        '炮' => new XiangqiPiece(XiangqiPieceType.Cannon, XiangqiSide.Red),
        '兵' => new XiangqiPiece(XiangqiPieceType.Pawn, XiangqiSide.Red),
        '卒' => new XiangqiPiece(XiangqiPieceType.Pawn, XiangqiSide.Black),
        _ => Empty,
    };
}

/// <summary>
/// 一步棋：从 (FromX, FromY) 走到 (ToX, ToY)，坐标是<b>棋盘内部坐标</b>
/// （x=0 最左、y=0 最上；红方在下）。
/// </summary>
public readonly record struct XiangqiMove(int FromX, int FromY, int ToX, int ToY)
{
    /// <summary>无效着法（快照 / 占位用）。</summary>
    public static XiangqiMove None => new(-1, -1, -1, -1);

    /// <summary>是不是「没有着法」。</summary>
    public bool IsNone => FromX < 0 || FromY < 0 || ToX < 0 || ToY < 0;

    /// <summary>稳定字符串 <c>"fxfy-txty"</c>（协议 / 快照用，例如 <c>"77-47"</c>）。</summary>
    public string Key => $"{FromX}{FromY}-{ToX}{ToY}";

    /// <summary>字符串化 = <see cref="Key"/>。</summary>
    public override string ToString() => Key;

    /// <summary>解析 <see cref="Key"/>；格式不对返回 <see cref="None"/>。</summary>
    public static XiangqiMove Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return None;
        var text = key.Trim();
        var dash = text.IndexOf('-');
        if (dash < 0) return None;
        var from = text[..dash];
        var to = text[(dash + 1)..];
        if (from.Length != 2 || to.Length != 2) return None;
        if (!char.IsAsciiDigit(from[0]) || !char.IsAsciiDigit(from[1]) ||
            !char.IsAsciiDigit(to[0]) || !char.IsAsciiDigit(to[1]))
        {
            return None;
        }
        return new XiangqiMove(from[0] - '0', from[1] - '0', to[0] - '0', to[1] - '0');
    }
}

/// <summary>阵营相关的小工具（取反、中文名、FEN 行棋方字符）。</summary>
public static class XiangqiSides
{
    /// <summary>对方阵营。</summary>
    public static XiangqiSide Opposite(this XiangqiSide side)
        => side == XiangqiSide.Red ? XiangqiSide.Black : XiangqiSide.Red;

    /// <summary>中文全称：红方 / 黑方。</summary>
    public static string DisplayName(this XiangqiSide side)
        => side == XiangqiSide.Red ? "红方" : "黑方";

    /// <summary>中文简称：红 / 黑。</summary>
    public static string ShortName(this XiangqiSide side)
        => side == XiangqiSide.Red ? "红" : "黑";

    /// <summary>协议 / 快照里用的字符串：red / black。</summary>
    public static string ToKey(this XiangqiSide side)
        => side == XiangqiSide.Red ? "red" : "black";

    /// <summary>FEN 里的行棋方字符：红 = w（先手）、黑 = b。</summary>
    public static string ToFenChar(this XiangqiSide side)
        => side == XiangqiSide.Red ? "w" : "b";

    /// <summary>解析协议 / FEN 里的阵营字符串（red/black/r/b/w/红/黑），认不出返回 null。</summary>
    public static XiangqiSide? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Trim().ToLowerInvariant() switch
        {
            "red" or "r" or "w" or "红" or "红方" => XiangqiSide.Red,
            "black" or "b" or "黑" or "黑方" => XiangqiSide.Black,
            _ => null,
        };
    }
}
