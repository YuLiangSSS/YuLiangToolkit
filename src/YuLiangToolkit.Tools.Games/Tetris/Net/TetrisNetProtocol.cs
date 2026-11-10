using System.Text.Json;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.Games.Tetris.Net;

/// <summary>联机对战模式（1 对 1）。</summary>
public enum TetrisNetMode
{
    /// <summary>垃圾行互攻：自己消行就给对手塞垃圾行。</summary>
    Garbage = 0,

    /// <summary>各自计分：双方互不干扰（同一个出块序列），比分数。</summary>
    Score = 1,
}

/// <summary>一局联机对战的阶段。</summary>
public enum TetrisNetPhase
{
    /// <summary>等人连进来。</summary>
    Waiting = 0,

    /// <summary>双方都在，等待房主开始。</summary>
    Ready = 1,

    /// <summary>对局进行中。</summary>
    Playing = 2,

    /// <summary>这一局结束了（有结果）。</summary>
    Over = 3,
}

/// <summary>
/// 联机对战的线上协议（纯逻辑，自检可直接断言）。
///
/// <para><b>拓扑</b>：房主 = 权威端（跑真正的引擎），客户端只上报按键动作、只按快照渲染。
/// 所以垃圾行的计算、顶死判定、结果判定全都只在房主一处发生，两边不会算出不同的结果。</para>
///
/// <para><b>帧格式</b>沿用 Core 的 <see cref="RoomProtocol"/>（4 字节长度前缀 + JSON 头 + 裸载荷），
/// 这里只约定包类型与各包的载荷结构。<b>包类型是字符串</b>，将来加类型不会让老版本错位。</para>
/// </summary>
public static class TetrisNetProtocol
{
    /// <summary>握手标识：对不上直接拒连（防止别的程序连进来把连接搞乱）。</summary>
    public const string Magic = "YLTTETRIS1";

    /// <summary>协议版本（两边必须一致）。</summary>
    public const int Version = 1;

    /// <summary>房间人数上限（含房主）：1 对 1，所以是 2。</summary>
    public const int MaxPlayers = 2;

    /// <summary>房主在会话里的固定成员 id（客户端由房主分配 <c>m1</c>）。</summary>
    public const string HostMemberId = "host";

    /// <summary>包类型。</summary>
    public static class Kinds
    {
        /// <summary>客户端 → 房主：报家门（magic / 版本 / 模式 / 玩家名 / 上次成员 id）。</summary>
        public const string Hello = "hello";

        /// <summary>房主 → 客户端：握手回执（成员 id + 房主名 + 模式 + 当前状态）。</summary>
        public const string Welcome = "welcome";

        /// <summary>房主 → 客户端：最新完整状态（阶段 + 双方数据 + 棋盘快照）。</summary>
        public const string State = "state";

        /// <summary>客户端 → 房主：按键动作（left / right / rotateCw / rotateCcw / softDrop / hardDrop / hold）。</summary>
        public const string Action = "action";

        /// <summary>双向：系统提示（谁加入了、谁顶死了、对方掉线了）。</summary>
        public const string Notice = "notice";

        /// <summary>双向：礼貌离开（带原因，对方能立刻知道不是掉线）。</summary>
        public const string Bye = "bye";
    }

    /// <summary>客户端能上报的动作类型（房主收到后在自己那份权威引擎上执行）。</summary>
    public static class Actions
    {
        public const string Left = "left";
        public const string Right = "right";
        public const string RotateCw = "rotateCw";
        public const string RotateCcw = "rotateCcw";
        public const string SoftDrop = "softDrop";
        public const string HardDrop = "hardDrop";
        public const string Hold = "hold";

        /// <summary>全部合法动作（房主收到不在表里的动作直接忽略）。</summary>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            Left, Right, RotateCw, RotateCcw, SoftDrop, HardDrop, Hold,
        };

        /// <summary>这个动作字符串认不认识。</summary>
        public static bool IsKnown(string? action)
            => !string.IsNullOrWhiteSpace(action) && All.Contains(action.Trim(), StringComparer.Ordinal);
    }

    /// <summary>模式 → 线上字符串。</summary>
    public static string ModeToString(TetrisNetMode mode)
        => mode == TetrisNetMode.Score ? "score" : "garbage";

    /// <summary>线上字符串 → 模式（认不出按垃圾行互攻，容错优先）。</summary>
    public static TetrisNetMode ParseMode(string? text)
        => string.Equals((text ?? "").Trim(), "score", StringComparison.OrdinalIgnoreCase)
            ? TetrisNetMode.Score
            : TetrisNetMode.Garbage;

    /// <summary>模式的中文名（界面 / 提示用）。</summary>
    public static string ModeDisplayName(TetrisNetMode mode)
        => mode == TetrisNetMode.Score ? "各自计分" : "垃圾行互攻";

    /// <summary>模式的一句话说明（房间卡 / 说明窗口用）。</summary>
    public static string ModeDescription(TetrisNetMode mode)
        => mode == TetrisNetMode.Score
            ? "双方各一块棋盘、互不干扰（出块序列一样，比谁分高）；谁先顶死谁输，都顶死时比分数"
            : "双方各一块棋盘；自己一次消 2/3/4 行，就分别给对手底部塞 1/2/4 行垃圾行；谁先被顶死谁输";

    /// <summary>把当前状态包装成 JSON（放进 <see cref="StateInfo.State"/>）。</summary>
    public static JsonElement WrapState(TetrisNetState state)
        => JsonSerializer.SerializeToElement(state, RoomProtocol.JsonOptions);

    /// <summary>读回状态；格式不对返回 <c>null</c>（一个坏包不能把界面搞崩）。</summary>
    public static TetrisNetState? ReadState(JsonElement wrapped)
    {
        if (wrapped.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        try
        {
            return wrapped.Deserialize<TetrisNetState>(RoomProtocol.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>协议摘要（排错 / 界面提示用）。</summary>
    public static string Describe()
        => $"YuLiangToolkit 俄罗斯方块联机协议 v{Version}（magic={Magic}，最多 {MaxPlayers} 人）"
           + "；包类型：" + string.Join(" / ", new[] { Kinds.Hello, Kinds.Welcome, Kinds.State, Kinds.Action, Kinds.Notice, Kinds.Bye })
           + "；动作：" + string.Join(" / ", Actions.All);
}

/// <summary>客户端 → 房主的「报家门」。</summary>
public sealed class TetrisHelloInfo
{
    public string Magic { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string PlayerName { get; set; } = "";

    /// <summary>双方希望玩哪种模式（对不上时以房主为准，客户端会在提示里被告知）。</summary>
    public string Mode { get; set; } = "";

    /// <summary>上次连接时房主分配的成员 id（首次进房为空；重连带回来可以复用身份）。</summary>
    public string MemberId { get; set; } = "";
}

/// <summary>房主 → 客户端的握手回执。</summary>
public sealed class TetrisWelcomeInfo
{
    public string Magic { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string HostName { get; set; } = "";
    public string YourMemberId { get; set; } = "";

    /// <summary>房主实际使用的模式（客户端据此决定界面文案）。</summary>
    public string Mode { get; set; } = "";

    /// <summary>当前完整状态（可能为空 = 还没开对局）。</summary>
    public JsonElement? State { get; set; }
}

/// <summary>房主 → 客户端的状态广播。</summary>
public sealed class TetrisStateInfo
{
    public JsonElement? State { get; set; }
}

/// <summary>客户端 → 房主的按键动作。</summary>
public sealed class TetrisActionInfo
{
    public string Action { get; set; } = "";
}

/// <summary>系统提示（<see cref="Level"/>：info / warn / error）。</summary>
public sealed class TetrisNoticeInfo
{
    public string Text { get; set; } = "";
    public string Level { get; set; } = "info";
}

/// <summary>礼貌离开。</summary>
public sealed class TetrisByeInfo
{
    public string? Reason { get; set; }
}

/// <summary>一个玩家在联机里的公开数据（界面只显示这些，不显示对手的完整棋盘细节也能玩）。</summary>
public sealed class TetrisNetPlayer
{
    public string MemberId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsHost { get; set; }

    /// <summary>分数。</summary>
    public int Score { get; set; }

    /// <summary>已消行。</summary>
    public int Lines { get; set; }

    /// <summary>等级。</summary>
    public int Level { get; set; } = 1;

    /// <summary>已经顶死。</summary>
    public bool IsDead { get; set; }

    /// <summary>欠着还没插进棋盘的垃圾行（对手能看到「他要被塞 N 行」）。</summary>
    public int PendingGarbage { get; set; }

    /// <summary>送出去多少行垃圾。</summary>
    public int GarbageSent { get; set; }

    /// <summary>收到多少行垃圾。</summary>
    public int GarbageTaken { get; set; }

    /// <summary>棋盘快照（房主自己那份一直有；对手的只有缩略图需要时才有）。</summary>
    public TetrisEngineSnapshot? Board { get; set; }
}

/// <summary>
/// 联机对战的完整状态（房主权威，客户端只读）。
/// <para><see cref="Seq"/> 只增不减，客户端据此忽略乱序的旧包。</para>
/// </summary>
public sealed class TetrisNetState
{
    /// <summary>状态序号（只增，用来丢弃乱序旧包）。</summary>
    public int Seq { get; set; }

    /// <summary>当前阶段。</summary>
    public TetrisNetPhase Phase { get; set; } = TetrisNetPhase.Waiting;

    /// <summary>模式（房主说了算）。</summary>
    public string Mode { get; set; } = "garbage";

    /// <summary>房主名。</summary>
    public string HostName { get; set; } = "";

    /// <summary>房主那边的一局随机种子（各自计分模式下两边共用，出块序列才一致）。</summary>
    public int Seed { get; set; }

    /// <summary>房主自己的数据。</summary>
    public TetrisNetPlayer? Host { get; set; }

    /// <summary>客户端的数据（没人进来时为 null）。</summary>
    public TetrisNetPlayer? Guest { get; set; }

    /// <summary>结果文案（<see cref="TetrisNetPhase.Over"/> 时才有内容）。</summary>
    public string ResultText { get; set; } = "";

    /// <summary>结果站在谁那边：<c>host</c> / <c>guest</c> / <c>draw</c> / 空 = 没结果。</summary>
    public string ResultSide { get; set; } = "";
}

/// <summary>
/// 对局结果判定（纯函数，自检直接断言）：谁先顶死谁输；双方都顶死时比分数；都一样算平局。
/// </summary>
public static class TetrisNetJudge
{
    /// <summary>结果：<c>host</c> / <c>guest</c> / <c>draw</c> / 空字符串（还没结束）。</summary>
    /// <param name="hostDead">房主顶死了没有。</param>
    /// <param name="guestDead">客户端顶死了没有。</param>
    /// <param name="hostScore">房主分数。</param>
    /// <param name="guestScore">客户端分数。</param>
    public static string Decide(bool hostDead, bool guestDead, int hostScore, int guestScore)
    {
        if (!hostDead && !guestDead) return "";
        if (hostDead && !guestDead) return "guest";
        if (!hostDead && guestDead) return "host";
        return hostScore == guestScore ? "draw" : hostScore > guestScore ? "host" : "guest";
    }

    /// <summary>结果对应的中文文案。</summary>
    public static string Describe(string side, string hostName, string guestName)
        => side switch
        {
            "host" => $"{hostName} 赢了！",
            "guest" => $"{guestName} 赢了！",
            "draw" => "平局！",
            _ => "",
        };
}
